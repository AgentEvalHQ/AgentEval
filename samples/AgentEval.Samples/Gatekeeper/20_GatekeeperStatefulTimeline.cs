// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json;
using AgentEval.Guardrails;
using AgentEval.MAF.Gatekeeper;
using Microsoft.Agents.AI;

namespace AgentEval.Samples;

/// <summary>Offline, content-free timeline across call, run, session, and durable state boundaries.</summary>
public static class GatekeeperStatefulTimeline
{
    private const string StableIdKey = "sample.logical-session";

    public static async Task RunAsync()
    {
        GatekeeperSampleContractRenderer.Print("20");
        Console.WriteLine("\n=== Gatekeeper — Stateful Gate Timeline (offline) ===\n");
        PrintTimelineHeader();

        await RunScopedBudgetTimelineAsync();
        await SessionReloadTimelineAsync();
        await DurableContainmentTimelineAsync();

        Console.WriteLine("\n   ✅ call/run/session/durable transitions matched their documented ownership and reset boundaries.");
    }

    private static async Task RunScopedBudgetTimelineAsync()
    {
        var gate = new RunBudgetGate(maxToolCalls: 1);
        var session = new SampleSession();
        ToolGateVerdict first;
        ToolGateVerdict second;
        using (AgentRunScope.Begin(session, "state-timeline", trace: null))
        {
            first = await gate.InspectAsync(Call("lookup"));
            second = await gate.InspectAsync(Call("lookup"));
        }

        ToolGateVerdict nextRun;
        using (AgentRunScope.Begin(session, "state-timeline", trace: null))
        {
            nextRun = await gate.InspectAsync(Call("lookup"));
        }

        Require(first.Action == ToolGateAction.Allow, "first call in a run must be admitted");
        Require(second.Action == ToolGateAction.Block, "second call in the same run must exhaust the budget");
        Require(nextRun.Action == ToolGateAction.Allow, "a new run must receive a new run ledger");
        PrintTransition("1  run budget", "call #1: ALLOW", "call #2: BLOCK", "new run: ALLOW");
    }

    private static async Task SessionReloadTimelineAsync()
    {
        var clock = new SampleClock(DateTimeOffset.UnixEpoch);
        var gate = new RateLimitGate(
            maxRuns: 1,
            window: TimeSpan.FromMinutes(1),
            timeProvider: clock,
            sessionKeySelector: StableId);
        var original = Session("logical-42");
        var reloaded = Session("logical-42");

        GateVerdict first;
        using (AgentRunScope.Begin(original, "state-timeline", trace: null))
        {
            first = await gate.InspectAsync("request");
        }

        GateVerdict sameLogicalSession;
        using (AgentRunScope.Begin(reloaded, "state-timeline", trace: null))
        {
            sameLogicalSession = await gate.InspectAsync("request");
        }

        clock.Advance(TimeSpan.FromMinutes(2));
        GateVerdict afterWindow;
        using (AgentRunScope.Begin(reloaded, "state-timeline", trace: null))
        {
            afterWindow = await gate.InspectAsync("request");
        }

        Require(first.Action == GateAction.Allow, "first logical-session run must be admitted");
        Require(sameLogicalSession.Action == GateAction.Block,
            "a fresh session object with the same host-attested id must retain the rate counter");
        Require(afterWindow.Action == GateAction.Allow, "the fixed rate window must reset after time advances");
        PrintTransition("2  session rate", "run #1: ALLOW", "reload: BLOCK", "window: ALLOW");
    }

    private static async Task DurableContainmentTimelineAsync()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "agenteval-gatekeeper-state-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "containment.json");
        var target = new ContainmentTarget.Session("sample-tenant", "logical-42");
        var clock = new SampleClock(new DateTimeOffset(2026, 8, 4, 10, 0, 0, TimeSpan.Zero));

        try
        {
            using (var store = new JsonFileContainmentStore(
                path,
                new SampleReleaseVerifier(),
                new JsonFileContainmentStoreOptions { BootstrapIfMissing = true },
                clock))
            {
                var result = await store.ContainAsync(
                    new ContainmentRequest(target, "sample_incident", "sample-evidence", "gatekeeper-sample"));
                Require(result.Disposition == ContainmentMutationDisposition.Applied,
                    "containment must be durably applied");
                Require(result.Snapshot.State == ContainmentSnapshotState.Active,
                    "applied containment must return an active snapshot");
            }

            using var reopened = new JsonFileContainmentStore(
                path,
                new SampleReleaseVerifier(),
                timeProvider: clock);
            Require(reopened.GetCurrent(target).State == ContainmentSnapshotState.Active,
                "containment must survive store reopen");

            var gate = new ContainedIdentityGate(reopened, _ => [target]);
            GateVerdict admission;
            using (AgentRunScope.Begin(new SampleSession(), "state-timeline", trace: null))
            {
                admission = await gate.InspectAsync("request");
            }

            Require(admission.Action == GateAction.Block,
                "a reopened active containment record must refuse session admission");
            PrintTransition("3  containment", "inactive", "persist: ACTIVE", "reopen: BLOCK");
        }
        finally
        {
            DeleteOwnedTemporaryDirectory(directory);
        }
    }

    private static GatedToolCall Call(string name) =>
        new(name, Arguments: null, AgentName: "state-timeline", Iteration: 0,
            FunctionCallIndex: 0, FunctionCount: 1, IsStreaming: false, Messages: null);

    private static SampleSession Session(string stableId)
    {
        var session = new SampleSession();
        session.StateBag.SetValue(StableIdKey, stableId, JsonSerializerOptions.Default);
        return session;
    }

    private static string? StableId(AgentSession session) =>
        session.StateBag.TryGetValue<string>(StableIdKey, out var id, JsonSerializerOptions.Default) ? id : null;

    private static void PrintTimelineHeader()
    {
        Console.WriteLine("   Scope                  First observation        Boundary observation     After reset/reload");
        Console.WriteLine("   ─────────────────────  ───────────────────────  ───────────────────────  ───────────────────");
    }

    private static void PrintTransition(string scope, string before, string decision, string after) =>
        Console.WriteLine($"   {scope,-22} {before,-24} {decision,-24} {after}");

    private static void DeleteOwnedTemporaryDirectory(string directory)
    {
        var full = Path.GetFullPath(directory);
        var tempRoot = Path.GetFullPath(Path.GetTempPath());
        if (!full.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(full).StartsWith("agenteval-gatekeeper-state-", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Refusing to delete a directory not owned by this sample.");
        }

        if (Directory.Exists(full))
        {
            Directory.Delete(full, recursive: true);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException("State timeline sample failed: " + message + ".");
        }
    }

    private sealed class SampleClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }

    private sealed class SampleReleaseVerifier : IContainmentReleaseAuthorizationVerifier
    {
        public bool Verify(
            ContainmentReleaseAuthorization authorization,
            ReadOnlyMemory<byte> canonicalPayload) => false;
    }

    private sealed class SampleSession : AgentSession;
}
