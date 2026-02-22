// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
// tests/AgentEval.Tests/RedTeam/RedTeamIntegrationTests.cs
using AgentEval.Core;
using AgentEval.RedTeam;
using Microsoft.Extensions.DependencyInjection;

namespace AgentEval.Tests.RedTeam;

public class RedTeamIntegrationTests
{
    [Fact]
    public void AddRedTeam_RegistersServices()
    {
        var services = new ServiceCollection();

        services.AddRedTeam();

        var provider = services.BuildServiceProvider();
        var runner = provider.GetService<IRedTeamRunner>();

        Assert.NotNull(runner);
        Assert.IsType<RedTeamRunner>(runner);
    }

    [Fact]
    public async Task QuickRedTeamScanAsync_RunsQuickScan()
    {
        var agent = new FakeResistantAgent();

        var result = await agent.QuickRedTeamScanAsync();

        Assert.NotNull(result);
        Assert.Equal(9, result.AttackResults.Count);
        Assert.True(result.Passed);
    }

    [Fact]
    public async Task ModerateRedTeamScanAsync_RunsModerateScan()
    {
        var agent = new FakeResistantAgent();

        var result = await agent.ModerateRedTeamScanAsync();

        Assert.NotNull(result);
        Assert.Equal(9, result.AttackResults.Count);
        Assert.True(result.TotalProbes > 0);
    }

    [Fact]
    public async Task RedTeamAsync_WithSpecificAttacks_RunsOnlyThoseAttacks()
    {
        var agent = new FakeResistantAgent();

        var result = await agent.RedTeamAsync(Attack.PromptInjection, Attack.Jailbreak);

        Assert.Equal(2, result.AttackResults.Count);
    }

    [Fact]
    public async Task RedTeamAsync_WithOptions_UsesOptions()
    {
        var agent = new FakeResistantAgent();

        var options = new ScanOptions
        {
            Intensity = Intensity.Quick,
            AttackTypes = [Attack.PromptInjection],
            MaxProbesPerAttack = 2
        };

        var result = await agent.RedTeamAsync(options);

        Assert.Single(result.AttackResults);
        Assert.True(result.TotalProbes <= 2);
    }

    [Fact]
    public async Task CanResistAsync_WithResistantAgent_ReturnsTrue()
    {
        var agent = new FakeResistantAgent();

        var canResist = await agent.CanResistAsync(Attack.PromptInjection);

        Assert.True(canResist);
    }

    [Fact]
    public async Task CanResistAsync_WithVulnerableAgent_ReturnsFalse()
    {
        var agent = new FakeVulnerableAgent();

        var canResist = await agent.CanResistAsync(Attack.PromptInjection);

        Assert.False(canResist);
    }

    // === Test Helper Classes ===

    private class FakeResistantAgent : IEvaluableAgent
    {
        public string Name => "FakeResistantAgent";

        public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken ct = default)
        {
            return Task.FromResult(new AgentResponse
            {
                Text = "I'm sorry, I cannot help with that request."
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
}
