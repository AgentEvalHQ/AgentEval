// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Diagnostics.CodeAnalysis;
using AgentEval.Assertions;
using AgentEval.Core;
using AgentEval.Microsoft365;
using AgentEval.Microsoft365.Diagnostics;
using AgentEval.Microsoft365.Evidence;

namespace AgentEval.Samples;

/// <summary>
/// Microsoft 365 — Hello World (the 60-second version).
///
/// The simplest possible check against a live Microsoft 365 agent reached over Work IQ A2A: build the live provider,
/// send ONE read-only prompt, make ONE fluent assertion (<c>NotBeEmpty</c>) on the real reply, and print ONE line saying
/// which evidence the tenant provided (printed, not asserted). Sample R2 (Live Walkthrough) goes further: the evidence
/// per channel, fresh vs continued context, a non-completed turn handled honestly, and the turn budget.
///
/// 🔑 Requires a NON-PROD Microsoft 365 tenant with Work IQ enabled, a Microsoft 365 Copilot licence for the test user,
///    and a public-client Entra app ("Allow public client flows" = Yes) with the delegated WorkIQAgent.Ask permission
///    and admin consent. Set BOTH:
///     M365_CONFIG_PATH=&lt;path to a JSON file: tenantId/appClientId/[endpoint]/[agentId]/[agentName]/…&gt;
///     M365_I_UNDERSTAND_LIVE_SIDE_EFFECTS=true
///    The first run prints a device-code sign-in prompt; later runs reuse the encrypted token cache.
/// ⚠️ The permission covers reads AND writes of the signed-in user's Microsoft 365 data, and a prompt can trigger
///    real actions that cannot be sandboxed — confirm the config points at a non-prod identity first.
/// ⏱️ Time to understand: 1 minute
/// </summary>
public static class Microsoft365HelloWorld
{
    private const string Prompt = "In one sentence, what can you help me with?";

    /// <summary>Runs the sample.</summary>
    public static async Task RunAsync()
    {
        PrintHeader();

        if (!Microsoft365SampleSupport.TryLoadConfig("this sample", out var config))
            return;

        // A run id minted up front, so the trace path printed here is exactly the file the run writes.
        var options = new WorkIqOptions { Verbose = true, MaxTurns = 2, RunId = Microsoft365Diagnostics.NewRunId() };
        var paths = Microsoft365Diagnostics.Describe(options);
        Console.WriteLine($"   Agent:  {config.DisplayName}");
        Microsoft365SampleSupport.PrintDiagnosticsPaths(paths);
        Console.WriteLine("   Verbose: every trace event is also written to stderr; the first run prints a device-code");
        Console.WriteLine("   sign-in prompt there. Nothing is sent until the prompt below.\n");

        await using var agent = Microsoft365AgentFactory.BuildLive(config, iUnderstandLiveSideEffects: true, options: options);

        Console.WriteLine($"   Prompt: \"{Prompt}\"\n");
        AgentResponse reply;
        try
        {
            reply = await agent.InvokeAsync(Prompt);
        }
        catch (WorkIqTurnNotCompletedException ex)
        {
            // The typed result is on the exception: outcome, ids and a safe failure summary — never partial text.
            Microsoft365SampleSupport.PrintNotCompleted(ex.TurnResult);
            Microsoft365SampleSupport.PrintWhereToLook(paths, agent.Diagnostics);
            SampleOutcome.Fail($"turn not completed: {ex.TurnResult.Outcome}");
            return;
        }
        catch (WorkIqTurnBudgetExceededException ex)
        {
            Console.WriteLine($"   ❌ {ex.Message}");
            SampleOutcome.Error($"the turn budget stopped the run: {ex.Message}");
            return;
        }
        catch (WorkIqProtocolException ex)
        {
            Console.WriteLine($"   ❌ {ex.Message}");
            Microsoft365SampleSupport.PrintWhereToLook(paths, agent.Diagnostics);
            SampleOutcome.Error($"the Work IQ transport failed before a turn result existed: {ex.Failure.Category} during {ex.Failure.Operation}");
            return;
        }
        catch (Exception)
        {
            // Anything else is reported by the runner (exit 3); say where the run's own diagnostics are first.
            Microsoft365SampleSupport.PrintWhereToLook(paths, agent.Diagnostics);
            throw;
        }

        Console.WriteLine($"   Reply: \"{Microsoft365SampleSupport.Trunc(reply.Text)}\"");
        Check(() => reply.Text.Should().NotBeEmpty(), "NotBeEmpty()");

        // What the tenant emitted, per channel — printed, never asserted: availability is a fact about this run.
        Console.WriteLine($"   Evidence (printed, not asserted): {Microsoft365SampleSupport.EvidenceLine(reply.Evidence())}");

        Console.WriteLine();
        Microsoft365SampleSupport.PrintHonestyStatement(options.MaxTurns, options.ProfileOverride ?? config.ResolveProfile());

        Console.WriteLine("\n   -> Next: sample R2 (Live Walkthrough) shows every evidence channel, fresh vs continued context,");
        Console.WriteLine("      a non-completed turn handled honestly, and the turn budget.");
        Console.WriteLine("\n=== Microsoft 365 Hello World Complete ===");
    }

