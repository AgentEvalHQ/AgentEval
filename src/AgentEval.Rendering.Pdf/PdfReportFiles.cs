// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
using AgentEval.Reporting;

namespace AgentEval.Rendering.Pdf;

/// <summary>Explicit provider validation and atomic persistence shared by PDF facades.</summary>
public static class PdfReportFiles
{
    /// <summary>Resolves a provider before report projection or expensive evaluation begins.</summary>
    public static IPdfReportRenderer RequireProvider(IPdfRendererRegistry? registry, string? providerId = null)
        => registry?.Get(providerId) ?? throw new InvalidOperationException(
            "Expected: an explicitly registered PDF provider. Actual: no PDF registry was supplied. Suggestions: install AgentEval.Rendering.PdfSharp, call services.AddAgentEvalPdfSharp(), and supply IPdfRendererRegistry; CLI users can use agenteval-pdf. Because: PDF generation is optional.");

    /// <summary>Renders before writing, then atomically moves the output. Cancellation/failure removes only the owned temporary file.</summary>
    public static async Task RenderAsync(IPdfReportRenderer provider, ReportDocument document, string outputPath,
        PdfRenderOptions? options = null, CancellationToken cancellationToken = default, bool overwrite = true)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = await provider.RenderAsync(document, options ?? new(), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, fullPath, overwrite);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
