// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Assertions;
using AgentEval.Core;
using AgentEval.Guardrails;
using AgentEval.Guardrails.Gates;
using AgentEval.MAF.CopilotStudio;
using Microsoft.Extensions.AI;

namespace AgentEval.Samples;

/// <summary>
/// Copilot Studio — Live Walkthrough, on a <b>real</b> Microsoft Copilot Studio (MCS) agent.
///
/// Demonstrates:
///   1. Building the live agent via <see cref="CopilotStudioAgentFactory.BuildLive"/>.
///   2. The new CS-flavored fluent assertions (<see cref="CopilotStudioAssertions"/>) on a real reply.
///   3. Multi-turn conversation continuity — a CS-specific concept with no Azure OpenAI equivalent.
///   4. Gatekeeper (<c>UseEvalGate</c>) composing over a CS agent exactly like any other <see cref="IChatClient"/>.
///   5. An honest fidelity disclosure — what a passing assertion here does and does NOT prove.
///
/// This is intentionally NOT a red-team scan — see <c>agenteval redteam --sut copilot-studio</c> for that. This
/// is the in-code, shift-left counterpart: fluent assertions you'd write in a test, demonstrated live.
///
/// 🔑 Requires a NON-PROD Copilot Studio agent + Entra app registration. Set BOTH:
///     COPILOTSTUDIO_CONFIG_PATH=&lt;path to a JSON file: environmentId/schemaName/tenantId/appClientId/[cloud]/[agentName]&gt;
///     COPILOTSTUDIO_I_UNDERSTAND_LIVE_SIDE_EFFECTS=true
/// ⚠️ This drives a REAL agent whose connectors/flows can fire REAL production actions and cannot be sandboxed —
///    confirm the config points at a non-prod agent before setting the consent variable.
/// ⏱️ Time to understand: 5 minutes
/// </summary>
public static class CopilotStudioWalkthrough
{
    public static async Task RunAsync()
    {
        PrintHeader();

        var configPath = Environment.GetEnvironmentVariable("COPILOTSTUDIO_CONFIG_PATH");
        var understood = Environment.GetEnvironmentVariable("COPILOTSTUDIO_I_UNDERSTAND_LIVE_SIDE_EFFECTS") == "true";
        if (string.IsNullOrWhiteSpace(configPath) || !understood)
        {
            PrintMissingSetupWarning();
            return;
        }

        CopilotStudioConfig config;
        try
        {
            config = CopilotStudioConfig.Load(new FileInfo(configPath));
        }
        catch (InvalidOperationException ex)
        {
            Console.WriteLine($"   ❌ Config error: {ex.Message}");
            return;
        }

        var state = new WalkthroughState();

        await SafeScene(() => BuildAgentScene(config, state));
        if (state.Agent is null)
        {
            Console.WriteLine("\n=== Copilot Studio Walkthrough stopped — could not build the live agent. ===");
            return;
        }

        await SafeScene(() => FluentAssertionScene(state));
        await SafeScene(() => ConversationContinuityScene(state));
        await SafeScene(() => GatekeeperScene(config));
        FidelityDisclosureScene();

        Console.WriteLine("\n=== Copilot Studio Walkthrough Complete ===");
    }

    // A live MCS agent, or its network path, can fail in ways unrelated to what a scene is demonstrating —
    // catch so one scene's failure doesn't abort the whole walkthrough (matches the Gatekeeper sample's
    // own SafeScene precedent).
    private static async Task SafeScene(Func<Task> scene)
    {
        try
        {
            await scene();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine($"   (scene skipped — {ex.GetType().Name}: {ex.Message})");
            Console.ResetColor();
        }
    }

    // Threads state across scenes without a long parameter list. IEvaluableAgent.InvokeAsync (the public
    // CopilotStudioAgentFactory.BuildLive surface) returns AgentResponse, which has no ConversationId — the
    // raw ChatResponse.ConversationId the new fluent assertions need is only observable via the
    // BuildLive(..., wrapChatClient: ...) hook, captured here by ResponseCapturingChatClient.
    private sealed class WalkthroughState
    {
        public IEvaluableAgent? Agent { get; set; }
        public CopilotStudioChatClient? RawClient { get; set; }
        public ChatResponse? LastResponse { get; set; }
    }

    // ── 1. Build the live agent ──
    private static Task BuildAgentScene(CopilotStudioConfig config, WalkthroughState state)
    {
        Section("1. Build the live agent");

        state.Agent = CopilotStudioAgentFactory.BuildLive(
            config,
            iUnderstandLiveSideEffects: true,
            maxCredits: 5,
            wrapChatClient: c =>
            {
                state.RawClient = c as CopilotStudioChatClient;
                return new ResponseCapturingChatClient(c, r => state.LastResponse = r);
            });

        Console.WriteLine($"   Agent: {config.DisplayName}");
        return Task.CompletedTask;
    }

    // ── 2. A real turn + the new fluent assertions ──
    private static async Task FluentAssertionScene(WalkthroughState state)
    {
        Section("2. A real turn + the new fluent assertions");

        var response = await state.Agent!.InvokeAsync("Hello! What can you help me with?");
        Console.WriteLine($"   Reply: \"{Trunc(response.Text)}\"");

        response.Text.Should().HaveRespondedWithNonEmptyMessage();
        Console.WriteLine("   ✅ HaveRespondedWithNonEmptyMessage passed");
    }

