using System;
using System.ComponentModel;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OmniTwin.Contracts;
using OmniTwin.Policies;
using OmniTwin.Transport;

namespace OmniTwin.Brain;

/// <summary>OS-enforced Windows AppContainer launcher, with zero capabilities and a kill-on-close resource job.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsPolicySandbox
{
    private readonly string _workerDirectory, _root;
    /// <summary>Uses trusted, built worker binaries and a dedicated owned scratch root. No generated code executes here.</summary>
    public WindowsPolicySandbox(string workerDirectory, string sandboxRoot)
    {
        _workerDirectory = Path.GetFullPath(workerDirectory); _root = Path.GetFullPath(sandboxRoot);
        if (!File.Exists(Path.Combine(_workerDirectory, "OmniTwin.PolicyWorker.dll"))) throw new ArgumentException("Trusted policy worker is missing.", nameof(workerDirectory));
    }
    /// <summary>Starts and handshakes an isolated policy. Constructors and property getters share the startup deadline.</summary>
    public async Task<IsolatedSwarmPolicy> StartAsync(CompiledPolicyImage image, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(image);
        var process = Launch(new[] { "policy.dll", image.TypeName }, image.CopyImage());
        try
        {
            var name = Encoding.UTF8.GetString(await process.ReadAsync(TimeSpan.FromSeconds(8), token).ConfigureAwait(false));
            if (string.IsNullOrWhiteSpace(name) || name.Length > 128 || name.Any(char.IsControl)) throw new PolicyExecutionException("worker_invalid_handshake");
            return new IsolatedSwarmPolicy(process, name);
        }
        catch { process.Dispose(); throw; }
    }
    /// <summary>Runs the trusted isolation probe, checking actual token, filesystem, loopback and credential boundaries.</summary>
    public async Task<string> ProbeAsync(string outsideCanary, int loopbackPort, CancellationToken token)
    {
        using var process = Launch(new[] { "--probe-sandbox", Path.GetFullPath(outsideCanary), loopbackPort.ToString(System.Globalization.CultureInfo.InvariantCulture) }, null);
        return Encoding.UTF8.GetString(await process.ReadAsync(TimeSpan.FromSeconds(8), token).ConfigureAwait(false));
    }
    private SandboxProcess Launch(string[] arguments, byte[]? image)
    {
        var id = "OmniTwin.Policy." + Guid.NewGuid().ToString("N");
        var directory = Path.Combine(_root, id); Directory.CreateDirectory(directory);
        IntPtr sid = IntPtr.Zero, job = IntPtr.Zero, attributes = IntPtr.Zero, security = IntPtr.Zero, handles = IntPtr.Zero, environment = IntPtr.Zero;
        var input = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
        var output = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        var error = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        ProcessInformation info = default;
        try
        {
            var result = CreateAppContainerProfile(id, id, "OmniTwin isolated policy", IntPtr.Zero, 0, out sid);
            if (result < 0) Marshal.ThrowExceptionForHR(result);
            foreach (var file in Directory.EnumerateFiles(_workerDirectory))
                if (Path.GetExtension(file) is ".dll" or ".json") File.Copy(file, Path.Combine(directory, Path.GetFileName(file)), false);
            if (image is not null) File.WriteAllBytes(Path.Combine(directory, "policy.dll"), image);
            var acl = new DirectoryInfo(directory).GetAccessControl();
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid), FileSystemRights.ReadAndExecute,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(directory).SetAccessControl(acl);
            job = CreateJobObject(IntPtr.Zero, null); Check(job != IntPtr.Zero);
            var limits = new JobLimits
            {
                Basic = new BasicLimits { Flags = 0x2000 | 0x8 | 0x100 | 0x200 | 0x2, ActiveProcesses = 1, ProcessTime = TimeSpan.FromSeconds(30).Ticks },
                ProcessMemory = (UIntPtr)(256L * 1024 * 1024), JobMemory = (UIntPtr)(384L * 1024 * 1024)
            };
            Check(SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<JobLimits>()));
            var size = IntPtr.Zero; InitializeProcThreadAttributeList(IntPtr.Zero, 2, 0, ref size);
            attributes = Marshal.AllocHGlobal(size); Check(InitializeProcThreadAttributeList(attributes, 2, 0, ref size));
            security = Marshal.AllocHGlobal(Marshal.SizeOf<SecurityCapabilities>());
            Marshal.StructureToPtr(new SecurityCapabilities { Sid = sid }, security, false);
            Check(UpdateProcThreadAttribute(attributes, 0, (IntPtr)0x20009, security, (IntPtr)Marshal.SizeOf<SecurityCapabilities>(), IntPtr.Zero, IntPtr.Zero));
            var inherited = new[] { input.ClientSafePipeHandle.DangerousGetHandle(), output.ClientSafePipeHandle.DangerousGetHandle(), error.ClientSafePipeHandle.DangerousGetHandle() };
            handles = Marshal.AllocHGlobal(IntPtr.Size * inherited.Length); Marshal.Copy(inherited, 0, handles, inherited.Length);
            Check(UpdateProcThreadAttribute(attributes, 0, (IntPtr)0x20002, handles, (IntPtr)(IntPtr.Size * inherited.Length), IntPtr.Zero, IntPtr.Zero));
            var startup = new StartupInfoEx { Attributes = attributes, Startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfoEx>(), Flags = 0x100, Input = inherited[0], Output = inherited[1], Error = inherited[2] } };
            var dotnetRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet");
            var executable = Path.Combine(dotnetRoot, "dotnet.exe");
            var variables = new[] { "SystemRoot=" + Environment.GetFolderPath(Environment.SpecialFolder.Windows), "windir=" + Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "USERPROFILE=" + Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "LOCALAPPDATA=" + Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TEMP=" + directory, "TMP=" + directory, "DOTNET_ROOT=" + dotnetRoot,
                "DOTNET_EnableDiagnostics=0", "DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1", "DOTNET_GCHeapHardLimit=0x8000000", "DOTNET_GCConserveMemory=9" };
            environment = Marshal.StringToHGlobalUni(string.Join('\0', variables.Order(StringComparer.OrdinalIgnoreCase)) + "\0\0");
            var command = new StringBuilder(Quote(executable) + " " + Quote(Path.Combine(directory, "OmniTwin.PolicyWorker.dll")) + " " + string.Join(" ", arguments.Select(Quote)));
            Check(CreateProcess(executable, command, IntPtr.Zero, IntPtr.Zero, true, 0x00080000 | 0x00000004 | 0x08000000 | 0x00000400,
                environment, directory, ref startup, out info));
            Check(AssignProcessToJobObject(job, info.Process));
            Check(ResumeThread(info.Thread) != uint.MaxValue);
            input.DisposeLocalCopyOfClientHandle(); output.DisposeLocalCopyOfClientHandle(); error.DisposeLocalCopyOfClientHandle();
            CloseHandle(info.Thread); info.Thread = IntPtr.Zero;
            var owner = new SandboxProcess(input, output, error, job, info.Process, id, directory, _root);
            job = IntPtr.Zero; info.Process = IntPtr.Zero; return owner;
        }
        catch
        {
            if (info.Process != IntPtr.Zero) { TerminateProcess(info.Process, 47); CloseHandle(info.Process); }
            if (info.Thread != IntPtr.Zero) CloseHandle(info.Thread);
            if (job != IntPtr.Zero) CloseHandle(job);
            input.Dispose(); output.Dispose(); error.Dispose(); DeleteAppContainerProfile(id); DeleteOwned(directory, _root); throw;
        }
        finally
        {
            if (attributes != IntPtr.Zero) { DeleteProcThreadAttributeList(attributes); Marshal.FreeHGlobal(attributes); }
            if (security != IntPtr.Zero) Marshal.FreeHGlobal(security);
            if (handles != IntPtr.Zero) Marshal.FreeHGlobal(handles);
            if (environment != IntPtr.Zero) Marshal.FreeHGlobal(environment);
            if (sid != IntPtr.Zero) FreeSid(sid);
        }
    }
    private static string Quote(string value)
    {
        if (value.IndexOfAny(new[] { '"', '\r', '\n', '\0' }) >= 0 || value.EndsWith('\\')) throw new ArgumentException("Invalid process argument.");
        return "\"" + value + "\"";
    }
    private static void Check(bool success) { if (!success) throw new Win32Exception(Marshal.GetLastWin32Error()); }
    internal static void DeleteOwned(string directory, string root)
    {
        var full = Path.GetFullPath(directory); var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || Path.GetDirectoryName(full) != Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)))
            throw new InvalidOperationException("Sandbox cleanup escaped its owner root.");
        // A worker has RX only. It cannot plant reparse points or write any child path here.
        if (Directory.Exists(full)) Directory.Delete(full, true);
    }
    internal sealed class SandboxProcess : IDisposable
    {
        private readonly AnonymousPipeServerStream _input, _output, _error;
        private readonly IntPtr _job, _process; private readonly string _profile, _directory, _root; private int _disposed;
        private readonly Timer _lifetime;
        internal SandboxProcess(AnonymousPipeServerStream input, AnonymousPipeServerStream output, AnonymousPipeServerStream error,
            IntPtr job, IntPtr process, string profile, string directory, string root)
        { _input = input; _output = output; _error = error; _job = job; _process = process; _profile = profile; _directory = directory; _root = root; _lifetime = new Timer(_ => Kill(), null, TimeSpan.FromMinutes(10), Timeout.InfiniteTimeSpan); }
        internal Task<byte[]> ReadAsync(TimeSpan timeout, CancellationToken token) => ExchangeAsync(null, timeout, token);
        internal async Task<byte[]> ExchangeAsync(byte[]? request, TimeSpan timeout, CancellationToken token)
        {
            if (Volatile.Read(ref _disposed) != 0) throw new PolicyExecutionException("worker_disposed");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(timeout);
            using var kill = deadline.Token.Register(Kill);
            try
            {
                if (request is not null) await PolicyWorkerProtocol.WriteAsync(_input, request, deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
                return await PolicyWorkerProtocol.ReadAsync(_output, deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new PolicyExecutionException("execution_timeout"); }
            catch (Exception failure) when (failure is IOException or WorldStreamProtocolException or ObjectDisposedException)
            { throw new PolicyExecutionException(deadline.IsCancellationRequested ? "execution_timeout" : "worker_protocol_or_exit"); }
        }
        private void Kill() { if (Volatile.Read(ref _disposed) == 0) TerminateJobObject(_job, 46); }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _lifetime.Dispose(); TerminateJobObject(_job, 46); WaitForSingleObject(_process, 2000); CloseHandle(_process); CloseHandle(_job);
            _input.Dispose(); _output.Dispose(); _error.Dispose(); DeleteAppContainerProfile(_profile); DeleteOwned(_directory, _root);
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct SecurityCapabilities { public IntPtr Sid, Capabilities; public uint Count, Reserved; }
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits { public long ProcessTime, JobTime; public uint Flags; public UIntPtr MinWorkingSet, MaxWorkingSet; public uint ActiveProcesses; public UIntPtr Affinity; public uint Priority, Scheduling; }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct JobLimits { public BasicLimits Basic; public IoCounters Io; public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo { public int Size; public IntPtr Reserved, Desktop, Title; public uint X, Y, XSize, YSize, XCount, YCount, Fill, Flags; public ushort Show, ReservedSize; public IntPtr ReservedBytes, Input, Output, Error; }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { public StartupInfo Startup; public IntPtr Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
    [DllImport("userenv.dll", CharSet = CharSet.Unicode)] private static extern int CreateAppContainerProfile(string name, string display, string description, IntPtr capabilities, uint count, out IntPtr sid);
    [DllImport("userenv.dll", CharSet = CharSet.Unicode)] private static extern int DeleteAppContainerProfile(string name);
    [DllImport("advapi32.dll")] private static extern IntPtr FreeSid(IntPtr sid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(IntPtr job, int type, ref JobLimits limits, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool InitializeProcThreadAttributeList(IntPtr attributes, int count, uint flags, ref IntPtr size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool UpdateProcThreadAttribute(IntPtr attributes, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returned);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr attributes);
    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcess(string executable, StringBuilder command, IntPtr processSecurity, IntPtr threadSecurity, bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfoEx startup, out ProcessInformation process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern bool TerminateProcess(IntPtr process, uint code);
    [DllImport("kernel32.dll")] private static extern bool TerminateJobObject(IntPtr job, uint code);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
}

/// <summary>A supervised reusable policy whose getters and compute method execute exclusively inside an AppContainer.</summary>
[SupportedOSPlatform("windows")]
public sealed class IsolatedSwarmPolicy : ISupervisedSwarmPolicy, IDisposable
{
    private readonly WindowsPolicySandbox.SandboxProcess _process; private readonly SemaphoreSlim _gate = new(1, 1);
    internal IsolatedSwarmPolicy(WindowsPolicySandbox.SandboxProcess process, string name) { _process = process; PolicyName = name; }
    /// <inheritdoc />
    public string PolicyName { get; }
    /// <inheritdoc />
    public async Task<System.Collections.Generic.IReadOnlyList<AgentTargetDirective>> ComputeAsync(WorldStateSnapshot world, float timeSeconds, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return SwarmWireCodec.DecodeDirectives(await _process.ExchangeAsync(PolicyWorkerProtocol.EncodeRequest(world, timeSeconds), TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false)); }
        catch (WorldStreamProtocolException) { Dispose(); throw new PolicyExecutionException("invalid_directives"); }
        finally { _gate.Release(); }
    }
    /// <summary>Kills the entire job and removes its private AppContainer profile and owned files.</summary>
    public void Dispose() => _process.Dispose();
}
