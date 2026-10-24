// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text;
using System.Text.Json;
using AgentEval.Output;
using AgentEval.Results.Json;

namespace AgentEval.Results.Adapters.StoreV1;

public static partial class StoreV1Exporter
{
    private static T? ReadOptional<T>(string path) where T : class => Path.Exists(path) ? ReadRequired<T>(path) : null;

    // Check the raw facts before DTO constructors can replace missing value types with zero/false.
    // In-memory semantic readers already supply typed observations and do not pass through this boundary.
    private static T ReadRequired<T>(string path) where T : class
    {
        try
        {
            // Preserve the legacy reader's BOM-compatible decoding, then reject ambiguous JSON uniformly.
            var text = File.ReadAllText(path);
            AefJsonReader.Check(Encoding.UTF8.GetBytes(text), 1 << 30);
            using var document = JsonDocument.Parse(text);
            RecordedFacts<T>(document.RootElement);
            return document.RootElement.Deserialize<T>(s_store)
                ?? throw InvalidSource($"The legacy file {Path.GetFileName(path)} contains null.");
        }
        catch (Exception error) when (error is JsonException or AefReadException)
        {
            throw InvalidSource($"The legacy file {Path.GetFileName(path)} contains unreadable or ambiguous JSON.", error);
        }
    }

    private static void RecordedFacts<T>(JsonElement root)
    {
        if (typeof(T) == typeof(ScenarioResult))
        {
            Fact(root, "passed", JsonValueKind.True, JsonValueKind.False);
            if (root.TryGetProperty("assertions", out var assertions) && assertions.ValueKind == JsonValueKind.Array)
                foreach (var assertion in assertions.EnumerateArray()) Fact(assertion, "passed", JsonValueKind.True, JsonValueKind.False);
        }
        else if (typeof(T) == typeof(RunManifest))
        {
            Fact(Fact(root, "solution", JsonValueKind.Object), "id", JsonValueKind.String);
            Fact(Fact(root, "subject", JsonValueKind.Object), "kind", JsonValueKind.String, JsonValueKind.Number);
            var run = Fact(root, "run", JsonValueKind.Object);
            Fact(run, "timestamp", JsonValueKind.String);
            Fact(run, "duration", JsonValueKind.String);
            Fact(run, "scenarioCount", JsonValueKind.Number);
            Fact(run, "verdict", JsonValueKind.String);
            Fact(Fact(root, "git", JsonValueKind.Object), "dirty", JsonValueKind.True, JsonValueKind.False);
            Fact(Fact(root, "environment", JsonValueKind.Object), "ci", JsonValueKind.True, JsonValueKind.False);
        }
        else if (typeof(T) == typeof(RunSummary))
        {
            var stats = Fact(root, "stats", JsonValueKind.Object);
            foreach (var name in new[] { "total", "passed", "failed", "warnings" }) Fact(stats, name, JsonValueKind.Number);
            // Skipped and measurement metadata have documented historical defaults; they remain optional.
            if (root.TryGetProperty("cost", out var cost) && cost.ValueKind != JsonValueKind.Null)
                foreach (var name in new[] { "estimatedCost", "promptTokens", "completionTokens" }) Fact(cost, name, JsonValueKind.Number);
        }
    }

    private static JsonElement Fact(JsonElement owner, string name, params JsonValueKind[] kinds) =>
        owner.ValueKind == JsonValueKind.Object && owner.TryGetProperty(name, out var value) && kinds.Contains(value.ValueKind)
            ? value : throw InvalidSource($"A required recorded legacy fact is missing or has the wrong type: {name}.");
}
