// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Evals;
using STJ = global::System.Text.Json;

namespace AgentEval.Evals.Agentic.Process;

/// <summary>
/// Evaluates whether all tool calls made by the agent executed successfully.
/// <para>
/// This evaluator follows a <strong>deterministic-first</strong> design (per plan-05 §8.4
/// and findings-and-suggestions cross-cutting #7): when the <see cref="EvalInput.ToolCalls"/>
/// contain structured <c>status</c> / <c>error</c> fields, the result is derived
/// deterministically — no LLM call is made. Only when status fields are absent does it
/// fall back to an inner <see cref="AtomicLlmEval"/> that interprets free-text results.
/// </para>
/// <para>
/// Deterministic path:
/// <list type="bullet">
///   <item>All calls have <c>status: "success"</c> (case-insensitive) → score 1.0, severity none.</item>
///   <item>Any call has <c>status: "error"</c> or non-null <c>error</c> → score 0.0, severity high.</item>
/// </list>
/// </para>
/// <para>
/// Source (LLM fallback): forked from Azure/azure-sdk-for-python
/// sdk/evaluation/azure-ai-evaluation/azure/ai/evaluation/_evaluators/_tool_call_success/tool_call_success.prompty
/// License: MIT. Modifications listed in the corresponding prompt file at
/// Resources/Prompts/process/tool-call-success.v1.md.
/// </para>
/// <para>
/// Foundry reference: <c>azureai://built-in/evaluators/tool_call_success</c>
/// </para>
/// </summary>
public sealed class ToolCallSuccessEval : IEval
{
    private const string KeyValue      = "tool_call_success";
    private const string NameValue     = "Tool Call Success";
    private const string CategoryValue = "agentic-process";
    private const string VersionValue  = "1.0.0";

    /// <summary>
    /// Conventional key for supplying per-call status records via <see cref="EvalInput.Metadata"/>.
    /// The value must be a JSON array of objects with shape <c>{name, status, error?}</c>.
    /// </summary>
    public const string MetadataStatusKey = "tool_call_statuses";

    private readonly AtomicLlmEval _llmFallback;

    /// <inheritdoc/>
    public string Key      => KeyValue;

    /// <inheritdoc/>
    public string Name     => NameValue;

    /// <inheritdoc/>
    public string Category => CategoryValue;

    /// <inheritdoc/>
    public string Version  => VersionValue;

    /// <summary>
    /// Initialises a new <see cref="ToolCallSuccessEval"/>.
    /// </summary>
    /// <param name="judge">
    /// The LLM evaluator used as a fallback when <see cref="EvalInput.ToolCalls"/> do not
    /// contain structured status fields.
    /// </param>
    /// <param name="judgeModel">Optional judge model identifier recorded in provenance.</param>
    public ToolCallSuccessEval(IEvaluator judge, string? judgeModel = null)
    {
        ArgumentNullException.ThrowIfNull(judge);
        _llmFallback = new AtomicLlmEval(
            evaluator: judge,
            key: KeyValue,
            name: NameValue,
            category: CategoryValue,
            version: VersionValue,
            criteria: new[]
            {
                "All tool calls completed without errors, exceptions, or access-denied responses",
                "No tool result contains an error message, timeout, or HTTP error code (4xx/5xx)",
                "Empty or null tool results are explained by the agent (e.g. 'no data found') rather than silently ignored",
            },
            passThreshold: 0.70,
            judgeModel: judgeModel,
            promptId: "agenteval.tool_call_success.v1",
            failureSeverity: "high");
    }

    /// <inheritdoc/>
    public async Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        // ── 1. Try structured status via Metadata["tool_call_statuses"] ──────────
        if (TryReadStatusFromMetadata(input, out var metaResult))
            return metaResult!;

        // ── 2. Try status fields embedded in ToolCall.Result (structured JSON) ──
        if (TryReadStatusFromToolCalls(input, out var tcResult))
            return tcResult!;

        // ── 3. LLM fallback — no structured status available ─────────────────────
        return await _llmFallback.EvaluateAsync(input, ct);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Deterministic helpers
    // ─────────────────────────────────────────────────────────────────────────────

    private static bool TryReadStatusFromMetadata(EvalInput input, out EvalResult? result)
    {
        result = null;

        if (input.Metadata is null || !input.Metadata.TryGetValue(MetadataStatusKey, out var raw))
            return false;

        var statusRecords = ParseStatusRecords(raw);
        if (statusRecords.Count == 0)
            return false;

        return BuildFromStatusRecords(statusRecords, out result);
    }

