// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
using AgentEval.Evals;
using AgentEval.Reporting;

namespace AgentEval.Rendering.Pdf;

/// <summary>Projects an evaluation tree and renders it with an explicitly registered PDF provider.</summary>
public sealed class PdfEvalResultRenderer(IPdfRendererRegistry? renderers = null, string? providerId = null) : IEvalResultRenderer
{
    /// <summary>Preserves the original construction signature. Rendering requires an explicitly supplied registry.</summary>
    public PdfEvalResultRenderer() : this(null, null) { }

    /// <inheritdoc />
    public string FormatId => "pdf";
    /// <inheritdoc />
    public string FileExtension => ".pdf";

    /// <inheritdoc />
    public async Task<byte[]> RenderAsync(EvalResult result, EvalResultRenderOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(options);
        ct.ThrowIfCancellationRequested();
        var provider = PdfReportFiles.RequireProvider(renderers, providerId);
        var bytes = await provider.RenderAsync(EvalReportProjection.Create(result, options), new(), ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return bytes;
    }
}
