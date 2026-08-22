// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Globalization;
using System.Text.Json;
using AgentEval.MAF.Gatekeeper;

namespace AgentEval.PartnerDeskDemo.Demo;

/// <summary>One tool call the model asked for, captured from the model's own response before any gate saw it.</summary>
/// <param name="Turn">The zero-based model turn that proposed it.</param>
/// <param name="ToolName">The requested tool.</param>
/// <param name="Arguments">The arguments as the model supplied them.</param>
public sealed record ProposedCall(int Turn, string ToolName, IReadOnlyDictionary<string, object?> Arguments)
{
    /// <summary>Reads a string argument, or <see langword="null"/> when absent, null, or blank.</summary>
    public string? String(string name) =>
        Arguments.TryGetValue(name, out var value) && value is not null
            ? Normalize(value) is { Length: > 0 } text ? text : null
            : null;

    /// <summary>Reads an integer argument, or <see langword="null"/> when absent or not a number.</summary>
    public int? Int(string name)
    {
        if (!Arguments.TryGetValue(name, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            int i => i,
            long l => ClampToInt(l),
            double d when double.IsFinite(d) => ClampToInt(d),
            JsonElement { ValueKind: JsonValueKind.Number } element when element.TryGetInt32(out var parsed) => parsed,
            // A model may emit an integer-valued float (e.g. 100.0); read it as its (clamped) integer value.
            JsonElement { ValueKind: JsonValueKind.Number } element when element.TryGetDouble(out var d2) && double.IsFinite(d2) => ClampToInt(d2),
            _ => int.TryParse(Normalize(value), NumberStyles.Integer, CultureInfo.InvariantCulture, out var text)
                ? text
                : double.TryParse(Normalize(value), NumberStyles.Float, CultureInfo.InvariantCulture, out var dbl) && double.IsFinite(dbl)
                    ? ClampToInt(dbl)
                    // NaN / Infinity / unparseable: not a readable number, so return null. Downstream this
                    // fails closed (treated as an over-limit attempt), matching the enforcing gate.
                    : null,
        };
    }

    // Saturating conversion so an out-of-range finite value (e.g. 1e30) can never silently wrap to a small or
    // negative int that would defeat an "> max" check downstream. Callers pass only finite values.
    private static int ClampToInt(double value) => (int)Math.Clamp(value, int.MinValue, int.MaxValue);

    private static int ClampToInt(long value) => (int)Math.Clamp(value, int.MinValue, int.MaxValue);

    /// <summary>True when this call carries the named argument at all (even if its value is null or unparseable).</summary>
    public bool Has(string name) => Arguments.ContainsKey(name);

    /// <summary>True when this call carries the named argument with a non-null value (C# null or JSON null count as absent).</summary>
    public bool HasValue(string name) =>
        Arguments.TryGetValue(name, out var value)
        && value is not null
        && value is not JsonElement { ValueKind: JsonValueKind.Null };

    /// <summary>A compact <c>name=value</c> rendering for the console.</summary>
    public string Render()
    {
        if (Arguments.Count == 0)
        {
            return string.Empty;
        }

        return string.Join(", ", Arguments.Select(pair =>
        {
            var text = pair.Value is null ? "null" : Normalize(pair.Value);
            if (text.Length > 34)
            {
                text = text[..33] + "…";
            }

            return pair.Value is null or bool || (pair.Value is JsonElement e && e.ValueKind == JsonValueKind.Number)
                || pair.Value is int or long or double
                ? $"{pair.Key}={text}"
                : $"{pair.Key}=\"{text}\"";
        }));
    }

    private static string Normalize(object value) => value switch
    {
        string s => s,
        JsonElement { ValueKind: JsonValueKind.String } element => element.GetString() ?? string.Empty,
        JsonElement element => element.ToString(),
        _ => value.ToString() ?? string.Empty,
    };
}

/// <summary>One enforced Gatekeeper finding, as Gatekeeper recorded it.</summary>
/// <param name="Stage">The seam: <c>tool</c> for a pre-execution block, <c>tool-result</c> for result admission.</param>
/// <param name="Policy">The policy name that acted.</param>
/// <param name="Action">The action enforcement applied (<c>Block</c>, <c>Redact</c>, …).</param>
/// <param name="ToolName">The tool involved, when the evidence carries one.</param>
/// <param name="Reason">The operator-visible reason.</param>
public sealed record GateFinding(string Stage, string Policy, string Action, string? ToolName, string? Reason)
{
    /// <summary>True when this finding stopped a proposed call before the tool executed.</summary>
    public bool IsPreExecutionBlock =>
        Action.Equals("Block", StringComparison.Ordinal) && Stage.Equals("tool", StringComparison.Ordinal);

    /// <summary>True when this finding withheld an executed tool's result from model context.</summary>
    public bool IsResultBlock =>
        Action.Equals("Block", StringComparison.Ordinal) && Stage.Equals("tool-result", StringComparison.Ordinal);
}

/// <summary>
/// The demo's trajectory recorder. It answers the two questions the phases are argued from: what did the agent
/// <b>try</b> to do, and what did Gatekeeper <b>do about it</b>.
/// </summary>
/// <remarks>
/// Proposals come from the model's own response, upstream of every gate, so a call refused before execution is
/// still recorded as an attempt — that evidence is the entire point of the Level 1 phase. Findings come from
/// Gatekeeper's evidence sink, which fires only on actions enforcement actually applied.
/// </remarks>
public sealed class ToolCallJournal : IGatekeeperObserver
{
    private readonly Lock _sync = new();
    private readonly List<ProposedCall> _proposals = [];
    private readonly List<GateFinding> _findings = [];
    private readonly DemoOutput _output;
    private int _turn = -1;

    /// <summary>Creates a journal that echoes each proposal and finding to <paramref name="output"/>.</summary>
    public ToolCallJournal(DemoOutput output) => _output = output;

    /// <summary>Every tool call the model asked for, in order.</summary>
    public IReadOnlyList<ProposedCall> Proposals
    {
        get { lock (_sync) { return _proposals.ToArray(); } }
    }

    /// <summary>Every enforced Gatekeeper finding, in order.</summary>
    public IReadOnlyList<GateFinding> Findings
    {
        get { lock (_sync) { return _findings.ToArray(); } }
    }

    /// <summary>Starts a new model turn, so proposals can be attributed to the turn that produced them.</summary>
    public int BeginTurn()
    {
        lock (_sync)
        {
            return ++_turn;
        }
    }

    /// <summary>Records one proposed call and prints it.</summary>
    public void RecordProposal(int turn, string toolName, IReadOnlyDictionary<string, object?> arguments)
    {
        var call = new ProposedCall(turn, toolName, arguments);
        lock (_sync)
        {
            _proposals.Add(call);
        }

        _output.ToolProposed(toolName, call.Render());
    }

    /// <inheritdoc />
    void IGatekeeperObserver.OnFinding(GateEvidence evidence)
    {
        var finding = new GateFinding(
            evidence.Stage,
            evidence.Policy,
            evidence.Action,
            evidence.ToolName,
            evidence.Reason);

        lock (_sync)
        {
            _findings.Add(finding);
        }

        var tool = finding.ToolName ?? "(unnamed tool)";
        if (finding.IsResultBlock)
        {
            _output.ResultRefused(tool, finding.Policy, finding.Reason);
        }
        else if (finding.Action.Equals("Block", StringComparison.Ordinal))
        {
            _output.ToolRefused(tool, finding.Policy, finding.Reason);
        }
        else
        {
            _output.ToolRefused(tool, $"{finding.Policy} ({finding.Action})", finding.Reason);
        }
    }
}
