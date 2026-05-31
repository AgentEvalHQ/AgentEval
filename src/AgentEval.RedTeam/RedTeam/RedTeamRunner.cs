// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
using System.Diagnostics;
using System.IO;
using AgentEval.Core;

namespace AgentEval.RedTeam;

/// <summary>
/// Default implementation of <see cref="IRedTeamRunner"/>.
/// Executes attack probes sequentially against an agent.
/// </summary>
public sealed class RedTeamRunner : IRedTeamRunner
{
    /// <summary>
    /// Initializes a new RedTeamRunner.
    /// </summary>
    public RedTeamRunner()
    {
    }

    /// <inheritdoc />
    public Task<RedTeamResult> ScanAsync(
        IEvaluableAgent agent,
        ScanOptions options,
        CancellationToken cancellationToken = default)
        => ScanAsync(agent, options, progress: null, cancellationToken);

    /// <inheritdoc />
    public async Task<RedTeamResult> ScanAsync(
        IEvaluableAgent agent,
        ScanOptions options,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(options);

        var sw = Stopwatch.StartNew();
        var attackResults = new List<AttackResult>();

        // Resolve attacks to run
        var attacks = ResolveAttacks(options);

        // Materialize each attack's probe list ONCE and reuse it for both the progress total and
        // execution — GetProbes builds a fresh List per call, so the previous count-then-execute
        // pattern ran every generator at least twice per scan (PERF-10).
        var probesByAttack = attacks.ToDictionary(
            a => a,
            a =>
            {
                var probes = a.GetProbes(options.Intensity).ToList();
                if (options.MaxProbesPerAttack > 0 && probes.Count > options.MaxProbesPerAttack)
                    probes = probes.Take(options.MaxProbesPerAttack).ToList();
                return (IReadOnlyList<AttackProbe>)probes;
            });

        // Count total probes for progress
        var totalProbes = probesByAttack.Values.Sum(p => p.Count);
        var completedProbes = 0;

        foreach (var attack in attacks)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (attackResult, probesExecuted) = await ExecuteAttackAsync(
                agent,
                attack,
                probesByAttack[attack],
                options,
                progress,
                completedProbes,
                totalProbes,
                sw,
                cancellationToken);

            completedProbes += probesExecuted;
            attackResults.Add(attackResult);

            // FailFast check
            if (options.FailFast && attackResult.SucceededCount > 0)
            {
                break;
            }
        }

        sw.Stop();

