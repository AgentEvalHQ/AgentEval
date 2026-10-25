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

        Config.PrintTarget();
        Console.WriteLine();

        Console.WriteLine("  Creating TravelAgent...\n");
        var agent = TravelAgentFactory.Create();

        Console.WriteLine("  ⏳ Running full-service agent — this may take ~30 seconds...\n");
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("  ─────────────────────────────────────────────────────────────────────────");
        Console.ResetColor();

        var request = TravelDemoPrompt.Canonical;

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
            // Azure SDK (legacy) — an error shape of the Azure-protocol providers (Azure OpenAI, Foundry).
            PrintProviderFailure(azureEx.Status, azureEx.ErrorCode, azureEx);
            return;
        }
        catch (ClientResultException clientEx)
        {
            // System.ClientModel — what both AzureOpenAIClient (GA SDK) and OpenAIClient throw on an HTTP error.
            PrintProviderFailure(clientEx.Status, errorCode: null, clientEx);
            return;
        }
        catch (TaskCanceledException timeoutEx)
        {
            PrintGenericFailure(
                kind: "Timeout",
                hint: $"{Config.HostLabel} did not respond inside the SDK's HTTP timeout. " +
                      "This often points to throttling, a slow model turn, or a long content-filter check.",
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

        // Diagnostic trace — useful when the provider cuts a run short, the agent stops
        // mid-flow, or tools error out. Prints which tools fired (in order) and
        // a preview of each result so you can pinpoint where the chain broke.
        PrintToolTrace(response, elapsed);

        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine("\n  Agent response:\n");
        Console.WriteLine(response.Text ?? "(empty)");
        Console.ResetColor();

        // Suspicious-output heuristic: a real "Tokyo + Cologne" trip summary is
        // multi-paragraph (flights + hotels + costs). A short response is almost
        // always the provider cutting the run short — content filter, rate limit, or
        // a tool-call loop that exited early.
        if (string.IsNullOrWhiteSpace(response.Text) || response.Text.Length < 200)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"\n  ⚠  Suspicious: agent response is only {response.Text?.Length ?? 0} characters.");
            Console.WriteLine("     A complete TravelAgent trip summary is normally 1–2 kB. Short responses");
            Console.WriteLine("     typically mean: (a) a provider content filter trimmed the output, (b) the");
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

    private static void PrintProviderFailure(int status, string? errorCode, Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        // The host and model names come off the SDK's message unless --show-model; the error itself stays.
        Console.WriteLine($"\n  ❌ Model request failed (HTTP {status}){(Config.ShowModel ? $" — {Config.ProviderName}" : "")}");
        if (!string.IsNullOrEmpty(errorCode))
            Console.WriteLine($"     ErrorCode: {Config.Redact(errorCode)}");
        Console.WriteLine($"     Message:   {Config.Redact(ex)}");

        // Targeted hints for the most common provider-side failure modes — these
        // are the ones that "look like" tool tampering from the demo's POV. The
        // variable names identify the provider, so they appear only with --show-model.
        var check = Config.ShowModel
            ? null
            : "Check your model configuration (run with --show-model for the variable names).";
        var hint = status switch
        {
            400 => "Bad request. If the message mentions a content filter, a content-safety policy trimmed the " +
                   "output mid-tool-call. Also check the model supports tool calling.",
            401 or 403 => check is null
                ? $"Auth / quota. Re-check {Config.ApiKeyVariable} and that the model has function-calling enabled."
                : $"Auth / quota. {check}",
            404 => check is null
                ? $"Model not found. Verify {Config.ModelVariable} (or --model) names a model this endpoint serves."
                : $"Model not found. {check}",
            408 => $"{Config.HostLabel} timed out reading the request.",
            429 => "Rate-limited. The demo will succeed on retry once the bucket refills.",
            >= 500 => $"{Config.HostLabel} returned a server error. Usually transient — retry. Persistent 5xx → contact the provider's support.",
            _ => null,
        };
        if (hint is not null)
            Console.WriteLine($"     Hint:      {hint}");

        if (!string.IsNullOrEmpty(ex.StackTrace))
            Console.WriteLine($"\n     Stack trace:\n{Config.Redact(ex.StackTrace)}");
        Console.ResetColor();
    }

    private static void PrintGenericFailure(string kind, string hint, Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"\n  ❌ Agent run failed — {kind}");
        // Message, then every distinct inner exception (an AggregateException's too), redacted.
        Console.WriteLine($"     Message:  {Config.Redact(ex)}");
        Console.WriteLine($"     Hint:     {hint}");
        Console.WriteLine($"\n     Stack trace:\n{Config.Redact(ex.StackTrace)}");
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

    private static void PrintMissingCredentials() => Config.PrintMissingProvider("Demo 01");
}
