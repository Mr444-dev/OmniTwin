using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using OmniTwin.Contracts;
using OmniTwin.Policies;

namespace OmniTwin.Brain;

/// <summary>Bounded synthesis telemetry; summary is public model output, never a private reasoning trace.</summary>
public sealed record BrainProgress(string Phase, string? Category, string? ModelIdUsed, bool UsedModelFallback, int Attempts, IReadOnlyList<string> Diagnostics);

/// <summary>Generates and verifies at most three candidates, starting generated code only inside OS isolation.</summary>
public sealed class SelfHealingLoop
{
    private readonly MuseSparkOpenClient _client; private readonly RoslynPolicyCompiler _compiler; private readonly string _worker, _scratch;
    /// <summary>Creates the asynchronous orchestrator with an explicit trusted worker deployment.</summary>
    public SelfHealingLoop(MuseSparkOpenClient client, RoslynPolicyCompiler compiler, string workerDirectory, string sandboxRoot)
    { _client = client; _compiler = compiler; _worker = workerDirectory; _scratch = sandboxRoot; }
    /// <summary>Returns an isolated policy on success, or null after a safely reported built-in fallback.</summary>
    public async Task<ISupervisedSwarmPolicy?> RunAsync(WorldStateSnapshot world, SwarmMissionCommand mission, Action<BrainProgress> report, CancellationToken token, WorkspaceBounds? bounds = null)
    {
        if (!OperatingSystem.IsWindows()) { report(new BrainProgress("fallback", "os_isolation_unavailable", null, false, 0, Array.Empty<string>())); return null; }
        if (!File.Exists(Path.Combine(_worker, "OmniTwin.PolicyWorker.dll")))
        { report(new BrainProgress("fallback", "policy_worker_not_deployed", null, false, 0, Array.Empty<string>())); return null; }
        IReadOnlyList<string> feedback = Array.Empty<string>(); string? model = null, previousSource = null; var alias = false;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            token.ThrowIfCancellationRequested(); report(new BrainProgress("generating", null, model, alias, attempt, feedback));
            try
            {
                var candidate = await _client.GenerateAsync(world, mission, feedback, token, bounds, previousSource).ConfigureAwait(false);
                model = candidate.ModelIdUsed; alias = candidate.UsedModelFallback; previousSource = candidate.PolicySource;
                report(new BrainProgress("verifying", null, model, alias, attempt, new[] { "Decision: " + candidate.DecisionSummary }));
                var compiled = await _compiler.CompileAsync(candidate.PolicySource, token).ConfigureAwait(false);
                if (!compiled.Success) { feedback = compiled.Diagnostics; report(new BrainProgress("repairing", "compilation_rejected", model, alias, attempt, feedback)); continue; }
                if (!OperatingSystem.IsWindows()) { report(new BrainProgress("fallback", "os_isolation_unavailable", model, alias, attempt, feedback)); return null; }
                var sandbox = new WindowsPolicySandbox(_worker, _scratch);
                var policy = await sandbox.StartAsync(compiled.Image!, token).ConfigureAwait(false);
                // Exercise the actual current context before activation; constructors and first compute cannot crash the host.
                try { SafeNavigation.ValidateDirectives(world, await policy.ComputeAsync(world, 0, token).ConfigureAwait(false), bounds ?? WorkspaceBounds.TacticalDesk); }
                catch { policy.Dispose(); throw; }
                report(new BrainProgress("verified", null, model, alias, attempt, new[] { "Decision: " + candidate.DecisionSummary, "Semantic verification: 0 errors, 0 warnings; AppContainer + resource job." }));
                return policy;
            }
            catch (BrainApiException error) { report(new BrainProgress("fallback", error.Category, error.ModelIdUsed, error.UsedModelFallback, attempt, feedback)); return null; }
            catch (Exception error) when (error is PolicyExecutionException or ArgumentException)
            { feedback = new[] { error is PolicyExecutionException failure ? failure.Category : "invalid_directives" }; }
            catch (Exception error) when (error is Win32Exception or IOException or UnauthorizedAccessException)
            { report(new BrainProgress("fallback", "os_isolation_start_failed", model, alias, attempt, Array.Empty<string>())); return null; }
        }
        report(new BrainProgress("fallback", "generation_attempts_exhausted", model, alias, 3, feedback)); return null;
    }
}
