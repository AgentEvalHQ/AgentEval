// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
using PdfSharp.Fonts;

namespace AgentEval.RedTeam.Reporting.Pdf;

/// <summary>
/// Cross-platform font resolver for PdfSharp that works on Windows, Linux, and macOS.
/// Uses fallback fonts when system fonts are not available.
/// </summary>
public sealed class CrossPlatformFontResolver : IFontResolver
{
    private static readonly Lazy<CrossPlatformFontResolver> s_instance = new(() => new CrossPlatformFontResolver());
    private static bool s_isRegistered;
    private static readonly object s_lock = new();

    /// <summary>
    /// Gets the singleton instance of the font resolver.
    /// </summary>
    public static CrossPlatformFontResolver Instance => s_instance.Value;

    /// <summary>
    /// Registers this font resolver with PdfSharp if not already registered.
    /// Call this before generating any PDF documents on non-Windows platforms.
    /// On Windows, the default font resolution is used.
    /// </summary>
    public static void Register()
    {
        // On Windows, PdfSharp uses GDI+ which handles fonts natively
        // Only register the custom resolver on non-Windows platforms
        if (OperatingSystem.IsWindows()) return;
        
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
                // Font resolver already set (happens in concurrent scenarios)
                s_isRegistered = true;
            }
        }
    }

    /// <inheritdoc />
    public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic)
    {
        // Normalize family name for lookup
        var normalizedName = familyName.ToLowerInvariant().Replace(" ", "");
        
        // Map common fonts to their fallback names
        var faceName = GetFallbackFontName(normalizedName, isBold, isItalic);
        
        return new FontResolverInfo(faceName);
    }

    /// <inheritdoc />
    public byte[]? GetFont(string faceName)
    {
        // Try to find the font in common locations
        var fontPath = FindFontFile(faceName);
        
        if (fontPath != null && File.Exists(fontPath))
        {
            return File.ReadAllBytes(fontPath);
        }

        // Return null to let PdfSharp try its default resolution
        return null;
    }

    private static string GetFallbackFontName(string normalizedName, bool isBold, bool isItalic)
    {
        // Build suffix based on style
        var suffix = GetStyleSuffix(isBold, isItalic);
        
        // Common font mappings
        return normalizedName switch
        {
            "arial" or "helvetica" or "sansserif" => $"LiberationSans{suffix}",
            "timesnewroman" or "times" or "serif" => $"LiberationSerif{suffix}",
            "couriernew" or "courier" or "monospace" => $"LiberationMono{suffix}",
            "verdana" => $"DejaVuSans{suffix}",
            "georgia" => $"DejaVuSerif{suffix}",
            "segoeui" => $"LiberationSans{suffix}",
            _ => $"LiberationSans{suffix}" // Default fallback
        };
    }

    private static string GetStyleSuffix(bool isBold, bool isItalic)
    {
        return (isBold, isItalic) switch
        {
            (true, true) => "-BoldItalic",
            (true, false) => "-Bold",
            (false, true) => "-Italic",
            _ => "-Regular"
        };
    }

    private static string? FindFontFile(string faceName)
    {
        // Common font directories on different platforms
        var fontDirs = GetFontDirectories();
        
        // Common file extensions
        var extensions = new[] { ".ttf", ".otf", ".TTF", ".OTF" };
        
        foreach (var dir in fontDirs)
        {
            if (!Directory.Exists(dir)) continue;
            
            foreach (var ext in extensions)
            {
                // Try direct match
                var directPath = Path.Combine(dir, faceName + ext);
                if (File.Exists(directPath)) return directPath;
                
                // Try lowercase
                var lowerPath = Path.Combine(dir, faceName.ToLowerInvariant() + ext);
                if (File.Exists(lowerPath)) return lowerPath;
            }
            
            // Try searching recursively for common font patterns
            try
            {
                var found = SearchFontInDirectory(dir, faceName);
                if (found != null) return found;
            }
            catch
            {
                // Ignore directory access errors
            }
        }
        
        return null;
    }

    private static string? SearchFontInDirectory(string dir, string faceName)
    {
        // Try to find font files that contain parts of the face name
        var searchPattern = faceName
            .Replace("-Regular", "")
            .Replace("-Bold", "")
            .Replace("-Italic", "")
            .Replace("-BoldItalic", "");
        
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*.ttf", SearchOption.AllDirectories))
            {
                var fileName = Path.GetFileNameWithoutExtension(file);
                if (fileName.Equals(faceName, StringComparison.OrdinalIgnoreCase) ||
                    fileName.Contains(searchPattern, StringComparison.OrdinalIgnoreCase))
                {
                    return file;
                }
            }
        }
        catch
        {
            // Access denied or other errors
        }
        
        return null;
    }

    private static IEnumerable<string> GetFontDirectories()
    {
        // Windows font directories  
        if (OperatingSystem.IsWindows())
        {
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts));
            yield return @"C:\Windows\Fonts";
            
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(localAppData))
            {
                yield return Path.Combine(localAppData, "Microsoft", "Windows", "Fonts");
            }
        }
        
        // Linux font directories
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
        
        // macOS font directories
        if (OperatingSystem.IsMacOS())
        {
            yield return "/System/Library/Fonts";
            yield return "/Library/Fonts";
            
            var home = Environment.GetEnvironmentVariable("HOME");
            if (!string.IsNullOrEmpty(home))
            {
                yield return Path.Combine(home, "Library", "Fonts");
            }
        }
    }
}
