// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentEval.Snapshots;

/// <summary>
/// Manages saving and loading snapshots.
/// <para>
/// <b>Thread Safety:</b> Instances of <see cref="SnapshotStore"/> are thread-safe for concurrent
/// save/load operations on different test names. Concurrent operations on the same test name
/// may produce race conditions at the file system level.
/// </para>
/// </summary>
public class SnapshotStore : ISnapshotStore
{
    private readonly string _basePath;

    private static readonly JsonSerializerOptions s_serializerOptions = new()
    {
        WriteIndented = true
    };

    /// <summary>
    /// Initializes a new snapshot store.
    /// </summary>
    /// <param name="basePath">Base directory for snapshots.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="basePath"/> is null or whitespace.</exception>
    public SnapshotStore(string basePath)
    {
        if (string.IsNullOrWhiteSpace(basePath))
            throw new ArgumentException("Base path cannot be null or empty.", nameof(basePath));

        _basePath = basePath;
        Directory.CreateDirectory(basePath);
    }

    /// <summary>
    /// Gets the path for a snapshot file.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when <paramref name="suffix"/> contains path separator characters.</exception>
    public string GetSnapshotPath(string testName, string suffix = "")
    {
        // Sanitize suffix to prevent path traversal (CODE-22 fix)
        if (!string.IsNullOrEmpty(suffix))
        {
            if (suffix.IndexOfAny(new[] { '/', '\\', '.' }) >= 0)
                throw new ArgumentException(
                    $"Suffix must not contain path separators or dots to prevent path traversal. Got: '{suffix}'",
                    nameof(suffix));
        }

        var sanitized = SanitizeFileName(testName);
        var fileName = string.IsNullOrEmpty(suffix) ? $"{sanitized}.json" : $"{sanitized}.{suffix}.json";
        return Path.Combine(_basePath, fileName);
    }

    /// <summary>
    /// Saves a snapshot.
    /// </summary>
    public async Task SaveAsync<T>(string testName, T value, string suffix = "", CancellationToken cancellationToken = default)
    {
        var path = GetSnapshotPath(testName, suffix);
        var json = JsonSerializer.Serialize(value, s_serializerOptions);
        await File.WriteAllTextAsync(path, json, cancellationToken);
    }

    /// <summary>
    /// Loads a snapshot if it exists. Returns <c>default</c> if the snapshot file is not found.
    /// </summary>
    public async Task<T?> LoadAsync<T>(string testName, string suffix = "", CancellationToken cancellationToken = default)
    {
        var path = GetSnapshotPath(testName, suffix);

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken);
            return JsonSerializer.Deserialize<T>(json);
        }
        catch (FileNotFoundException)
        {
            return default;
        }
        catch (DirectoryNotFoundException)
        {
            return default;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Snapshot file '{path}' contains invalid JSON and cannot be deserialized.", ex);
        }
    }

    /// <summary>
    /// Checks if a snapshot exists.
    /// </summary>
    public bool Exists(string testName, string suffix = "")
    {
        return File.Exists(GetSnapshotPath(testName, suffix));
    }

    /// <summary>
    /// Deletes a snapshot if it exists.
    /// </summary>
    /// <returns><c>true</c> if the file was deleted; <c>false</c> if it did not exist.</returns>
    public bool Delete(string testName, string suffix = "")
    {
        var path = GetSnapshotPath(testName, suffix);
        if (File.Exists(path))
        {
            File.Delete(path);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Lists all snapshot names in the store.
    /// </summary>
    public IReadOnlyList<string> ListSnapshots()
    {
        if (!Directory.Exists(_basePath))
            return Array.Empty<string>();

        return Directory.GetFiles(_basePath, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => name is not null)
            .Select(name => name!)
            .ToList()
            .AsReadOnly();
    }

    /// <summary>
    /// Gets the number of snapshots in the store.
    /// </summary>
    public int Count
    {
        get
        {
            if (!Directory.Exists(_basePath))
                return 0;
            return Directory.GetFiles(_basePath, "*.json").Length;
        }
    }

    // Characters that should be sanitized for cross-platform file name compatibility
    private static readonly HashSet<char> s_invalidFileNameChars = new(
        Path.GetInvalidFileNameChars()
            .Concat(new[] { ':', '\\', '/', '*', '?', '"', '<', '>', '|' }));

    internal static string SanitizeFileName(string name)
    {
        var sanitized = new string(name.Select(c => s_invalidFileNameChars.Contains(c) ? '_' : c).ToArray());

        // Append a short hash to avoid collisions (CODE-17 fix)
        // e.g., "test:1" and "test/1" both sanitize to "test_1" — hash differentiates them
        if (sanitized != name)
        {
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..8];
            sanitized = $"{sanitized}_{hash}";
        }

        return sanitized;
    }
}
