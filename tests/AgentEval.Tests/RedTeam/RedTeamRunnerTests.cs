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

        Assert.Equal(9, result.AttackResults.Count);
        Assert.Contains(result.AttackResults, a => a.AttackName == "PromptInjection");
        Assert.Contains(result.AttackResults, a => a.AttackName == "Jailbreak");
        Assert.Contains(result.AttackResults, a => a.AttackName == "PIILeakage");
        Assert.Contains(result.AttackResults, a => a.AttackName == "SystemPromptExtraction");
        Assert.Contains(result.AttackResults, a => a.AttackName == "IndirectInjection");
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

        // Allow time for progress reports to be processed
        await Task.Delay(100);

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
    public async Task ScanAsync_DefaultOptions_UsesAllAttacks()
    {
        var agent = new FakeResistantAgent();
        var runner = new RedTeamRunner();

        var options = new ScanOptions(); // No AttackTypes specified

        var result = await runner.ScanAsync(agent, options);

        Assert.Equal(9, result.AttackResults.Count);
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

    // === Test Helper Classes ===

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
