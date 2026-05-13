// SPDX-License-Identifier: MIT
// Copyright (c) 2026 ECS2026 Demo

using System.ClientModel;
using Azure;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using AgentEval.TravelDemo.Agents;

namespace AgentEval.TravelDemo.Demos;

/// <summary>
/// Demo 01 — TravelAgent (single all-in-one agent)
///
/// Shows a single MAF <see cref="ChatClientAgent"/> handling an entire trip:
///   GetInfoAbout → SearchFlights → BookFlight
///                → SearchHotel  → BookHotel → SendConfirmation
///
/// Same task as Demo 02 — but handled by ONE agent instead of a pipeline.
/// Compare results to see how a workflow adds determinism.
///
/// ⏱️ Runtime: ~20–40 seconds (multiple LLM + tool round-trips)
/// </summary>
public static class Demo01_TravelAgent
{
    public static async Task RunAsync()
    {
        PrintHeader();

        if (!Config.IsConfigured)
        {
            PrintMissingCredentials();
            return;
        }

        Console.WriteLine("  Creating TravelAgent...\n");
        var agent = TravelAgentFactory.Create();

        Console.WriteLine("  ⏳ Running full-service agent — this may take ~30 seconds...\n");
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("  ─────────────────────────────────────────────────────────────────────────");
        Console.ResetColor();

        var request = "Plan a 7-day trip visiting both Tokyo and Cologne. " +
                      "I need city information, flights between them, " +
                      "and hotel bookings for each city. " +
                      "I live in Zurich, leaving from this city and returning to it. " +
                      "Please send a trip summary to traveller@example.com.";

        Console.WriteLine($"  Request: \"{request}\"\n");

        var session  = await agent.CreateSessionAsync();
        var messages = new[] { new ChatMessage(ChatRole.User, request) };

        AgentResponse? response = null;
        var startedAt = DateTimeOffset.UtcNow;
        try
        {
            response = await agent.RunAsync(messages, session);
        }
        catch (RequestFailedException azureEx)
        {
            // Azure SDK (legacy) — most common shape for AzureOpenAIClient errors.
            PrintAzureFailure(azureEx.Status, azureEx.ErrorCode, azureEx.Message, azureEx.StackTrace);
            return;
        }
        catch (ClientResultException clientEx)
        {
            // Azure SDK (System.ClientModel) — newer GA SDK; same family of failures.
            PrintAzureFailure(clientEx.Status, errorCode: null, clientEx.Message, clientEx.StackTrace);
            return;
        }
        catch (TaskCanceledException timeoutEx)
        {
            PrintGenericFailure(
                kind: "Timeout",
                hint: "Azure response did not return inside the SDK's HTTP timeout. " +
                      "This often points to Azure-side throttling or a long content-filter check on the deployment.",
                ex: timeoutEx);
            return;
        }
        catch (Exception ex)
        {
            // Catch-all: MAF agent loop, tool-execution exception, malformed tool-call etc.
            // We want diagnostics, not silent failure — so dump the stack and surface
            // the inner exception explicitly. Tools that throw bubble here.
            PrintGenericFailure(
                kind: $"{ex.GetType().Name} (agent run failed)",
                hint: "Most often a tool method threw, or the LLM returned a malformed tool call. " +
                      "Check the inner exception below for the tool name + stack trace.",
                ex: ex);
            return;
        }

        var elapsed = DateTimeOffset.UtcNow - startedAt;

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("  ─────────────────────────────────────────────────────────────────────────");
        Console.ResetColor();
        Console.WriteLine();

        // Diagnostic trace — useful when Azure cuts a run short, the agent stops
        // mid-flow, or tools error out. Prints which tools fired (in order) and
        // a preview of each result so you can pinpoint where the chain broke.
        PrintToolTrace(response, elapsed);

        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine("\n  Agent response:\n");
        Console.WriteLine(response.Text ?? "(empty)");
        Console.ResetColor();

        // Suspicious-output heuristic: a real "Tokyo + Cologne" trip summary is
        // multi-paragraph (flights + hotels + costs). A short response is almost
        // always Azure cutting the run short — content filter, rate limit, or
        // a tool-call loop that exited early.
        if (string.IsNullOrWhiteSpace(response.Text) || response.Text.Length < 200)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"\n  ⚠  Suspicious: agent response is only {response.Text?.Length ?? 0} characters.");
            Console.WriteLine("     A complete TravelAgent trip summary is normally 1–2 kB. Short responses");
            Console.WriteLine("     typically mean: (a) Azure content filter trimmed the output, (b) the");
            Console.WriteLine("     agent loop hit a tool error and bailed, or (c) the model hit the max-tokens");
            Console.WriteLine("     budget. Inspect the tool trace above for which step (if any) returned last.");
            Console.ResetColor();
        }
    }

    // ── Diagnostics ───────────────────────────────────────────────────────────

    /// <summary>
    /// Dumps every tool invocation in the response in call order, paired with
    /// the result preview. The agent loop interleaves <see cref="FunctionCallContent"/>
    /// (model says "call X with args Y") and <see cref="FunctionResultContent"/>
    /// (tool returns Z). Pairing them by <c>CallId</c> shows the full request /
    /// response chain — invaluable when the trip output is missing a leg.
    /// </summary>
    private static void PrintToolTrace(AgentResponse response, TimeSpan elapsed)
    {
        var calls = response.Messages
            .SelectMany(m => m.Contents)
            .OfType<FunctionCallContent>()
            .ToList();

        var resultsByCallId = response.Messages
            .SelectMany(m => m.Contents)
            .OfType<FunctionResultContent>()
            .GroupBy(r => r.CallId)
            .ToDictionary(g => g.Key, g => g.First());

        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine($"  📊 Tool trace — {calls.Count} call(s) in {elapsed.TotalSeconds:0.0}s");
        Console.ResetColor();

        if (calls.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("     ⚠  No tools invoked — the agent answered from prompt context only.");
            Console.WriteLine("        This usually means the LLM ignored the tool surface (instructions");
            Console.WriteLine("        unclear), the deployment doesn't support function calling, or the");
            Console.WriteLine("        request did not require external lookup.");
            Console.ResetColor();
            return;
        }

        Console.ForegroundColor = ConsoleColor.DarkGray;
        for (var i = 0; i < calls.Count; i++)
        {
            var call    = calls[i];
            var preview = resultsByCallId.TryGetValue(call.CallId, out var r)
                ? FormatResultPreview(r.Result)
                : "(no result returned — tool may have errored)";
            Console.WriteLine($"     [{i + 1,2}] {call.Name}  →  {preview}");
        }
        Console.ResetColor();
    }

    private static string FormatResultPreview(object? result)
    {
        if (result is null) return "(null)";
        var text = result.ToString() ?? "";
        // Collapse newlines + clip to keep the trace readable; full result is
        // available in the AgentResponse for the caller if needed.
        text = text.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return text.Length <= 120 ? text : text[..117] + "…";
    }

    private static void PrintAzureFailure(int status, string? errorCode, string message, string? stackTrace)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"\n  ❌ Azure OpenAI request failed (HTTP {status})");
        if (!string.IsNullOrEmpty(errorCode))
            Console.WriteLine($"     ErrorCode: {errorCode}");
        Console.WriteLine($"     Message:   {message}");

        // Targeted hints for the most common Azure-side failure modes — these
        // are the ones that "look like" tool tampering from the demo's POV.
        var hint = status switch
        {
            400 => "Bad request. If the model says 'response cut off / content filter', " +
                   "Azure's content-safety policy trimmed the output mid-tool-call. Check the deployment's filter settings.",
            401 or 403 => "Auth / quota. Re-check AZURE_OPENAI_API_KEY and that the deployment has function-calling enabled.",
            404 => "Deployment not found. Verify AZURE_OPENAI_DEPLOYMENT matches a deployment that exists in this resource.",
            408 => "Azure timed out reading the request.",
            429 => "Rate-limited. The demo will succeed on retry once the bucket refills.",
            >= 500 => "Azure-side server error. Usually transient — retry. Persistent 5xx → open Azure support ticket.",
            _ => null,
        };
        if (hint is not null)
            Console.WriteLine($"     Hint:      {hint}");

        if (!string.IsNullOrEmpty(stackTrace))
            Console.WriteLine($"\n     Stack trace:\n{stackTrace}");
        Console.ResetColor();
    }

    private static void PrintGenericFailure(string kind, string hint, Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"\n  ❌ Agent run failed — {kind}");
        Console.WriteLine($"     Message:  {ex.Message}");
        Console.WriteLine($"     Hint:     {hint}");
        if (ex.InnerException is not null)
            Console.WriteLine($"     Inner:    {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
        Console.WriteLine($"\n     Stack trace:\n{ex.StackTrace}");
        Console.ResetColor();
    }

    // ── UI helpers ────────────────────────────────────────────────────────────

    private static void PrintHeader()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(@"
╔══════════════════════════════════════════════════════════════════════════════╗
║   Demo 01 — TravelAgent (single agent)                                       ║
║   Research → Flights → Hotels → Confirmation  (same task as Demo 02)        ║
╚══════════════════════════════════════════════════════════════════════════════╝
");
        Console.ResetColor();
    }

    private static void PrintMissingCredentials()
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine(@"
  ⚠️  Skipping Demo 01 — Azure OpenAI credentials required.

     Set the following environment variables and try again:
       AZURE_OPENAI_ENDPOINT
       AZURE_OPENAI_API_KEY
       AZURE_OPENAI_DEPLOYMENT
");
        Console.ResetColor();
    }
}
