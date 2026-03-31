// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AgentEval.Memory.Models;

namespace AgentEval.Memory.Reporting;

/// <summary>
/// File-system-based baseline store that persists baselines as individual JSON files.
/// On each save: writes baseline JSON, rebuilds manifest.json, copies report template if missing.
/// </summary>
public partial class JsonFileBaselineStore : IBaselineStore
{
    private readonly MemoryReportingOptions _options;

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    /// <summary>
    /// Creates a store with default options (.agenteval/benchmarks/{AgentName}).
    /// </summary>
    public JsonFileBaselineStore() : this(new MemoryReportingOptions())
    {
    }

    /// <summary>
    /// Creates a store with custom options.
    /// </summary>
    public JsonFileBaselineStore(MemoryReportingOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task SaveAsync(MemoryBaseline baseline, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseline);

        var rootPath = ResolveRootPath(baseline.AgentConfig.AgentName);
        var baselinesDir = Path.Combine(rootPath, "baselines");
        Directory.CreateDirectory(baselinesDir);

        var filename = $"{baseline.Timestamp:yyyy-MM-dd}_{Slugify(baseline.Name)}.json";
        var path = Path.Combine(baselinesDir, filename);
        var json = JsonSerializer.Serialize(baseline, JsonOptions);
        await File.WriteAllTextAsync(path, json, ct);

        await RebuildManifestAsync(rootPath, baseline.AgentConfig, ct);

        if (_options.AutoCopyReportTemplate)
            await EnsureEmbeddedResourceAsync(rootPath, "report.html", ct);
        if (_options.IncludeArchetypes)
            await EnsureEmbeddedResourceAsync(rootPath, "archetypes.json", ct);
    }

    public async Task<MemoryBaseline?> LoadAsync(string id, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(id);

        var rootPattern = _options.OutputPath.Replace("{AgentName}", "*");
        var parentDir = Path.GetDirectoryName(rootPattern) ?? ".";
        if (!Directory.Exists(parentDir)) return null;

        foreach (var agentDir in Directory.GetDirectories(parentDir))
        {
            var baseline = await FindBaselineInDirectoryAsync(
                Path.Combine(agentDir, "baselines"), id, ct);
            if (baseline != null) return baseline;
        }

        return null;
    }

    public async Task<IReadOnlyList<MemoryBaseline>> ListAsync(
        string? agentName = null,
        IEnumerable<string>? tags = null,
        CancellationToken ct = default)
    {
        var results = new List<MemoryBaseline>();
        var tagSet = tags?.ToHashSet(StringComparer.OrdinalIgnoreCase);

        string searchPath;
        if (agentName != null)
        {
            searchPath = ResolveRootPath(agentName);
        }
        else
        {
            var rootPattern = _options.OutputPath.Replace("{AgentName}", "");
            searchPath = Path.GetDirectoryName(rootPattern) ?? ".";
        }

        if (!Directory.Exists(searchPath)) return results;

        var baselinesDirs = agentName != null
            ? [Path.Combine(searchPath, "baselines")]
            : Directory.GetDirectories(searchPath).Select(d => Path.Combine(d, "baselines"));

        foreach (var dir in baselinesDirs)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.GetFiles(dir, "*.json"))
            {
                ct.ThrowIfCancellationRequested();
                var baseline = TryDeserializeBaseline(await File.ReadAllTextAsync(file, ct));
                if (baseline == null) continue;

                if (agentName != null &&
                    !string.Equals(baseline.AgentConfig.AgentName, agentName, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (tagSet is { Count: > 0 } &&
                    !tagSet.Any(t => baseline.Tags.Contains(t, StringComparer.OrdinalIgnoreCase)))
                    continue;

                results.Add(baseline);
            }
        }

        return results.OrderBy(b => b.Timestamp).ToList();
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(id);

        var rootPattern = _options.OutputPath.Replace("{AgentName}", "*");
        var parentDir = Path.GetDirectoryName(rootPattern) ?? ".";
        if (!Directory.Exists(parentDir)) return false;

        foreach (var agentDir in Directory.GetDirectories(parentDir))
        {
            var baselinesDir = Path.Combine(agentDir, "baselines");
            if (!Directory.Exists(baselinesDir)) continue;

            foreach (var file in Directory.GetFiles(baselinesDir, "*.json"))
            {
                ct.ThrowIfCancellationRequested();
                var baseline = TryDeserializeBaseline(await File.ReadAllTextAsync(file, ct));
                if (baseline?.Id == id)
                {
                    File.Delete(file);
                    await RebuildManifestAsync(agentDir, baseline.AgentConfig, ct);
                    return true;
                }
            }
        }

        return false;
    }

