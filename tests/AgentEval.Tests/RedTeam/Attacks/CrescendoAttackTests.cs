// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
using AgentEval.RedTeam;
using AgentEval.RedTeam.Attacks;
using AgentEval.RedTeam.Evaluators;
using AgentEval.Testing;

namespace AgentEval.Tests.RedTeam.Attacks;

public class CrescendoAttackTests
{
    private readonly CrescendoAttack _attack = new();

    [Fact]
    public void Identity_IsLLM01_Jailbreak_MultiTurn()
    {
        Assert.Equal("Crescendo", _attack.Name);
        Assert.Equal("LLM01", _attack.OwaspLlmId);
        Assert.Equal(Severity.High, _attack.DefaultSeverity);
        Assert.Equal(6, _attack.MaxTurns);
        Assert.IsType<SuccessOnlyConvergenceDetector>(_attack.ConvergenceDetector);   // persists through refusals
    }

    [Fact]
    public void GetEvaluator_IsRefusalGated()
        => Assert.IsType<RefusalGatedEvaluator>(_attack.GetEvaluator());

    [Theory]
    [InlineData(Intensity.Quick, 1)]
    [InlineData(Intensity.Moderate, 2)]
    [InlineData(Intensity.Comprehensive, 3)]
    public void GetProbes_ScalesByIntensity(Intensity intensity, int expected)
        => Assert.Equal(expected, _attack.GetProbes(intensity).Count);

    [Fact]
    public void EverySeed_HasAtLeastFiveRungs_AndUniqueCrIds()
    {
        var probes = _attack.GetProbes(Intensity.Comprehensive);
        Assert.Equal(probes.Count, probes.Select(p => p.Id).Distinct().Count());
        foreach (var p in probes)
        {
            Assert.StartsWith("CR-", p.Id);
            var rungs = (string[])p.Metadata![CrescendoAttack.RungsMetadataKey];
            Assert.True(rungs.Length >= 5, $"{p.Id} should have ≥5 rungs, had {rungs.Length}");
            Assert.Equal(rungs[0], p.Prompt);   // opener doubles as the single-turn fallback prompt
        }
    }

    [Fact]
    public async Task NextTurnAsync_ReturnsRungForIndex_ThenNullPastEnd()
    {
        var seed = _attack.GetProbes(Intensity.Quick).Single();
        var rungs = (string[])seed.Metadata![CrescendoAttack.RungsMetadataKey];

        for (var i = 0; i < rungs.Length; i++)
        {
            var next = await _attack.NextTurnAsync(new MultiTurnContext { Seed = seed, History = [], TurnIndex = i });
            Assert.Equal(rungs[i], next);
        }
        Assert.Null(await _attack.NextTurnAsync(new MultiTurnContext { Seed = seed, History = [], TurnIndex = rungs.Length }));
    }

    [Fact]
    public async Task NextTurnAsync_WithJudgeClient_ThrowsNotSupported_HonestDeferral()
    {
        var seed = _attack.GetProbes(Intensity.Quick).Single();
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            _attack.NextTurnAsync(new MultiTurnContext { Seed = seed, History = [], TurnIndex = 0, JudgeClient = new ScriptedChatClient() }));
    }

    [Fact]
    public async Task NextTurnAsync_SeedMissingOrWrongTypeRungs_ReturnsNull()
    {
        var noMeta = new AttackProbe { Id = "X", Prompt = "p", Difficulty = Difficulty.Hard };
        Assert.Null(await _attack.NextTurnAsync(new MultiTurnContext { Seed = noMeta, History = [], TurnIndex = 0 }));

        var wrongType = new AttackProbe
        {
            Id = "Y", Prompt = "p", Difficulty = Difficulty.Hard,
            Metadata = new Dictionary<string, object> { [CrescendoAttack.RungsMetadataKey] = "not-an-array" },
        };
        Assert.Null(await _attack.NextTurnAsync(new MultiTurnContext { Seed = wrongType, History = [], TurnIndex = 0 }));
    }

    [Fact]
    public void Registered_ByName_ButNotInDefaultRoster()
    {
        Assert.Same(Attack.Crescendo, Attack.ByName("Crescendo"));
        Assert.DoesNotContain(Attack.All, a => a.Name == "Crescendo");   // opt-in: multi-turn not in the default scan
    }
}
