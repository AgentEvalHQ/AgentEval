// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Guardrails;
using AgentEval.Testing;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentEval.Tests.Guardrails;

/// <summary>Phase 7.4 promotion coverage for honest, bounded cross-gate near-miss correlation.</summary>
public sealed class FleetCorrelatorHardeningTests
{
    [Fact]
    public void BlockAndCleanAllow_AreNotMisreportedAsNearMisses()
    {
        var correlator = new FleetCorrelator();
        correlator.AdvanceTurn();
        correlator.Observe(
            GateVerdict.Block("judge:block", "acted") with
            {
                Confidence = 0.9,
                Provenance = Provenance(
                    "judge:block",
                    threshold: 1.0,
                    actual: 0.9),
            },
            "pre");
        correlator.Observe(
            GateVerdict.Allow("judge:clean") with
            {
                Confidence = 0.9,
            },
            "pre");
        correlator.Observe(
            NearMiss("judge:near-miss", 0.6),
            "pre");

        Assert.Null(correlator.CheckCorrelation());
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    public void MalformedConfidence_FailsBeforeEnteringState(
        double confidence)
    {
        var correlator = new FleetCorrelator();
        correlator.AdvanceTurn();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => correlator.Observe(
                GateVerdict.Allow("judge:a") with
                {
                    Confidence = confidence,
                    Provenance = Provenance(
                        "judge:a",
                        threshold: 0.8,
                        actual: 0.5),
                },
                "pre"));
        Assert.Null(correlator.CheckCorrelation());
    }

