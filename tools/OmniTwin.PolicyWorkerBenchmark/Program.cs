using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OmniTwin.Brain;
using OmniTwin.Contracts;

namespace OmniTwin.PolicyWorkerBenchmark;

internal static class Program
{
    private const string PolicySource = """
        using System.Collections.Generic;
        using OmniTwin.Contracts;
        namespace OmniTwin.Generated;
        public sealed class VerifiedPolicy : ISwarmPolicy
        {
            public string PolicyName => "AppContainer evaluation benchmark";
            public IReadOnlyList<AgentTargetDirective> ComputeDirectives(WorldStateSnapshot worldState, float timeSeconds)
            {
                var result = new List<AgentTargetDirective>(worldState.Agents.Count);
                foreach (var agent in worldState.Agents)
                    result.Add(new AgentTargetDirective(agent.AgentId, agent.CurrentPosition, 0, "benchmark_hold"));
                return result;
            }
        }
        """;

    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows AppContainer is required for this benchmark.");
            var samples = ReadInt(args, "--samples", 500, 20, 10000);
            var warmup = ReadInt(args, "--warmup", 30, 1, 1000);
            var reportPath = ReadPath(args, "--report", Path.Combine(FindRepositoryRoot(), "docs", "benchmarks", "stage7-policyworker.json"));
            var root = FindRepositoryRoot();
            var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Release";
            var worker = Path.Combine(root, "src", "OmniTwin.PolicyWorker", "bin", configuration, "net10.0");
            var scratch = Path.Combine(root, "local", "policy-sandbox-benchmarks");
            if (!File.Exists(Path.Combine(worker, "OmniTwin.PolicyWorker.dll")))
                throw new FileNotFoundException("The trusted PolicyWorker Release deployment is missing.", worker);

            using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var compiler = new RoslynPolicyCompiler();
            var compilation = await compiler.CompileAsync(PolicySource, cancellation.Token).ConfigureAwait(false);
            if (!compilation.Success || compilation.Image == null)
                throw new InvalidOperationException("The controlled benchmark policy did not pass semantic verification: " + string.Join(";", compilation.Diagnostics));

            var world = CreateWorld();
            var startup = Stopwatch.StartNew();
            using var policy = await new WindowsPolicySandbox(worker, scratch).StartAsync(compilation.Image, cancellation.Token).ConfigureAwait(false);
            startup.Stop();
            for (var i = 0; i < warmup; i++)
                _ = await policy.ComputeAsync(world, i, cancellation.Token).ConfigureAwait(false);

            var times = new double[samples];
            for (var i = 0; i < samples; i++)
            {
                var timer = Stopwatch.StartNew();
                var directives = await policy.ComputeAsync(world, warmup + i, cancellation.Token).ConfigureAwait(false);
                timer.Stop();
                if (directives.Count != world.Agents.Count) throw new InvalidDataException("The isolated worker returned an incomplete directive set.");
                times[i] = timer.Elapsed.TotalMilliseconds;
            }
            Array.Sort(times);
            var mean = times.Average();
            var report = new WorkerReport
            {
                CapturedAtUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                OperatingSystem = Environment.OSVersion.VersionString,
                Runtime = Environment.Version.ToString(),
                Isolation = "Windows AppContainer with zero capabilities and kill-on-close resource job",
                Policy = "Locally compiled deterministic hold fixture; no model or provider call",
                WorkerStartupMilliseconds = startup.Elapsed.TotalMilliseconds,
                WarmupExchanges = warmup,
                MeasuredExchanges = samples,
                AgentsPerRequest = world.Agents.Count,
                MeanRoundTripMilliseconds = mean,
                P50RoundTripMilliseconds = Percentile(times, 0.50),
                P95RoundTripMilliseconds = Percentile(times, 0.95),
                P99RoundTripMilliseconds = Percentile(times, 0.99),
                MaximumRoundTripMilliseconds = times[^1],
                MinimumRoundTripMilliseconds = times[0],
                ExecutionTimeoutMilliseconds = 100,
                MeasurementBoundary = "Parent wall time around Policy.ComputeAsync request/response IPC. Includes AppContainer worker dispatch and protocol serialization; excludes policy compilation and worker startup from steady-state percentiles."
            };
            var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            await File.WriteAllTextAsync(reportPath, json, cancellation.Token).ConfigureAwait(false);
            Console.WriteLine(json);
            Console.WriteLine("Report: " + reportPath);
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.GetType().Name + ": " + error.Message);
            return 1;
        }
    }

    private static WorldStateSnapshot CreateWorld()
    {
        var agents = new AgentTelemetry[5];
        for (var i = 0; i < agents.Length; i++)
            agents[i] = new AgentTelemetry(i + 1, i < 3 ? AgentRole.GroundVehicle : AgentRole.AerialDrone,
                new Vector2D(i - 2, 0), default, 1, AgentStatus.Holding);
        return new WorldStateSnapshot(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Array.Empty<SegmentedObstacle>(),
            agents, "AppContainer benchmark", "Hold agents at their current measured positions.");
    }

    private static double Percentile(double[] sorted, double percentile)
    {
        var index = Math.Clamp((int)Math.Ceiling(percentile * sorted.Length) - 1, 0, sorted.Length - 1);
        return sorted[index];
    }

    private static int ReadInt(string[] args, string name, int fallback, int minimum, int maximum)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] != name) continue;
            if (i + 1 >= args.Length || !int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < minimum || value > maximum)
                throw new ArgumentException("Invalid value for " + name + ".");
            return value;
        }
        return fallback;
    }

    private static string ReadPath(string[] args, string name, string fallback)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] != name) continue;
            if (i + 1 >= args.Length || !Path.IsPathFullyQualified(args[i + 1])) throw new ArgumentException("The report path must be absolute.");
            return Path.GetFullPath(args[i + 1]);
        }
        return fallback;
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "OmniTwin.sln"))) return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Could not find OmniTwin.sln from the current tool directory.");
    }

    private sealed class WorkerReport
    {
        public required string CapturedAtUtc { get; init; }
        public required string OperatingSystem { get; init; }
        public required string Runtime { get; init; }
        public required string Isolation { get; init; }
        public required string Policy { get; init; }
        public double WorkerStartupMilliseconds { get; init; }
        public int WarmupExchanges { get; init; }
        public int MeasuredExchanges { get; init; }
        public int AgentsPerRequest { get; init; }
        public double MeanRoundTripMilliseconds { get; init; }
        public double P50RoundTripMilliseconds { get; init; }
        public double P95RoundTripMilliseconds { get; init; }
        public double P99RoundTripMilliseconds { get; init; }
        public double MaximumRoundTripMilliseconds { get; init; }
        public double MinimumRoundTripMilliseconds { get; init; }
        public double ExecutionTimeoutMilliseconds { get; init; }
        public required string MeasurementBoundary { get; init; }
    }
}