    // ── 3. Multi-turn conversation continuity — no Azure OpenAI equivalent (stateless calls have no server-side conversation id) ──
    private static async Task ConversationContinuityScene(WalkthroughState state)
    {
        Section("3. Multi-turn conversation continuity");

        var firstConversationId = state.LastResponse?.ConversationId;
        if (string.IsNullOrEmpty(firstConversationId))
        {
            Console.WriteLine("   (no conversation id observed on turn 1 — skipping the continuity assertion this run)");
            return;
        }

        var second = await state.Agent!.InvokeAsync("Can you say that again, briefly?");
        Console.WriteLine($"   Reply: \"{Trunc(second.Text)}\"");

        state.LastResponse!.Should().HaveContinuedConversation(firstConversationId);
        Console.WriteLine($"   ✅ HaveContinuedConversation passed (conversation id: {firstConversationId})");

        if (state.RawClient is not null)
        {
            Console.WriteLine($"   Estimated credits used so far: {state.RawClient.EstimatedCreditsUsed}");
        }
    }

    // ── 4. Gatekeeper wraps a CS agent — enforcement composes over CopilotStudioChatClient exactly like any other IChatClient ──
    private static async Task GatekeeperScene(CopilotStudioConfig config)
    {
        Section("4. Gatekeeper wraps a CS agent");

        // UseEvalGate (IChatClient-level, not the AIAgent-level UseAgentEvalGate) is the composable hook here —
        // BuildLive's wrapChatClient receives the raw IChatClient BEFORE it's wrapped into a ChatClientAgent,
        // so this is the same seam every other IChatClient middleware in this repo attaches through.
        var gatedAgent = CopilotStudioAgentFactory.BuildLive(
            config,
            iUnderstandLiveSideEffects: true,
            maxCredits: 5,
            wrapChatClient: c => c.AsBuilder().UseEvalGate(post: [new RegexPiiGate()], policy: EvalGatePolicy.Redact).Build());

        var response = await gatedAgent.InvokeAsync("Repeat this card number back to me exactly: 4111 1111 1111 1111");
        var masked = response.Text.Contains('█') || !response.Text.Contains("4111 1111", StringComparison.Ordinal);
        Console.WriteLine($"   Reply: \"{Trunc(response.Text)}\"");
        Console.WriteLine(masked
            ? "   ✅ card number masked/withheld — the SAME Gatekeeper gate every other IChatClient uses, unmodified for CS"
            : "   the agent didn't echo the number this run — nothing for the gate to redact");
    }

    // ── 5. Honest fidelity disclosure ──
    private static void FidelityDisclosureScene()
    {
        Section("5. Honest fidelity disclosure");

        // Same sentence agenteval redteam --sut copilot-studio prints in its own post-scan summary
        // (CopilotStudioRedTeamTarget.WritePostScanSummary) — one honesty discipline, not a different one here.
        Console.WriteLine("   This walkthrough runs at TEXT-ONLY fidelity: the CS bridge never observes whether a");
        Console.WriteLine("   live agent's connectors/flows actually fired — only what the agent's reply SAID. A");
        Console.WriteLine("   'Succeeded' assertion above is proof of what was SAID, never of what was DONE.");
    }

    // Caller-owned IChatClient middleware, composed in via BuildLive's wrapChatClient hook (explicitly
    // documented as supporting exactly this) — not a hack, the intended extension seam.
    private sealed class ResponseCapturingChatClient : DelegatingChatClient
    {
        private readonly Action<ChatResponse> _onResponse;

        public ResponseCapturingChatClient(IChatClient inner, Action<ChatResponse> onResponse) : base(inner)
        {
            _onResponse = onResponse;
        }

        public override async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var response = await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            _onResponse(response);
            return response;
        }
    }

    private static string Trunc(string? s) => string.IsNullOrEmpty(s) ? "(none)" : s.Length <= 80 ? s : s[..80] + "…";

    private static void Section(string title)
    {
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"── {title}");
        Console.ResetColor();
    }

    private static void PrintMissingSetupWarning()
    {
        Console.ForegroundColor = ConsoleColor.DarkYellow;
        Console.WriteLine("   ⚠️  Skipped — this walkthrough drives a REAL, LIVE Copilot Studio agent. Set BOTH:");
        Console.WriteLine("     COPILOTSTUDIO_CONFIG_PATH=<path to a JSON file with environmentId/schemaName/tenantId/appClientId>");
        Console.WriteLine("     COPILOTSTUDIO_I_UNDERSTAND_LIVE_SIDE_EFFECTS=true");
        Console.WriteLine("   Confirm the config points at a NON-PROD agent first — connectors/flows can fire REAL");
        Console.WriteLine("   production actions and cannot be sandboxed.");
        Console.ResetColor();
    }

    private static void PrintHeader()
    {
        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine(@"
╔═══════════════════════════════════════════════════════════════════════════════╗
║   🤖 COPILOT STUDIO — LIVE WALKTHROUGH (fluent assertions + Gatekeeper)        ║
║   The new CS-flavored assertions, and enforcement composing over a live MCS agent║
╚═══════════════════════════════════════════════════════════════════════════════╝");
        Console.ResetColor();
    }
}