    [Theory]
    [InlineData(0.5, 0.8, 0.6)]
    [InlineData(0.8, 0.8, 0.8)]
    [InlineData(0.9, 0.8, 0.9)]
    [InlineData(0.5, double.NaN, 0.5)]
    [InlineData(0.5, 0.8, double.PositiveInfinity)]
    public void InvalidThresholdActualRelationship_Fails(
        double confidence,
        double threshold,
        double actual)
    {
        var correlator = new FleetCorrelator();
        correlator.AdvanceTurn();

        Assert.Throws<ArgumentException>(
            () => correlator.Observe(
                GateVerdict.Allow("judge:a") with
                {
                    Confidence = confidence,
                    Provenance = Provenance(
                        "judge:a",
                        threshold,
                        actual),
                },
                "pre"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(" family")]
    [InlineData("family ")]
    [InlineData("family\nforged")]
    [InlineData("\u202Efamily")]
    public void NonCanonicalFamily_Fails(string family)
    {
        var correlator = new FleetCorrelator(
            new FleetCorrelatorOptions
            {
                FamilyOf = _ => family,
            });
        correlator.AdvanceTurn();

        Assert.Throws<ArgumentException>(
            () => correlator.Observe(
                NearMiss("judge:a", 0.5),
                "pre"));
    }

    [Fact]
    public void NullOrOversizedFamily_Fails()
    {
        var nullFamily = new FleetCorrelator(
            new FleetCorrelatorOptions
            {
                FamilyOf = _ => null!,
            });
        var oversizedFamily = new FleetCorrelator(
            new FleetCorrelatorOptions
            {
                FamilyOf = _ => new string('a', 129),
            });
        nullFamily.AdvanceTurn();
        oversizedFamily.AdvanceTurn();

        Assert.Throws<ArgumentException>(
            () => nullFamily.Observe(
                NearMiss("judge:a", 0.5),
                "pre"));
        Assert.Throws<ArgumentException>(
            () => oversizedFamily.Observe(
                NearMiss("judge:a", 0.5),
                "pre"));
    }

    [Fact]
    public void RepeatedFamily_CannotSkewCombinedConfidence()
    {
        var correlator = new FleetCorrelator();
        correlator.AdvanceTurn();
        correlator.Observe(NearMiss("family-a", 0.9), "pre");
        correlator.AdvanceTurn();
        correlator.Observe(NearMiss("family-a", 0.8), "pre");
        correlator.Observe(NearMiss("family-b", 0.5), "pre");

        var result = correlator.CheckCorrelation();

        Assert.NotNull(result);
        Assert.Equal(0.7, result!.Confidence!.Value, precision: 10);
        Assert.Equal(["family-a", "family-b"], result.Matches);
    }

    [Fact]
    public void Correlation_ContributingProvenanceIsOnePerFamilyAndDeterministic()
    {
        var correlator = new FleetCorrelator();
        correlator.AdvanceTurn();
        correlator.Observe(
            NearMiss("family-b", 0.6, "evidence-b"),
            "post");
        correlator.Observe(
            NearMiss("family-a", 0.5, "evidence-a"),
            "pre");

        var result = correlator.CheckCorrelation();

        Assert.NotNull(result);
        Assert.NotNull(result!.Provenance);
        Assert.Equal("fleet-correlation", result.Provenance!.RuleName);
        Assert.Empty(result.Provenance.Evidence);
        Assert.Collection(
            result.Provenance.Contributing!,
            first =>
            {
                Assert.Equal("family-a", first.RuleName);
                Assert.Empty(first.Evidence);
            },
            second =>
            {
                Assert.Equal("family-b", second.RuleName);
                Assert.Empty(second.Evidence);
            });
    }

    [Fact]
    public void Capacity_IsBoundedButSameFamilySameTurnCanStrengthen()
    {
        var correlator = new FleetCorrelator(
            new FleetCorrelatorOptions
            {
                MaxObservations = 2,
            });
        correlator.AdvanceTurn();
        correlator.Observe(NearMiss("family-a", 0.4), "pre");
        correlator.Observe(NearMiss("family-b", 0.4), "pre");
        correlator.Observe(NearMiss("family-a", 0.8), "post");

        var result = correlator.CheckCorrelation();

        Assert.Equal(0.6, result!.Confidence!.Value, precision: 10);
        Assert.Throws<InvalidOperationException>(
            () => correlator.Observe(
                NearMiss("family-c", 0.5),
                "pre"));
    }

    [Fact]
    public void ExpiredObservations_FreeCapacity()
    {
        var correlator = new FleetCorrelator(
            new FleetCorrelatorOptions
            {
                MaxObservations = 2,
                WindowTurns = 1,
            });
        correlator.AdvanceTurn();
        correlator.Observe(NearMiss("family-a", 0.5), "pre");
        correlator.Observe(NearMiss("family-b", 0.5), "pre");
        correlator.AdvanceTurn();

        correlator.Observe(NearMiss("family-c", 0.5), "pre");

        Assert.Null(correlator.CheckCorrelation());
    }

    [Fact]
    public void InvalidCapacityOrStage_Fails()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new FleetCorrelator(
                new FleetCorrelatorOptions
                {
                    MaxObservations = 1,
                }));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new FleetCorrelator(
                new FleetCorrelatorOptions
                {
                    MaxObservations = 65_537,
                }));

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new FleetCorrelator(
                new FleetCorrelatorOptions
                {
                    MinDistinctFamilies = 65,
                }));

        var correlator = new FleetCorrelator();
        Assert.Throws<ArgumentException>(
            () => correlator.Observe(
                NearMiss("family-a", 0.5),
                "tool"));
    }

    [Fact]
    public void ExplicitTurnToken_DropsLateExpiredObservationWithoutReattribution()
    {
        var correlator = new FleetCorrelator(
            new FleetCorrelatorOptions
            {
                WindowTurns = 1,
            });
        var firstTurn = correlator.BeginTurn();
        var secondTurn = correlator.BeginTurn();
        correlator.Observe(
            NearMiss("family-b", 0.5),
            "pre",
            secondTurn);

        correlator.Observe(
            NearMiss("family-a", 0.5),
            "pre",
            firstTurn);

        Assert.Null(correlator.CheckCorrelation());
    }

    [Fact]
    public void ExplicitTurnToken_RejectsNonPositiveOrFutureTurn()
    {
        var correlator = new FleetCorrelator();
        var currentTurn = correlator.BeginTurn();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => correlator.Observe(
                NearMiss("family-a", 0.5),
                "pre",
                turn: 0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => correlator.Observe(
                NearMiss("family-a", 0.5),
                "pre",
                currentTurn + 1));
    }

    [Fact]
    public async Task ConcurrentClientRoundTrips_PreserveTheirOwnTurnTokens()
    {
        var gate = new InterleavingNearMissGate();
        using var inner = new ScriptedChatClient()
            .AddText("response-1")
            .AddText("response-2");
        var client = new EvalGatingChatClient(
            inner,
            pre: [gate],
            post: null,
            EvalGatePolicy.ThrowOnFail,
            correlator: new FleetCorrelator(
                new FleetCorrelatorOptions
                {
                    WindowTurns = 1,
                }));

        var first = client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "first")]);
        await gate.FirstStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(5));
        var second = client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "second")]);
        try
        {
            await second;
        }
        finally
        {
            gate.ReleaseFirst.TrySetResult();
        }

        await first;
    }

    [Fact]
    public void CorrelationOutput_BoundsRepresentedFamiliesAndStatesTruncation()
    {
        var correlator = new FleetCorrelator();
        correlator.AdvanceTurn();
        for (var index = 0; index < 65; index++)
        {
            correlator.Observe(
                NearMiss($"family-{index:D2}", 0.5),
                "pre");
        }

        var result = correlator.CheckCorrelation();

        Assert.NotNull(result);
        Assert.Equal(64, result!.Matches!.Count);
        Assert.Equal(64, result.Provenance!.Contributing!.Count);
        Assert.Contains(
            "65 qualifying gate families",
            result.Reason,
            StringComparison.Ordinal);
        Assert.Contains(
            "first 64 in canonical order",
            result.Reason,
            StringComparison.Ordinal);
    }

    private sealed class InterleavingNearMissGate : IChatGate
    {
        public TaskCompletionSource FirstStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseFirst { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string PolicyName => "interleaving-near-miss";

        public async ValueTask<GateVerdict> InspectAsync(
            string text,
            CancellationToken cancellationToken = default)
        {
            if (string.Equals(text, "first", StringComparison.Ordinal))
            {
                FirstStarted.TrySetResult();
                await ReleaseFirst.Task.WaitAsync(cancellationToken);
                return NearMiss("family-a", 0.5);
            }

            return NearMiss("family-b", 0.5);
        }
    }

    private static GateVerdict NearMiss(
        string policy,
        double confidence,
        params string[] evidence)
        => GateVerdict.Allow(policy) with
        {
            Confidence = confidence,
            Provenance = new GateProvenance(
                policy,
                evidence,
                Threshold: 1.0,
                ActualValue: confidence),
        };

    private static GateProvenance Provenance(
        string rule,
        double threshold,
        double actual)
        => new(
            rule,
            Array.Empty<string>(),
            Threshold: threshold,
            ActualValue: actual);
}