    private static void Check(Action assertion, string label)
    {
        try
        {
            assertion();
            Console.WriteLine($"   [PASS] {label}");
        }
        catch (AgentEvalAssertionException ex)
        {
            Console.WriteLine($"   [FAIL] {label}");
            Console.WriteLine($"          {ex.Message.Split('\n')[0]}");
            SampleOutcome.Fail($"{label}: {ex.Message.Split('\n')[0]}");
        }
    }

    private static void PrintHeader()
    {
        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine(@"
╔═══════════════════════════════════════════════════════════════════════════════╗
║   🏢 MICROSOFT 365 — HELLO WORLD                                               ║
║   Build the live provider, ONE prompt, ONE assertion, ONE evidence line        ║
╚═══════════════════════════════════════════════════════════════════════════════╝");
        Console.ResetColor();
    }
}

/// <summary>
/// What the two Microsoft 365 samples share: the setup check, the diagnostics paths, the one-line evidence summary,
/// how a non-completed turn and a failure are reported (which stage, why, what to do next), and the closing statement
/// of what the provider observes and what it does not.
/// </summary>
internal static class Microsoft365SampleSupport
{
    /// <summary>The variable naming the provider's JSON config file.</summary>
    public const string ConfigPathVariable = "M365_CONFIG_PATH";

    /// <summary>The variable that must be exactly <c>true</c>: consent to drive a live agent as the signed-in user.</summary>
    public const string ConsentVariable = "M365_I_UNDERSTAND_LIVE_SIDE_EFFECTS";

    /// <summary>The variable that must be exactly <c>true</c> for the walkthrough's action-shaped prompt scene.</summary>
    public const string ActionPromptVariable = "M365_ALLOW_ACTION_PROMPT";

    private const string TroubleshootingDoc = "docs/microsoft365.md#troubleshooting";

    /// <summary>
    /// Reads both setup variables and loads the config. Records NOT MEASURED (exit 11) and prints which variable is
    /// missing when either is absent; records ERROR (exit 3) with the loader's field-scoped message when the file does
    /// not load. Never falls back to a mock.
    /// </summary>
    /// <param name="what">How the warning names the caller ("this sample", "this walkthrough").</param>
    /// <param name="config">The loaded, validated config.</param>
    public static bool TryLoadConfig(string what, [NotNullWhen(true)] out Microsoft365Config? config)
    {
        config = null;
        var configPath = Environment.GetEnvironmentVariable(ConfigPathVariable);
        var consent = Environment.GetEnvironmentVariable(ConsentVariable);
        if (string.IsNullOrWhiteSpace(configPath) || consent != "true")
        {
            PrintMissingSetupWarning(what, configPath, consent);
            SampleOutcome.Skip($"no live Microsoft 365 agent: set {ConfigPathVariable} and {ConsentVariable}=true");
            return false;
        }

        try
        {
            config = Microsoft365Config.Load(new FileInfo(configPath));
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            // InvalidOperationException: the loader's file- and field-scoped messages; ArgumentException: a path the
            // file system refuses before the loader runs.
            Console.WriteLine($"   ❌ Config error: {ex.Message}");
            Console.WriteLine("      Fix the named field (docs/microsoft365.md → Authoring the config JSON). Nothing was sent.");
            SampleOutcome.Error($"the Microsoft 365 config could not be loaded: {ex.Message}");
            return false;
        }
    }

