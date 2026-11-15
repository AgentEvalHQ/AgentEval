// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Output;

namespace AgentEval.Evals;

/// <summary>
/// Renders a generic <see cref="EvalResult"/> composite tree to a byte stream
/// in a specific output format (HTML, PDF, Markdown, ...).
/// </summary>
/// <remarks>
/// <para>
/// v0.10.1 Phase A — introduces a uniform rendering contract that any benchmark family
/// can target so that consumers see the same shape of output regardless of which family
/// produced the result. The interface is intentionally minimal: a format id, a file
/// extension hint, and an async <see cref="RenderAsync"/> entry point that returns the
/// rendered bytes. Persisting them is the caller's responsibility — keeping I/O out of
/// the renderer lets it run in pure-memory unit tests and lets callers (CLI, Mission
/// Control, samples) write to any sink they like (filesystem, blob storage, HTTP response).
/// </para>
/// <para>
/// <b>Convention 5A (canonical renderer contract, ADR-017 / plan-13 T4.1b item 12)</b>:
/// every renderer that targets the unified <see cref="EvalResult"/> shape MUST
/// implement this interface. Three contract guarantees:
/// </para>
/// <list type="number">
///   <item><b>Pure projection</b>: <see cref="RenderAsync"/> must not write to disk,
///         contact the network, or mutate its <c>result</c> argument. Persistence is
///         the caller's job (see <see cref="IOutputStore"/> Convention 5B).</item>
///   <item><b>Deterministic format id</b>: <see cref="FormatId"/> + <see cref="FileExtension"/>
///         must be stable across versions so report-browser UIs can persist user
///         preferences ("default to PDF when present").</item>
///   <item><b>Honour <see cref="EvalResultRenderOptions.IncludeProvenance"/></b>:
///         when <c>false</c>, the renderer must omit judge-model id, prompt id, and
///         token-cost rows so the artefact is safe to share with non-trusted readers.</item>
/// </list>
/// <para>
/// Reference implementations are <c>HtmlEvalResultRenderer</c> and the engine-neutral
/// <c>PdfEvalResultRenderer</c> adapter. PDF engines are installed and registered separately;
/// the default package has no PDF engine. Rich family facades retain their metadata and
/// use the same registered provider boundary.
/// </para>
/// <example>
/// Render a composite evaluation result to HTML bytes:
/// <code>
/// var renderer = new HtmlEvalResultRenderer();
/// var opts = new EvalResultRenderOptions(
///     Subject: new SubjectIdentity(SubjectKind.Agent, "BookingAgent", "1.0.0", null, null, null, null),
///     Title: "Tool-Call Accuracy",
///     RegulationOrBenchmark: "Agentic Smoke",
///     RunId: manifest.Run.RunId,
///     AuditHash: manifest.ContentHash,
///     AgentEvalVersion: "0.10.1-beta");
/// byte[] bytes = await renderer.RenderAsync(evalResult, opts, ct);
/// await File.WriteAllBytesAsync($"report{renderer.FileExtension}", bytes, ct);
/// </code>
/// </example>
/// </remarks>
public interface IEvalResultRenderer
{
    /// <summary>Stable identifier for the output format, e.g. <c>"html"</c>, <c>"pdf"</c>, <c>"markdown"</c>.</summary>
    string FormatId { get; }

    /// <summary>Canonical file extension including the leading dot, e.g. <c>".html"</c>, <c>".pdf"</c>, <c>".md"</c>.</summary>
    string FileExtension { get; }

    /// <summary>
    /// Renders <paramref name="result"/> to a byte buffer in this renderer's format.
    /// </summary>
    /// <param name="result">The composite or atomic eval result tree to render.</param>
    /// <param name="options">Subject identity + framing metadata for the report header.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The rendered output as a byte buffer (UTF-8 encoded for text formats).</returns>
    Task<byte[]> RenderAsync(EvalResult result, EvalResultRenderOptions options, CancellationToken ct = default);
}

/// <summary>
/// Per-render framing options consumed by every <see cref="IEvalResultRenderer"/>. Keeps
/// the rendering surface uniform across families.
/// </summary>
/// <param name="Subject">The agent / workflow under test. Required.</param>
/// <param name="Title">
/// Optional report title. When omitted, renderers fall back to the root eval's
/// <c>Metric.Name</c>.
/// </param>
/// <param name="RegulationOrBenchmark">
/// Optional benchmark / regulation framing label, e.g. <c>"GDPR Standard preset"</c>
/// or <c>"OWASP LLM Top 10"</c>. Surfaced on the cover page.
/// </param>
/// <param name="RunId">Optional run identifier (matches the <c>.agenteval/</c> directory naming).</param>
/// <param name="GeneratedAt">Optional report generation timestamp; defaults to <see cref="DateTimeOffset.UtcNow"/>.</param>
/// <param name="IncludeProvenance">
/// When <c>true</c> (default) renderers emit per-leaf provenance — judge model id,
/// estimated cost, prompt hash. Disable for sanitised "executive" views.
/// </param>
/// <param name="AuditHash">
/// Optional canonical audit-chain hash. Surfaced in the footer/appendix when present
/// so the rendered artefact is independently traceable back to the source run.
/// </param>
/// <param name="AgentEvalVersion">
/// Optional AgentEval version string, surfaced in the footer for audit traceability.
/// </param>
/// <param name="EvidenceTruncationLength">
/// Maximum length (in characters) of an individual evidence message before the
/// renderer truncates it with a friendly "… (truncated)" suffix. Default is
/// <c>800</c>, the v0.10.1 hardcoded value (plan-13 T4.1b item 8 — F-PDF-1).
/// Set <c>0</c> (or any non-positive value) to disable truncation entirely
/// — long evidence will then flow across multiple PDF / HTML page-breaks
/// untouched. The HTML renderer ignores this option today (it streams evidence
/// directly into <c>&lt;pre&gt;</c> blocks); PDF renderers honour it for layout
/// reasons. Truncation is disclosed in the output.
/// </param>
public sealed record EvalResultRenderOptions(
    SubjectIdentity Subject,
    string? Title = null,
    string? RegulationOrBenchmark = null,
    string? RunId = null,
    DateTimeOffset? GeneratedAt = null,
    bool IncludeProvenance = true,
    string? AuditHash = null,
    string? AgentEvalVersion = null,
    int EvidenceTruncationLength = 800);
