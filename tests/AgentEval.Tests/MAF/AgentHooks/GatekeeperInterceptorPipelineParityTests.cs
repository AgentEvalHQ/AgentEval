// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using AgentEval.MAF.AgentHooks;
using AgentEval.MAF.AgentHooks.Aevp;
using AgentEval.MAF.Gatekeeper;
using AgentHooks;
using Xunit;

namespace AgentEval.Tests.MAF.AgentHooks;

/// <summary>
/// The AgentHooks adapter must enforce what Gatekeeper's own tool pipeline enforces. Each test here is a way the
/// first adapter differed: a rewrite that skipped later gates, a throwing gate, a mutable gate list, a run-scoped
/// gate sharing process-wide state, a missing conversation read as a full check, arguments flattened to strings,
/// and evidence addresses that pointed at nothing.
/// </summary>
public sealed class GatekeeperInterceptorPipelineParityTests
{
    private static AgentContextBuilder Builder() =>
        new("agenteval-test", "agenteval", "session-1", "TestAgent", "1.0", "2026-08-09T00:00:00Z");

    private static AgentContext PreToolCall(string path = "/tmp/file", bool withConversation = false)
    {
        var context = Builder().PreToolCall("tc-1", "read_file", new JsonObject { ["path"] = path });
        if (!withConversation)
            return context;

        var json = (JsonObject)context.Json.DeepClone();
        json["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "Read /tmp/file please." });
        return new AgentContext(json);
    }

    private sealed class Gate(string policy, Func<GatedToolCall, ToolGateVerdict> decide, GateRequirements requirements = GateRequirements.None) : IToolGate
    {
        public string PolicyName => policy;
        public GateCost Cost => GateCost.PureCode;
        public GateRequirements Requirements => requirements;
        public ValueTask<ToolGateVerdict> InspectAsync(GatedToolCall call, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(decide(call));
    }

    private static string? PathOf(GatedToolCall call) => call.Arguments?["path"] as string;

    private static Gate Rewrite(string from, string to) => new("Rewriter", call =>
        PathOf(call) == from
            ? ToolGateVerdict.Mutate("Rewriter", new Dictionary<string, object?> { ["path"] = to }, "rewritten")
            : ToolGateVerdict.Allow("Rewriter"));

    private static Gate BlockPath(string path) => new("PathBlocker", call =>
        PathOf(call) == path ? ToolGateVerdict.Block("PathBlocker", $"{path} is forbidden") : ToolGateVerdict.Allow("PathBlocker"));

    // ── Mutation: the rewritten call is checked by every gate ───────────────────────────────────────

    [Fact]
    public async Task ARewrite_IsCheckedByTheGatesAfterIt()
    {
        var interceptor = new GatekeeperInterceptor([Rewrite("/tmp/file", "/etc/passwd"), BlockPath("/etc/passwd")]);

        var verdict = await interceptor.InterceptAsync(PreToolCall(), CancellationToken.None);

        Assert.Equal(Decision.Deny, verdict.Decision);
        Assert.Contains("/etc/passwd is forbidden", verdict.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARewrite_IsCheckedByTheGatesBeforeIt_TooBecauseTheScanRestarts()
    {
        var interceptor = new GatekeeperInterceptor([BlockPath("/etc/passwd"), Rewrite("/tmp/file", "/etc/passwd")]);

        var verdict = await interceptor.InterceptAsync(PreToolCall(), CancellationToken.None);

        Assert.Equal(Decision.Deny, verdict.Decision);
    }

    [Fact]
    public async Task AConvergentRewrite_BecomesATransformOfTheFinalArguments()
    {
        var interceptor = new GatekeeperInterceptor(
            [Rewrite("/tmp/file", "/tmp/a"), Rewrite("/tmp/a", "/tmp/b"), BlockPath("/etc/passwd")]);

        var verdict = await interceptor.InterceptAsync(PreToolCall(), CancellationToken.None);

        Assert.Equal(Decision.Transform, verdict.Decision);
        Assert.Equal("/tmp/b", verdict.Transform!.Value!["path"]!.GetValue<string>());
        verdict.Validate();
    }

    [Fact]
    public async Task ARewriteThatNeverConverges_IsDenied()
    {
        var counter = 0;
        var restless = new Gate("Restless", _ =>
            ToolGateVerdict.Mutate("Restless", new Dictionary<string, object?> { ["path"] = $"/tmp/{++counter}" }, "again"));
        var interceptor = new GatekeeperInterceptor([restless]);

        var verdict = await interceptor.InterceptAsync(PreToolCall(), CancellationToken.None);

        Assert.Equal(Decision.Deny, verdict.Decision);
        Assert.Contains("mutation_revalidation", verdict.Reason, StringComparison.Ordinal);
    }

    // ── Fail closed ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AGateThatThrows_Denies()
    {
        var interceptor = new GatekeeperInterceptor([new Gate("Broken", _ => throw new InvalidOperationException("boom"))]);

        var verdict = await interceptor.InterceptAsync(PreToolCall(), CancellationToken.None);

        Assert.Equal(Decision.Deny, verdict.Decision);
        Assert.Contains("failing closed", verdict.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARewriteThatCannotBeSerializedFaithfully_Denies()
    {
        // A delegate has no JSON form. The old fallback sent its ToString() (a type name) as the new argument.
        var gate = new Gate("Weird", _ => ToolGateVerdict.Mutate("Weird",
            new Dictionary<string, object?> { ["path"] = new Func<int>(() => 1) }, "unserializable"));
        var interceptor = new GatekeeperInterceptor([gate]);

        var verdict = await interceptor.InterceptAsync(PreToolCall(), CancellationToken.None);

        Assert.Equal(Decision.Deny, verdict.Decision);
    }

    [Fact]
    public async Task RewrittenArguments_KeepTheirJsonTypes()
    {
        var gate = new Gate("Typed", call => call.Arguments!.ContainsKey("limit")
            ? ToolGateVerdict.Allow("Typed")
            : ToolGateVerdict.Mutate("Typed", new Dictionary<string, object?>
            {
                ["path"] = "/tmp/file",
                ["limit"] = 1.5m,
                ["tags"] = new List<int> { 1, 2 },
                ["options"] = new Dictionary<string, object?> { ["recursive"] = false },
            }, "typed"));
        var interceptor = new GatekeeperInterceptor([gate]);

        var verdict = await interceptor.InterceptAsync(PreToolCall(), CancellationToken.None);

        var value = verdict.Transform!.Value!;
        Assert.Equal(1.5m, value["limit"]!.GetValue<decimal>());
        Assert.Equal("[1,2]", value["tags"]!.ToJsonString());
        Assert.False(value["options"]!["recursive"]!.GetValue<bool>());
    }

    // ── Construction ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ChangingTheCallersList_AfterConstruction_DoesNotChangeEnforcement()
    {
        var gates = new List<IToolGate> { BlockPath("/tmp/file") };
        var interceptor = new GatekeeperInterceptor(gates);

        gates.Clear();
        var verdict = await interceptor.InterceptAsync(PreToolCall(), CancellationToken.None);

        Assert.Equal(Decision.Deny, verdict.Decision);
    }

    [Fact]
    public void ARunScopedGate_IsRejected_BecauseNoHostEstablishesTheScope()
    {
        var scoped = new Gate("Budget", _ => ToolGateVerdict.Allow("Budget"), GateRequirements.RunScope);

        var ex = Assert.Throws<ArgumentException>(() => new GatekeeperInterceptor([scoped]));

        Assert.Contains("process-wide", ex.Message, StringComparison.Ordinal);
    }

    // ── Coverage gaps are said ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnAllowWithoutAConversation_WarnsThatConversationGatesHadNothingToCheck()
    {
        var interceptor = new GatekeeperInterceptor([BlockPath("/etc/passwd")]);

        var without = await interceptor.InterceptAsync(PreToolCall(), CancellationToken.None);
        var with = await interceptor.InterceptAsync(PreToolCall(withConversation: true), CancellationToken.None);

        Assert.Equal(Decision.Allow, without.Decision);
        Assert.Contains(without.Warnings, w => w.Reason == $"{GatekeeperInterceptor.ReasonPrefix}.no_conversation");
        Assert.DoesNotContain(with.Warnings, w => w.Reason == $"{GatekeeperInterceptor.ReasonPrefix}.no_conversation");
        without.Validate();
    }

    // ── Evidence resolves ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EveryVerdictsEvidence_ResolvesToBytesThatHashToItsAddress()
    {
        var store = new InMemoryAevpArtifactStore();
        var interceptor = new GatekeeperInterceptor([Rewrite("/tmp/rewrite", "/tmp/safe"), BlockPath("/etc/passwd")], store);

        var verdicts = new[]
        {
            await interceptor.InterceptAsync(PreToolCall(), CancellationToken.None),                     // allow
            await interceptor.InterceptAsync(PreToolCall("/etc/passwd"), CancellationToken.None),         // deny
            await interceptor.InterceptAsync(PreToolCall("/tmp/rewrite"), CancellationToken.None),        // transform
            await interceptor.InterceptAsync(Builder().Output(JsonValue.Create("done")), CancellationToken.None),   // not enforced
        };

        Assert.Equal([Decision.Allow, Decision.Deny, Decision.Transform, Decision.Allow], verdicts.Select(v => v.Decision));
        foreach (var verdict in verdicts)
        {
            var address = verdict.Evidence!.Artefact;
            Assert.True(store.TryGet(address, out var canonical), $"{verdict.Decision}: {address} does not resolve");
            Assert.Equal(address, AgentEvidenceProfile.AddressOf(canonical));
            Assert.Equal(canonical, AgentEvidenceProfile.FromJson(canonical).ToCanonicalJson());
        }
    }

    [Fact]
    public async Task TheStore_RefusesBytesThatDoNotHashToTheAddress()
    {
        var store = new InMemoryAevpArtifactStore();

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await store.PutAsync("sha256:" + new string('0', 64), """{"aevp":"aevp/0.1"}"""));
    }
}
