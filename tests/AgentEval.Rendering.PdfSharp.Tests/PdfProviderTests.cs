// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
using System.Text;
using System.Text.Json;
using AgentEval.Compliance.Gdpr.Articles;
using AgentEval.Compliance.Gdpr.Reporting.Pdf;
using AgentEval.Compliance.EuAiAct.Articles;
using AgentEval.Compliance.EuAiAct.Reporting.Pdf;
using AgentEval.Evals;
using AgentEval.Evals.Agentic.Reporting.Pdf;
using AgentEval.Output;
using AgentEval.RedTeam.Reporting.Pdf;
using AgentEval.Rendering.Pdf;
using AgentEval.Reporting;
using Microsoft.Extensions.DependencyInjection;

namespace AgentEval.Rendering.PdfSharp.Tests;

public sealed class PdfProviderTests
{
    private static readonly DateTimeOffset Generated = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);
    private static ReportDocument Document(string text = "Recorded facts") => new("Test report", [new("Summary", [text])], Generated);
    private static EvalResult Result() => new(new("check", "Check", "test", "1"), new(0, null, "fail", false, null, "high", null),
        new(null, [new("test", "ref", "Swedish evidence")], null, null, null),
        new("llm", "PRIVATE-JUDGE", "PRIVATE-PROMPT", null, 999, 0.234, false), Generated);

    [Fact]
    public void Constructors_OriginalMetadataSignatures_ArePreserved()
    {
        foreach (var type in new[] { typeof(PdfEvalResultRenderer), typeof(AgenticPdfRenderer), typeof(PdfReportGenerator) })
        {
            var constructor = type.GetConstructor(Type.EmptyTypes);
            Assert.NotNull(constructor);
            Assert.IsType(type, constructor.Invoke([]));
        }
        foreach (var (type, argument) in new[] { (typeof(GDPRPdfRenderer), typeof(ArticlesRegistry)), (typeof(EuAiActPdfRenderer), typeof(EuAiActArticlesRegistry)) })
        {
            var constructor = type.GetConstructor([argument]);
            Assert.NotNull(constructor);
            var parameter = Assert.Single(constructor.GetParameters());
            Assert.True(parameter.IsOptional);
            Assert.Null(parameter.DefaultValue);
            Assert.IsType(type, constructor.Invoke([null]));
        }
    }

    [Fact]
    public void Constructors_OriginalAndNamedProviderSourceCalls_AreUnambiguousAndDoNotConstructEngine()
    {
        CountingProvider.Created = 0;
        using var host = new ServiceCollection().AddAgentEvalPdfProvider<CountingProvider>("customer").BuildServiceProvider();
        var registry = host.GetRequiredService<IPdfRendererRegistry>();
        object[] renderers =
        [
            new PdfEvalResultRenderer(), new PdfEvalResultRenderer(null), new PdfEvalResultRenderer(renderers: registry),
            new AgenticPdfRenderer(), new AgenticPdfRenderer(null), new AgenticPdfRenderer(pdfRenderers: registry),
            new PdfReportGenerator(), new PdfReportGenerator(null), new PdfReportGenerator(pdfRenderers: registry),
            new GDPRPdfRenderer(), new GDPRPdfRenderer(null), new GDPRPdfRenderer(articles: null),
            new GDPRPdfRenderer(pdfRenderers: registry), new GDPRPdfRenderer(articles: null, pdfRenderers: registry),
            new EuAiActPdfRenderer(), new EuAiActPdfRenderer(null), new EuAiActPdfRenderer(articles: null),
            new EuAiActPdfRenderer(pdfRenderers: registry), new EuAiActPdfRenderer(articles: null, pdfRenderers: registry)
        ];
        Assert.All(renderers, Assert.NotNull);
        Assert.Equal(0, CountingProvider.Created);
    }

    [Fact]
    public async Task RenderAsync_NoRegistry_ExplainsInstallationAndRegistration()
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => new PdfEvalResultRenderer().RenderAsync(Result(), new(new(SubjectKind.Agent, "subject"))));
        Assert.Contains("install", exception.Message);
        Assert.Contains("AddAgentEvalPdfSharp", exception.Message);
    }

    [Fact]
    public void Registration_WithoutOutput_DoesNotConstructProvider()
    {
        CountingProvider.Created = 0;
        var services = new ServiceCollection().AddAgentEvalPdfProvider<CountingProvider>("custom");
        using var host = services.BuildServiceProvider();
        var registry = host.GetRequiredService<IPdfRendererRegistry>();
        Assert.Equal(["custom"], registry.ProviderIds);
        Assert.Equal(0, CountingProvider.Created);
    }

    [Fact]
    public void Registration_RepeatedSameIdAndType_IsIdempotent()
    {
        var services = new ServiceCollection().AddAgentEvalPdfSharp().AddAgentEvalPdfSharp("PDFSHARP");
        using var host = services.BuildServiceProvider();
        var registry = host.GetRequiredService<IPdfRendererRegistry>();
        Assert.Single(registry.ProviderIds);
        Assert.IsType<PdfSharpReportRenderer>(registry.Get());
    }

    [Fact]
    public void Registration_ConflictingId_FailsBeforeHostOrEvaluation()
    {
        var services = new ServiceCollection().AddAgentEvalPdfSharp();
        Assert.Throws<InvalidOperationException>(() => services.AddAgentEvalPdfProvider<CountingProvider>("PDFSHARP"));
    }

    [Fact]
    public void Get_MultipleProviders_RequiresExplicitSelection()
    {
        using var host = new ServiceCollection().AddAgentEvalPdfSharp().AddAgentEvalPdfProvider<CountingProvider>("customer").BuildServiceProvider();
        var registry = host.GetRequiredService<IPdfRendererRegistry>();
        Assert.Throws<InvalidOperationException>(() => registry.Get());
        Assert.Throws<InvalidOperationException>(() => registry.Get("missing"));
        Assert.IsType<CountingProvider>(registry.Get("customer"));
        Assert.IsType<PdfSharpReportRenderer>(registry.Get("pdfsharp"));
    }

    [Fact]
    public void Get_CustomScopedDependency_UsesTheCallingScope()
    {
        using var host = new ServiceCollection().AddScoped<ScopedState>().AddAgentEvalPdfProvider<ScopedProvider>("scoped")
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var first = host.CreateScope();
        using var second = host.CreateScope();
        var a = Assert.IsType<ScopedProvider>(first.ServiceProvider.GetRequiredService<IPdfRendererRegistry>().Get());
        var b = Assert.IsType<ScopedProvider>(second.ServiceProvider.GetRequiredService<IPdfRendererRegistry>().Get());
        Assert.Same(first.ServiceProvider.GetRequiredService<ScopedState>(), a.State);
        Assert.NotSame(a.State, b.State);
    }

    [Fact]
    public async Task Adapter_CustomProvider_ReceivesNeutralFactsAndPrivacyAlreadyApplied()
    {
        var provider = new CaptureProvider();
        using var host = new ServiceCollection().AddSingleton(provider).AddAgentEvalPdfProvider<CaptureProvider>("custom").BuildServiceProvider();
        var result = Result();
        var options = new EvalResultRenderOptions(new(SubjectKind.Agent, "Åsa"), IncludeProvenance: false);
        var bytes = await host.GetRequiredService<IPdfRendererRegistry>().ForEvalResults().RenderAsync(result, options);
        Assert.Equal("custom-pdf", Encoding.UTF8.GetString(bytes));
        var serialized = JsonSerializer.Serialize(provider.Last);
        Assert.Contains("Swedish evidence", serialized);
        Assert.DoesNotContain("PRIVATE-JUDGE", serialized);
        Assert.DoesNotContain("PRIVATE-PROMPT", serialized);
        Assert.DoesNotContain("999", serialized);
        Assert.DoesNotContain("0.234", serialized);
        Assert.Equal(0, result.Score.Value);
    }

    [Fact]
    public async Task Adapter_ThrowingSelectedProvider_DoesNotFallBack()
    {
        CountingProvider.Rendered = 0;
        using var host = new ServiceCollection().AddAgentEvalPdfProvider<ThrowingProvider>("broken")
            .AddAgentEvalPdfProvider<CountingProvider>("fallback").BuildServiceProvider();
        var renderer = host.GetRequiredService<IPdfRendererRegistry>().ForEvalResults("broken");
        await Assert.ThrowsAsync<InvalidOperationException>(() => renderer.RenderAsync(Result(), new(new(SubjectKind.Agent, "x"))));
        Assert.Equal(0, CountingProvider.Rendered);
    }

    [Fact]
    public async Task Adapter_PreCancelledToken_DoesNotConstructEngine()
    {
        CountingProvider.Created = 0;
        using var host = new ServiceCollection().AddAgentEvalPdfProvider<CountingProvider>("counting").BuildServiceProvider();
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.GetRequiredService<IPdfRendererRegistry>().ForEvalResults()
            .RenderAsync(Result(), new(new(SubjectKind.Agent, "x")), cancellation.Token));
        Assert.Equal(0, CountingProvider.Created);
    }

    [Fact]
    public async Task Files_RenderFailure_PreservesExistingDestinationAndCreatesNoTemporaryFile()
    {
        var directory = Directory.CreateTempSubdirectory("pdf-atomic-");
        try
        {
            var path = Path.Combine(directory.FullName, "report.pdf");
            await File.WriteAllTextAsync(path, "original");
            await Assert.ThrowsAsync<InvalidOperationException>(() => PdfReportFiles.RenderAsync(new ThrowingProvider(), Document(), path));
            Assert.Equal("original", await File.ReadAllTextAsync(path));
            Assert.Single(directory.GetFiles());
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task Files_DestinationCreatedDuringRender_RequiresExplicitOverwrite()
    {
        var directory = Directory.CreateTempSubdirectory("pdf-race-");
        try
        {
            var path = Path.Combine(directory.FullName, "report.pdf");
            await Assert.ThrowsAsync<IOException>(() => PdfReportFiles.RenderAsync(
                new CompetingWriterProvider(path), Document(), path, overwrite: false));
            Assert.Equal("other writer", await File.ReadAllTextAsync(path));
            Assert.Single(directory.GetFiles());
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public void Document_MutableSourceCollections_AreSnapshotted()
    {
        var row = new[] { "before" }; var headers = new[] { "Header" }; var paragraphs = new[] { "Original" };
        var table = new ReportTable(headers, [row]);
        var sections = new[] { new ReportSection("Summary", paragraphs, [table]) };
        var document = new ReportDocument("Report", sections, Generated);
        row[0] = "changed"; headers[0] = "changed"; paragraphs[0] = "changed"; sections[0] = new("changed");
        Assert.Equal("Original", document.Sections[0].Paragraphs[0]);
        Assert.Equal("before", document.Sections[0].Tables[0].Rows[0][0]);
        Assert.Equal("Header", document.Sections[0].Tables[0].Headers[0]);
    }

    [Fact]
    public void Table_MismatchedCells_RejectsSilentDataLoss()
        => Assert.Throws<ArgumentException>(() => new ReportTable(["one", "two"], [new[] { "one" }]));

    [Fact]
    public async Task PdfSharp_SwedishTextAndFontStyles_RemainExtractable()
    {
        const string swedish = "Åsa säger: Räksmörgås, Göteborg, öppen källkod och svenska bedömningskriterier.";
        using var host = new ServiceCollection().AddAgentEvalPdfSharp().BuildServiceProvider();
        var bytes = await host.GetRequiredService<IPdfRendererRegistry>().Get().RenderAsync(Document(swedish), new());
        using var pdf = UglyToad.PdfPig.PdfDocument.Open(bytes);
        Assert.Contains(swedish, string.Join(" ", pdf.GetPages().Select(p => UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor.ContentOrderTextExtractor.GetText(p))));
        Assert.Equal(Generated.UtcDateTime.Year.ToString(), pdf.Information.CreationDate?.Contains("2026") == true ? "2026" : "missing");
    }

    [Fact]
    public async Task PdfSharp_LongTableAndEvidence_KeepLastContentAcrossPages()
    {
        var longText = string.Join("\n", Enumerable.Range(0, 300).Select(i => $"Recorded evidence line {i}: Swedish å ä ö.")) + "\nLAST-LONG-MARKER";
        var document = new ReportDocument("Long report", [new("Evidence", [longText], [new(["Field", "Value"], [new[] { "Long cell", longText }])])], Generated);
        var bytes = await new PdfSharpReportRenderer().RenderAsync(document, new());
        using var pdf = UglyToad.PdfPig.PdfDocument.Open(bytes);
        Assert.True(pdf.NumberOfPages > 2);
        var text = string.Join(" ", pdf.GetPages().Select(p => UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor.ContentOrderTextExtractor.GetText(p)));
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(text, "LAST-LONG-MARKER").Count);
        Assert.Contains("Recorded evidence line 299", text);
    }

    [Theory]
    [InlineData(PdfPageSize.A4)]
    [InlineData(PdfPageSize.Letter)]
    [InlineData(PdfPageSize.Legal)]
    public async Task PdfSharp_EmptyDocument_IsExplicitAndReadable(PdfPageSize size)
    {
        var bytes = await new PdfSharpReportRenderer().RenderAsync(new("Empty report", [], Generated), new(size));
        using var pdf = UglyToad.PdfPig.PdfDocument.Open(bytes);
        Assert.Contains("No report sections", UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor.ContentOrderTextExtractor.GetText(pdf.GetPage(1)));
    }

    [Fact]
    public async Task PdfSharp_ConcurrentRenders_DoNotShareDocumentState()
    {
        var provider = new PdfSharpReportRenderer();
        var bytes = await Task.WhenAll(Enumerable.Range(0, 4).Select(i => Task.Run(() => provider.RenderAsync(Document("TENANT-" + i), new()))));
        for (var i = 0; i < bytes.Length; i++)
        {
            using var pdf = UglyToad.PdfPig.PdfDocument.Open(bytes[i]);
            var text = pdf.GetPage(1).Text;
            Assert.Contains("TENANT-" + i, text);
            foreach (var other in Enumerable.Range(0, 4).Where(n => n != i)) Assert.DoesNotContain("TENANT-" + other, text);
        }
    }

    public sealed class CountingProvider : IPdfReportRenderer
    {
        public static int Created; public static int Rendered;
        public CountingProvider() => Interlocked.Increment(ref Created);
        public Task<byte[]> RenderAsync(ReportDocument document, PdfRenderOptions options, CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref Rendered); return Task.FromResult(new byte[] { 1 }); }
    }
    public sealed class ScopedState { }
    public sealed class ScopedProvider(ScopedState state) : IPdfReportRenderer
    {
        public ScopedState State => state;
        public Task<byte[]> RenderAsync(ReportDocument document, PdfRenderOptions options, CancellationToken cancellationToken = default)
            => Task.FromResult(new byte[] { 1 });
    }
    public sealed class CaptureProvider : IPdfReportRenderer
    {
        public ReportDocument? Last;
        public Task<byte[]> RenderAsync(ReportDocument document, PdfRenderOptions options, CancellationToken cancellationToken = default)
        { Last = document; return Task.FromResult(Encoding.UTF8.GetBytes("custom-pdf")); }
    }
    public sealed class ThrowingProvider : IPdfReportRenderer
    {
        public Task<byte[]> RenderAsync(ReportDocument document, PdfRenderOptions options, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Synthetic render failure");
    }
    private sealed class CompetingWriterProvider(string path) : IPdfReportRenderer
    {
        public async Task<byte[]> RenderAsync(ReportDocument document, PdfRenderOptions options, CancellationToken cancellationToken = default)
        {
            await File.WriteAllTextAsync(path, "other writer", cancellationToken);
            return Encoding.ASCII.GetBytes("candidate PDF");
        }
    }
}
