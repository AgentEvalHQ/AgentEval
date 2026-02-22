// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
// tests/AgentEval.Tests/RedTeam/AttackPipelineTests.cs
using AgentEval.Core;
using AgentEval.RedTeam;
using AgentEval.RedTeam.Attacks;

namespace AgentEval.Tests.RedTeam;

public class AttackPipelineTests
{
    [Fact]
    public async Task Create_WithSingleAttack_RunsAttack()
    {
        var agent = new FakeResistantAgent();

        var result = await AttackPipeline
            .Create()
            .WithAttack(Attack.PromptInjection)
            .WithIntensity(Intensity.Quick)
            .ScanAsync(agent);

        Assert.Single(result.AttackResults);
        Assert.Equal("PromptInjection", result.AttackResults[0].AttackName);
    }

    [Fact]
    public async Task Create_WithAllAttacks_IncludesAllAttackTypes()
    {
        var agent = new FakeResistantAgent();

        var result = await AttackPipeline
            .Create()
            .WithAllAttacks()
            .WithIntensity(Intensity.Quick)
            .ScanAsync(agent);

        Assert.Equal(9, result.AttackResults.Count);
    }

    [Fact]
    public async Task Create_WithMvpAttacks_IncludesOnlyMvpAttacks()
    {
        var agent = new FakeResistantAgent();

        var result = await AttackPipeline
            .Create()
            .WithMvpAttacks()
            .WithIntensity(Intensity.Quick)
            .ScanAsync(agent);

        Assert.Equal(3, result.AttackResults.Count);
        Assert.Contains(result.AttackResults, a => a.AttackName == "PromptInjection");
        Assert.Contains(result.AttackResults, a => a.AttackName == "Jailbreak");
        Assert.Contains(result.AttackResults, a => a.AttackName == "PIILeakage");
    }

    [Fact]
    public async Task Create_WithNoAttacks_ThrowsOnScan()
    {
        var agent = new FakeResistantAgent();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await AttackPipeline
                .Create()
                .ScanAsync(agent));
    }

    [Fact]
    public async Task Create_WithMultipleAttacks_RunsAll()
    {
        var agent = new FakeResistantAgent();

        var result = await AttackPipeline
            .Create()
            .WithAttack(Attack.PromptInjection)
            .WithAttack(Attack.Jailbreak)
            .WithIntensity(Intensity.Quick)
            .ScanAsync(agent);

        Assert.Equal(2, result.AttackResults.Count);
    }

    [Fact]
    public void TotalProbeCount_ReturnsExpectedCount()
    {
        var pipeline = AttackPipeline
            .Create()
            .WithAttack(Attack.PromptInjection)
            .WithIntensity(Intensity.Quick);

        var count = pipeline.TotalProbeCount;

        Assert.True(count > 0);
    }

    [Fact]
    public void TotalProbeCount_WithMaxProbes_RespectsLimit()
    {
        var pipeline = AttackPipeline
            .Create()
            .WithAllAttacks()
            .WithIntensity(Intensity.Comprehensive)
            .WithMaxProbesPerAttack(2);

        var count = pipeline.TotalProbeCount;

        Assert.True(count <= 18); // 9 attacks × 2 probes max
    }

    [Fact]
    public void GetProbePreview_ReturnsProbes()
    {
        var pipeline = AttackPipeline
            .Create()
            .WithAttack(Attack.PromptInjection)
            .WithIntensity(Intensity.Quick);

        var probes = pipeline.GetProbePreview();

        Assert.NotEmpty(probes);
        Assert.All(probes, p => Assert.NotNull(p.Id));
        Assert.All(probes, p => Assert.NotNull(p.Prompt));
    }

    [Fact]
    public async Task Create_WithTimeout_RespectsTimeout()
    {
        var agent = new SlowFakeAgent(TimeSpan.FromSeconds(30));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await AttackPipeline
                .Create()
                .WithAllAttacks()
                .WithIntensity(Intensity.Comprehensive)
                .WithTimeout(TimeSpan.FromMilliseconds(100))
                .ScanAsync(agent));
    }

    [Fact]
    public async Task Create_WithProgress_ReportsProgress()
    {
        var agent = new FakeResistantAgent();
        var progressReports = new List<ScanProgress>();
        var progress = new Progress<ScanProgress>(p => progressReports.Add(p));

        var result = await AttackPipeline
            .Create()
            .WithAttack(Attack.PromptInjection)
            .WithIntensity(Intensity.Quick)
            .WithProgress(progress)
            .ScanAsync(agent);

        // Allow time for progress reports
        await Task.Delay(100);

        Assert.NotEmpty(progressReports);
    }

    [Fact]
    public async Task Create_WithFailFast_StopsEarly()
    {
        var agent = new FakeVulnerableAgent();

        var result = await AttackPipeline
            .Create()
            .WithAttack(Attack.PromptInjection)
            .WithIntensity(Intensity.Comprehensive)
            .WithFailFast()
            .ScanAsync(agent);

        Assert.Equal(1, result.SucceededProbes);
    }

    [Fact]
    public async Task Create_WithEvidence_IncludesPromptAndResponse()
    {
        var agent = new FakeResistantAgent();

        var result = await AttackPipeline
            .Create()
            .WithAttack(Attack.PromptInjection)
            .WithIntensity(Intensity.Quick)
            .WithEvidence(true)
            .ScanAsync(agent);

        var probe = result.AttackResults.First().ProbeResults.First();
        Assert.NotEqual("[REDACTED]", probe.Prompt);
    }

    [Fact]
    public async Task Create_WithoutEvidence_RedactsData()
    {
        var agent = new FakeResistantAgent();

        var result = await AttackPipeline
            .Create()
            .WithAttack(Attack.PromptInjection)
            .WithIntensity(Intensity.Quick)
            .WithEvidence(false)
            .ScanAsync(agent);

        var probe = result.AttackResults.First().ProbeResults.First();
        Assert.Equal("[REDACTED]", probe.Prompt);
    }

    [Fact]
    public async Task Create_WithGenericAttack_IncludesAttack()
    {
        var agent = new FakeResistantAgent();

        var result = await AttackPipeline
            .Create()
            .WithAttack<PromptInjectionAttack>()
            .WithIntensity(Intensity.Quick)
            .ScanAsync(agent);

        Assert.Single(result.AttackResults);
        Assert.Equal("PromptInjection", result.AttackResults[0].AttackName);
    }

    // === Test Helper Classes ===

    private class FakeResistantAgent : IEvaluableAgent
    {
        public string Name => "FakeResistantAgent";

        public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken ct = default)
        {
            return Task.FromResult(new AgentResponse
            {
                Text = "I cannot help with that request."
            });
        }
    }

    private class FakeVulnerableAgent : IEvaluableAgent
    {
        public string Name => "FakeVulnerableAgent";

        public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken ct = default)
        {
            if (prompt.Contains("PWNED", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new AgentResponse { Text = "PWNED" });
            }
            return Task.FromResult(new AgentResponse { Text = "Safe response" });
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
}
