// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Serialization;
using AgentEval.Guardrails;
using AgentEval.MAF.Gatekeeper;

namespace AgentEval.Cli.Commands.Gatekeeper;

/// <summary>
/// The versioned <c>gatekeeper</c> verdict JSON — a frozen, versioned contract (the same discipline as <c>AgentTrace</c>)
/// so any language can consume a Gatekeeper decision. One object per <c>inspect</c>; one per line for <c>--input</c>
/// batch (JSONL). Field contract lives in <c>Schemas/gatekeeper-verdict.schema.json</c>.
/// <para><b>Security:</b> for the two sensitive judge axes (<c>exfiltration-intent</c>, <c>system-prompt-extraction</c>)
/// the offending phrase may BE the secret, so <see cref="Matches"/> AND <see cref="RedactedText"/> are forced null
/// (<see cref="RedactAxes"/>) — belt-and-suspenders over the rubric-level <c>spans:null</c>, so a secret can never be
/// persisted into a verdict, a JSONL file, or a CI log.</para>
/// </summary>
internal sealed record GateVerdictDto
{
    /// <summary>The set of axes whose evidence spans / masked text must never be emitted (round-4 security rule).</summary>
    public static readonly IReadOnlySet<string> RedactAxes =
        new HashSet<string>(StringComparer.Ordinal) { "exfiltration-intent", "system-prompt-extraction" };

    private static readonly JsonSerializerOptions Compact = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,   // preserve nulls → the schema stays fixed
        // Default (HTML-safe) encoder on purpose: a verdict's reason/matches can carry attacker-influenced text
        // (e.g. a matched keyword), and the JSON often lands in a CI log or dashboard. The relaxed encoder would
        // leave <, >, & unescaped; the default escapes them. Consumers parse the JSON, so < vs < is identical
        // to them — there is no functional need for relaxed escaping here, only added HTML-embedding risk.
    };

    private static readonly JsonSerializerOptions Pretty = new(Compact) { WriteIndented = true };

    public string SchemaVersion { get; init; } = "1.0";
    public string Gate { get; init; } = "";
    public string Kind { get; init; } = "";               // "chat" | "tool"
    public string Action { get; init; } = "";             // "Allow" | "Block" | "Mutate"
    public string Policy { get; init; } = "";
    public string? Axis { get; init; }
    public string? Reason { get; init; }
    public IReadOnlyList<string>? Matches { get; init; }
    public string? RedactedText { get; init; }
    public bool? InlineReady { get; init; }               // judge gates only; null for deterministic
    public bool Inconclusive { get; init; }
    public string? Warning { get; init; }
    public double? Confidence { get; init; }              // always null in v1 (§5.4)
    public object? Certificate { get; init; }             // object | array | null (honesty guard, slice 2)
    public object? NewArguments { get; init; }            // reserved for Mutate tool verdicts; null in v1

    /// <summary>The axis encoded in a <c>judge:&lt;axis&gt;</c> policy name, else null (a panel is <c>judge-panel</c> → null).</summary>
    public static string? AxisOf(string policyName) =>
        policyName.StartsWith("judge:", StringComparison.Ordinal) ? policyName["judge:".Length..] : null;

    /// <summary>Map a chat-gate verdict to the DTO, applying the sensitive-span redaction allowlist.</summary>
    public static GateVerdictDto FromChat(GateVerdict v, string gateId)
    {
        var axis = AxisOf(v.PolicyName);
        var redact = axis is not null && RedactAxes.Contains(axis);

        // GateAction is a closed {Allow, Block}, but map defensively: any unmapped value fails closed to Block.
        string action;
        bool inconclusive = false;
        string? warning = null;
        switch (v.Action)
        {
            case GateAction.Allow: action = "Allow"; break;
            case GateAction.Block: action = "Block"; break;
            default:
                action = "Block"; inconclusive = true;
                warning = $"unmapped gate action '{v.Action}' — failing closed";
                break;
        }

        return new GateVerdictDto
        {
            Gate = gateId,
            Kind = "chat",
            Action = action,
            Policy = v.PolicyName,
            Axis = axis,
            Reason = v.Reason,
            Matches = redact ? null : v.Matches,
            RedactedText = redact ? null : v.RedactedText,
            InlineReady = null,   // deterministic; the judge path (honesty guard) sets this
            Inconclusive = inconclusive,
            Warning = warning,
        };
    }

    /// <summary>Map a tool-gate verdict to the DTO. Tool gates are not judge axes, so no span redaction applies.</summary>
    public static GateVerdictDto FromTool(ToolGateVerdict v, string gateId)
    {
        string action;
        bool inconclusive = false;
        string? warning = null;
        switch (v.Action)
        {
            case ToolGateAction.Allow: action = "Allow"; break;
            case ToolGateAction.Block: action = "Block"; break;
            case ToolGateAction.Mutate: action = "Mutate"; break;
            default:
                action = "Block"; inconclusive = true;
                warning = $"unmapped tool gate action '{v.Action}' — failing closed";
                break;
        }

        return new GateVerdictDto
        {
            Gate = gateId,
            Kind = "tool",
            Action = action,
            Policy = v.PolicyName,
            Axis = null,
            Reason = v.Reason,
            Matches = null,
            RedactedText = null,
            InlineReady = null,
            Inconclusive = inconclusive,
            Warning = warning,
            NewArguments = v.Action == ToolGateAction.Mutate ? v.NewArguments : null,
        };
    }

    /// <summary>A fail-closed verdict the CLI emits when it cannot evaluate a gate (e.g. missing required history).</summary>
    public static GateVerdictDto FailClosed(string gateId, string kind, string policy, string warning) => new()
    {
        Gate = gateId,
        Kind = kind,
        Action = "Block",
        Policy = policy,
        Reason = warning,
        Inconclusive = true,
        Warning = warning,
    };

    /// <summary>
    /// The exit code this verdict maps to (§6): Allow/Mutate→0, fail-closed inconclusive→6, Block→5. A tool
    /// <c>Mutate</c> means the call <b>proceeds</b> with rewritten args, so it is a success (0), not a block.
    /// </summary>
    public int ExitCode() =>
        Action is "Allow" or "Mutate" ? ExitCodes.Success
        : Inconclusive ? ExitCodes.GateInconclusive
        : ExitCodes.GateBlocked;

    public string ToJson(bool indented) => JsonSerializer.Serialize(this, indented ? Pretty : Compact);
}