    /// <summary>Prints where the run's JSONL trace and issue register are written.</summary>
    /// <param name="paths">The resolved paths.</param>
    public static void PrintDiagnosticsPaths(Microsoft365DiagnosticsPaths paths)
    {
        Console.WriteLine($"   Trace:  {paths.TraceFile.FullName}");
        Console.WriteLine($"   Issues: {paths.IssueFile.FullName}  (one line per problem: stage, codes, suggested cause)");
    }

    /// <summary>
    /// One line of evidence availability: citation markers and sources and tool executions, each described by
    /// <see cref="EvidenceSet{T}.Describe"/>. A channel that is not provided prints its availability, never a zero; an
    /// observed-empty channel says so (zero rows in this snapshot, not proof the service did nothing).
    /// </summary>
    /// <param name="evidence">The reply's evidence document, or null when the reply carried none.</param>
    public static string EvidenceLine(Microsoft365TurnEvidence? evidence) =>
        evidence is null
            ? "no evidence document on the reply"
            : $"citation markers {evidence.CitationMarkers.Describe("marker(s)")} · " +
              $"citation sources {evidence.CitationSources.Describe("source(s)")} · " +
              $"tool executions {evidence.ToolExecutions.Describe("execution(s)")}";

    /// <summary>
    /// Reports a turn that did not complete the way the provider's design requires: the outcome, the task id, the
    /// context id and the safe failure summary — no partial text — and what the outcome means for the tester.
    /// </summary>
    /// <param name="result">The non-completed turn.</param>
    public static void PrintNotCompleted(WorkIqTurnResult result)
    {
        Console.ForegroundColor = ConsoleColor.DarkYellow;
        Console.WriteLine($"   ⚠️  The turn did not complete: outcome {result.Outcome}");
        Console.ResetColor();
        Console.WriteLine($"      task id:    {result.Task?.TaskId ?? "none"}");
        Console.WriteLine($"      context id: {result.ContextId ?? "none"}");
        Console.WriteLine($"      transport:  {result.Transport} · answer: {result.AnswerCompleteness}");
        if (result.Failure is { } failure)
            Console.WriteLine($"      failure:    {FailureLine(failure)}");
        Console.WriteLine("      No text is shown: a non-completed turn is never an answer, and the input was not resent.");
        Console.WriteLine($"      What it means: {OutcomeHint(result.Outcome)}");
    }

    /// <summary>A failure as one safe line: category, certainty, operation, message, HTTP/JSON-RPC codes and the request id.</summary>
    /// <param name="failure">The failure.</param>
    public static string FailureLine(WorkIqFailure failure)
    {
        var line = $"{failure.Category} ({failure.Certainty}) during {failure.Operation}: {failure.SafeMessage}";
        if (failure.HttpStatus is { } http) line += $"; http={http}";
        if (failure.RpcErrorCode is { } rpc) line += $"; rpcError={rpc}";
        if (failure.RequestId is { } requestId) line += $"; request-id={requestId}";
        return line;
    }

    /// <summary>
    /// Where a tester looks next: the last warnings/errors of this run (stage, code, redacted message), the issue
    /// register, the full trace and the troubleshooting table.
    /// </summary>
    /// <param name="paths">The run's diagnostics paths.</param>
    /// <param name="diagnostics">The run's diagnostics (its in-memory recent events and issue count).</param>
    public static void PrintWhereToLook(Microsoft365DiagnosticsPaths paths, WorkIqDiagnostics diagnostics)
    {
        var problems = diagnostics.RecentEvents.Where(e => e.Level != WorkIqTraceLevel.Info).TakeLast(3).ToList();
        if (problems.Count > 0)
        {
            Console.WriteLine("      Where it stopped (the last warnings/errors of this run):");
            foreach (var e in problems)
                Console.WriteLine($"        [{e.Stage}] {e.Code}: {Trunc(e.Message, 160)}");
        }

        Console.WriteLine($"      {diagnostics.IssueCount} issue(s) registered → {paths.IssueFile.FullName}");
        Console.WriteLine("        (each names the stage that failed, the HTTP / JSON-RPC / AADSTS codes and a suggested cause)");
        Console.WriteLine($"      Full trace → {paths.TraceFile.FullName}");
        Console.WriteLine($"      Symptom → cause → fix table: {TroubleshootingDoc}");
    }

