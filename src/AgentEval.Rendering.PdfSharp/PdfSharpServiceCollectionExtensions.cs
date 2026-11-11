// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
using AgentEval.Rendering.Pdf;
using Microsoft.Extensions.DependencyInjection;

namespace AgentEval.Rendering.PdfSharp;

/// <summary>Opt-in registration for the PDFsharp/MigraDoc provider.</summary>
public static class PdfSharpServiceCollectionExtensions
{
    /// <summary>Registers PDFsharp. No engine, font, file, or layout is initialized until rendering is requested.</summary>
    public static IServiceCollection AddAgentEvalPdfSharp(this IServiceCollection services, string providerId = "pdfsharp")
        => services.AddAgentEvalPdfProvider<PdfSharpReportRenderer>(providerId);
}
