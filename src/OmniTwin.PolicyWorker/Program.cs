using System;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OmniTwin.Contracts;
using OmniTwin.Transport;

namespace OmniTwin.PolicyWorker;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (!OperatingSystem.IsWindows() || !IsContainer()) return 41;
        try
        {
            using var input = Console.OpenStandardInput(); using var output = Console.OpenStandardOutput();
            if (args.Length == 3 && args[0] == "--probe-sandbox")
            {
                var deniedFile = false; var deniedNetwork = false;
                try { File.ReadAllBytes(args[1]); } catch (UnauthorizedAccessException) { deniedFile = true; }
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                try { using var deadline = new CancellationTokenSource(1000); await socket.ConnectAsync(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture)), deadline.Token); }
                catch (SocketException error) when (error.SocketErrorCode == SocketError.AccessDenied) { deniedNetwork = true; }
                catch (OperationCanceledException) { deniedNetwork = true; } // AppContainer WFP may silently drop loopback rather than return WSAEACCES.
                var noKeys = Environment.GetEnvironmentVariable("META_API_KEY") is null && Environment.GetEnvironmentVariable("BASETEN_API_KEY") is null;
                await PolicyWorkerProtocol.WriteAsync(output, Encoding.UTF8.GetBytes($"container={IsContainer()};fileDenied={deniedFile};networkDenied={deniedNetwork};noKeys={noKeys}"), CancellationToken.None); return 0;
            }
            if (args.Length != 2) return 42;
            // Loading and constructors run only after the OS isolation check, inside the restricted process.
            var assembly = Assembly.Load(File.ReadAllBytes(args[0]));
            var policyType = assembly.GetType(args[1], throwOnError: true)!;
            foreach (var method in policyType.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                if (!method.ContainsGenericParameters) RuntimeHelpers.PrepareMethod(method.MethodHandle);
            // Initialize trusted serializer metadata during the bounded startup phase, before the compute budget starts.
            _ = SwarmWireCodec.EncodeDirectives(Array.Empty<AgentTargetDirective>());
            var policy = (ISwarmPolicy)Activator.CreateInstance(policyType)!;
            var name = policy.PolicyName;
            if (string.IsNullOrWhiteSpace(name) || name.Length > 128 || name.Any(char.IsControl)) return 43;
            await PolicyWorkerProtocol.WriteAsync(output, Encoding.UTF8.GetBytes(name), CancellationToken.None);
            while (true)
            {
                byte[] bytes;
                try { bytes = await PolicyWorkerProtocol.ReadAsync(input, CancellationToken.None); } catch (EndOfStreamException) { return 0; }
                var request = PolicyWorkerProtocol.DecodeRequest(bytes);
                var directives = policy.ComputeDirectives(request.World, request.Seconds);
                if (directives is null || directives.Count > 128) return 44;
                var owned = directives.ToArray();
                await PolicyWorkerProtocol.WriteAsync(output, SwarmWireCodec.EncodeDirectives(owned), CancellationToken.None);
            }
        }
        catch (Exception) { return 45; } // The parent reports a safe category, never generated exception text or provider content.
    }
    private static bool IsContainer()
    {
        if (!OpenProcessToken(GetCurrentProcess(), 8, out var token)) return false;
        try { return GetTokenInformation(token, 29, out var value, 4, out _) && value != 0; }
        finally { CloseHandle(token); }
    }
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(IntPtr token, int information, out int value, int length, out int returned);
}