    // ═══════════════════════════════════════════════════════════════
    // Internal helpers
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Attempts to deserialize a baseline from JSON. Returns null for corrupt/invalid files
    /// instead of throwing, since the store must tolerate partially corrupt baseline directories.
    /// </summary>
    private static MemoryBaseline? TryDeserializeBaseline(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<MemoryBaseline>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AgentEval.Memory] Skipping corrupt baseline file: {ex.Message}");
            return null;
        }
    }

    private async Task<MemoryBaseline?> FindBaselineInDirectoryAsync(
        string baselinesDir, string id, CancellationToken ct)
    {
        if (!Directory.Exists(baselinesDir)) return null;

        foreach (var file in Directory.GetFiles(baselinesDir, "*.json"))
        {
            ct.ThrowIfCancellationRequested();
            var baseline = TryDeserializeBaseline(await File.ReadAllTextAsync(file, ct));
            if (baseline?.Id == id) return baseline;
        }

        return null;
    }

    private async Task RebuildManifestAsync(
        string rootPath, AgentBenchmarkConfig agentConfig, CancellationToken ct)
    {
        var baselinesDir = Path.Combine(rootPath, "baselines");
        if (!Directory.Exists(baselinesDir)) return;

        var entries = new List<(MemoryBaseline baseline, string relativePath)>();

        foreach (var file in Directory.GetFiles(baselinesDir, "*.json").OrderBy(f => f))
        {
            ct.ThrowIfCancellationRequested();
            var baseline = TryDeserializeBaseline(await File.ReadAllTextAsync(file, ct));
            if (baseline != null)
            {
                var relativePath = $"baselines/{Path.GetFileName(file)}";
                entries.Add((baseline, relativePath));
            }
        }

        var groups = entries
            .GroupBy(e => e.baseline.Benchmark.Preset)
            .Select(g => new ManifestBenchmarkGroup
            {
                BenchmarkId = $"memory-{g.Key.ToLowerInvariant()}",
                Preset = g.Key,
                Categories = g.First().baseline.CategoryResults.Keys.ToList(),
                Baselines = g.OrderBy(e => e.baseline.Timestamp).Select(e => new ManifestBaselineEntry
                {
                    Id = e.baseline.Id,
                    File = e.relativePath,
                    Name = e.baseline.Name,
                    ConfigurationId = e.baseline.ConfigurationId,
                    Timestamp = e.baseline.Timestamp,
                    OverallScore = e.baseline.OverallScore,
                    Grade = e.baseline.Grade,
                    Tags = e.baseline.Tags
                }).ToList()
            }).ToList();

        var manifest = new BenchmarkManifest
        {
            SchemaVersion = "1.0",
            GeneratedAt = DateTimeOffset.UtcNow,
            GeneratedBy = $"AgentEval.Memory v{typeof(JsonFileBaselineStore).Assembly.GetName().Version}",
            Agent = new ManifestAgentInfo { Name = agentConfig.AgentName },
            Benchmarks = groups,
            Archetypes = "archetypes.json"
        };

        var manifestJson = JsonSerializer.Serialize(manifest, JsonOptions);
        await File.WriteAllTextAsync(Path.Combine(rootPath, "manifest.json"), manifestJson, ct);
    }

    private static async Task EnsureEmbeddedResourceAsync(
        string rootPath, string resourceFileName, CancellationToken ct)
    {
        var outputPath = Path.Combine(rootPath, resourceFileName);
        if (File.Exists(outputPath)) return;

        var assembly = typeof(JsonFileBaselineStore).Assembly;
        var names = assembly.GetManifestResourceNames();
        var resourceName = names.FirstOrDefault(n => n.EndsWith(resourceFileName));
        if (resourceName == null) return;

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null) return;

        using var reader = new StreamReader(stream);
        var content = await reader.ReadToEndAsync(ct);
        await File.WriteAllTextAsync(outputPath, content, ct);
    }

    /// <summary>
    /// Gets the absolute path to the report directory for a given agent.
    /// </summary>
    public string GetReportDirectory(string agentName)
    {
        return Path.GetFullPath(ResolveRootPath(agentName));
    }

    /// <summary>
    /// Starts a local HTTP server in the report directory and opens the report in the default browser.
    /// The server runs in the background. Press Ctrl+C to stop it when done viewing.
    /// Returns the server process (or null if unable to start).
    /// </summary>
    /// <param name="agentName">The agent name to locate the report for.</param>
    /// <param name="port">Port for the HTTP server (default: 8080).</param>
    /// <returns>The server process, or null if no server could be started.</returns>
    public System.Diagnostics.Process? OpenReport(string agentName, int port = 8080)
    {
        var reportDir = Path.GetFullPath(ResolveRootPath(agentName));
        var reportFile = Path.Combine(reportDir, "report.html");

        if (!File.Exists(reportFile))
        {
            Console.WriteLine($"Report not found: {reportFile}");
            return null;
        }

        var url = $"http://localhost:{port}/report.html";

        // Try python first, then npx, then dotnet serve
        var serverCommands = new[]
        {
            ("python", $"-m http.server {port}"),
            ("python3", $"-m http.server {port}"),
            ("npx", $"serve -l {port} -s ."),
            ("dotnet", $"serve -p {port}"),
        };

        System.Diagnostics.Process? serverProcess = null;

        foreach (var (cmd, args) in serverCommands)
        {
            try
            {
                serverProcess = new System.Diagnostics.Process
                {
                    StartInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = cmd,
                        Arguments = args,
                        WorkingDirectory = reportDir,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };
                serverProcess.Start();

                // Give the server a moment to bind the port
                Thread.Sleep(800);

                if (!serverProcess.HasExited)
                {
                    Console.WriteLine($"\n   Server started: {cmd} {args}");
                    Console.WriteLine($"   Report directory: {reportDir}");
                    Console.WriteLine($"   Opening: {url}");
                    Console.WriteLine($"   (Press Ctrl+C to stop the server when done)\n");

                    // Open browser
                    try
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = url,
                            UseShellExecute = true
                        });
                    }
                    catch
                    {
                        Console.WriteLine($"   Could not open browser automatically. Open: {url}");
                    }

                    return serverProcess;
                }

                // Process exited immediately — command not found or failed
                serverProcess.Dispose();
                serverProcess = null;
            }
            catch
            {
                // Command not available, try next
                serverProcess?.Dispose();
                serverProcess = null;
            }
        }

        // No server could be started — print manual instructions
        Console.WriteLine($"\n   Could not start a local server automatically.");
        Console.WriteLine($"   Install Python, Node.js, or dotnet-serve, then run:");
        Console.WriteLine($"     cd \"{reportDir}\"");
        Console.WriteLine($"     python -m http.server {port}");
        Console.WriteLine($"   Then open: {url}\n");
        return null;
    }

    internal string ResolveRootPath(string agentName)
    {
        var sanitized = SanitizeName(agentName);
        return _options.OutputPath.Replace("{AgentName}", sanitized);
    }

    internal static string Slugify(string name)
    {
        var slug = SanitizeName(name);
        return slug.Length > 60 ? slug[..60].TrimEnd('-') : slug;
    }

    /// <summary>
    /// Shared sanitization: lowercase, replace non-alphanumeric with hyphens, trim.
    /// </summary>
    private static string SanitizeName(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "unnamed";
        var sanitized = SlugifyRegex().Replace(input.ToLowerInvariant(), "-").Trim('-');
        return string.IsNullOrEmpty(sanitized) ? "unnamed" : sanitized;
    }

    [GeneratedRegex(@"[^a-z0-9]+", RegexOptions.Compiled)]
    private static partial Regex SlugifyRegex();
}
