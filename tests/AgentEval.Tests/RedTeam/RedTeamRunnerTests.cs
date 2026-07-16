// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
// tests/AgentEval.Tests/RedTeam/RedTeamRunnerTests.cs
using AgentEval.Core;
using AgentEval.RedTeam;
using AgentEval.RedTeam.Attacks;

namespace AgentEval.Tests.RedTeam;

public class RedTeamRunnerTests
{
    [Fact]
    public async Task ScanAsync_WithQuickIntensity_RunsProbes()
    {
        var agent = new FakeResistantAgent();
        var runner = new RedTeamRunner();

        var options = new ScanOptions
        {
            Intensity = Intensity.Quick,
            AttackTypes = [Attack.PromptInjection]
        };

        var result = await runner.ScanAsync(agent, options);

        Assert.True(result.TotalProbes > 0);
        Assert.True(result.ResistedProbes > 0);
        Assert.Equal(result.TotalProbes, result.ResistedProbes);
        Assert.Equal(Verdict.Pass, result.Verdict);
    }

    [Fact]
    public async Task ScanAsync_DuplicateAttackInstances_AreDeduped_NoCrash()
    {
        // 5e: the same attack instance twice (e.g. CLI `--attacks PromptInjection,PROMPT_INJECTION` resolving to
        // the same singleton) must dedup, not crash the per-attack ToDictionary with a duplicate-key error.
        var options = new ScanOptions
        {
            Intensity = Intensity.Quick,
            AttackTypes = [Attack.PromptInjection, Attack.PromptInjection]
        };

        var result = await new RedTeamRunner().ScanAsync(new FakeResistantAgent(), options);

        Assert.Single(result.AttackResults);
    }

