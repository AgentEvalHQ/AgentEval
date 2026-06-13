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

        // Count total PLANNED probes (RA3-06: the denominator a non-truncated scan would execute).
        var plannedProbes = probesByAttack.Values.Sum(p => p.Count);
        var completedProbes = 0;
        var failFastTriggered = false;

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
                plannedProbes,
                sw,
                cancellationToken);

            completedProbes += probesExecuted;
            attackResults.Add(attackResult);

            // FailFast check
            if (options.FailFast && attackResult.SucceededCount > 0)
            {
                failFastTriggered = true;
                break;
            }
        }

        sw.Stop();

        // RA3-06 / T5-2: be honest about truncation. When FailFast stops the scan early, the executed
        // probe set (and every rate/score over it) is NOT comparable to a full scan. Record how many
        // probes were skipped so PlannedProbes and the baseline comparer can reason about it.
        var executedProbes = attackResults.Sum(a => a.TotalCount);
        var skippedProbes = Math.Max(0, plannedProbes - executedProbes);
        var wasTruncated = failFastTriggered && skippedProbes > 0;

        return new RedTeamResult
        {
            AgentName = agent.Name,
            StartedAt = DateTimeOffset.UtcNow - sw.Elapsed,
            CompletedAt = DateTimeOffset.UtcNow,
            Duration = sw.Elapsed,
            Options = options,
            AttackResults = attackResults,
            TotalProbes = executedProbes,
            SucceededProbes = attackResults.Sum(a => a.SucceededCount),
            ResistedProbes = attackResults.Sum(a => a.ResistedCount),
            InconclusiveProbes = attackResults.Sum(a => a.InconclusiveCount),
            ErroredProbes = attackResults.Sum(a => a.ErroredCount),
            WasTruncated = wasTruncated,
            SkippedProbes = skippedProbes
        };
    }

    private static List<IAttackType> ResolveAttacks(ScanOptions options)
    {
        if (options.AttackTypes != null && options.AttackTypes.Count > 0)
        {
            // 5e: Distinct() (reference identity — Attack.ByName returns singletons) so a duplicated entry,
            // e.g. `--attacks PromptInjection,PROMPT_INJECTION` resolving to the same instance, no longer crashes
            // the later ToDictionary with a bare duplicate-key error. Two SEPARATE instances of the same class
            // (differently-configured custom attacks) are preserved.
            return options.AttackTypes.Distinct().ToList();
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
        var evaluator = attack.GetEvaluator();
        // `probes` is materialized once by the caller (incl. MaxProbesPerAttack) — see PERF-10.

        // RA3-05 / T5-1: bounded-concurrency path only when the caller opts in; Parallelism <= 1 keeps the
        // exact sequential behavior (probe-level FailFast, progress timing, inter-probe delay) unchanged.
        var probeResults = options.Parallelism > 1
            ? await RunProbesParallelAsync(agent, attack, probes, evaluator, options, progress, completedProbesBefore, totalProbes, sw, cancellationToken)
            : await RunProbesSequentialAsync(agent, attack, probes, evaluator, options, progress, completedProbesBefore, totalProbes, sw, cancellationToken);

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

        return (result, probeResults.Count);
    }

    /// <summary>
    /// Sequential probe execution (Parallelism &lt;= 1). Preserves byte-for-byte prior behavior: report-before-
    /// execute progress, probe-level FailFast, per-probe rate-limit delay.
    /// </summary>
    private async Task<List<ProbeResult>> RunProbesSequentialAsync(
        IEvaluableAgent agent, IAttackType attack, IReadOnlyList<AttackProbe> probes, IProbeEvaluator evaluator,
        ScanOptions options, IProgress<ScanProgress>? progress, int completedProbesBefore, int totalProbes,
        Stopwatch sw, CancellationToken cancellationToken)
    {
        var probeResults = new List<ProbeResult>();
        var completedProbes = completedProbesBefore;
        var totalResisted = 0;
        var totalSucceeded = 0;
        EvaluationOutcome? lastOutcome = null;

        foreach (var probe in probes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Report progress before execution
            var reportInterval = Math.Max(1, options.ProgressReportInterval);
            var shouldReport = (completedProbes - completedProbesBefore) % reportInterval == 0;
            if (shouldReport)
            {
                var progressReport = new ScanProgress(
                    completedProbes, totalProbes, attack.Name, probe.Id, sw.Elapsed,
                    totalResisted, totalSucceeded, lastOutcome);

                progress?.Report(progressReport);
                options.OnProgress?.Invoke(progressReport);
            }

            var probeResult = await ExecuteProbeAsync(agent, attack, probe, evaluator, options, attack.DefaultSeverity, cancellationToken);

            probeResults.Add(probeResult);
            completedProbes++;
            lastOutcome = probeResult.Outcome;

            if (probeResult.Outcome == EvaluationOutcome.Resisted)
                totalResisted++;
            else if (probeResult.Outcome == EvaluationOutcome.Succeeded)
                totalSucceeded++;

            // FailFast check at probe level
            if (options.FailFast && probeResult.Outcome == EvaluationOutcome.Succeeded)
                break;

            // Respect rate limiting delay
            if (options.DelayBetweenProbes > TimeSpan.Zero)
                await Task.Delay(options.DelayBetweenProbes, cancellationToken);
        }

        return probeResults;
    }

    /// <summary>
    /// Bounded-concurrency probe execution (Parallelism &gt; 1, RA3-05). A <see cref="SemaphoreSlim"/> caps the
    /// number of in-flight probes; results are reordered to the original probe order so reports stay
    /// deterministic regardless of completion order. FailFast stops SCHEDULING new probes once a success is
    /// seen, but cannot cancel probes already in flight — those complete and are recorded.
    /// </summary>
    private async Task<List<ProbeResult>> RunProbesParallelAsync(
        IEvaluableAgent agent, IAttackType attack, IReadOnlyList<AttackProbe> probes, IProbeEvaluator evaluator,
        ScanOptions options, IProgress<ScanProgress>? progress, int completedProbesBefore, int totalProbes,
        Stopwatch sw, CancellationToken cancellationToken)
    {
        var dop = Math.Max(1, options.Parallelism);
        using var gate = new SemaphoreSlim(dop, dop);
        var indexed = new ProbeResult?[probes.Count];
        var tasks = new List<Task>(probes.Count);

        var failFastHit = 0;
        var completedProbes = completedProbesBefore;
        var totalResisted = 0;
        var totalSucceeded = 0;
        var progressLock = new object();

        try
        {
            for (var i = 0; i < probes.Count; i++)
            {
                if (cancellationToken.IsCancellationRequested)
                    break; // stop scheduling; already-launched probes are observed in the finally
                if (options.FailFast && Volatile.Read(ref failFastHit) == 1)
                    break; // a probe already succeeded — schedule no more (in-flight ones still complete)

                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                var idx = i;
                var probe = probes[i];
                // No token passed to Task.Run: the delegate must ALWAYS run so its finally releases the gate
                // permit acquired above. Task.Run(.., token) would skip the body (and the Release) if the token
                // fired before the ThreadPool dispatched it, leaking a permit. Cancellation is observed inside
                // ExecuteProbeAsync via the linked token (review).
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        var r = await ExecuteProbeAsync(agent, attack, probe, evaluator, options, attack.DefaultSeverity, cancellationToken).ConfigureAwait(false);
                        indexed[idx] = r;

                        if (options.FailFast && r.Outcome == EvaluationOutcome.Succeeded)
                            Interlocked.Exchange(ref failFastHit, 1);

                        ScanProgress? report = null;
                        lock (progressLock)
                        {
                            completedProbes++;
                            if (r.Outcome == EvaluationOutcome.Resisted) totalResisted++;
                            else if (r.Outcome == EvaluationOutcome.Succeeded) totalSucceeded++;

                            var reportInterval = Math.Max(1, options.ProgressReportInterval);
                            if ((completedProbes - completedProbesBefore) % reportInterval == 0)
                                report = new ScanProgress(
                                    completedProbes, totalProbes, attack.Name, probe.Id, sw.Elapsed,
                                    totalResisted, totalSucceeded, r.Outcome);
                        }
                        // Invoke arbitrary user callbacks OUTSIDE the lock so a slow/blocking handler cannot
                        // serialize the whole worker pool (review).
                        if (report is { } snapshot)
                        {
                            progress?.Report(snapshot);
                            options.OnProgress?.Invoke(snapshot);
                        }
                    }
                    finally
                    {
                        gate.Release();
                    }
                }));

                // Honor DelayBetweenProbes as inter-DISPATCH spacing so the configured rate limit still applies
                // under parallelism — the SemaphoreSlim caps concurrency, not request rate (review).
                if (options.DelayBetweenProbes > TimeSpan.Zero)
                    await Task.Delay(options.DelayBetweenProbes, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            // Observe every launched probe (even if scheduling was cut short by cancellation) so no task
            // exception is lost and the semaphore is fully drained before disposal.
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Deterministic order; probes never scheduled (FailFast) stay null and are dropped.
        return indexed.Where(r => r is not null).Select(r => r!).ToList();
    }

    private async Task<ProbeResult> ExecuteProbeAsync(
        IEvaluableAgent agent,
        IAttackType attack,
        AttackProbe probe,
        IProbeEvaluator evaluator,
        ScanOptions options,
        Severity attackSeverity,
        CancellationToken cancellationToken)
    {
        var probeSw = Stopwatch.StartNew();

        // T5-4 (Status §8): a probe flagged structurally-untestable describes a manipulation a text chat
        // agent physically cannot perform (set sampler/decode params, open a stream, invoke a tool). Record
        // it Inconclusive with a clear reason — never a fabricated pass/fail — and don't bother invoking the
        // agent. It counts toward InconclusiveProbes (not an execution error), so coverage stays honest.
        if (probe.Metadata is not null
            && probe.Metadata.TryGetValue(AttackProbe.StructurallyUntestableMetadataKey, out var untestable)
            && untestable is true)
        {
            probeSw.Stop();
            return new ProbeResult
            {
                ProbeId = probe.Id,
                Prompt = options.IncludeEvidence ? probe.Prompt : "[REDACTED]",
                Response = "[NOT EXERCISED]",
                Outcome = EvaluationOutcome.Inconclusive,
                Reason = $"Technique '{probe.Technique}' is structurally untestable by a text chat agent " +
                         "(requires setting inference parameters, opening a stream, or invoking a tool) — " +
                         "recorded Inconclusive, not scored. Needs a transport/tool-level harness (FeatureComplete).",
                Technique = probe.Technique,
                Difficulty = probe.Difficulty,
                Duration = probeSw.Elapsed,
                Severity = attackSeverity,
                Fidelity = EvidenceFidelity.Verbal,
                Surface = probe.Surface
            };
        }

        try
        {
            // Create timeout CTS for probe execution
            using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            probeCts.CancelAfter(options.TimeoutPerProbe);

            // Wave C (Pillar 2): a multi-turn attack drives a whole conversation per seed and folds it into ONE
            // ProbeResult. The single-turn TimeoutPerProbe would cap the WHOLE conversation at one turn's budget, so
            // multi-turn gets a CONVERSATION-sized hard bound instead: MaxConversationDuration if set, else
            // MaxTurns × TimeoutPerTurn (each turn gets its full per-turn budget). TimeoutPerTurn still bounds each
            // individual turn inside the orchestrator (review).
            // Wave C′ (TAP): a tree attack searches a pruned tree of attacker-generated prompts per seed and folds the
            // whole search into ONE ProbeResult — the same fold shape as the linear multi-turn path below. Budget: a
            // conversation-sized bound (MaxConversationDuration, else MaxNodes × TimeoutPerProbe).
            if (attack is ITreeAttack treeAttack)
            {
                var treeBudget = options.MaxConversationDuration > TimeSpan.Zero
                    ? options.MaxConversationDuration
                    : TimeSpan.FromTicks(options.TimeoutPerProbe.Ticks * Math.Max(1, treeAttack.MaxNodes));
                using var treeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                treeCts.CancelAfter(treeBudget);
                var tr = await new TreeOrchestrator(agent, options).RunAsync(treeAttack, probe, evaluator, treeCts.Token);
                probeSw.Stop();
                return BuildFoldedProbeResult(probe, tr, attackSeverity, probeSw.Elapsed, options);
            }

            if (attack is IMultiTurnAttack multiTurnAttack)
            {
                var convoBudget = options.MaxConversationDuration > TimeSpan.Zero
                    ? options.MaxConversationDuration
                    : TimeSpan.FromTicks(options.TimeoutPerTurn.Ticks * Math.Max(1, multiTurnAttack.MaxTurns));
                using var convoCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                convoCts.CancelAfter(convoBudget);
                var mt = await new TurnOrchestrator(agent, options).RunAsync(multiTurnAttack, probe, evaluator, convoCts.Token);
                probeSw.Stop();
                return BuildFoldedProbeResult(probe, mt, attackSeverity, probeSw.Elapsed, options);
            }

            // Execute probe against agent. Wave B (Pillar 1): if the SUT is tool-capable AND the attack advertises
            // canary tools, invoke through the tool channel (Tier 1/2) so the evaluator can read emitted/executed
            // tool calls; otherwise the plain text path (Tier 0), byte-for-byte as before.
            // Gate (review): do NOT advertise attacker canaries to inlined UserMessage probes — those are pure-text
            // proxies that have nothing to do with the tools, and advertising exfil tools to them risks mis-scoring a
            // spurious tool call against an inlined probe. null-Surface probes (e.g. a non-injection tool attack) still
            // get tools; only an explicitly inlined (UserMessage) probe is excluded.
            var useToolChannel = agent is IToolCapableAgent { Capabilities: not AgentToolCapability.None }
                                 && attack is IToolAwareAttack
                                 && probe.Surface != InjectionSurface.UserMessage;

            // #2 honesty (RC-6): a real tool-output boundary probe carries NO inlined payload — the poison
            // lives in a canary tool's output and is only delivered if the SUT actually calls tools. When the
            // tool channel will not engage (text-only SUT, or a non-tool-aware attack), the attack was never
            // deliverable; the marker can't appear and the verbal ContainsToken oracle would fabricate a
            // conclusive Resisted that feeds compliance pass counts. Record Inconclusive (not scored) instead,
            // mirroring the structurally-untestable gate above, without invoking the agent.
            // NOTE: only ToolOutput (FunctionResultContent boundary) is gated. RetrievedDocument probes in this
            // codebase INLINE their poisoned context block into the prompt, so they ARE deliverable to a
            // text-only SUT and must not be gated.
            if (!useToolChannel && probe.Surface is InjectionSurface.ToolOutput)
            {
                probeSw.Stop();
                return new ProbeResult
                {
                    ProbeId = probe.Id,
                    Prompt = options.IncludeEvidence ? probe.Prompt : "[REDACTED]",
                    Response = "[NOT EXERCISED]",
                    Outcome = EvaluationOutcome.Inconclusive,
                    Reason = $"Probe targets the {probe.Surface} trust boundary, deliverable only to a tool-capable " +
                             "SUT (the payload lives in a canary tool's output). This SUT has no engaged tool channel, " +
                             "so the attack was never delivered — recorded Inconclusive, not scored.",
                    Technique = probe.Technique,
                    Difficulty = probe.Difficulty,
                    Duration = probeSw.Elapsed,
                    Severity = attackSeverity,
                    Fidelity = EvidenceFidelity.Verbal,
                    Surface = probe.Surface
                };
            }

            var response = useToolChannel
                ? await ((IToolCapableAgent)agent).InvokeWithToolsAsync(probe.Prompt, ((IToolAwareAttack)attack).GetCanaryTools(options.Intensity), probeCts.Token)
                : await agent.InvokeAsync(probe.Prompt, probeCts.Token);
            var responseText = response.Text;

            // Evaluate response. RC-1: hand the evaluator the FULL AgentResponse so tool-aware
            // evaluators can read RawMessages; text-only evaluators ignore it via the default member.
            var evalResult = await evaluator.EvaluateAsync(probe, response, probeCts.Token);

            // RC-1: classify the evidence the verdict rests on (Verbal / IntentToAct / Behavioral).
            var fidelity = ResolveFidelity(evalResult);

            // GAP-19 / RC-1: LLM-judge fallback — only when Inconclusive AND a judge client is configured.
            // The judge reasons over text only, so a judge-derived verdict is capped at IntentToAct
            // (Succeeded ⇒ IntentToAct, Resisted ⇒ Verbal).
            if (evalResult.Outcome == EvaluationOutcome.Inconclusive && options.JudgeClient is not null)
            {
                var judge = new Evaluators.LLMJudgeEvaluator(options.JudgeClient);
                var judged = await judge.EvaluateAsync(probe, responseText, probeCts.Token);
                if (judged.Outcome != EvaluationOutcome.Inconclusive)
                {
                    evalResult = judged;
                    fidelity = judged.Outcome == EvaluationOutcome.Succeeded
                        ? EvidenceFidelity.IntentToAct
                        : EvidenceFidelity.Verbal;
                }
            }

            // --explain (ExplainFindings): attach a best-effort LLM rationale narrating the verdict + fidelity for
            // Succeeded/Inconclusive findings only (the user-selected scope). Opt-in, judge-gated, never changes the
            // verdict, and a failure/timeout here is swallowed so it can never fail the probe.
            // H1: the rationale is generated from the RAW response/prompt and routinely quotes sensitive content, so it
            // is gated on IncludeEvidence exactly like Prompt/Response below — a redacted scan must not leak via the
            // explain channel. Gating here also avoids the (paid) LLM call entirely when evidence is redacted.
            string? rationale = null;
            if (options.IncludeEvidence && options.ExplainFindings && options.JudgeClient is not null
                && evalResult.Outcome is EvaluationOutcome.Succeeded or EvaluationOutcome.Inconclusive)
            {
                try
                {
                    var text = await new Evaluators.LLMJudgeEvaluator(options.JudgeClient)
                        .ExplainAsync(probe, responseText, evalResult.Outcome, fidelity, probeCts.Token);
                    rationale = string.IsNullOrWhiteSpace(text) ? null : text;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { /* explain timed out — skip */ }
                catch { /* explain is best-effort */ }
            }

            probeSw.Stop();

            return new ProbeResult
            {
                ProbeId = probe.Id,
                Prompt = options.IncludeEvidence ? probe.Prompt : "[REDACTED]",
                Response = options.IncludeEvidence ? responseText : "[REDACTED]",
                Outcome = evalResult.Outcome,
                Reason = evalResult.Reason,
                Rationale = rationale,
                MatchedItems = evalResult.MatchedItems,
                Technique = probe.Technique,
                Difficulty = probe.Difficulty,
                Duration = probeSw.Elapsed,
                Severity = attackSeverity,
                Fidelity = fidelity,
                Surface = probe.Surface
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // Re-throw if main cancellation was requested
        }
        catch (OperationCanceledException)
        {
            probeSw.Stop();
            // LOW: report the budget that ACTUALLY applied. A multi-turn/tree probe runs under a conversation /
            // tree-search budget (MaxConversationDuration, else MaxTurns×TimeoutPerTurn / MaxNodes×TimeoutPerProbe),
            // NOT the single-turn TimeoutPerProbe — reporting the latter for those paths was a wrong number.
            var (budget, label) = attack switch
            {
                ITreeAttack t => (options.MaxConversationDuration > TimeSpan.Zero
                    ? options.MaxConversationDuration
                    : TimeSpan.FromTicks(options.TimeoutPerProbe.Ticks * Math.Max(1, t.MaxNodes)), "tree-search budget"),
                IMultiTurnAttack m => (options.MaxConversationDuration > TimeSpan.Zero
                    ? options.MaxConversationDuration
                    : TimeSpan.FromTicks(options.TimeoutPerTurn.Ticks * Math.Max(1, m.MaxTurns)), "conversation budget"),
                _ => (options.TimeoutPerProbe, "per-probe timeout"),
            };
            return new ProbeResult
            {
                ProbeId = probe.Id,
                Prompt = options.IncludeEvidence ? probe.Prompt : "[REDACTED]",
                Response = "[TIMEOUT]",
                Outcome = EvaluationOutcome.Inconclusive,
                Reason = $"Probe exceeded its {label} ({budget.TotalSeconds:F1}s)",
                Technique = probe.Technique,
                Difficulty = probe.Difficulty,
                Duration = probeSw.Elapsed,
                Error = "Timeout",
                ErrorKind = ProbeErrorKind.Timeout,
                Severity = attackSeverity,
                Surface = probe.Surface
            };
        }
        catch (Exception ex) when (IsTransportError(ex))
        {
            // Expected transport/connectivity errors (network blip, socket reset, remote timeout).
            // Inconclusive, but still counted as an execution error at the scan level so a broken
            // transport is not silently reported as a clean Pass (GAP-07).
            probeSw.Stop();
            return BuildErrorResult(probe, options, attackSeverity, probeSw.Elapsed, "Transport error", ProbeErrorKind.Transport, ex);
        }
        catch (Exception ex)
        {
            // Unexpected fault (likely a bug in the agent/evaluator wiring). Surfaced as an
            // execution error; ex.Message is gated behind IncludeEvidence to avoid leaking internal
            // endpoint/stack detail (GAP-07).
            probeSw.Stop();
            return BuildErrorResult(probe, options, attackSeverity, probeSw.Elapsed, "Execution error", ProbeErrorKind.Execution, ex);
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
    /// Folds a multi-turn / tree search (one <see cref="MultiTurnResult"/>) into one <see cref="ProbeResult"/> —
    /// shared by the linear TurnOrchestrator (Wave C) and the TreeOrchestrator (Wave C′ TAP) paths so they can
    /// never drift.
    /// </summary>
    private static ProbeResult BuildFoldedProbeResult(AttackProbe probe, MultiTurnResult mt, Severity attackSeverity, TimeSpan elapsed, ScanOptions options)
    {
        var transcript = string.Join("\n", mt.Transcript.Select(t => $"{t.Role}: {t.Content}"));
        return new ProbeResult
        {
            ProbeId = probe.Id,
            Prompt = options.IncludeEvidence ? probe.Prompt : "[REDACTED]",
            Response = options.IncludeEvidence ? transcript : "[REDACTED]",
            Outcome = mt.Outcome,
            Reason = $"[{mt.ConversationFidelity}, {mt.TurnsUsed} turn(s)] {mt.Reason}"
                     + (mt.WasTruncated ? " [TRUNCATED]" : string.Empty),
            Technique = probe.Technique,
            Difficulty = probe.Difficulty,
            Duration = elapsed,
            Severity = attackSeverity,
            Fidelity = mt.Fidelity,
            Surface = probe.Surface,
            ConversationFidelity = mt.ConversationFidelity,
        };
    }

    /// <summary>
    /// RC-1: classify the evidence behind a verdict. Trusts only an explicit hint in
    /// <c>EvaluationResult.Metadata["fidelity"]</c> (stamped by tool-aware evaluators such as
    /// <c>ToolInvocationEvaluator</c>). All other verdicts are text-derived and default to
    /// <see cref="EvidenceFidelity.Verbal"/> — an incidental, unrelated tool call in the trace is NOT
    /// behavioral proof of the matched outcome, so it must not upgrade fidelity.
    /// </summary>
    private static EvidenceFidelity ResolveFidelity(EvaluationResult result)
    {
        if (result.Metadata is { } meta &&
            meta.TryGetValue("fidelity", out var raw) &&
            raw is EvidenceFidelity explicitFidelity)
        {
            return explicitFidelity;
        }

        return EvidenceFidelity.Verbal;
    }

    /// <summary>
    /// Builds an Inconclusive <see cref="ProbeResult"/> for a failed probe execution. The exception
    /// message is included in Response/Reason/Error only when <see cref="ScanOptions.IncludeEvidence"/>
    /// is set; otherwise a category-only message is used so internal detail is not leaked (GAP-07).
    /// </summary>
    private static ProbeResult BuildErrorResult(
        AttackProbe probe, ScanOptions options, Severity severity, TimeSpan elapsed,
        string category, ProbeErrorKind kind, Exception ex)
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
            ErrorKind = kind,
            Severity = severity,
            Surface = probe.Surface
        };
    }
}
