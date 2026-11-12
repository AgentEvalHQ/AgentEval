// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
using System.Text.Json;
using AgentEval.Reporting;

namespace AgentEval.Tests.Rendering.Pdf;

internal sealed class TestPdfRegistry : IPdfRendererRegistry, IPdfReportRenderer
{
    public IReadOnlyList<string> ProviderIds => ["test"];
    public IPdfReportRenderer Get(string? providerId = null) => this;
    public Task<byte[]> RenderAsync(ReportDocument document, PdfRenderOptions options, CancellationToken cancellationToken = default)
        => Task.FromResult(JsonSerializer.SerializeToUtf8Bytes(document));
}
