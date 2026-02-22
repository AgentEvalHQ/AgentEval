// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentEval.RedTeam.Baseline;

/// <summary>
/// Serializes and deserializes RedTeam baselines.
/// </summary>
public static class BaselineSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Saves a baseline to a JSON file.
    /// </summary>
    public static async Task SaveAsync(
        RedTeamBaseline baseline,
        string path,
        CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(baseline, Options);
        await File.WriteAllTextAsync(path, json, cancellationToken);
    }

    /// <summary>
    /// Loads a baseline from a JSON file.
    /// </summary>
    public static async Task<RedTeamBaseline> LoadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Baseline file not found: {path}", path);
        }

        var json = await File.ReadAllTextAsync(path, cancellationToken);
        return JsonSerializer.Deserialize<RedTeamBaseline>(json, Options)
            ?? throw new InvalidOperationException($"Failed to deserialize baseline from: {path}");
    }

    /// <summary>
    /// Converts a baseline to JSON string.
    /// </summary>
    public static string ToJson(RedTeamBaseline baseline)
        => JsonSerializer.Serialize(baseline, Options);

    /// <summary>
    /// Parses a baseline from JSON string.
    /// </summary>
    public static RedTeamBaseline FromJson(string json)
        => JsonSerializer.Deserialize<RedTeamBaseline>(json, Options)
            ?? throw new InvalidOperationException("Failed to deserialize baseline from JSON");
}
