// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace AgentEval.Microsoft365.Diagnostics;

/// <summary>
/// One entry of the issue register (<c>m365-issues.jsonl</c>): an anomaly with its stage, category, stable code,
/// safe message and a plain-language suggested cause, plus every correlation fact the emitter knew. The register
/// is what an external tester sends back; it must be readable without the code. <see cref="ConfigSha256"/>,
/// <see cref="AdapterVersion"/> and <see cref="A2AVersion"/> are stamped by <see cref="WorkIqDiagnostics"/> when
/// left null, as are <see cref="At"/> (when <c>default</c>) and <see cref="RunId"/>.
/// </summary>
/// <param name="At">When the issue was observed; <c>default</c> means "stamp it".</param>
/// <param name="Stage">The pipeline stage.</param>
/// <param name="Category">Who or what can fix it.</param>
/// <param name="Code">A stable, machine-readable code such as <c>auth.aadsts</c> or <c>http.403</c> (non-blank).</param>
/// <param name="Message">A human-readable, safe message (never null).</param>
/// <param name="SuggestedCause">A plain-language cause the tester can act on, with the raw code kept (never null; may be empty when nothing is known).</param>
public sealed record WorkIqIssue(
    DateTimeOffset At,
    WorkIqTraceStage Stage,
    WorkIqIssueCategory Category,
    string Code,
    string Message,
    string SuggestedCause)
{
    private readonly string _code = RequireCode(Code);
    private readonly string _message = Message ?? throw new ArgumentNullException(nameof(Message));
    private readonly string _suggestedCause = SuggestedCause ?? throw new ArgumentNullException(nameof(SuggestedCause));

    /// <summary>A stable, machine-readable code (non-blank).</summary>
    public string Code
    {
        get => _code;
        init => _code = RequireCode(value);
    }

    /// <summary>A human-readable, safe message (never null).</summary>
    public string Message
    {
        get => _message;
        init => _message = value ?? throw new ArgumentNullException(nameof(Message));
    }

    /// <summary>A plain-language cause the tester can act on (never null).</summary>
    public string SuggestedCause
    {
        get => _suggestedCause;
        init => _suggestedCause = value ?? throw new ArgumentNullException(nameof(SuggestedCause));
    }

    /// <summary>The run id; null means "stamp it".</summary>
    public string? RunId { get; init; }

    /// <summary>The local benchmark case id, when known.</summary>
    public string? CaseId { get; init; }

    /// <summary>The client turn id, when known.</summary>
    public string? TurnId { get; init; }

    /// <summary>The HTTP status code, when a response was received.</summary>
    public int? HttpStatus { get; init; }

    /// <summary>The JSON-RPC error code, when an error object was received.</summary>
    public int? RpcErrorCode { get; init; }

    /// <summary>The Entra error code (<c>AADSTS…</c>), when the issue came from MSAL.</summary>
    public string? AadstsCode { get; init; }

    /// <summary>The raw A2A task state, when observed. Serialized as <c>a2aState</c>.</summary>
    [JsonPropertyName("a2aState")]
    public string? A2AState { get; init; }

    /// <summary>The service's <c>request-id</c> response header, when received.</summary>
    public string? RequestId { get; init; }

    /// <summary>The server task id, when known.</summary>
    public string? TaskId { get; init; }

    /// <summary>The server context id, when known.</summary>
    public string? ContextId { get; init; }

    /// <summary>SHA-256 of the config's canonical identity (no secrets exist in the config); null means "stamp it".</summary>
    public string? ConfigSha256 { get; init; }

    /// <summary>The provider assembly's informational version; null means "stamp it".</summary>
    public string? AdapterVersion { get; init; }

    /// <summary>The A2A protocol version spoken (<c>1.0</c>); null means "stamp it". Serialized as <c>a2aVersion</c>.</summary>
    [JsonPropertyName("a2aVersion")]
    public string? A2AVersion { get; init; }

    /// <summary>Creates an issue that stamps nothing (<see cref="At"/> <c>default</c>, every stamped field null).</summary>
    /// <param name="stage">The pipeline stage.</param>
    /// <param name="category">Who or what can fix it.</param>
    /// <param name="code">The stable issue code (non-blank).</param>
    /// <param name="message">The safe message.</param>
    /// <param name="suggestedCause">The plain-language cause.</param>
    public static WorkIqIssue Create(WorkIqTraceStage stage, WorkIqIssueCategory category, string code, string message, string suggestedCause) =>
        new(default, stage, category, code, message, suggestedCause);

    private static string RequireCode(string code)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code, nameof(Code));
        return code;
    }
}
