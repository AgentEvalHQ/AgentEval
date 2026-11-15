// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
using AgentEval.Reporting;
using MigraDoc.DocumentObjectModel;
using MigraDoc.Rendering;

namespace AgentEval.Rendering.PdfSharp;

/// <summary>PDFsharp/MigraDoc 6.2.4 engine. Each render owns its document; fonts are initialized only on use.</summary>
public sealed class PdfSharpReportRenderer : IPdfReportRenderer
{
    /// <inheritdoc />
    public Task<byte[]> RenderAsync(ReportDocument report, PdfRenderOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        BundledFontResolver.EnsureRegistered();
        var document = new Document();
        document.Info.Title = report.Title;
        document.Info.Subject = report.Subject ?? "AgentEval evaluation report";
        document.Info.Author = report.Author ?? "AgentEval";
        var normal = document.Styles["Normal"]!;
        normal.Font.Name = options.FontFamily;
        normal.Font.Size = 10;
        normal.ParagraphFormat.SpaceAfter = Unit.FromPoint(6);
        normal.ParagraphFormat.WidowControl = true;
        var heading = document.Styles["Heading1"]!;
        heading.Font.Size = 20;
        heading.Font.Bold = true;
        heading.Font.Color = ParseColor(options.PrimaryColor);
        heading.ParagraphFormat.SpaceAfter = Unit.FromPoint(14);
        heading.ParagraphFormat.KeepWithNext = true;

        var sourceSections = report.Sections.Count == 0 ? new[] { new ReportSection(report.Title, ["No report sections were supplied."]) } : report.Sections;
        foreach (var source in sourceSections)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var section = document.AddSection();
            section.PageSetup.PageFormat = options.PageSize switch
            {
                PdfPageSize.A4 => PageFormat.A4, PdfPageSize.Letter => PageFormat.Letter, PdfPageSize.Legal => PageFormat.Legal,
                _ => throw new ArgumentOutOfRangeException(nameof(options), "Unsupported PDF page size."),
            };
            section.PageSetup.LeftMargin = section.PageSetup.RightMargin = Unit.FromCentimeter(2);
            section.PageSetup.TopMargin = section.PageSetup.BottomMargin = Unit.FromCentimeter(2);
            section.Headers.Primary.AddParagraph(report.Title).Format.Font.Size = 8;
            var footer = section.Footers.Primary.AddParagraph();
            footer.Format.Alignment = ParagraphAlignment.Center;
            footer.AddText("Page "); footer.AddPageField(); footer.AddText(" of "); footer.AddNumPagesField();
            if (document.Sections.Count == 1 && !options.Logo.IsEmpty)
            {
                var logo = section.AddImage("base64:" + Convert.ToBase64String(options.Logo.Span));
                logo.Width = Unit.FromCentimeter(4); logo.LockAspectRatio = true;
            }
            section.AddParagraph(source.Title, "Heading1");
            foreach (var text in source.Paragraphs) AddText(section, text);
            foreach (var table in source.Tables) AddTable(section, table, options);
        }
        var renderer = new PdfDocumentRenderer { Document = document };
        renderer.RenderDocument(); // Synchronous layout is not interruptible; cancellation is checked on both sides.
        cancellationToken.ThrowIfCancellationRequested();
        renderer.PdfDocument.Info.CreationDate = report.GeneratedAt.UtcDateTime;
        using var output = new MemoryStream();
        renderer.PdfDocument.Save(output, false);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(output.ToArray());
    }

    private static void AddText(Section section, string text)
    {
        // Separate paragraphs let long multiline evidence flow across pages instead of becoming an indivisible table row.
        foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')) section.AddParagraph(WrapLongWords(line));
    }

    private static void AddTable(Section section, ReportTable source, PdfRenderOptions options)
    {
        // MigraDoc cannot split an oversized row. Use explicit field paragraphs for long rows, preserving every cell.
        if (source.Rows.Any(r => r.Any(c => c.Length > 500 || c.Count(ch => ch == '\n') > 5)))
        {
            for (var row = 0; row < source.Rows.Count; row++)
            {
                section.AddParagraph($"Row {row + 1}").Format.Font.Bold = true;
                for (var column = 0; column < source.Headers.Count; column++) AddText(section, source.Headers[column] + ": " + source.Rows[row][column]);
            }
            return;
        }
        var table = section.AddTable();
        table.Borders.Width = 0.5;
        table.Borders.Color = Colors.LightGray;
        var width = options.PageSize == PdfPageSize.A4 ? 17.0 : 17.59;
        for (var column = 0; column < source.Headers.Count; column++) table.AddColumn(Unit.FromCentimeter(width / source.Headers.Count));
        var header = table.AddRow();
        header.HeadingFormat = true;
        header.Format.Font.Bold = true;
        header.Shading.Color = ParseColor(options.SecondaryColor);
        header.Format.Font.Color = Colors.White;
        for (var column = 0; column < source.Headers.Count; column++) header.Cells[column].AddParagraph(WrapLongWords(source.Headers[column]));
        foreach (var cells in source.Rows)
        {
            var row = table.AddRow();
            for (var column = 0; column < source.Headers.Count; column++) row.Cells[column].AddParagraph(WrapLongWords(cells[column]));
        }
    }

    private static string WrapLongWords(string value) => System.Text.RegularExpressions.Regex.Replace(value, @"\S{81,}",
        match => string.Join("\u200b", Enumerable.Range(0, (match.Length + 59) / 60).Select(i => match.Value.Substring(i * 60, Math.Min(60, match.Length - i * 60)))));

    private static Color ParseColor(string? hex)
    {
        hex = hex?.TrimStart('#');
        return hex is { Length: 6 } && uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var rgb)
            ? new Color((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb) : Colors.DarkBlue;
    }
}