    [Fact]
    public async Task ScanAsync_WithZeroProgressInterval_DoesNotThrow()
    {
        var options = new ScanOptions
        {
            Intensity = Intensity.Quick,
            AttackTypes = [Attack.EncodingEvasion],
            ProgressReportInterval = 0 // would have thrown DivideByZeroException
        };
        var result = await new RedTeamRunner().ScanAsync(new FakeResistantAgent(), options);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task ScanAsync_WithVulnerableAgent_DetectsCompromise()
    {
        var agent = new FakeVulnerableAgent();
        var runner = new RedTeamRunner();

        var options = new ScanOptions
        {
            Intensity = Intensity.Quick,
            AttackTypes = [Attack.PromptInjection]
        };

        var result = await runner.ScanAsync(agent, options);

        Assert.True(result.SucceededProbes > 0);
        Assert.True(result.AttackSuccessRate > 0);
        Assert.NotEqual(Verdict.Pass, result.Verdict);
    }

    [Fact]
    public async Task ScanAsync_WithAllAttacks_RunsAllAttackTypes()
    {
        var agent = new FakeResistantAgent();
        var runner = new RedTeamRunner();

        var options = new ScanOptions
        {
            Intensity = Intensity.Quick,
            AttackTypes = Attack.All.ToList()
        };

        var result = await runner.ScanAsync(agent, options);

        Assert.Equal(14, result.AttackResults.Count);
        Assert.Contains(result.AttackResults, a => a.AttackName == "PromptInjection");
        Assert.Contains(result.AttackResults, a => a.AttackName == "Jailbreak");
        Assert.Contains(result.AttackResults, a => a.AttackName == "PIILeakage");
        Assert.Contains(result.AttackResults, a => a.AttackName == "SystemPromptExtraction");
        Assert.Contains(result.AttackResults, a => a.AttackName == "IndirectInjection");
    }

    [Fact]
    public async Task ScanAsync_StructurallyUntestableInferenceProbes_RecordedInconclusive_NotScored()
    {
        // T5-4 / Status §8: the sampler/decode/stream/tool probes a text agent cannot exercise are recorded
        // Inconclusive with a clear reason — never a fabricated pass/fail — and are NOT execution errors.
        var options = new ScanOptions
        {
            Intensity = Intensity.Comprehensive,
            AttackTypes = [Attack.InferenceAPIAbuse]
        };

        var result = await new RedTeamRunner().ScanAsync(new FakeResistantAgent(), options);

        var attack = result.AttackResults.Single();
        var untestable = attack.ProbeResults
            .Where(p => p.Reason.Contains("structurally untestable", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.NotEmpty(untestable);
        Assert.All(untestable, p =>
        {
            Assert.Equal(EvaluationOutcome.Inconclusive, p.Outcome);
            Assert.Null(p.Error); // a deliberate "can't test here", not a transport/timeout failure
        });

        var untestableTechniques = untestable.Select(p => p.Technique).ToHashSet();
        Assert.Contains("hyperparameter_abuse", untestableTechniques);
        Assert.Contains("function_abuse", untestableTechniques);
        Assert.Contains("stream_manipulation", untestableTechniques);
    }

    [Fact]
    public async Task ScanAsync_ReportsProgress()
    {
        var agent = new FakeResistantAgent();
        var runner = new RedTeamRunner();
        var progressReports = new List<ScanProgress>();
        var progress = new Progress<ScanProgress>(p => progressReports.Add(p));

        var options = new ScanOptions
        {
            Intensity = Intensity.Quick,
            AttackTypes = [Attack.PromptInjection]
        };

        await runner.ScanAsync(agent, options, progress);

        // Progress<T> marshals its callbacks through the captured SynchronizationContext (here the
        // thread pool), so they can lag the awaited scan. A fixed sleep races that scheduling and
        // flaked on the loaded CI runner; poll until the first report lands (fast in the normal
        // case) with a generous ceiling instead.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (progressReports.Count == 0 && sw.Elapsed < TimeSpan.FromSeconds(30))
        {
            await Task.Delay(25);
        }

        Assert.NotEmpty(progressReports);
        Assert.All(progressReports, p => Assert.True(p.TotalProbes > 0));
    }

    [Fact]
    public async Task ScanAsync_RespectsCancellation()
    {
        var agent = new SlowFakeAgent(TimeSpan.FromSeconds(10));
        var runner = new RedTeamRunner();

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var options = new ScanOptions
        {
            Intensity = Intensity.Comprehensive,
            AttackTypes = Attack.All.ToList()
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runner.ScanAsync(agent, options, cts.Token));
    }

    [Fact]
    public async Task ScanAsync_FailFast_StopsOnFirstSuccess()
    {
        var agent = new FakeVulnerableAgent();
        var runner = new RedTeamRunner();

        var options = new ScanOptions
        {
            Intensity = Intensity.Comprehensive,
            AttackTypes = [Attack.PromptInjection],
            FailFast = true
        };

        var result = await runner.ScanAsync(agent, options);

        // Should stop after first successful attack
        Assert.Equal(1, result.SucceededProbes);
    }

    [Fact]
    public async Task ScanAsync_FailFast_MarksTruncated_AndCountsSkipped()
    {
        // RA3-06 / T5-2: a FailFast scan that stops early is honestly marked truncated, with the skipped
        // probe count tracked so PlannedProbes == executed + skipped and the summary flags non-comparability.
        var options = new ScanOptions
        {
            Intensity = Intensity.Comprehensive,
            AttackTypes = [Attack.PromptInjection],
            FailFast = true
        };

        var result = await new RedTeamRunner().ScanAsync(new FakeVulnerableAgent(), options);

        Assert.True(result.WasTruncated);
        Assert.True(result.SkippedProbes > 0);
        Assert.Equal(result.PlannedProbes, result.TotalProbes + result.SkippedProbes);
        Assert.Contains("TRUNCATED", result.Summary);
    }

    [Fact]
    public async Task ScanAsync_FullScan_IsNotMarkedTruncated()
    {
        var options = new ScanOptions { Intensity = Intensity.Quick, AttackTypes = [Attack.EncodingEvasion] };

        var result = await new RedTeamRunner().ScanAsync(new FakeResistantAgent(), options);

        Assert.False(result.WasTruncated);
        Assert.Equal(0, result.SkippedProbes);
        Assert.Equal(result.TotalProbes, result.PlannedProbes);
    }

    [Fact]
    public async Task ScanAsync_WithParallelism_ProducesSameResultsAsSequential()
    {
        // RA3-05 / T5-1: bounded-concurrency execution yields the same probe set, counts, and (reordered)
        // deterministic probe ordering as the sequential path.
        var agent = new FakeResistantAgent();
        var seqOptions = new ScanOptions { Intensity = Intensity.Comprehensive, AttackTypes = [Attack.EncodingEvasion], Parallelism = 1 };
        var parOptions = new ScanOptions { Intensity = Intensity.Comprehensive, AttackTypes = [Attack.EncodingEvasion], Parallelism = 4 };

        var seq = await new RedTeamRunner().ScanAsync(agent, seqOptions);
        var par = await new RedTeamRunner().ScanAsync(agent, parOptions);

        Assert.Equal(seq.TotalProbes, par.TotalProbes);
        Assert.Equal(seq.ResistedProbes, par.ResistedProbes);
        Assert.Equal(seq.SucceededProbes, par.SucceededProbes);
        Assert.Equal(seq.InconclusiveProbes, par.InconclusiveProbes);
        Assert.Equal(
            seq.AttackResults.Single().ProbeResults.Select(p => p.ProbeId),
            par.AttackResults.Single().ProbeResults.Select(p => p.ProbeId)); // deterministic ordering preserved
    }

    [Fact]
    public async Task ScanAsync_MaxProbesPerAttack_LimitsProbes()
    {
        var agent = new FakeResistantAgent();
        var runner = new RedTeamRunner();

        var options = new ScanOptions
        {
            Intensity = Intensity.Comprehensive,
            AttackTypes = [Attack.PromptInjection],
            MaxProbesPerAttack = 3
        };

        var result = await runner.ScanAsync(agent, options);

        Assert.True(result.TotalProbes <= 3);
    }

    [Fact]
    public async Task ScanAsync_RespectsDelay()
    {
        var agent = new FakeResistantAgent();
        var runner = new RedTeamRunner();

        var options = new ScanOptions
        {
            Intensity = Intensity.Quick,
            AttackTypes = [Attack.PromptInjection],
            DelayBetweenProbes = TimeSpan.FromMilliseconds(50)
        };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await runner.ScanAsync(agent, options);
        sw.Stop();

        // Should take at least (probes - 1) * delay milliseconds
        var minExpected = TimeSpan.FromMilliseconds((result.TotalProbes - 1) * 50);
        Assert.True(sw.Elapsed >= minExpected * 0.8); // Allow 20% variance
    }

    [Fact]
    public async Task ScanAsync_IncludeEvidence_ContainsPromptAndResponse()
    {
        var agent = new FakeResistantAgent();
        var runner = new RedTeamRunner();

        var options = new ScanOptions
        {
            Intensity = Intensity.Quick,
            AttackTypes = [Attack.PromptInjection],
            IncludeEvidence = true
        };

        var result = await runner.ScanAsync(agent, options);

        var probe = result.AttackResults.First().ProbeResults.First();
        Assert.NotEqual("[REDACTED]", probe.Prompt);
        Assert.NotEqual("[REDACTED]", probe.Response);
    }

    [Fact]
    public async Task ScanAsync_ExcludeEvidence_RedactsPromptAndResponse()
    {
        var agent = new FakeResistantAgent();
        var runner = new RedTeamRunner();

        var options = new ScanOptions
        {
            Intensity = Intensity.Quick,
            AttackTypes = [Attack.PromptInjection],
            IncludeEvidence = false
        };

        var result = await runner.ScanAsync(agent, options);

        var probe = result.AttackResults.First().ProbeResults.First();
        Assert.Equal("[REDACTED]", probe.Prompt);
        Assert.Equal("[REDACTED]", probe.Response);
    }

    [Fact]
    public async Task ScanAsync_HandlesAgentErrors_GracefullyAsInconclusive()
    {
        var agent = new FakeThrowingAgent(new InvalidOperationException("Agent error"));
        var runner = new RedTeamRunner();

        var options = new ScanOptions
        {
            Intensity = Intensity.Quick,
            AttackTypes = [Attack.PromptInjection]
        };

        var result = await runner.ScanAsync(agent, options);

        Assert.True(result.InconclusiveProbes > 0);
        Assert.Contains(result.AttackResults.First().ProbeResults, p => p.HasError);
    }

    [Fact]
    public async Task ScanAsync_Timeout_SetsProbeErrorKindTimeout()
    {
        var agent = new SlowFakeAgent(TimeSpan.FromSeconds(30));
        var runner = new RedTeamRunner();
        var options = new ScanOptions
        {
            Intensity = Intensity.Quick,
            AttackTypes = [Attack.PromptInjection],
            TimeoutPerProbe = TimeSpan.FromMilliseconds(50)
        };

        var result = await runner.ScanAsync(agent, options);
        var probeResult = result.AttackResults.SelectMany(a => a.ProbeResults).First();

        Assert.Equal(ProbeErrorKind.Timeout, probeResult.ErrorKind);
    }

    // ── GAP-07: error classification, evidence gating, scan-level error counter ──

    [Fact]
    public async Task ScanAsync_AllProbesError_VerdictInconclusiveNotSilentlyPassable()
    {
        var agent = new FakeThrowingAgent(new InvalidOperationException("boom"));
        var runner = new RedTeamRunner();
        var options = new ScanOptions { Intensity = Intensity.Quick, AttackTypes = [Attack.PromptInjection] };

        var result = await runner.ScanAsync(agent, options);

        Assert.True(result.HasExecutionErrors);
        Assert.Equal(result.TotalProbes, result.ErroredProbes);
        Assert.Equal(0, result.SucceededProbes);
        // Previously this returned a clean Pass despite the whole scan failing to run (GAP-07).
        Assert.Equal(Verdict.Inconclusive, result.Verdict);
        Assert.False(result.Passed);
    }

    [Fact]
    public async Task ScanAsync_ExcludeEvidence_DoesNotLeakExceptionMessage()
    {
        var agent = new FakeThrowingAgent(new InvalidOperationException("SECRET-ENDPOINT-12345"));
        var runner = new RedTeamRunner();
        var options = new ScanOptions
        {
            Intensity = Intensity.Quick,
            AttackTypes = [Attack.PromptInjection],
            IncludeEvidence = false
        };

        var result = await runner.ScanAsync(agent, options);

        var probes = result.AttackResults.SelectMany(a => a.ProbeResults).ToList();
        Assert.NotEmpty(probes);
        Assert.All(probes, p =>
        {
            Assert.DoesNotContain("SECRET-ENDPOINT-12345", p.Error ?? "");
            Assert.DoesNotContain("SECRET-ENDPOINT-12345", p.Response);
            Assert.DoesNotContain("SECRET-ENDPOINT-12345", p.Reason);
            Assert.Equal("Execution error", p.Error); // category only, no detail
        });
    }

    [Fact]
    public async Task ScanAsync_IncludeEvidence_SurfacesExceptionMessage()
    {
        var agent = new FakeThrowingAgent(new InvalidOperationException("DIAGNOSTIC-DETAIL-99"));
        var runner = new RedTeamRunner();
        var options = new ScanOptions
        {
            Intensity = Intensity.Quick,
            AttackTypes = [Attack.PromptInjection],
            IncludeEvidence = true
        };

        var result = await runner.ScanAsync(agent, options);

        var probes = result.AttackResults.SelectMany(a => a.ProbeResults).ToList();
        Assert.Contains(probes, p => (p.Error ?? "").Contains("DIAGNOSTIC-DETAIL-99"));
    }

    [Fact]
    public async Task ScanAsync_TransportError_ClassifiedAsTransportButStillCounted()
    {
        var agent = new FakeThrowingAgent(new System.Net.Http.HttpRequestException("connection refused"));
        var runner = new RedTeamRunner();
        var options = new ScanOptions { Intensity = Intensity.Quick, AttackTypes = [Attack.PromptInjection] };

        var result = await runner.ScanAsync(agent, options);

        var probes = result.AttackResults.SelectMany(a => a.ProbeResults).ToList();
        Assert.All(probes, p => Assert.Equal(EvaluationOutcome.Inconclusive, p.Outcome));
        Assert.All(probes, p => Assert.Equal(ProbeErrorKind.Transport, p.ErrorKind));
        Assert.All(probes, p => Assert.Contains("Transport error", p.Reason));
        // Transport failures still count toward the scan-level error counter.
        Assert.Equal(result.TotalProbes, result.ErroredProbes);
    }

    [Fact]
    public async Task ScanAsync_DefaultOptions_UsesAllAttacks()
    {
        var agent = new FakeResistantAgent();
        var runner = new RedTeamRunner();

        var options = new ScanOptions(); // No AttackTypes specified

        var result = await runner.ScanAsync(agent, options);

        Assert.Equal(14, result.AttackResults.Count);
    }

    [Fact]
    public async Task ScanAsync_WithOnProgressCallback_InvokesCallback()
    {
        var agent = new FakeResistantAgent();
        var runner = new RedTeamRunner();
        var progressReports = new List<ScanProgress>();

        var options = new ScanOptions
        {
            Intensity = Intensity.Quick,
            AttackTypes = [Attack.PromptInjection],
            OnProgress = p => progressReports.Add(p)
        };

        await runner.ScanAsync(agent, options);

        Assert.NotEmpty(progressReports);
        Assert.All(progressReports, p => Assert.True(p.TotalProbes > 0));
    }

    [Fact]
    public async Task ScanAsync_ProgressReportsResistedAndSucceededCounts()
    {
        var agent = new FakeResistantAgent();
        var runner = new RedTeamRunner();
        ScanProgress? lastProgress = null;

        var options = new ScanOptions
        {
            Intensity = Intensity.Quick,
            AttackTypes = [Attack.PromptInjection],
            OnProgress = p => lastProgress = p
        };

        await runner.ScanAsync(agent, options);

        Assert.NotNull(lastProgress);
        Assert.True(lastProgress.Value.ResistedCount > 0);
        Assert.Equal(0, lastProgress.Value.SucceededCount);
    }

    [Fact]
    public async Task ScanAsync_WithProgressInterval_ReportsAtInterval()
    {
        var agent = new FakeResistantAgent();
        var runner = new RedTeamRunner();
        var progressReports = new List<ScanProgress>();

        var options = new ScanOptions
        {
            Intensity = Intensity.Quick,
            AttackTypes = [Attack.PromptInjection],
            ProgressReportInterval = 2, // Report every 2nd probe
            OnProgress = p => progressReports.Add(p)
        };

        var result = await runner.ScanAsync(agent, options);

        // Should have fewer reports due to interval
        Assert.True(progressReports.Count < result.TotalProbes);
    }

    [Fact]
    public void ScanProgress_CurrentSuccessRate_CalculatesCorrectly()
    {
        var progress = new ScanProgress(
            CompletedProbes: 10,
            TotalProbes: 20,
            CurrentAttack: "Test",
            CurrentProbe: "P1",
            Elapsed: TimeSpan.FromSeconds(5),
            ResistedCount: 8,
            SucceededCount: 2);

        Assert.Equal(0.8, progress.CurrentSuccessRate);
    }

    [Fact]
    public void ScanProgress_StatusEmoji_ReturnsCorrectEmoji()
    {
        var resisted = new ScanProgress(0, 10, "Test", "P1", TimeSpan.Zero, LastOutcome: EvaluationOutcome.Resisted);
        var succeeded = new ScanProgress(0, 10, "Test", "P1", TimeSpan.Zero, LastOutcome: EvaluationOutcome.Succeeded);
        var inconclusive = new ScanProgress(0, 10, "Test", "P1", TimeSpan.Zero, LastOutcome: EvaluationOutcome.Inconclusive);
        var none = new ScanProgress(0, 10, "Test", "P1", TimeSpan.Zero, LastOutcome: null);

        Assert.Equal("✅", resisted.StatusEmoji);
        Assert.Equal("❌", succeeded.StatusEmoji);
        Assert.Equal("⚪", inconclusive.StatusEmoji);
        Assert.Equal("⚪", none.StatusEmoji);
    }

    [Fact]
    public async Task ScanAsync_GeneratesProbesOncePerAttack()
    {
        // PERF-10: the runner materializes each attack's probe list once and reuses it for both the
        // progress total and execution. GetProbes was previously called twice (count + execute).
        var attack = new CountingAttack();
        var runner = new RedTeamRunner();
        var options = new ScanOptions { AttackTypes = [attack], Intensity = Intensity.Quick };

        await runner.ScanAsync(new FakeResistantAgent(), options);

        Assert.Equal(1, attack.GetProbesCallCount);
    }

    // === Test Helper Classes ===

    /// <summary>Attack that counts GetProbes invocations — for PERF-10.</summary>
    private sealed class CountingAttack : IAttackType
    {
        private int _getProbesCallCount;
        public int GetProbesCallCount => _getProbesCallCount;

        public string Name => "CountingAttack";
        public string DisplayName => "Counting Attack";
        public string Description => "counts GetProbes calls";
        public string OwaspLlmId => "LLM01";
        public string[] MitreAtlasIds => [];
        public Severity DefaultSeverity => Severity.Medium;

        public IReadOnlyList<AttackProbe> GetProbes(Intensity intensity)
        {
            Interlocked.Increment(ref _getProbesCallCount);
            return new[]
            {
                new AttackProbe { Id = "P1", Prompt = "p1", Difficulty = Difficulty.Easy },
                new AttackProbe { Id = "P2", Prompt = "p2", Difficulty = Difficulty.Easy },
            };
        }

        public IProbeEvaluator GetEvaluator() => new AlwaysResistedEvaluator();

        private sealed class AlwaysResistedEvaluator : IProbeEvaluator
        {
            public string Name => "AlwaysResisted";
            public Task<AgentEval.RedTeam.EvaluationResult> EvaluateAsync(AttackProbe probe, string response, CancellationToken cancellationToken = default)
                => Task.FromResult(AgentEval.RedTeam.EvaluationResult.Resisted("resisted"));
        }
    }

    private class FakeResistantAgent : IEvaluableAgent
    {
        public string Name => "FakeResistantAgent";

        public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken ct = default)
        {
            return Task.FromResult(new AgentResponse
            {
                Text = "I cannot help with that request. It would be unsafe or unethical."
            });
        }
    }

    private class FakeVulnerableAgent : IEvaluableAgent
    {
        public string Name => "FakeVulnerableAgent";

        public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken ct = default)
        {
            // Echo back certain markers if found in prompt
            if (prompt.Contains("PWNED", StringComparison.OrdinalIgnoreCase) ||
                prompt.Contains("say PWNED", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new AgentResponse { Text = "PWNED as requested!" });
            }

            return Task.FromResult(new AgentResponse { Text = "I'm happy to help!" });
        }
    }

    private class SlowFakeAgent : IEvaluableAgent
    {
        private readonly TimeSpan _delay;

        public SlowFakeAgent(TimeSpan delay) => _delay = delay;

        public string Name => "SlowFakeAgent";

        public async Task<AgentResponse> InvokeAsync(string prompt, CancellationToken ct = default)
        {
            await Task.Delay(_delay, ct);
            return new AgentResponse { Text = "Slow response" };
        }
    }

    private class FakeThrowingAgent : IEvaluableAgent
    {
        private readonly Exception _exception;

        public FakeThrowingAgent(Exception exception) => _exception = exception;

        public string Name => "FakeThrowingAgent";

        public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken ct = default)
        {
            throw _exception;
        }
    }
}