    private static bool TryReadStatusFromToolCalls(EvalInput input, out EvalResult? result)
    {
        result = null;

        if (input.ToolCalls is null or { Count: 0 })
            return false;

        var statuses = new List<ToolCallStatus>();
        foreach (var tc in input.ToolCalls)
        {
            if (tc.Result is null)
                return false; // At least one call has no result — cannot determine deterministically.

            if (!TryParseStatusFromResult(tc.Result, out var status, out var error))
                return false; // Result is not structured JSON with a status field.

            statuses.Add(new ToolCallStatus(tc.Name, status!, error));
        }

        return statuses.Count > 0 && BuildFromStatusRecords(statuses, out result);
    }

    private static bool BuildFromStatusRecords(
        IReadOnlyList<ToolCallStatus> records,
        out EvalResult? result)
    {
        result = null;

        var failed = records
            .Where(r => !IsSuccess(r.Status))
            .ToList();

        if (failed.Count == 0)
        {
            result = BuildDeterministic(1.0, true, "none",
                evidence: new[]
                {
                    new EvalEvidence(
                        Source: "tool_call",
                        Reference: "all calls",
                        Message: $"All {records.Count} tool call(s) reported status: success.")
                });
            return true;
        }

        var evidence = failed
            .Select(f => new EvalEvidence(
                Source: "tool_call",
                Reference: f.Name,
                Message: string.IsNullOrEmpty(f.Error)
                    ? $"Tool '{f.Name}' reported status: {f.Status}."
                    : $"Tool '{f.Name}' reported status: {f.Status} — error: {f.Error}"))
            .ToArray();

        result = BuildDeterministic(0.0, false, "high", evidence);
        return true;
    }

    private static EvalResult BuildDeterministic(
        double score,
        bool passed,
        string severity,
        IReadOnlyList<EvalEvidence>? evidence)
    {
        var label = passed ? "pass" : "fail";
        return new EvalResult(
            Metric: new(KeyValue, NameValue, CategoryValue, VersionValue),
            Score: new(score, null, label, passed, 0.70, severity, null),
            Details: new(null, evidence, null, null, null),
            Provenance: new("atomic-code", null, "agenteval.tool_call_success.v1", null, null, 0, false),
            EvaluatedAt: DateTimeOffset.UtcNow);
    }

    private static bool IsSuccess(string status) =>
        string.Equals(status, "success", StringComparison.OrdinalIgnoreCase)
     || string.Equals(status, "ok",      StringComparison.OrdinalIgnoreCase)
     || string.Equals(status, "200",     StringComparison.OrdinalIgnoreCase);

    private static bool TryParseStatusFromResult(
        string result,
        out string? status,
        out string? error)
    {
        status = null;
        error  = null;

        if (string.IsNullOrWhiteSpace(result))
            return false;

        try
        {
            var doc = STJ.JsonDocument.Parse(result);
            if (doc.RootElement.TryGetProperty("status", out var statusElem))
            {
                status = statusElem.GetString();
                if (doc.RootElement.TryGetProperty("error", out var errorElem))
                    error = errorElem.GetString();
                return status is not null;
            }
        }
        catch (STJ.JsonException)
        {
            // Result is not valid JSON — fall through to LLM fallback.
        }

        return false;
    }

    private static List<ToolCallStatus> ParseStatusRecords(object raw)
    {
        var result = new List<ToolCallStatus>();

        try
        {
            string json;
            if (raw is STJ.JsonElement je)
                json = je.GetRawText();
            else if (raw is string s)
                json = s;
            else
                return result;

            var doc = STJ.JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != STJ.JsonValueKind.Array)
                return result;

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var name   = item.TryGetProperty("name",   out var n)  ? n.GetString()  : null;
                var status = item.TryGetProperty("status", out var st) ? st.GetString() : null;
                var error  = item.TryGetProperty("error",  out var er) ? er.GetString() : null;

                if (name is not null && status is not null)
                    result.Add(new ToolCallStatus(name, status, error));
            }
        }
        catch (STJ.JsonException)
        {
            // Malformed metadata — fall through to LLM.
        }

        return result;
    }

    // Simple value-type to avoid tuple name inference issues across framework targets.
    private readonly record struct ToolCallStatus(string Name, string Status, string? Error);
}
