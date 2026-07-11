// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Guardrails;
using AgentEval.Guardrails.Gates;
using AgentEval.Guardrails.Judges;
using AgentEval.MAF.Gatekeeper;

namespace AgentEval.Cli.Commands.Gatekeeper;

/// <summary>The kind of subject a gate inspects.</summary>
internal enum GateKind { Chat, Tool }

/// <summary>
/// A discoverable Gatekeeper gate (rendered by <c>list-gates</c>). <see cref="StateClass"/> is the key property:
/// <c>stateless-text</c> / <c>needs-args</c> gates are callable in the stateless <c>inspect</c>; <c>needs-history</c>
/// gates recompute from a caller-passed history; <c>needs-run-state</c> gates need the <c>serve</c> daemon.
/// </summary>
internal sealed record GateDescriptor(
    string Id,
    GateKind Kind,
    string StateClass,   // stateless-text | needs-args | needs-history | needs-run-state
    bool NeedsModel,
    bool HasGoldSet,
    string SpanPolicy,   // echo | redact
    string Phase,        // inspect | serve
    string Description);

/// <summary>Parsed gate-specific flags a resolver consumes (populated by the inspect command from the parse result).</summary>
internal sealed class GateFlags
{
    public List<string> Keywords { get; } = [];
    public List<string> Forbidden { get; } = [];
    public string? Pattern { get; set; }
    public List<string> AllowedDomains { get; } = [];
    public List<string> IdArgs { get; } = [];
    public List<string> GuardedTools { get; } = [];
    public List<string> TrustedTools { get; } = [];
    public List<string> SourceTools { get; } = [];
    public List<string> SinkTools { get; } = [];
    public int? MinTaintLength { get; set; }
}

/// <summary>
/// The hand-authored Gatekeeper gate registry (no central registry exists in the codebase). Renders <c>list-gates</c>
/// and resolves an id + flags to a live gate. Slice 1 resolves the deterministic text gates and the tool/history
/// gates (no model, byte-stable — the credential-free CI core); judge gates (<c>judge:*</c>/<c>panel:*</c>) are
/// listed here but resolved on the model path.
/// </summary>
internal static class GateRegistry
{
    /// <summary>The four calibrated judge axes.</summary>
    public static readonly IReadOnlyList<string> Axes =
        ["indirect-injection", "exfiltration-intent", "system-prompt-extraction", "over-refusal"];

    public static IReadOnlyList<GateDescriptor> All { get; } = BuildDescriptors();

    public static GateDescriptor? Find(string id) => All.FirstOrDefault(d => d.Id == id);

    private static IReadOnlyList<GateDescriptor> BuildDescriptors()
    {
        var list = new List<GateDescriptor>
        {
            new("keyword-injection", GateKind.Chat, "stateless-text", false, false, "echo", "inspect",
                "Deterministic keyword oracle over the classic prompt-injection keyword list (override with --keyword/--keywords)."),
            new("keyword", GateKind.Chat, "stateless-text", false, false, "echo", "inspect",
                "Deterministic keyword oracle over a caller-supplied list (--keyword … or --keywords <file>)."),
            new("rendered-exfil", GateKind.Chat, "stateless-text", false, false, "echo", "inspect",
                "Neutralizes rendered-output exfil channels (markdown-image beacons, data: URIs, hidden HTML, zero-width); surfaces the sanitized text."),
        };

        foreach (var axis in Axes)
        {
            list.Add(new($"keyword:{axis}", GateKind.Chat, "stateless-text", false, false, "echo", "inspect",
                $"Deterministic keyword-oracle baseline for the '{axis}' axis (the detector a judge must beat)."));
        }

        foreach (var axis in Axes)
        {
            var redact = GateVerdictDto.RedactAxes.Contains(axis);
            list.Add(new($"judge:{axis}", GateKind.Chat, "stateless-text", true, true, redact ? "redact" : "echo", "inspect",
                $"Calibrated LLM judge for the '{axis}' axis (requires --model; honesty guard applies)."));
        }

        list.Add(new("panel:<a,b,…>", GateKind.Chat, "stateless-text", true, true, "per-child", "inspect",
            "A fail-closed-OR fan-out over the comma-listed child gates (per-child span redaction)."));

        list.Add(new("tool:forbidden-tool", GateKind.Tool, "needs-args", false, false, "echo", "inspect",
            "Blocks a call to any --forbidden tool name."));
        list.Add(new("tool:argument-pattern", GateKind.Tool, "needs-args", false, false, "echo", "inspect",
            "Blocks when a tool argument matches the --pattern regex (bounded)."));
        list.Add(new("tool:domain-allowlist", GateKind.Tool, "needs-args", false, false, "echo", "inspect",
            "Blocks a URL argument whose host is not in --allowed-domain (exfil egress)."));
        list.Add(new("tool:referential-integrity", GateKind.Tool, "needs-history", false, false, "echo", "inspect",
            "Blocks a guarded call referencing an --id-arg value never surfaced by the user or a --trusted-tool (needs messages)."));
        list.Add(new("tool:taint-tracking", GateKind.Tool, "needs-history", false, false, "echo", "inspect",
            "Blocks a --source-tool value reaching a --sink-tool argument (needs messages)."));

        list.Add(new("tool:run-budget", GateKind.Tool, "needs-run-state", false, false, "echo", "serve",
            "Per-run token/$/call budget — needs held run state; available via 'serve', not stateless inspect."));

        return list;
    }

