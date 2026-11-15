// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Reporting;

/// <summary>Engine-neutral reporting facts. Providers choose layout without interpreting evaluation scores.</summary>
public sealed record ReportDocument
{
    /// <summary>Creates a report with a snapshot of its sections.</summary>
    public ReportDocument(string title, IEnumerable<ReportSection> sections, DateTimeOffset generatedAt,
        string? subject = null, string? author = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(sections);
        Title = title;
        Sections = Array.AsReadOnly(sections.ToArray());
        GeneratedAt = generatedAt;
        Subject = subject;
        Author = author;
    }
    /// <summary>Report title, also suitable for PDF metadata.</summary>
    public string Title { get; }
    /// <summary>Sections in display order.</summary>
    public IReadOnlyList<ReportSection> Sections { get; }
    /// <summary>Supplied generation time; rendering does not change evaluation time.</summary>
    public DateTimeOffset GeneratedAt { get; }
    /// <summary>Optional subject description.</summary>
    public string? Subject { get; }
    /// <summary>Optional report author.</summary>
    public string? Author { get; }
}

/// <summary>A named group of plain text and optional tabular facts. Text is never interpreted as HTML or a URL.</summary>
public sealed record ReportSection
{
    /// <summary>Creates a section with snapshots of its paragraphs and tables.</summary>
    public ReportSection(string title, IEnumerable<string>? paragraphs = null, IEnumerable<ReportTable>? tables = null)
    {
        Title = title ?? throw new ArgumentNullException(nameof(title));
        Paragraphs = Array.AsReadOnly((paragraphs ?? []).ToArray());
        Tables = Array.AsReadOnly((tables ?? []).ToArray());
    }
    /// <summary>Section heading.</summary>
    public string Title { get; }
    /// <summary>Plain text paragraphs; embedded line breaks are preserved.</summary>
    public IReadOnlyList<string> Paragraphs { get; }
    /// <summary>Tables displayed after the section paragraphs.</summary>
    public IReadOnlyList<ReportTable> Tables { get; }
}

/// <summary>A rectangular table with immutable row snapshots.</summary>
public sealed record ReportTable
{
    /// <summary>Creates a rectangular table and snapshots every row.</summary>
    public ReportTable(IEnumerable<string> headers, IEnumerable<IEnumerable<string>> rows)
    {
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(rows);
        Headers = Array.AsReadOnly(headers.ToArray());
        if (Headers.Count == 0) throw new ArgumentException("A report table requires at least one column.", nameof(headers));
        var snapshot = rows.Select(row => (IReadOnlyList<string>)Array.AsReadOnly(row.ToArray())).ToArray();
        if (snapshot.Any(row => row.Count != Headers.Count))
            throw new ArgumentException("Expected: one cell per header. Actual: mismatched row width. Suggestions: provide a rectangular table. Because: PDF providers must not drop cells.", nameof(rows));
        Rows = Array.AsReadOnly(snapshot);
    }
    /// <summary>Column headings.</summary>
    public IReadOnlyList<string> Headers { get; }
    /// <summary>Rows with exactly one cell for every heading.</summary>
    public IReadOnlyList<IReadOnlyList<string>> Rows { get; }
}

/// <summary>Portable PDF paper sizes.</summary>
public enum PdfPageSize
{
    /// <summary>ISO A4 paper.</summary>
    A4,
    /// <summary>US Letter paper.</summary>
    Letter,
    /// <summary>US Legal paper.</summary>
    Legal,
}

/// <summary>Presentation options shared by providers. Images must already be host-approved local bytes.</summary>
public sealed record PdfRenderOptions(PdfPageSize PageSize = PdfPageSize.A4,
    string FontFamily = "AgentEval Sans", string PrimaryColor = "#0078D4", string SecondaryColor = "#2B579A",
    ReadOnlyMemory<byte> Logo = default);

/// <summary>Optional PDF engine boundary. Implementations must not mutate the document or fetch network resources.</summary>
public interface IPdfReportRenderer
{
    /// <summary>Renders the supplied facts to PDF bytes; persistence belongs to the caller.</summary>
    Task<byte[]> RenderAsync(ReportDocument document, PdfRenderOptions options, CancellationToken cancellationToken = default);
}

/// <summary>Selects explicitly registered PDF engines. Selection never falls back to another provider.</summary>
public interface IPdfRendererRegistry
{
    /// <summary>Registered IDs in stable ordinal order.</summary>
    IReadOnlyList<string> ProviderIds { get; }
    /// <summary>Selects an explicit provider, or the sole provider when no ID is supplied.</summary>
    IPdfReportRenderer Get(string? providerId = null);
}
