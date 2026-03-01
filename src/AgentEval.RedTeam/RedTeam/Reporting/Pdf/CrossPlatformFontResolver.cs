// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
using PdfSharp.Fonts;

namespace AgentEval.RedTeam.Reporting.Pdf;

/// <summary>
/// Cross-platform font resolver for PdfSharp that works on Windows, Linux, and macOS.
/// PdfSharp-MigraDoc 6.2+ requires an explicit IFontResolver on ALL platforms.
/// On Windows: maps family names to native TTF file names in %windir%\Fonts.
/// On Linux/macOS: maps to Liberation/DejaVu fallback fonts.
/// </summary>
public sealed class CrossPlatformFontResolver : IFontResolver
{
    private static readonly Lazy<CrossPlatformFontResolver> s_instance = new(() => new CrossPlatformFontResolver());
    private static bool s_isRegistered;
    private static readonly object s_lock = new();

    /// <summary>Gets the singleton instance of the font resolver.</summary>
    public static CrossPlatformFontResolver Instance => s_instance.Value;

    /// <summary>
    /// Registers this font resolver with PdfSharp if not already registered.
    /// Must be called on all platforms — PdfSharp-MigraDoc 6.2+ no longer uses
    /// GDI+ for automatic font resolution on Windows.
    /// </summary>
    public static void Register()
    {
        if (s_isRegistered) return;

        lock (s_lock)
        {
            if (s_isRegistered) return;

            try
            {
                GlobalFontSettings.FontResolver = Instance;
                s_isRegistered = true;
            }
            catch (InvalidOperationException)
            {
                // Font resolver already set (happens in concurrent test scenarios)
                s_isRegistered = true;
            }
        }
    }

