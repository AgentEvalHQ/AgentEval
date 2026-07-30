// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentEval.MAF.Gatekeeper;
using AgentEval.Testing;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentEval.Tests.MAF.Gatekeeper;

/// <summary>Phase 3, Task 3.5 — bounded, secret-free, precise repeated-denial correlation.</summary>
public sealed class PreciseDenialCorrelationTests
{
    [Fact]
    public async Task CanonicalNestedObjectsAndNumbersShareHashAndIncrementAttempts()
    {
        const string secret = "do-not-persist-this-value";
        var first = Arguments(new Dictionary<string, object?>
        {
            ["z"] = new object?[] { 1, 2 },
            ["a"] = new Dictionary<string, object?>
            {
                ["text"] = secret,
                ["number"] = 1.0m,
            },
        });
        var second = Arguments(new Dictionary<string, object?>
        {
            ["a"] = new Dictionary<string, object?>
            {
                ["number"] = 1,
                ["text"] = secret,
            },
            ["z"] = new object?[] { 1, 2 },
        });

        var evidence = await RunAsync([first, second], Session("tenant-secret", "session-secret"));

        Assert.Equal(2, evidence.Count);
        Assert.Equal(Hash(evidence[0]), Hash(evidence[1]));
        Assert.Equal([1, 2], evidence.Select(Attempts));
        Assert.All(evidence, item =>
        {
            Assert.True(Canonical(item));
            Assert.Equal("stable_session", item.Extra!["denialIdentitySource"]);
            var serialized = JsonSerializer.Serialize(item.ToMetadata());
            Assert.DoesNotContain(secret, serialized, StringComparison.Ordinal);
            Assert.DoesNotContain("tenant-secret", serialized, StringComparison.Ordinal);
            Assert.DoesNotContain("session-secret", serialized, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task ArrayOrderAndValueDifferencesDoNotCorrelate()
    {
        var evidence = await RunAsync(
            [
                Arguments(new object?[] { "a", "b" }),
                Arguments(new object?[] { "b", "a" }),
                Arguments(new object?[] { "a", "different" }),
            ],
            Session());

        Assert.Equal(3, evidence.Count);
        Assert.Equal(3, evidence.Select(Hash).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal([1, 1, 1], evidence.Select(Attempts));
    }

    [Fact]
    public async Task StableIdentityPolicyAndConfigurationChangeCorrelationDimension()
    {
        var args = new[] { Arguments("same") };
        var baseline = Assert.Single(await RunAsync(args, Session("tenant-a", "session-a")));
        var repeat = Assert.Single(await RunAsync(args, Session("tenant-a", "session-a")));
        var otherTenant = Assert.Single(await RunAsync(args, Session("tenant-b", "session-a")));
        var otherSession = Assert.Single(await RunAsync(args, Session("tenant-a", "session-b")));
        var otherConfiguration = Assert.Single(
            await RunAsync(args, Session("tenant-a", "session-a"), addConfigurationGate: true));
        var otherPolicy = Assert.Single(
            await RunAsync(args, Session("tenant-a", "session-a"), blocker: new NamedBlockGate("OtherPolicy")));

        Assert.Equal(Hash(baseline), Hash(repeat));
        Assert.NotEqual(Hash(baseline), Hash(otherTenant));
        Assert.NotEqual(Hash(baseline), Hash(otherSession));
        Assert.NotEqual(Hash(baseline), Hash(otherConfiguration));
        Assert.NotEqual(Hash(baseline), Hash(otherPolicy));
    }

    [Fact]
    public async Task DuplicateJsonPropertiesUseInconclusiveSecretFreeCorrelationAndStillBlock()
    {
        using var document = JsonDocument.Parse("""{"secret":"first","secret":"second"}""");
        var evidence = Assert.Single(
            await RunAsync([Arguments(document.RootElement.Clone())], Session()));

        Assert.False(Canonical(evidence));
        Assert.Equal(1, Attempts(evidence));
        Assert.Matches(HashPattern(), Hash(evidence));
        Assert.DoesNotContain("first", JsonSerializer.Serialize(evidence.ToMetadata()), StringComparison.Ordinal);
        Assert.DoesNotContain("second", JsonSerializer.Serialize(evidence.ToMetadata()), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DirectCompositionFallsBackToOpaquePerRunIdentity()
    {
        var first = Assert.Single(await RunAsync([Arguments("same")], stableTarget: null));
        var second = Assert.Single(await RunAsync([Arguments("same")], stableTarget: null));

        Assert.Equal("run", first.Extra!["denialIdentitySource"]);
        Assert.Equal("run", second.Extra!["denialIdentitySource"]);
        Assert.NotEqual(Hash(first), Hash(second));
        Assert.Equal(1, Attempts(first));
        Assert.Equal(1, Attempts(second));
    }

    [Fact]
    public async Task ThrowingStableIdentityResolverFallsBackToOpaqueRunIdentity()
    {
        var script = new ScriptedChatClient()
            .AddToolCall("call-0", "dangerous", Arguments("same"))
            .AddText("done");
        var tool = AIFunctionFactory.Create((string payload) => payload, "dangerous");
        var agent = new ChatClientAgent(
            script,
            new ChatClientAgentOptions
            {
                Name = "agent",
                ChatOptions = new ChatOptions { Tools = [tool] },
            });
        var sink = new CapturingSink();
        var gated = agent.AsBuilder()
            .UseAgentEvalGate(evidenceSink: sink)
            .UseAgentEvalToolGate(
                [new ForbiddenToolGate("dangerous")],
                ToolGatePolicy.ReplaceResult,
                evidenceSink: sink,
                denialCorrelationTargets: _ => throw new InvalidOperationException("secret resolver failure"))
            .Build();
        var session = await gated.CreateSessionAsync();

        await gated.RunAsync("go", session);

        var evidence = Assert.Single(sink.Records, item => item.Stage == "tool");
        Assert.Equal("run", evidence.Extra!["denialIdentitySource"]);
        Assert.True(Canonical(evidence));
        Assert.Matches(HashPattern(), Hash(evidence));
    }

    [Fact]
    public void RunLedger_DenialStorageIsSha256OnlyBoundedAndSaturating()
    {
        var ledger = new RunLedger();
        Assert.Equal(1, ledger.RecordDenial("first"));
        for (var index = 1; index < 1_024; index++)
        {
            Assert.Equal(1, ledger.RecordDenial($"key-{index}"));
        }

        Assert.Equal(1, ledger.RecordDenial("overflow-not-retained"));

        var storage = Assert.IsType<Dictionary<string, int>>(
            typeof(RunLedger)
                .GetField("_denials", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(ledger));
        Assert.Equal(1_024, storage.Count);
        Assert.All(storage.Keys, key => Assert.Matches(HashPattern(), key));
        Assert.DoesNotContain("first", storage.Keys);

        var firstHash = storage.First().Key;
        storage[firstHash] = int.MaxValue;
        var matchingInput = Enumerable.Range(0, 1_024)
            .Select(index => index == 0 ? "first" : $"key-{index}")
            .Single(value => ledger.DenialCount(value) == int.MaxValue);
        Assert.Equal(int.MaxValue, ledger.RecordDenial(matchingInput));
    }

    private static async Task<IReadOnlyList<GateEvidence>> RunAsync(
        IReadOnlyList<Dictionary<string, object?>> calls,
        ContainmentTarget.Session? stableTarget,
        bool addConfigurationGate = false,
        IToolGate? blocker = null)
    {
        var script = new ScriptedChatClient();
        for (var index = 0; index < calls.Count; index++)
        {
            script.AddToolCall($"call-{index}", "dangerous", calls[index]);
        }

        script.AddText("done");
        var tool = AIFunctionFactory.Create((string payload) => payload, "dangerous");
        var agent = new ChatClientAgent(
            script,
            new ChatClientAgentOptions
            {
                Name = "agent",
                ChatOptions = new ChatOptions { Tools = [tool] },
            });
        var sink = new CapturingSink();
        var gated = agent.AsBuilder()
            .UseGatekeeper(GatekeeperEnforcement.ReplaceResult, options =>
            {
                if (stableTarget is not null)
                {
                    options.ContainmentStore = new CleanStore();
                    options.ContainmentTargets = _ => [stableTarget];
                    options.ContainmentRetryThreshold = 1_000;
                }

                options.Add(blocker ?? new ForbiddenToolGate("dangerous"));
                if (addConfigurationGate)
                {
                    options.Add(new AllowGate());
                }

                options.EvidenceSink = sink;
            })
            .Build();
        var session = await gated.CreateSessionAsync();

        await gated.RunAsync("go", session);

        return sink.Records
            .Where(item => item.Stage == "tool" && item.Action == "Block")
            .ToArray();
    }

    private static Dictionary<string, object?> Arguments(object? payload)
        => new() { ["payload"] = payload };

    private static ContainmentTarget.Session Session(
        string tenant = "tenant-a",
        string identifier = "session-a")
        => new(tenant, identifier);

    private static string Hash(GateEvidence evidence)
        => Assert.IsType<string>(evidence.Extra!["denialCorrelationHash"]);

    private static int Attempts(GateEvidence evidence)
        => Assert.IsType<int>(evidence.Extra!["attempts"]);

    private static bool Canonical(GateEvidence evidence)
        => Assert.IsType<bool>(evidence.Extra!["denialArgumentsCanonical"]);

    private static Regex HashPattern()
        => new("^[0-9A-F]{64}$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private sealed class CapturingSink : IGateEvidenceSink
    {
        private readonly object _lock = new();
        private readonly List<GateEvidence> _records = [];

        public IReadOnlyList<GateEvidence> Records
        {
            get { lock (_lock) { return [.. _records]; } }
        }

        public void Record(GateEvidence evidence, int sequence)
        {
            lock (_lock)
            {
                _records.Add(evidence);
            }
        }
    }

    private sealed class CleanStore : IContainmentStore
    {
        public ContainmentSnapshot GetCurrent(ContainmentTarget target)
            => ContainmentSnapshot.NotContained(target);

        public ValueTask<ContainmentMutationResult> ContainAsync(
            ContainmentRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<ContainmentMutationResult> ReleaseAsync(
            ContainmentReleaseAuthorization authorization,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public void Dispose() { }
    }

    private sealed class NamedBlockGate(string policyName) : IToolGate
    {
        public string PolicyName { get; } = policyName;

        public GateCost Cost => GateCost.PureCode;

        public ValueTask<ToolGateVerdict> InspectAsync(
            GatedToolCall call,
            CancellationToken cancellationToken = default)
            => new(ToolGateVerdict.Block(PolicyName, "blocked"));
    }

    private sealed class AllowGate : IToolGate
    {
        public string PolicyName => "AllowGate";

        public GateCost Cost => GateCost.PureCode;

        public ValueTask<ToolGateVerdict> InspectAsync(
            GatedToolCall call,
            CancellationToken cancellationToken = default)
            => new(ToolGateVerdict.Allow(PolicyName));
    }
}
