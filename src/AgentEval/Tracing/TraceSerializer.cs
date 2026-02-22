// SPDX-License-Identifier: MIT
// Copyright (c) 2025 AgentEval. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;

namespace AgentEval.Tracing;

/// <summary>
/// Serializes and deserializes AgentTrace objects to/from JSON.
/// </summary>
public static class TraceSerializer
{
    private static readonly JsonSerializerOptions DefaultOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Serializes an AgentTrace to a stream.
    /// </summary>
    public static async Task SerializeAsync(AgentTrace trace, Stream stream, CancellationToken cancellationToken = default)
    {
        await JsonSerializer.SerializeAsync(stream, trace, DefaultOptions, cancellationToken);
    }

    /// <summary>
    /// Serializes an AgentTrace to a JSON string.
    /// </summary>
    public static async Task<string> SerializeToStringAsync(AgentTrace trace, CancellationToken cancellationToken = default)
    {
        using var stream = new MemoryStream();
        await SerializeAsync(trace, stream, cancellationToken);
        stream.Position = 0;
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    /// <summary>
    /// Deserializes an AgentTrace from a stream.
    /// </summary>
    public static async Task<AgentTrace> DeserializeAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var trace = await JsonSerializer.DeserializeAsync<AgentTrace>(stream, DefaultOptions, cancellationToken);
        return trace ?? throw new InvalidOperationException("Failed to deserialize trace: result was null.");
    }

    /// <summary>
    /// Deserializes an AgentTrace from a JSON string.
    /// </summary>
    public static async Task<AgentTrace> DeserializeFromStringAsync(string json, CancellationToken cancellationToken = default)
    {
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        return await DeserializeAsync(stream, cancellationToken);
    }

    /// <summary>
    /// Loads an AgentTrace from a file.
    /// </summary>
    public static async Task<AgentTrace> LoadFromFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(filePath);
        return await DeserializeAsync(stream, cancellationToken);
    }

    /// <summary>
    /// Saves an AgentTrace to a file.
    /// </summary>
    public static async Task SaveToFileAsync(AgentTrace trace, string filePath, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = File.Create(filePath);
        await SerializeAsync(trace, stream, cancellationToken);
    }
}
