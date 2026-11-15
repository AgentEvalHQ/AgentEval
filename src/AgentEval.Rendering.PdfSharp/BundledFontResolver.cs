// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
using PdfSharp.Fonts;

namespace AgentEval.Rendering.PdfSharp;

/// <summary>Provider-owned, bundled OFL fonts. Registration respects an existing host resolver.</summary>
internal sealed class BundledFontResolver : IFontResolver
{
    private static readonly object Sync = new();
    private static readonly Lazy<BundledFontResolver> Instance = new(() => new());
    private readonly Dictionary<string, byte[]> fonts = new(StringComparer.Ordinal);

    private BundledFontResolver()
    {
        foreach (var face in new[] { "Regular", "Bold", "Italic", "BoldItalic" })
        {
            using var stream = typeof(BundledFontResolver).Assembly.GetManifestResourceStream($"AgentEval.Rendering.PdfSharp.Fonts.Lato-{face}.ttf")
                ?? throw new InvalidOperationException("Expected: bundled Lato font. Actual: font resource missing. Suggestions: reinstall the PDF provider. Because: PDF generation must not depend on operating-system fonts.");
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            fonts.Add(face, memory.ToArray());
        }
    }

    internal static void EnsureRegistered()
    {
        lock (Sync)
        {
            // Never replace a host's policy or swallow a PDFsharp global-configuration failure.
            if (GlobalFontSettings.FontResolver is null) GlobalFontSettings.FontResolver = Instance.Value;
        }
    }

    public FontResolverInfo ResolveTypeface(string familyName, bool isBold, bool isItalic)
        => new((isBold, isItalic) switch { (true, true) => "BoldItalic", (true, false) => "Bold", (false, true) => "Italic", _ => "Regular" });
    public byte[]? GetFont(string faceName) => fonts.GetValueOrDefault(faceName);
}
