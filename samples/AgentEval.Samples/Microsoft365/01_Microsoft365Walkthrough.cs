// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Assertions;
using AgentEval.Core;
using AgentEval.Microsoft365;
using AgentEval.Microsoft365.Diagnostics;
using AgentEval.Microsoft365.Evidence;

namespace AgentEval.Samples;

/// <summary>
/// Microsoft 365 — Live Walkthrough, on a <b>real</b> Microsoft 365 agent reached over Work IQ A2A.
///
/// Demonstrates:
///   1. Building the live provider via <see cref="Microsoft365AgentFactory.BuildLive"/> with verbose diagnostics, and
///      where its trace and issue register are written.
///   2. One read-only turn and its evidence per channel (Provided / NotProvided / NotRequested, never inferred),
///      citation markers, sources and links, and the raw vs display answer text.
///   3. Fresh vs continued server context, read from the turn results the <c>onTurn</c> sink receives.
///   4. Opt-in (<c>M365_ALLOW_ACTION_PROMPT=true</c>): an action-shaped prompt. A Completed reply, or an InputRequired
///      pause reported as NOT completed with its proposed calls listed and nothing approved.
///   5. The turn budget: an agent with <c>MaxTurns = 1</c> refuses its second turn before anything is sent.
///   6. An honest disclosure — what a pass here does and does NOT prove.
///
/// Live turns: at most 4 on the walkthrough's agent (3 when scene 4 is off) plus 1 on the budget agent.
///
/// 🔑 Requires a NON-PROD Microsoft 365 tenant with Work IQ enabled, a Microsoft 365 Copilot licence for the test user,
///    and a public-client Entra app ("Allow public client flows" = Yes) with the delegated WorkIQAgent.Ask permission
///    and admin consent. Set BOTH:
///     M365_CONFIG_PATH=&lt;path to a JSON file: tenantId/appClientId/[endpoint]/[agentId]/[agentName]/…&gt;
///     M365_I_UNDERSTAND_LIVE_SIDE_EFFECTS=true
///    Optional: M365_ALLOW_ACTION_PROMPT=true runs scene 4 (off by default; the scene then records NOT MEASURED).
/// ⚠️ The permission covers reads AND writes of the signed-in user's Microsoft 365 data, and a prompt can trigger
///    real actions that cannot be sandboxed — confirm the config points at a non-prod identity first.
/// ⏱️ Time to understand: 5 minutes
/// </summary>
public static class Microsoft365Walkthrough
{
    private const string ReadOnlyPrompt = "In one sentence, what can you help me with?";
    private const string FollowUpPrompt = "Say that again in at most ten words.";
    private const string ActionPrompt =
        "Describe, without doing it, how you would schedule a 30-minute meeting with my manager next week. " +
        "Do not create any calendar event.";
    private const string BudgetPrompt = "Reply with one word: ready.";

    // Scene 2 (1 turn) + scene 3 (2 turns) + scene 4 (1 turn, opt-in): the cap is the plan, not a guess.
    private const int WalkthroughMaxTurns = 4;

    /// <summary>Runs the sample.</summary>
    public static async Task RunAsync()
    {
        PrintHeader();

        if (!Microsoft365SampleSupport.TryLoadConfig("this walkthrough", out var config))
            return;

        var state = new WalkthroughState(config);
        try
        {
            await SafeScene(state, () => BuildAgentScene(state));
            if (state.Agent is null)
            {
                Console.WriteLine("\n=== Microsoft 365 Walkthrough stopped — could not build the live agent. ===");
                return;
            }

            await SafeScene(state, () => TurnAndEvidenceScene(state));
            await SafeScene(state, () => ContextScene(state));
            await SafeScene(state, () => ActionPromptScene(state));
            await SafeScene(state, () => BudgetScene(state));
            DisclosureScene(state);
        }
        finally
        {
            // Closes the trace and issue files of the walkthrough's agent.
            if (state.Agent is not null)
                await state.Agent.DisposeAsync();
        }

        Console.WriteLine("\n=== Microsoft 365 Walkthrough Complete ===");
    }

