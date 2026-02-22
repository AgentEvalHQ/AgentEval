// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
using System.Diagnostics;
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

        // Count total probes for progress
        var totalProbes = attacks.Sum(a => 
        {
            var count = a.GetProbes(options.Intensity).Count();
            return options.MaxProbesPerAttack > 0 ? Math.Min(count, options.MaxProbesPerAttack) : count;
        });
        var completedProbes = 0;

        foreach (var attack in attacks)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (attackResult, probesExecuted) = await ExecuteAttackAsync(
                agent,
                attack,
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
            InconclusiveProbes = attackResults.Sum(a => a.InconclusiveCount)
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
        ScanOptions options,
        IProgress<ScanProgress>? progress,
        int completedProbesBefore,
        int totalProbes,
        Stopwatch sw,
        CancellationToken cancellationToken)
    {
        var probeResults = new List<ProbeResult>();
        var evaluator = attack.GetEvaluator();
        var probes = attack.GetProbes(options.Intensity).ToList();

        // Apply MaxProbesPerAttack limit if set
        if (options.MaxProbesPerAttack > 0 && probes.Count > options.MaxProbesPerAttack)
        {
            probes = probes.Take(options.MaxProbesPerAttack).ToList();
        }

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
        catch (Exception ex)
        {
            probeSw.Stop();
            return new ProbeResult
            {
                ProbeId = probe.Id,
                Prompt = options.IncludeEvidence ? probe.Prompt : "[REDACTED]",
                Response = $"[ERROR: {ex.Message}]",
                Outcome = EvaluationOutcome.Inconclusive,
                Reason = $"Exception during probe execution: {ex.Message}",
                Technique = probe.Technique,
                Difficulty = probe.Difficulty,
                Duration = probeSw.Elapsed,
                Error = ex.Message,
                Severity = attackSeverity
            };
        }
    }
}