    /// <summary>Resolve a deterministic (no-model) chat gate. Judge/panel gates return null (model path — later slice).</summary>
    public static IChatGate? TryResolveChatGate(string id, GateFlags flags, out string? error)
    {
        error = null;
        switch (id)
        {
            case "keyword-injection":
                return new KeywordOracleGate(flags.Keywords.Count > 0 ? flags.Keywords : null, policyName: "keyword-oracle");
            case "keyword":
                if (flags.Keywords.Count == 0)
                {
                    error = "gate 'keyword' requires --keyword <k> … or --keywords <file>";
                    return null;
                }

                return new KeywordOracleGate(flags.Keywords, policyName: "keyword-oracle");
            case "rendered-exfil":
                return new RenderedOutputExfilGate();
        }

        if (id.StartsWith("keyword:", StringComparison.Ordinal))
        {
            var axis = id["keyword:".Length..];
            return AxisKeywordBaseline(axis) ?? Fail(out error, $"unknown keyword axis '{axis}' (expected one of: {string.Join(", ", Axes)})");
        }

        if (id.StartsWith("judge:", StringComparison.Ordinal) || id.StartsWith("panel:", StringComparison.Ordinal))
        {
            // judge/panel gates are model-backed and handled by 'inspect's model path (with the honesty guard), not
            // this deterministic resolver — reaching here means model flags weren't supplied.
            error = $"gate '{id}' is a model-backed judge/panel gate — supply model flags (--model / --endpoint / " +
                    "--azure, or the AZURE_OPENAI_* env), or --model-reply.";
            return null;
        }

        error = $"unknown chat gate '{id}'";
        return null;
    }

    /// <summary>Resolve a tool gate from the parsed flags (deterministic; no model).</summary>
    public static IToolGate? TryResolveToolGate(string id, GateFlags flags, out string? error)
    {
        error = null;
        try
        {
            switch (id)
            {
                case "tool:forbidden-tool":
                    if (flags.Forbidden.Count == 0)
                    {
                        error = "gate 'tool:forbidden-tool' requires --forbidden <name> …";
                        return null;
                    }

                    return new ForbiddenToolGate(flags.Forbidden.ToArray());
                case "tool:argument-pattern":
                    if (string.IsNullOrEmpty(flags.Pattern))
                    {
                        error = "gate 'tool:argument-pattern' requires --pattern <regex>";
                        return null;
                    }

                    return new ArgumentPatternGate(flags.Pattern);
                case "tool:domain-allowlist":
                    if (flags.AllowedDomains.Count == 0)
                    {
                        error = "gate 'tool:domain-allowlist' requires --allowed-domain <d> …";
                        return null;
                    }

                    return new DomainAllowListGate(flags.AllowedDomains);
                case "tool:referential-integrity":
                    if (flags.IdArgs.Count == 0)
                    {
                        error = "gate 'tool:referential-integrity' requires --id-arg <name> …";
                        return null;
                    }

                    return new ReferentialIntegrityGate(
                        flags.IdArgs, flags.GuardedTools, flags.TrustedTools.Count > 0 ? flags.TrustedTools : null);
                case "tool:taint-tracking":
                    if (flags.SourceTools.Count == 0 || flags.SinkTools.Count == 0)
                    {
                        error = "gate 'tool:taint-tracking' requires --source-tool <t> … and --sink-tool <t> …";
                        return null;
                    }

                    return flags.MinTaintLength is int min
                        ? new TaintTrackingGate(flags.SourceTools, flags.SinkTools, min)
                        : new TaintTrackingGate(flags.SourceTools, flags.SinkTools);
                case "tool:run-budget":
                    error = "gate 'tool:run-budget' needs held run state — available via 'serve', not stateless inspect.";
                    return null;
                default:
                    error = $"unknown tool gate '{id}'";
                    return null;
            }
        }
        catch (ArgumentException ex)
        {
            error = $"gate '{id}' rejected its parameters: {ex.Message}";
            return null;
        }
    }

    private static IChatGate? AxisKeywordBaseline(string axis) => axis switch
    {
        "indirect-injection" => IndirectInjectionJudge.KeywordBaseline(),
        "exfiltration-intent" => ExfiltrationIntentJudge.KeywordBaseline(),
        "system-prompt-extraction" => SystemPromptExtractionJudge.KeywordBaseline(),
        "over-refusal" => OverRefusalJudge.KeywordBaseline(),
        _ => null,
    };

    private static IChatGate? Fail(out string? error, string message)
    {
        error = message;
        return null;
    }
}
