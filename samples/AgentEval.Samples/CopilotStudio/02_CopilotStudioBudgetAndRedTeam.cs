// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Assertions;
using AgentEval.Core;
using AgentEval.MAF.CopilotStudio;
using AgentEval.RedTeam;
using Microsoft.Extensions.AI;

namespace AgentEval.Samples;

/// <summary>
/// Copilot Studio — Budget Enforcement + Red Team, a deeper companion to
/// <see cref="CopilotStudioWalkthrough"/> (01) on a <b>real</b> Microsoft Copilot Studio (MCS) agent.
///
/// Where 01 shows the basics (build, one assertion, continuity, Gatekeeper, fidelity), this sample goes
/// one layer deeper into the two things that are genuinely DIFFERENT about a live MCS target versus a
/// stateless Azure OpenAI endpoint:
///
///   1. Credit-budget enforcement — <c>--max-credits</c>'s library-level twin: a tight cap that actually
///      trips <see cref="CopilotStudioBudgetExceededException"/> mid-conversation, and the
///      <c>HaveStayedWithinCreditBudget</c> fluent assertion reading the SAME estimate.
///   2. Red-team composability — the exact same <c>CanResistAsync</c> one-liner
///      used against an Azure OpenAI agent elsewhere in this sample suite, run against a live MCS agent
///      instead — proving <c>agenteval redteam --sut copilot-studio</c>'s CLI behavior is not special-cased
///      plumbing, it is this same extension method with a different <see cref="IEvaluableAgent"/>.
///   3. New-vs-continued conversation identity — <c>HaveStartedNewConversation</c> /
///      <c>HaveStartedDifferentConversation</c>, the two assertions 01 doesn't demonstrate (01 only shows
///      <c>HaveContinuedConversation</c>).
///
/// A single targeted probe (<see cref="Attack.PromptInjection"/> at Quick intensity), not a full
/// <c>QuickRedTeamScanAsync()</c> — a live MCS session is stateful and <c>--parallelism</c>-floored to 1 (see
/// docs/copilot-studio.md), so a full 14-attack scan here would be slow and credit-expensive for a
/// sample whose point is to be quick to read and cheap to run.
///
/// ⚠️ Honesty note: like 01, this sample's live network path (the actual MSAL device-code sign-in, the real
/// HTTP round trip, whether a real tenant's budget/exception shape matches what's coded here) has NOT been
/// independently verified against a real Copilot Studio tenant in this session — see
/// docs/copilot-studio.md#whats-verified-vs-what-still-needs-a-live-check for the exact boundary.
/// The code compiles against, and matches, the real <c>AgentEval.MAF.CopilotStudio</c> API surface; only the
/// live round trip itself awaits a first real-credential run.
///
/// 🔑 Requires a NON-PROD Copilot Studio agent + Entra app registration. Set BOTH:
///     COPILOTSTUDIO_CONFIG_PATH=&lt;path to a JSON file: environmentId/schemaName/tenantId/appClientId/[cloud]/[agentName]&gt;
///     COPILOTSTUDIO_I_UNDERSTAND_LIVE_SIDE_EFFECTS=true
/// ⚠️ This drives a REAL agent whose connectors/flows can fire REAL production actions and cannot be sandboxed —
///    confirm the config points at a non-prod agent before setting the consent variable.
/// ⏱️ Time to understand: 6 minutes
/// </summary>
public static class CopilotStudioBudgetAndRedTeam
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

        await SafeScene(() => BudgetExceededScene(config));
        await SafeScene(() => CreditBudgetAssertionScene(config));
        await SafeScene(() => TargetedRedTeamScene(config));
        await SafeScene(() => ConversationIdentityScene(config));

        Console.WriteLine("\n=== Copilot Studio Budget + Red Team Complete ===");
    }

    // A live MCS agent, or its network path, can fail in ways unrelated to what a scene is demonstrating —
    // catch so one scene's failure doesn't abort the whole walkthrough (matches 01's own SafeScene).
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

    // ── 1. A tight credit cap actually trips CopilotStudioBudgetExceededException ──
    private static async Task BudgetExceededScene(CopilotStudioConfig config)
    {
        Section("1. Credit-budget enforcement — a tight cap trips a real exception");

        // maxCredits: 1 — the SECOND turn (not the first) is refused: spend may legitimately reach exactly
        // the cap, only the turn that would push PAST it is stopped (see ExitCodes.BudgetExceeded's own
        // remarks — the same enforcement point --max-credits uses at the CLI layer).
        var agent = CopilotStudioAgentFactory.BuildLive(config, iUnderstandLiveSideEffects: true, maxCredits: 1);

        Console.WriteLine("   Turn 1 (spend reaches the cap — allowed):");
        var first = await agent.InvokeAsync("Hello! What can you help me with?");
        Console.WriteLine($"     Reply: \"{Trunc(first.Text)}\"");

        Console.WriteLine("   Turn 2 (would push spend PAST the cap — expect a refusal):");
        try
        {
            await agent.InvokeAsync("Tell me more, in detail.");
            Console.WriteLine("     (no exception this run — the estimate may not have reached the cap yet; this is turn-count-based, not deterministic across all tenants)");
        }
        catch (CopilotStudioBudgetExceededException ex)
        {
            Console.WriteLine($"   ✅ CopilotStudioBudgetExceededException: spent {ex.EstimatedCreditsUsed} estimated credit(s), cap was {ex.MaxCredits}");
            Console.WriteLine($"     {Trunc(ex.Message, 160)}");
        }
    }

    // ── 2. HaveStayedWithinCreditBudget — the fluent counterpart, reading the same live estimate ──
    private static async Task CreditBudgetAssertionScene(CopilotStudioConfig config)
    {
        Section("2. HaveStayedWithinCreditBudget — a generous cap, asserted rather than relied on to trip");

        CopilotStudioChatClient? rawClient = null;
        var agent = CopilotStudioAgentFactory.BuildLive(
            config, iUnderstandLiveSideEffects: true, maxCredits: 10,
            wrapChatClient: c => { rawClient = c as CopilotStudioChatClient; return c; });

        var response = await agent.InvokeAsync("What's the weather like today?");
        Console.WriteLine($"   Reply: \"{Trunc(response.Text)}\"");

        if (rawClient is null)
        {
            Console.WriteLine("   (raw client not captured this run — wrapChatClient's hook didn't fire as expected)");
            return;
        }

        var chatResponse = new ChatResponse([new ChatMessage(ChatRole.Assistant, response.Text)]);
        chatResponse.Should().HaveStayedWithinCreditBudget(rawClient.EstimatedCreditsUsed, maxCredits: 10);
        Console.WriteLine($"   ✅ HaveStayedWithinCreditBudget passed (estimated {rawClient.EstimatedCreditsUsed}/10 credits used)");
        Console.WriteLine("   Reminder: this is an ESTIMATE (turns counted, not metered spend) — cross-check real");
        Console.WriteLine("   Copilot Credit consumption through Power Platform's own admin tooling for anything cost-sensitive.");
    }

    // ── 3. Red-team composability: the SAME CanResistAsync one-liner, against a live MCS agent ──
    private static async Task TargetedRedTeamScene(CopilotStudioConfig config)
    {
        Section("3. Red-team composability — CanResistAsync against a LIVE MCS agent");

        var agent = CopilotStudioAgentFactory.BuildLive(config, iUnderstandLiveSideEffects: true, maxCredits: 5);

        Console.WriteLine("   var resisted = await agent.CanResistAsync(Attack.PromptInjection);");
        Console.WriteLine("   (a single Quick-intensity probe — not a full scan; see this file's header for why)\n");

        var resisted = await agent.CanResistAsync(Attack.PromptInjection);
        Console.WriteLine($"   Prompt Injection: {(resisted ? "✅ Resisted" : "❌ Vulnerable")}");
        Console.WriteLine("   This is the SAME extension method RedTeamBasic (SafetyAndSecurity) runs against an Azure");
        Console.WriteLine("   OpenAI agent — CanResistAsync doesn't know or care that IEvaluableAgent here is backed by");
        Console.WriteLine("   a live Copilot Studio conversation instead of a stateless chat completion.");
        Console.WriteLine("   Evidence fidelity note: this scan is text-only (SutTier.TextOnly / EvidenceFidelity.Verbal)");
        Console.WriteLine("   for this target by design — see docs/copilot-studio.md#how-it-fits-red-team-fidelity.");
    }

    // ── 4. HaveStartedNewConversation / HaveStartedDifferentConversation ──
    private static async Task ConversationIdentityScene(CopilotStudioConfig config)
    {
        Section("4. New-vs-continued conversation identity");

        ChatResponse? firstAgentResponse = null;
        var firstAgent = CopilotStudioAgentFactory.BuildLive(
            config, iUnderstandLiveSideEffects: true, maxCredits: 5,
            wrapChatClient: c => { var wrapped = new ResponseCapturingChatClient(c, r => firstAgentResponse = r); return wrapped; });
        await firstAgent.InvokeAsync("Hi there.");

        if (firstAgentResponse is null)
        {
            Console.WriteLine("   (no response captured this run — skipping)");
            return;
        }

        firstAgentResponse.Should().HaveStartedNewConversation();
        Console.WriteLine($"   ✅ HaveStartedNewConversation passed (conversation id: {firstAgentResponse.ConversationId})");

        // A SECOND, independently-built agent talks to the SAME underlying bot but starts its own
        // server-side conversation — proves the two are genuinely distinct, not the first one resuming.
        ChatResponse? secondAgentResponse = null;
        var secondAgent = CopilotStudioAgentFactory.BuildLive(
            config, iUnderstandLiveSideEffects: true, maxCredits: 5,
            wrapChatClient: c => { var wrapped = new ResponseCapturingChatClient(c, r => secondAgentResponse = r); return wrapped; });
        await secondAgent.InvokeAsync("Hi there, separately.");

        if (secondAgentResponse is null)
        {
            Console.WriteLine("   (no response captured this run — skipping)");
            return;
        }

        secondAgentResponse.Should().HaveStartedDifferentConversation(firstAgentResponse.ConversationId!);
        Console.WriteLine($"   ✅ HaveStartedDifferentConversation passed (conversation id: {secondAgentResponse.ConversationId}, distinct from {firstAgentResponse.ConversationId})");
    }

    // Caller-owned IChatClient middleware, composed in via BuildLive's wrapChatClient hook — same pattern
    // CopilotStudioWalkthrough (01) uses.
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

    private static string Trunc(string? s, int max = 80) => string.IsNullOrEmpty(s) ? "(none)" : s.Length <= max ? s : s[..max] + "…";

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
║   🤖 COPILOT STUDIO — BUDGET ENFORCEMENT + RED TEAM                            ║
║   A tight credit cap tripping for real, and red-team composing over a live MCS ║
╚═══════════════════════════════════════════════════════════════════════════════╝");
        Console.ResetColor();
    }
}
