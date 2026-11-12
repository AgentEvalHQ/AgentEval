// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AgentEval.DataLoaders;

namespace AgentEval.Prompts;

/// <summary>Bounded, non-executable prompt-pack files. Only local Markdown files below the manifest directory are read.</summary>
public sealed class JudgePromptPackLoader : IJudgePromptPackLoader
{
    private const int MaxManifestBytes = 1048576;
    private const int MaxMessageBytes = 524288;
    private const int MaxPackBytes = 16 * 1048576;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };
    private static readonly (string File, string Text)[] Messages =
        [("systemFile", "systemText"), ("userFile", "userTemplate"), ("retryFile", "retryText"), ("safetyFile", "safetyText")];

    /// <inheritdoc />
    public async Task<JudgePromptPack> LoadAsync(string manifestPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        var path = Path.GetFullPath(manifestPath);
        EnsureRegularPath(path);
        var root = Path.GetDirectoryName(path)!;
        var bytes = await ReadBoundedAsync(path, MaxManifestBytes, cancellationToken).ConfigureAwait(false);
        var usedBytes = bytes.Length;
        JsonObject manifest;
        try
        {
            var text = Utf8.GetString(bytes).TrimStart('\uFEFF');
            if (Path.GetExtension(path).ToLowerInvariant() is ".yaml" or ".yml")
                manifest = new BoundedYamlObjectReader(text).Read();
            else if (Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase))
            {
                using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 32 });
                CheckDuplicates(doc.RootElement);
                manifest = JsonNode.Parse(text) as JsonObject ?? throw Invalid("manifest root is not an object");
            }
            else throw Invalid("unsupported manifest extension; use .json, .yaml or .yml");
            if (manifest.Any(p => p.Key is not ("schemaVersion" or "definitions")) || manifest["schemaVersion"]?.GetValue<int>() != 1)
                throw Invalid("unknown manifest field or schema version; expected schemaVersion 1");
            if (manifest["definitions"] is not JsonArray entries || entries.Count is 0 or > 256)
                throw Invalid("expected 1–256 definitions");
            var definitions = new List<JudgePromptDefinition>();
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry is not JsonObject item) throw Invalid("definition is not an object");
                foreach (var (file, content) in Messages)
                {
                    if (item.ContainsKey(content)) throw Invalid("inline message content is not supported; use Markdown file references");
                    var relative = item[file]?.GetValue<string>() ?? throw Invalid($"missing {file}");
                    var messagePath = ResolveMarkdownPath(root, relative);
                    var message = await ReadBoundedAsync(messagePath, Math.Min(MaxMessageBytes, MaxPackBytes - usedBytes), cancellationToken).ConfigureAwait(false);
                    usedBytes += message.Length;
                    item.Remove(file);
                    item[content] = Utf8.GetString(message).TrimStart('\uFEFF');
                }
                definitions.Add(item.Deserialize<JudgePromptDefinition>(JsonOptions) ?? throw Invalid("empty definition"));
            }
            return new JudgePromptPack(definitions);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or DecoderFallbackException)
        {
            throw Invalid("invalid metadata, field type or UTF-8 content", ex);
        }
    }

    /// <inheritdoc />
    public async Task ExportAsync(JudgePromptPack pack, string directory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        cancellationToken.ThrowIfCancellationRequested();
        var destination = Path.GetFullPath(directory);
        if (Directory.Exists(destination) || File.Exists(destination)) throw Invalid("export destination already exists; choose a new directory");
        var parent = Path.GetDirectoryName(destination) ?? throw Invalid("export destination has no parent");
        EnsureRegularPath(parent);
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, ".prompt-pack-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        var created = new List<string>();
        try
        {
            var definitions = new JsonArray();
            for (var index = 0; index < pack.Definitions.Count; index++)
            {
                var item = JsonSerializer.SerializeToNode(pack.Definitions[index], JsonOptions)!.AsObject();
                foreach (var (file, content) in Messages)
                {
                    var name = $"{index + 1:D3}-{file[..^4]}.md";
                    var output = Path.Combine(staging, name);
                    created.Add(output);
                    await File.WriteAllTextAsync(output, item[content]!.GetValue<string>(), Utf8, cancellationToken).ConfigureAwait(false);
                    item.Remove(content);
                    item[file] = name;
                }
                definitions.Add(item);
            }
            var manifest = new JsonObject { ["schemaVersion"] = 1, ["definitions"] = definitions };
            var manifestPath = Path.Combine(staging, "prompts.json");
            created.Add(manifestPath);
            await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString(JsonOptions), Utf8, cancellationToken).ConfigureAwait(false);
            // Round-trip applies aggregate limits too; no unusable export is promoted into place.
            _ = await LoadAsync(manifestPath, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(staging, destination);
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                foreach (var file in created) File.Delete(file);
                Directory.Delete(staging);
            }
        }
    }

    private static string ResolveMarkdownPath(string root, string relative)
    {
        var segments = relative.Replace('\\', '/').Split('/');
        if (relative.Length is 0 or > 512 || Path.IsPathRooted(relative) || relative.Contains(':') ||
            segments.Any(s => s is "" or "." or "..") || !Path.GetExtension(relative).Equals(".md", StringComparison.OrdinalIgnoreCase))
            throw Invalid("message path must be a relative .md path without traversal or drive syntax");
        var path = Path.GetFullPath(Path.Combine(root, Path.Combine(segments)));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, comparison))
            throw Invalid("message path escapes the pack directory");
        EnsureRegularPath(path);
        return path;
    }

    private static void EnsureRegularPath(string path)
    {
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw Invalid("symbolic links or reparse points are not supported in pack paths; copy files into a regular directory");
    }

    private static async Task<byte[]> ReadBoundedAsync(string path, int limit, CancellationToken ct)
    {
        if (limit <= 0) throw Invalid("pack exceeds its 16 MiB aggregate bound");
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, FileOptions.Asynchronous);
        using var output = new MemoryStream();
        var block = new byte[8192];
        while (output.Length <= limit)
        {
            var read = await input.ReadAsync(block.AsMemory(0, Math.Min(block.Length, limit + 1 - (int)output.Length)), ct).ConfigureAwait(false);
            if (read == 0) break;
            output.Write(block, 0, read);
        }
        if (output.Length > limit) throw Invalid("manifest, message or aggregate byte limit exceeded");
        return output.ToArray();
    }

    private static void CheckDuplicates(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in node.EnumerateObject())
            {
                if (!keys.Add(property.Name)) throw Invalid("duplicate JSON property");
                CheckDuplicates(property.Value);
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
            foreach (var child in node.EnumerateArray()) CheckDuplicates(child);
    }

    private static InvalidDataException Invalid(string actual, Exception? inner = null) => new(
        $"Expected: a bounded, unambiguous local prompt pack. Actual: {actual}. Suggestions: validate exported metadata and Markdown paths. Because: packs must be reproducible and cannot execute code.", inner);
}
