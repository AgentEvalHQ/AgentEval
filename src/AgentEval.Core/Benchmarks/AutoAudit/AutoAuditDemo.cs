// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Guardrails;
using AgentEval.Guardrails.Gates;
using AgentEval.Testing;
using AgentEval.Tracing;
using Microsoft.Extensions.AI;

using ChatRole = Microsoft.Extensions.AI.ChatRole;

namespace AgentEval.Benchmarks;

/// <summary>
/// Offline, credential-free showcase for the Glass Box auto-audit: three scripted "endpoints" with
/// deliberately different behaviour (clean / silent-retry / PII+content-filter), each driven through the
/// Glass Box stack (chat-boundary recording + PII post-gate) and reconciled via Trace Fidelity, then ranked.
/// Shared by the <c>bench autoaudit</c> CLI and the Observability sample so the demo lives in one place.
/// </summary>
public static class AutoAuditDemo
{
    /// <summary>Builds the offline cross-endpoint comparison report (deterministic; no LLM calls).</summary>
    public static async Task<AutoAuditReport> BuildOfflineReportAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<AutoAuditEndpointResult>
        {
            await EvaluateScriptedAsync("Azure-GPT-4o-mini", retry: false, leak: false, cancellationToken),
            await EvaluateScriptedAsync("Ollama-Llama3.1", retry: true, leak: false, cancellationToken),
            await EvaluateScriptedAsync("DeepSeek-V3", retry: false, leak: true, cancellationToken),
        };
        return AutoAuditRunner.Compare(results);
    }

    private static async Task<AutoAuditEndpointResult> EvaluateScriptedAsync(
        string endpoint, bool retry, bool leak, CancellationToken cancellationToken)
    {
        var scripted = new ScriptedChatClient();
        scripted.AddToolCall("c1", "Lookup", new Dictionary<string, object?>());
        if (retry)
        {
            scripted.AddToolCall("c1", "Lookup", new Dictionary<string, object?>());   // silent retry
        }

        if (leak)
        {
            scripted.Add(new ScriptedTurn
            {
                Text = "Done. The customer SSN is 123-45-6789.",
                FinishReason = ChatFinishReason.ContentFilter,
                InputTokens = 50,
                OutputTokens = 20,
            });
        }
        else
        {
            scripted.AddText("Done.", inTok: 30, outTok: 10);
        }

        // Chat-boundary recording + a PII post-gate (Redact). Recorder is outer of the gate so it records
        // the redacted response; the gate records its Block verdict into the same trace's Metadata.
        var chatTrace = new AgentTrace { AgentName = endpoint };
        var pipeline = scripted
            .AsBuilder()
            .UseTraceRecording(endpoint, chatTrace, SamplePreset.AuditGrade)
            .UseEvalGate(post: new IChatGate[] { new RegexPiiGate() }, policy: EvalGatePolicy.Redact, trace: chatTrace)
            .Build();

        var completed = true;
        var turns = retry ? 3 : 2;
        try
        {
            for (var i = 0; i < turns; i++)
            {
                await pipeline.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "look up the record") }, cancellationToken: cancellationToken);
            }
        }
        catch
        {
            completed = false;
        }

        // The framework's agent-boundary "account": one Lookup, a clean finish — what a black-box recorder sees.
        // Report matching token totals so this demo isolates the tool/finish discrepancies (not token drift).
        var chatResponses = chatTrace.Entries
            .Where(e => e.EffectiveScope == TraceEntryScope.ChatTurn && e.Type == TraceEntryType.Response)
            .ToList();
        var agentBoundary = new AgentTrace
        {
            AgentName = endpoint,
            Performance = new TracePerformance
            {
                TotalPromptTokens = chatResponses.Sum(r => r.TokenUsage?.PromptTokens ?? 0),
                TotalCompletionTokens = chatResponses.Sum(r => r.TokenUsage?.CompletionTokens ?? 0),
            },
        };
        agentBoundary.Entries.Add(new TraceEntry
        {
            Type = TraceEntryType.Response,
            Index = 0,
            // Args match the chat-boundary's serialized empty-dict form ("{}") so this demo isolates the
            // tool-count / finish-reason discrepancies (no spurious argument_drift).
            ToolCalls = new List<TraceToolCall> { new() { Name = "Lookup", Arguments = "{}" } },
            FinishReason = "stop",
        });

        return AutoAuditRunner.Evaluate(endpoint, agentBoundary, chatTrace, completed);
    }
}