    /// <inheritdoc />
    public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic)
    {
        var normalized = familyName.ToLowerInvariant().Replace(" ", "");

        // On Windows, use native font file base-names (without extension).
        // GetFont() will look these up directly in C:\Windows\Fonts.
        if (OperatingSystem.IsWindows())
        {
            var winFace = GetWindowsFaceName(normalized, isBold, isItalic);
            return new FontResolverInfo(winFace);
        }

        // Linux / macOS — map to Liberation / DejaVu open-source fonts.
        var crossFace = GetCrossPlatformFaceName(normalized, isBold, isItalic);
        return new FontResolverInfo(crossFace);
    }

    /// <inheritdoc />
    public byte[]? GetFont(string faceName)
    {
        if (OperatingSystem.IsWindows())
        {
            // faceName is a Windows TTF base name, e.g. "cour", "arialbd"
            var winFonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);

            // Fallback: derive %WINDIR%\Fonts from System directory (portable across drive letters)
            if (string.IsNullOrEmpty(winFonts))
            {
                var systemDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
                if (!string.IsNullOrEmpty(systemDir))
                    winFonts = Path.GetFullPath(Path.Combine(systemDir, "..", "Fonts"));
            }

            if (!string.IsNullOrEmpty(winFonts))
            {
                foreach (var ext in new[] { ".ttf", ".otf" })
                {
                    var path = Path.Combine(winFonts, faceName + ext);
                    if (File.Exists(path)) return File.ReadAllBytes(path);
                }

                // Last resort: use Arial as an emergency fallback
                var arialPath = Path.Combine(winFonts, "arial.ttf");
                if (File.Exists(arialPath)) return File.ReadAllBytes(arialPath);
            }

            return null;
        }

        // Linux / macOS — search known font directories for Liberation/DejaVu
        var fontPath = FindFontFile(faceName);
        return fontPath != null ? File.ReadAllBytes(fontPath) : null;
    }

    // ── Windows font-name mappings ──────────────────────────────────────────
    // Maps CSS-style family + style to the Windows TTF file base-name (no extension).
    // Reference: https://learn.microsoft.com/en-us/typography/font-list/

    private static string GetWindowsFaceName(string normalized, bool isBold, bool isItalic) =>
        (normalized, isBold, isItalic) switch
        {
            // Courier New
            ("couriernew" or "courier" or "monospace", false, false) => "cour",
            ("couriernew" or "courier" or "monospace", true,  false) => "courbd",
            ("couriernew" or "courier" or "monospace", false, true)  => "couri",
            ("couriernew" or "courier" or "monospace", true,  true)  => "courbi",
            // Arial / Helvetica
            ("arial" or "helvetica" or "sansserif", false, false) => "arial",
            ("arial" or "helvetica" or "sansserif", true,  false) => "arialbd",
            ("arial" or "helvetica" or "sansserif", false, true)  => "ariali",
            ("arial" or "helvetica" or "sansserif", true,  true)  => "arialbi",
            // Times New Roman
            ("timesnewroman" or "times" or "serif", false, false) => "times",
            ("timesnewroman" or "times" or "serif", true,  false) => "timesbd",
            ("timesnewroman" or "times" or "serif", false, true)  => "timesi",
            ("timesnewroman" or "times" or "serif", true,  true)  => "timesbi",
            // Verdana
            ("verdana", false, false) => "verdana",
            ("verdana", true,  false) => "verdanab",
            ("verdana", false, true)  => "verdanai",
            ("verdana", true,  true)  => "verdanaz",
            // Georgia
            ("georgia", false, false) => "georgia",
            ("georgia", true,  false) => "georgiab",
            ("georgia", false, true)  => "georgiai",
            ("georgia", true,  true)  => "georgiaz",
            // Segoe UI
            ("segoeui", false, false) => "segoeui",
            ("segoeui", true,  false) => "segoeuib",
            ("segoeui", false, true)  => "segoeuii",
            ("segoeui", true,  true)  => "segoeuiz",
            // Calibri
            ("calibri", false, false) => "calibri",
            ("calibri", true,  false) => "calibrib",
            ("calibri", false, true)  => "calibrii",
            ("calibri", true,  true)  => "calibriz",
            // Tahoma
            ("tahoma", false, _) => "tahoma",
            ("tahoma", true,  _) => "tahomabd",
            // Default: Arial
            _ => isBold && isItalic ? "arialbi"
               : isBold             ? "arialbd"
               : isItalic           ? "ariali"
               :                      "arial"
        };

    // ── Cross-platform (Liberation / DejaVu) name mappings ─────────────────

    private static string GetCrossPlatformFaceName(string normalized, bool isBold, bool isItalic)
    {
        var suffix = (isBold, isItalic) switch
        {
            (true,  true)  => "-BoldItalic",
            (true,  false) => "-Bold",
            (false, true)  => "-Italic",
            _              => "-Regular"
        };

        return normalized switch
        {
            "arial" or "helvetica" or "sansserif" => $"LiberationSans{suffix}",
            "timesnewroman" or "times" or "serif"  => $"LiberationSerif{suffix}",
            "couriernew" or "courier" or "monospace" => $"LiberationMono{suffix}",
            "verdana"  => $"DejaVuSans{suffix}",
            "georgia"  => $"DejaVuSerif{suffix}",
            "segoeui"  => $"LiberationSans{suffix}",
            _          => $"LiberationSans{suffix}"
        };
    }

    // ── File search (Linux / macOS) ─────────────────────────────────────────

    private static string? FindFontFile(string faceName)
    {
        foreach (var dir in GetLinuxMacFontDirectories())
        {
            if (!Directory.Exists(dir)) continue;

            foreach (var ext in new[] { ".ttf", ".otf" })
            {
                var direct = Path.Combine(dir, faceName + ext);
                if (File.Exists(direct)) return direct;

                var lower = Path.Combine(dir, faceName.ToLowerInvariant() + ext);
                if (File.Exists(lower)) return lower;
            }

            try
            {
                var pattern = faceName
                    .Replace("-Regular", "").Replace("-Bold", "")
                    .Replace("-Italic", "").Replace("-BoldItalic", "");

                foreach (var file in Directory.EnumerateFiles(dir, "*.ttf", SearchOption.AllDirectories))
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    if (name.Equals(faceName, StringComparison.OrdinalIgnoreCase) ||
                        name.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                        return file;
                }
            }
            catch { /* access denied or similar — skip */ }
        }

        return null;
    }

    private static IEnumerable<string> GetLinuxMacFontDirectories()
    {
        if (OperatingSystem.IsLinux())
        {
            yield return "/usr/share/fonts";
            yield return "/usr/local/share/fonts";
            yield return "/usr/share/fonts/truetype";
            yield return "/usr/share/fonts/truetype/liberation";
            yield return "/usr/share/fonts/truetype/dejavu";
            yield return "/usr/share/fonts/opentype";

            var home = Environment.GetEnvironmentVariable("HOME");
            if (!string.IsNullOrEmpty(home))
            {
                yield return Path.Combine(home, ".fonts");
                yield return Path.Combine(home, ".local", "share", "fonts");
            }
        }

        if (OperatingSystem.IsMacOS())
        {
            yield return "/System/Library/Fonts";
            yield return "/Library/Fonts";

            var home = Environment.GetEnvironmentVariable("HOME");
            if (!string.IsNullOrEmpty(home))
                yield return Path.Combine(home, "Library", "Fonts");
        }
    }
}