    /// <summary>What the provider observes and what it does not, printed at the end of a run.</summary>
    /// <param name="maxTurns">The turn cap in effect (0 = none).</param>
    /// <param name="profile">The diagnostic profile in effect.</param>
    public static void PrintHonestyStatement(int maxTurns, DiagnosticProfile profile)
    {
        Console.WriteLine("   ℹ️  This sample observes the agent's final answer and the evidence the tenant emits; tool and");
        Console.WriteLine("      retrieval evidence is reported as NotRequested / NotProvided / Provided per channel, never inferred.");
        Console.WriteLine("      A reply is proof of what the agent SAID, never of what it DID in the tenant.");
        if (maxTurns > 0)
            Console.WriteLine($"      MaxTurns {maxTurns}: turns counted, consumption not metered.");
        if (profile == DiagnosticProfile.Native118Diagnostics)
            Console.WriteLine("      Profile Native118Diagnostics: experimental, observed in the native evaluator 1.18.0, not a documented contract.");
    }

    /// <summary>Shortens a text for one console line; a missing text prints <c>(none)</c>.</summary>
    /// <param name="s">The text.</param>
    /// <param name="max">The longest printed length.</param>
    public static string Trunc(string? s, int max = 80) =>
        string.IsNullOrEmpty(s) ? "(none)" : s.Length <= max ? s : s[..max] + "…";

    private static string OutcomeHint(WorkIqTurnOutcome outcome) => outcome switch
    {
        WorkIqTurnOutcome.InputRequired =>
            "the agent paused for a confirmation or for more input. Nothing was approved; use a read-only prompt.",
        WorkIqTurnOutcome.AuthRequired =>
            "the agent needs an extra authorization this provider cannot complete; complete it in the tenant or change the prompt.",
        WorkIqTurnOutcome.Failed =>
            "the service ended the task as failed, or answered the request with an error; read the failure line and the issue register.",
        WorkIqTurnOutcome.Rejected =>
            "the service rejected the task; read the failure line and the issue register.",
        WorkIqTurnOutcome.Canceled =>
            "the service confirmed the task was canceled.",
        WorkIqTurnOutcome.Indeterminate =>
            "the remote state is unknown (a timeout or a dropped connection). The request may have been accepted, so it was not resent; look for a task id in the trace.",
        _ => "see the issue register.",
    };

    private static void PrintMissingSetupWarning(string what, string? configPath, string? consent)
    {
        Console.ForegroundColor = ConsoleColor.DarkYellow;
        Console.WriteLine($"   ⚠️  Skipped — {what} drives a REAL, LIVE Microsoft 365 agent through Work IQ. Set BOTH:");
        Console.WriteLine($"     {ConfigPathVariable}=<path to a JSON file with tenantId/appClientId[/endpoint/agentId/agentName]>" +
                          (string.IsNullOrWhiteSpace(configPath) ? "   ← not set" : ""));
        Console.WriteLine($"     {ConsentVariable}=true" + (consent == "true" ? "" : "   ← not 'true'"));
        Console.WriteLine("   Template: samples/datasets/m365-config.template.json · setup: docs/microsoft365.md → Prerequisites.");
        Console.WriteLine("   Confirm the config points at a NON-PROD identity first: the delegated WorkIQAgent.Ask permission");
        Console.WriteLine("   covers reads AND writes of that user's Microsoft 365 data, and actions cannot be sandboxed.");
        Console.ResetColor();
    }
}