    // A live tenant, or its network path, can fail in ways unrelated to what a scene demonstrates: catch so one scene's
    // failure does not abort the walkthrough (the Copilot Studio walkthrough's SafeScene, plus the provider's typed
    // outcomes). Every failure says where the run's diagnostics are.
    private static async Task SafeScene(WalkthroughState state, Func<Task> scene)
    {
        try
        {
            await scene();
        }
        catch (AgentEvalAssertionException ex)
        {
            // An assertion the scene makes did not hold: the check failing, not the scene being skipped.
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"   ❌ assertion failed — {ex.Message.Split('\n')[0]}");
            Console.ResetColor();
            SampleOutcome.Fail($"assertion failed: {ex.Message.Split('\n')[0]}");
        }
        catch (WorkIqTurnNotCompletedException ex)
        {
            Microsoft365SampleSupport.PrintNotCompleted(ex.TurnResult);
            state.PrintWhereToLook();
            SampleOutcome.Fail($"turn not completed: {ex.TurnResult.Outcome}");
        }
        catch (WorkIqTurnBudgetExceededException ex)
        {
            Console.WriteLine($"   ❌ {ex.Message}");
            SampleOutcome.Error($"the turn budget stopped a scene: {ex.Message}");
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine($"   (scene stopped — {ex.GetType().Name}: {ex.Message})");
            Console.ResetColor();
            state.PrintWhereToLook();
            SampleOutcome.Error($"a scene threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    // Threads the agent, its options, its diagnostics paths and every turn result the onTurn sink received across
    // scenes. "Driven" is the agent the current scene talks to, so a failure points at that agent's own files.
    private sealed class WalkthroughState(Microsoft365Config config)
    {
        public Microsoft365Config Config { get; } = config;
        public WorkIqEvaluableAgent? Agent { get; set; }
        public WorkIqOptions? Options { get; set; }
        public List<WorkIqTurnResult> Turns { get; } = [];
        private Microsoft365DiagnosticsPaths? _drivenPaths;
        private WorkIqDiagnostics? _drivenDiagnostics;

        public void Drive(Microsoft365DiagnosticsPaths paths, WorkIqDiagnostics diagnostics)
        {
            _drivenPaths = paths;
            _drivenDiagnostics = diagnostics;
        }

        public void PrintWhereToLook()
        {
            if (_drivenPaths is not null && _drivenDiagnostics is not null)
                Microsoft365SampleSupport.PrintWhereToLook(_drivenPaths, _drivenDiagnostics);
        }
    }

    // ── 1. Build the live provider ──
    private static Task BuildAgentScene(WalkthroughState state)
    {
        Section("1. Build the live provider");

        var options = new WorkIqOptions
        {
            Verbose = true,
            MaxTurns = WalkthroughMaxTurns,
            ConversationPolicy = ConversationPolicy.ContinueContext,
            RunId = Microsoft365Diagnostics.NewRunId(),
        };
        var paths = Microsoft365Diagnostics.Describe(options);
        state.Options = options;
        state.Agent = Microsoft365AgentFactory.BuildLive(
            state.Config, iUnderstandLiveSideEffects: true, options: options, onTurn: state.Turns.Add);
        state.Drive(paths, state.Agent.Diagnostics);

        Console.WriteLine($"   Agent:   {state.Config.DisplayName}");
        Console.WriteLine($"   Profile: {state.Config.ResolveProfile()} · policy: {options.ConversationPolicy} · " +
                          $"MaxTurns: {options.MaxTurns} (turns counted, consumption not metered)");
        Microsoft365SampleSupport.PrintDiagnosticsPaths(paths);
        Console.WriteLine("   No network call yet: sign-in and card discovery run on the first turn. The first run prints a");
        Console.WriteLine("   device-code prompt on stderr; with Verbose on, every trace event is printed there too.");
        return Task.CompletedTask;
    }

    // ── 2. One read-only turn + the evidence per channel ──
    private static async Task TurnAndEvidenceScene(WalkthroughState state)
    {
        Section("2. One read-only turn + the evidence per channel");

        var (reply, _) = await InvokeAsync(state, "scene-2", ReadOnlyPrompt);

        if (reply.Evidence() is { } evidence)
        {
            PrintEvidenceSummary(evidence);
        }
        else
        {
            Console.WriteLine("   (the reply carried no evidence document)");
            SampleOutcome.Skip("the reply carried no evidence document, so the evidence channels were not shown");
        }

        reply.Text.Should().NotBeEmpty();
        Console.WriteLine("   ✅ NotBeEmpty passed");
    }

    // ── 3. Fresh vs continued server context ──
    private static async Task ContextScene(WalkthroughState state)
    {
        Section("3. Fresh vs continued server context");

        var first = state.Turns.LastOrDefault();
        if (first is null || !first.IsCompleted)
        {
            Console.WriteLine("   (scene 2 completed no turn, so there is no context to continue — skipping)");
            SampleOutcome.Skip("context continuity not measured: scene 2 completed no turn");
            return;
        }

        if (first.ContextId is null)
        {
            Console.WriteLine("   (the service returned no context id on scene 2's turn — nothing to compare)");
            SampleOutcome.Skip("context continuity not measured: the first turn carried no context id");
            return;
        }

        // ContinueContext: the follow-up is sent with the first turn's context id.
        var (_, continued) = await InvokeAsync(state, "scene-3-continued", FollowUpPrompt);

        // ResetSessionAsync drops the local context only: nothing is canceled or deleted remotely.
        await state.Agent!.ResetSessionAsync();
        Console.WriteLine("   ResetSessionAsync(): local context dropped (no remote call).");
        var (_, fresh) = await InvokeAsync(state, "scene-3-fresh", ReadOnlyPrompt);

        Console.WriteLine();
        Console.WriteLine($"   {"turn",-30} context id (from the onTurn sink's WorkIqTurnResult.ContextId)");
        Console.WriteLine($"   {"scene 2 (first turn)",-30} {first.ContextId}");
        Console.WriteLine($"   {"continued (ContinueContext)",-30} {continued.ContextId ?? "none"}");
        Console.WriteLine($"   {"after ResetSessionAsync()",-30} {fresh.ContextId ?? "none"}");
        Console.WriteLine();

        // Three outcomes per check: it held, it did not, or the service gave no id to compare.
        if (continued.ContextId is null)
        {
            Console.WriteLine("   the continued turn carried no context id — continuity not measured");
            SampleOutcome.Skip("the continued turn carried no context id, so continuity was not measured");
        }
        else if (continued.ContextId == first.ContextId)
        {
            Console.WriteLine("   ✅ the follow-up stayed in the first turn's context");
        }
        else
        {
            Console.WriteLine("   ❌ the follow-up came back in a different context although it was sent with the first one");
            SampleOutcome.Fail("ContinueContext: the follow-up turn came back in a different context");
        }

        if (fresh.ContextId is null)
        {
            Console.WriteLine("   the turn after the reset carried no context id — a fresh context was not measured");
            SampleOutcome.Skip("the turn after ResetSessionAsync carried no context id, so a fresh context was not measured");
        }
        else if (fresh.ContextId != first.ContextId)
        {
            Console.WriteLine("   ✅ after ResetSessionAsync the service started a new context");
        }
        else
        {
            Console.WriteLine("   ❌ after ResetSessionAsync the turn came back in the old context");
            SampleOutcome.Fail("after ResetSessionAsync the next turn reused the previous context id");
        }
    }

    // ── 4. An action-shaped prompt (opt-in) ──
    private static async Task ActionPromptScene(WalkthroughState state)
    {
        Section("4. An action-shaped prompt (opt-in)");

        if (Environment.GetEnvironmentVariable(Microsoft365SampleSupport.ActionPromptVariable) != "true")
        {
            Console.WriteLine("   Skipped by default: the prompt is about scheduling a meeting, and an agent may propose (or pause");
            Console.WriteLine("   to confirm) a real calendar action. Set M365_ALLOW_ACTION_PROMPT=true to run it against a");
            Console.WriteLine("   NON-PROD identity.");
            SampleOutcome.Skip($"the action-shaped prompt scene is off by default: set {Microsoft365SampleSupport.ActionPromptVariable}=true to run it");
            return;
        }

        await state.Agent!.ResetSessionAsync();
        try
        {
            var (_, result) = await InvokeAsync(state, "scene-4-action", ActionPrompt);
            Console.WriteLine("   Outcome: Completed — the agent answered in words.");
            Console.WriteLine($"   Approval intents: {result.Evidence.ApprovalIntents.Describe("intent(s)")}");
            Console.WriteLine("   A completed reply proves what the agent SAID; whether anything was created is not observable");
            Console.WriteLine("   here — check the test user's calendar.");
        }
        catch (WorkIqTurnNotCompletedException ex) when (ex.TurnResult.Outcome == WorkIqTurnOutcome.InputRequired)
        {
            var result = ex.TurnResult;
            Console.WriteLine("   Outcome: InputRequired — reported as NOT completed, never graded as an answer.");
            Console.WriteLine($"      task id {result.Task?.TaskId ?? "none"} · context id {result.ContextId ?? "none"}");
            PrintPendingInteraction(result.PendingInteraction);
            Console.WriteLine($"      approval intents: {result.Evidence.ApprovalIntents.Describe("intent(s)")}");
            Console.WriteLine("   ✅ Nothing was approved: this sample never builds a WorkIqInteractionResponse, and the provider");
            Console.WriteLine("      never answers a card on its own.");
        }
    }

    // ── 5. The turn budget ──
    private static async Task BudgetScene(WalkthroughState state)
    {
        Section("5. The turn budget (MaxTurns = 1)");

        var options = state.Options! with
        {
            MaxTurns = 1,
            ConversationPolicy = ConversationPolicy.FreshContextPerInvocation,
            RunId = Microsoft365Diagnostics.NewRunId(),
        };
        var paths = Microsoft365Diagnostics.Describe(options);
        await using var budgetAgent = Microsoft365AgentFactory.BuildLive(state.Config, iUnderstandLiveSideEffects: true, options: options);
        state.Drive(paths, budgetAgent.Diagnostics);
        Console.WriteLine("   A second agent on the same config, capped at ONE turn. Its own trace:");
        Console.WriteLine($"   {paths.TraceFile.FullName}");

        Console.WriteLine($"   Turn 1: \"{BudgetPrompt}\"");
        try
        {
            var reply = await budgetAgent.InvokeAsync(BudgetPrompt);
            Console.WriteLine($"   Reply: \"{Microsoft365SampleSupport.Trunc(reply.Text)}\"  (turns used: {budgetAgent.TurnsUsed} of {options.MaxTurns})");
        }
        catch (WorkIqTurnNotCompletedException ex)
        {
            // It reached the service, so it counts toward the cap; the budget check below still runs.
            Microsoft365SampleSupport.PrintNotCompleted(ex.TurnResult);
            SampleOutcome.Fail($"turn not completed: {ex.TurnResult.Outcome}");
            Console.WriteLine($"   (it still counts: turns used {budgetAgent.TurnsUsed} of {options.MaxTurns})");
        }

        Console.WriteLine($"   Turn 2: \"{BudgetPrompt}\"");
        try
        {
            await budgetAgent.InvokeAsync(BudgetPrompt);
            Console.WriteLine("   ❌ the second turn was sent although MaxTurns is 1");
            SampleOutcome.Fail("MaxTurns = 1 did not refuse the second turn");
        }
        catch (WorkIqTurnBudgetExceededException ex)
        {
            Console.WriteLine($"   ✅ refused BEFORE any send: {nameof(WorkIqTurnBudgetExceededException)} (TurnsUsed {ex.TurnsUsed}, MaxTurns {ex.MaxTurns})");
            Console.WriteLine($"      \"{ex.Message}\"");
        }
    }

    // ── 6. Honest disclosure ──
    private static void DisclosureScene(WalkthroughState state)
    {
        Section("6. Honest disclosure");

        Microsoft365SampleSupport.PrintHonestyStatement(WalkthroughMaxTurns, state.Options?.ProfileOverride ?? state.Config.ResolveProfile());
        Console.WriteLine("      Only a Completed turn is an answer. InputRequired, AuthRequired, Failed, Rejected, Canceled and");
        Console.WriteLine("      Indeterminate are reported with their outcome, task id, context id and a safe failure summary —");
        Console.WriteLine("      no partial text, and the input is never resent.");
        Console.WriteLine("      A matching context id shows the service kept the context; it says nothing about what the agent");
        Console.WriteLine("      remembered. Pauses are never approved by the provider: approving one is the host's decision.");
    }

    // One turn on the walkthrough's agent, with the case id the trace and the evidence will carry, and the turn result
    // the onTurn sink received (it sees every result before the agent returns or throws).
    private static async Task<(AgentResponse Reply, WorkIqTurnResult Turn)> InvokeAsync(WalkthroughState state, string caseId, string prompt)
    {
        var agent = state.Agent!;
        state.Drive(Microsoft365Diagnostics.Describe(state.Options!), agent.Diagnostics);

        Console.WriteLine($"   Prompt: \"{prompt}\"");
        var before = state.Turns.Count;
        agent.NextCaseId = caseId;
        var reply = await agent.InvokeAsync(prompt);
        Console.WriteLine($"   Reply:  \"{Microsoft365SampleSupport.Trunc(reply.Text)}\"");
        if (state.Turns.Count == before)
            throw new InvalidOperationException("the onTurn sink received no result for this turn");

        return (reply, state.Turns[^1]);
    }

    private static void PrintEvidenceSummary(Microsoft365TurnEvidence evidence)
    {
        Console.WriteLine();
        Console.WriteLine($"   Task state: {evidence.TaskState ?? "(no task: a direct message reply)"} · capture: {evidence.CaptureState}");
        if (evidence.Answer is { } answer)
        {
            Console.WriteLine($"   Answer text: raw {Length(answer.RawText)} (markers kept) · display {Length(answer.DisplayText)}" +
                              (answer.DisplayText.DerivationRuleId is { } rule ? $" (rule {rule})" : ""));
        }
        else
        {
            Console.WriteLine("   Answer text: no answer evidence (no valid answer was selected)");
        }

        Console.WriteLine();
        Console.WriteLine($"   {"channel",-22} availability");
        Row("citation markers", evidence.CitationMarkers.Describe("marker(s)"));
        Row("citation sources", evidence.CitationSources.Describe("source(s)"));
        Row("tool selections", evidence.ToolSelections.Describe("selection(s)"));
        Row("tool executions", evidence.ToolExecutions.Describe("execution(s)"));
        Row("retrieval executions", evidence.RetrievalExecutions.Describe("execution(s)"));
        Row("approval intents", evidence.ApprovalIntents.Describe("intent(s)"));
        Row("skill activations", evidence.SkillActivations.Describe("activation(s)"));
        Console.WriteLine();

        if (evidence.CitationMarkers.IsProvided && !evidence.CitationMarkers.Items.IsDefaultOrEmpty)
        {
            var formats = evidence.CitationMarkers.Items.Select(m => m.MarkerFormat).Distinct(StringComparer.Ordinal);
            Console.WriteLine($"   Marker formats: {string.Join(", ", formats)}");
        }

        if (evidence.CitationLinks.IsDefaultOrEmpty)
        {
            Console.WriteLine("   Citation links: none recorded");
        }
        else
        {
            var links = evidence.CitationLinks;
            var byBinding = links.GroupBy(l => l.Binding).Select(g => $"{g.Count()} {g.Key}");
            var unresolved = links.Count(l => l.Binding == CitationBinding.Unresolved);
            Console.WriteLine($"   Citation links: {links.Length} ({string.Join(", ", byBinding)})" +
                              (unresolved > 0 ? " — an Unresolved marker is kept as is: no source was bound to it" : ""));
        }

        var issues = evidence.Issues.IsDefault ? 0 : evidence.Issues.Length;
        Console.WriteLine($"   Normalisation issues on the document: {issues} (each is also in the issue register)");

        static void Row(string channel, string availability) => Console.WriteLine($"   {channel,-22} {availability}");
        static string Length(Observed<string> text) => text.IsPresent ? $"{text.Value?.Length ?? 0} chars" : text.State.ToString();
    }

    private static void PrintPendingInteraction(WorkIqPendingInteraction? pending)
    {
        if (pending is null)
        {
            Console.WriteLine("      the pause payload was not decoded (no pending interaction on the result)");
            return;
        }

        var options = pending.RecognizedOptions.IsDefaultOrEmpty ? "none recognised" : string.Join(", ", pending.RecognizedOptions);
        Console.WriteLine($"      pause: {pending.Kind} (profile {pending.ProfileId}) · options the card offers: {options}");
        if (pending.PromptText is { } promptText)
            Console.WriteLine($"      card text: \"{Microsoft365SampleSupport.Trunc(promptText)}\"");

        if (pending.ProposedCalls.IsDefaultOrEmpty)
        {
            Console.WriteLine("      proposed calls: none listed in the pause");
            return;
        }

        for (var i = 0; i < pending.ProposedCalls.Length; i++)
        {
            var call = pending.ProposedCalls[i];
            Console.WriteLine($"      proposed call {i + 1}: {call.FunctionName ?? "(no function name)"} " +
                              "(arguments not printed: they can carry names and addresses from the tenant)");
        }
    }

    private static void Section(string title)
    {
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"── {title}");
        Console.ResetColor();
    }

    private static void PrintHeader()
    {
        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine(@"
╔═══════════════════════════════════════════════════════════════════════════════╗
║   🏢 MICROSOFT 365 — LIVE WALKTHROUGH (evidence, context, outcomes, budget)    ║
║   What the tenant emitted per channel, and a non-completed turn told honestly  ║
╚═══════════════════════════════════════════════════════════════════════════════╝");
        Console.ResetColor();
    }
}