        return new RedTeamResult
        {
            AgentName = agent.Name,
            StartedAt = DateTimeOffset.UtcNow - sw.Elapsed,
            CompletedAt = DateTimeOffset.UtcNow,
            Duration = sw.Elapsed,
            Options = options,
            AttackResults = attackResults,
            TotalProbes = attackResults.Sum(a => a.TotalCount),
            SucceededProbes = attackResults.Sum(a => a.SucceededCount),
            ResistedProbes = attackResults.Sum(a => a.ResistedCount),
            InconclusiveProbes = attackResults.Sum(a => a.InconclusiveCount),
            ErroredProbes = attackResults.Sum(a => a.ErroredCount)
        };
    }

    private static List<IAttackType> ResolveAttacks(ScanOptions options)
    {
        if (options.AttackTypes != null && options.AttackTypes.Count > 0)
        {
            return options.AttackTypes.ToList();
        }

        // Use all registered attacks at default intensity
        return Attack.All.ToList();
    }

    private async Task<(AttackResult Result, int ProbesExecuted)> ExecuteAttackAsync(
        IEvaluableAgent agent,
        IAttackType attack,
        IReadOnlyList<AttackProbe> probes,
        ScanOptions options,
        IProgress<ScanProgress>? progress,
        int completedProbesBefore,
        int totalProbes,
        Stopwatch sw,
        CancellationToken cancellationToken)
    {
        var probeResults = new List<ProbeResult>();
        var evaluator = attack.GetEvaluator();
        // `probes` is materialized once by the caller (incl. MaxProbesPerAttack) — see PERF-10.

        var completedProbes = completedProbesBefore;
        var totalResisted = 0;
        var totalSucceeded = 0;
        EvaluationOutcome? lastOutcome = null;

        foreach (var probe in probes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Report progress before execution
            var shouldReport = (completedProbes - completedProbesBefore) % options.ProgressReportInterval == 0;
            if (shouldReport)
            {
                var progressReport = new ScanProgress(
                    completedProbes,
                    totalProbes,
                    attack.Name,
                    probe.Id,
                    sw.Elapsed,
                    totalResisted,
                    totalSucceeded,
                    lastOutcome);

                progress?.Report(progressReport);
                options.OnProgress?.Invoke(progressReport);
            }

            var probeResult = await ExecuteProbeAsync(
                agent,
                probe,
                evaluator,
                options,
                attack.DefaultSeverity,
                cancellationToken);

            probeResults.Add(probeResult);
            completedProbes++;
            lastOutcome = probeResult.Outcome;

            // Update counters
            if (probeResult.Outcome == EvaluationOutcome.Resisted)
                totalResisted++;
            else if (probeResult.Outcome == EvaluationOutcome.Succeeded)
                totalSucceeded++;

            // FailFast check at probe level
            if (options.FailFast && probeResult.Outcome == EvaluationOutcome.Succeeded)
            {
                break;
            }

            // Respect rate limiting delay
            if (options.DelayBetweenProbes > TimeSpan.Zero)
            {
                await Task.Delay(options.DelayBetweenProbes, cancellationToken);
            }
        }

        var result = new AttackResult
        {
            AttackName = attack.Name,
            AttackDisplayName = attack.DisplayName,
            OwaspId = attack.OwaspLlmId,
            MitreAtlasIds = attack.MitreAtlasIds.ToArray(),
            Severity = attack.DefaultSeverity,
            ProbeResults = probeResults,
            SucceededCount = probeResults.Count(p => p.Outcome == EvaluationOutcome.Succeeded),
            ResistedCount = probeResults.Count(p => p.Outcome == EvaluationOutcome.Resisted),
            InconclusiveCount = probeResults.Count(p => p.Outcome == EvaluationOutcome.Inconclusive)
        };

        return (result, completedProbes - completedProbesBefore);
    }

    private async Task<ProbeResult> ExecuteProbeAsync(
        IEvaluableAgent agent,
        AttackProbe probe,
        IProbeEvaluator evaluator,
        ScanOptions options,
        Severity attackSeverity,
        CancellationToken cancellationToken)
    {
        var probeSw = Stopwatch.StartNew();

        try
        {
            // Create timeout CTS for probe execution
            using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            probeCts.CancelAfter(options.TimeoutPerProbe);

            // Execute probe against agent
            var response = await agent.InvokeAsync(probe.Prompt, probeCts.Token);
            var responseText = response.Text;

            // Evaluate response
            var evalResult = await evaluator.EvaluateAsync(probe, responseText, probeCts.Token);

            probeSw.Stop();

            return new ProbeResult
            {
                ProbeId = probe.Id,
                Prompt = options.IncludeEvidence ? probe.Prompt : "[REDACTED]",
                Response = options.IncludeEvidence ? responseText : "[REDACTED]",
                Outcome = evalResult.Outcome,
                Reason = evalResult.Reason,
                MatchedItems = evalResult.MatchedItems,
                Technique = probe.Technique,
                Difficulty = probe.Difficulty,
                Duration = probeSw.Elapsed,
                Severity = attackSeverity
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // Re-throw if main cancellation was requested
        }
        catch (OperationCanceledException)
        {
            probeSw.Stop();
            return new ProbeResult
            {
                ProbeId = probe.Id,
                Prompt = options.IncludeEvidence ? probe.Prompt : "[REDACTED]",
                Response = "[TIMEOUT]",
                Outcome = EvaluationOutcome.Inconclusive,
                Reason = $"Probe timed out after {options.TimeoutPerProbe.TotalSeconds:F1}s",
                Technique = probe.Technique,
                Difficulty = probe.Difficulty,
                Duration = probeSw.Elapsed,
                Error = "Timeout",
                Severity = attackSeverity
            };
        }
        catch (Exception ex) when (IsTransportError(ex))
        {
            // Expected transport/connectivity errors (network blip, socket reset, remote timeout).
            // Inconclusive, but still counted as an execution error at the scan level so a broken
            // transport is not silently reported as a clean Pass (GAP-07).
            probeSw.Stop();
            return BuildErrorResult(probe, options, attackSeverity, probeSw.Elapsed, "Transport error", ex);
        }
        catch (Exception ex)
        {
            // Unexpected fault (likely a bug in the agent/evaluator wiring). Surfaced as an
            // execution error; ex.Message is gated behind IncludeEvidence to avoid leaking internal
            // endpoint/stack detail (GAP-07).
            probeSw.Stop();
            return BuildErrorResult(probe, options, attackSeverity, probeSw.Elapsed, "Execution error", ex);
        }
    }

    /// <summary>Transport/connectivity exceptions treated as expected (Inconclusive) rather than
    /// unexpected faults — see GAP-07.</summary>
    private static bool IsTransportError(Exception ex) =>
        ex is System.Net.Http.HttpRequestException
            or TimeoutException
            or IOException
            or System.Net.Sockets.SocketException;

    /// <summary>
    /// Builds an Inconclusive <see cref="ProbeResult"/> for a failed probe execution. The exception
    /// message is included in Response/Reason/Error only when <see cref="ScanOptions.IncludeEvidence"/>
    /// is set; otherwise a category-only message is used so internal detail is not leaked (GAP-07).
    /// </summary>
    private static ProbeResult BuildErrorResult(
        AttackProbe probe, ScanOptions options, Severity severity, TimeSpan elapsed, string category, Exception ex)
    {
        var includeEvidence = options.IncludeEvidence;
        return new ProbeResult
        {
            ProbeId = probe.Id,
            Prompt = includeEvidence ? probe.Prompt : "[REDACTED]",
            Response = includeEvidence ? $"[{category.ToUpperInvariant()}: {ex.Message}]" : $"[{category.ToUpperInvariant()}]",
            Outcome = EvaluationOutcome.Inconclusive,
            Reason = includeEvidence
                ? $"{category} during probe execution: {ex.Message}"
                : $"{category} during probe execution (details suppressed; enable IncludeEvidence to see them)",
            Technique = probe.Technique,
            Difficulty = probe.Difficulty,
            Duration = elapsed,
            Error = includeEvidence ? $"{category}: {ex.Message}" : category,
            Severity = severity
        };
    }
}
