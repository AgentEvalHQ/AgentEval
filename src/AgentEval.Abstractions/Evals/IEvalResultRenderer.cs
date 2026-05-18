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
/// <b>Relationship to family-specific renderers</b>: existing
/// <c>GDPRPdfRenderer</c> / <c>EuAiActPdfRenderer</c> / <c>AgenticPdfRenderer</c> stay
/// in place — they consume the family-specific evidence envelope
/// (<c>GdprComplianceEvidence</c>, ...) which carries cover-page-worthy metadata the
/// generic <see cref="EvalResult"/> does not have. Implementations of this interface
/// consume the universal <see cref="EvalResult"/> shape directly and are the right
/// choice for cross-family scenarios — discovery walkthroughs, custom sample apps,
/// third-party benchmark plugins.
/// </para>
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
public sealed record EvalResultRenderOptions(
    SubjectIdentity Subject,
    string? Title = null,
    string? RegulationOrBenchmark = null,
    string? RunId = null,
    DateTimeOffset? GeneratedAt = null,
    bool IncludeProvenance = true,
    string? AuditHash = null,
    string? AgentEvalVersion = null);
