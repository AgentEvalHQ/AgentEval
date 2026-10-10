// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AgentEval.Models;

namespace AgentEval.Snapshots;

/// <summary>One tool call in a golden trace: the tool's name and its arguments as canonical JSON.</summary>
/// <param name="Name">The tool's name.</param>
/// <param name="Arguments">
/// The arguments as JSON with object keys sorted at every level, so two calls with the same arguments in a different key
/// order compare equal. <see langword="null"/> when the call carried no arguments.
/// </param>
public sealed record GoldenToolCall(string Name, string? Arguments);

/// <summary>One test case in a golden trace: its verdict, its output and the tool calls it made, in order.</summary>
/// <param name="Name">The test case's name.</param>
/// <param name="Passed">Whether the test case passed.</param>
/// <param name="Score">Its score (0–100).</param>
/// <param name="Output">The agent's final output, or <see langword="null"/> when there was none.</param>
/// <param name="ToolCalls">
/// The tool calls in the order they were made, or <see langword="null"/> when the run recorded no tool data (the
/// adapter returned no messages, or tool tracking was off). An empty list means tool data was recorded and no tool was
/// called.
/// </param>
public sealed record GoldenTraceCase(
    string Name,
    bool Passed,
    int Score,
    string? Output,
    IReadOnlyList<GoldenToolCall>? ToolCalls);

/// <summary>
/// A saved run to compare later runs against: for each test case, its verdict, its output and the tool calls it made.
/// Commit it beside the dataset; <see cref="GoldenTraceComparer"/> says what a rerun changed.
/// </summary>
/// <param name="SchemaVersion">The file format version; <see cref="CurrentSchemaVersion"/> when written.</param>
/// <param name="CreatedAt">When the run was saved.</param>
/// <param name="Model">The model the run used, when known.</param>
/// <param name="Cases">The test cases, in run order.</param>
public sealed record GoldenTrace(
    int SchemaVersion,
    DateTimeOffset CreatedAt,
    string? Model,
    IReadOnlyList<GoldenTraceCase> Cases)
{
    /// <summary>The schema version this release writes and reads.</summary>
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions s_json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Builds a golden trace from a run's test results.</summary>
    /// <param name="results">The run's results, in run order.</param>
    /// <param name="model">The model the run used, when known.</param>
    /// <param name="createdAt">When the run was saved; the current time when omitted.</param>
    public static GoldenTrace FromResults(
        IEnumerable<TestResult> results, string? model = null, DateTimeOffset? createdAt = null)
    {
        ArgumentNullException.ThrowIfNull(results);
        return new GoldenTrace(
            CurrentSchemaVersion,
            createdAt ?? DateTimeOffset.UtcNow,
            model,
            results.Select(r => new GoldenTraceCase(
                r.TestName,
                r.Passed,
                r.Score,
                r.ActualOutput,
                r.ToolUsage?.Calls.OrderBy(c => c.Order).Select(c => new GoldenToolCall(c.Name, CanonicalArguments(c.Arguments))).ToList()))
                .ToList());
    }

    /// <summary>Writes the trace as indented JSON, so a change to it reads well in a diff.</summary>
    public async Task SaveAsync(string path, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, this, s_json, ct).ConfigureAwait(false);
    }

    /// <summary>Reads a trace written by <see cref="SaveAsync"/>.</summary>
    /// <exception cref="InvalidDataException">The file is not a golden trace, or was written by a newer schema.</exception>
    public static async Task<GoldenTrace> LoadAsync(string path, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        await using var stream = File.OpenRead(path);
        GoldenTrace? trace;
        try
        {
            trace = await JsonSerializer.DeserializeAsync<GoldenTrace>(stream, s_json, ct).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"'{path}' is not a golden trace: {ex.Message}", ex);
        }

        if (trace?.Cases is null)
            throw new InvalidDataException($"'{path}' is not a golden trace: it has no cases.");
        if (trace.SchemaVersion > CurrentSchemaVersion)
            throw new InvalidDataException(
                $"'{path}' was written with golden-trace schema {trace.SchemaVersion}; this release reads up to {CurrentSchemaVersion}.");
        return trace;
    }

    /// <summary>The arguments as JSON with object keys sorted at every level, or null when there are none.</summary>
    internal static string? CanonicalArguments(IDictionary<string, object?>? arguments)
    {
        if (arguments is null || arguments.Count == 0)
            return null;
        return Canonical(JsonSerializer.SerializeToNode(arguments))?.ToJsonString();
    }

    private static JsonNode? Canonical(JsonNode? node) => node switch
    {
        JsonObject obj => new JsonObject(obj
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => KeyValuePair.Create(p.Key, Canonical(p.Value)))),
        JsonArray array => new JsonArray(array.Select(Canonical).ToArray()),
        null => null,
        _ => node.DeepClone(),
    };
}

/// <summary>How a test case's rerun differs from its golden trace. Ordered from most to least serious.</summary>
public enum TraceChange
{
    /// <summary>It passed in the golden trace and fails now.</summary>
    Regressed = 0,

    /// <summary>It failed in the golden trace and passes now.</summary>
    Improved = 1,

    /// <summary>Same verdict, but a different sequence of tool calls: a tool added, dropped, reordered, or called with other arguments.</summary>
    ToolsChanged = 2,

    /// <summary>Same verdict and tool calls, different output.</summary>
    OutputChanged = 3,

    /// <summary>Same verdict, tool calls and output.</summary>
    Unchanged = 4,

    /// <summary>In this run but not in the golden trace.</summary>
    Added = 5,

    /// <summary>In the golden trace but not in this run.</summary>
    Removed = 6,
}

/// <summary>One test case's comparison with its golden trace.</summary>
/// <param name="Name">The test case's name.</param>
/// <param name="Change">The most serious change; <see cref="ToolsChanged"/> and <see cref="OutputChanged"/> are also set on a verdict change.</param>
/// <param name="ToolsChanged">Whether the tool calls differ (false when either side recorded no tool data).</param>
/// <param name="OutputChanged">Whether the output differs.</param>
/// <param name="Detail">What changed, in a sentence, or <see langword="null"/> when nothing did.</param>
public sealed record TraceCaseComparison(string Name, TraceChange Change, bool ToolsChanged, bool OutputChanged, string? Detail);

/// <summary>A rerun compared with its golden trace, case by case.</summary>
/// <param name="Cases">One entry per test case in either run.</param>
public sealed record GoldenTraceComparison(IReadOnlyList<TraceCaseComparison> Cases)
{
    /// <summary>The number of test cases with the given change.</summary>
    public int Count(TraceChange change) => Cases.Count(c => c.Change == change);

    /// <summary>True when a test case that passed in the golden trace fails now.</summary>
    public bool HasRegression => Cases.Any(c => c.Change == TraceChange.Regressed);

    /// <summary>True when any test case called different tools, whatever its verdict.</summary>
    public bool HasToolChange => Cases.Any(c => c.ToolsChanged);
}

/// <summary>Compares a rerun with its golden trace.</summary>
/// <remarks>
/// <para>
/// Test cases are matched by name; a name used more than once is matched by its occurrence (the second "refund" with
/// the second "refund"). Outputs are compared after trimming and normalising line endings, so a change of output is
/// any change of text. Model output varies between runs, so an output change is reported, not a failure in itself.
/// </para>
/// <para>
/// Tool calls are compared in order, by name and by canonical arguments. When either run recorded no tool data, the tool
/// calls are not compared at all, rather than read as "no tools called".
/// </para>
/// </remarks>
public static class GoldenTraceComparer
{
    /// <summary>Compares <paramref name="current"/> with <paramref name="golden"/>.</summary>
    public static GoldenTraceComparison Compare(GoldenTrace golden, GoldenTrace current)
    {
        ArgumentNullException.ThrowIfNull(golden);
        ArgumentNullException.ThrowIfNull(current);

        var goldenKeyed = Keyed(golden.Cases);
        var currentKeyed = Keyed(current.Cases);
        var goldenByKey = goldenKeyed.ToDictionary(k => k.Key, k => k.Case, StringComparer.Ordinal);
        var currentKeys = currentKeyed.Select(k => k.Key).ToHashSet(StringComparer.Ordinal);
        var results = new List<TraceCaseComparison>();

        foreach (var (key, now) in currentKeyed)
        {
            if (!goldenByKey.TryGetValue(key, out var then))
            {
                results.Add(new TraceCaseComparison(now.Name, TraceChange.Added, false, false, "not in the golden trace"));
                continue;
            }

            var toolsChanged = then.ToolCalls is not null && now.ToolCalls is not null && !SameCalls(then.ToolCalls, now.ToolCalls);
            var outputChanged = !string.Equals(Normalise(then.Output), Normalise(now.Output), StringComparison.Ordinal);
            var change = (then.Passed, now.Passed) switch
            {
                (true, false) => TraceChange.Regressed,
                (false, true) => TraceChange.Improved,
                _ when toolsChanged => TraceChange.ToolsChanged,
                _ when outputChanged => TraceChange.OutputChanged,
                _ => TraceChange.Unchanged,
            };
            results.Add(new TraceCaseComparison(now.Name, change, toolsChanged, outputChanged, Describe(then, now, toolsChanged, outputChanged)));
        }

        foreach (var (key, then) in goldenKeyed)
            if (!currentKeys.Contains(key))
                results.Add(new TraceCaseComparison(then.Name, TraceChange.Removed, false, false, "not in this run"));

        return new GoldenTraceComparison(results);
    }

    // In run order; a repeated name gets its occurrence number, so the second "refund" matches the second "refund".
    private static List<(string Key, GoldenTraceCase Case)> Keyed(IReadOnlyList<GoldenTraceCase> cases)
    {
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var keyed = new List<(string Key, GoldenTraceCase Case)>(cases.Count);
        foreach (var c in cases)
        {
            var n = seen[c.Name] = seen.GetValueOrDefault(c.Name) + 1;
            keyed.Add((n == 1 ? c.Name : $"{c.Name}#{n}", c));
        }

        return keyed;
    }

    private static bool SameCalls(IReadOnlyList<GoldenToolCall> a, IReadOnlyList<GoldenToolCall> b) =>
        a.Count == b.Count && a.Zip(b).All(p => p.First == p.Second);

    private static string Normalise(string? output) => (output ?? string.Empty).Replace("\r\n", "\n").Trim();

    private static string? Describe(GoldenTraceCase then, GoldenTraceCase now, bool toolsChanged, bool outputChanged)
    {
        var parts = new List<string>();
        if (then.Passed != now.Passed)
            parts.Add($"{(then.Passed ? "passed" : "failed")} → {(now.Passed ? "passed" : "failed")} (score {then.Score} → {now.Score})");
        if (toolsChanged)
            parts.Add($"tools {Sequence(then.ToolCalls!)} → {Sequence(now.ToolCalls!)}{(SameNames(then.ToolCalls!, now.ToolCalls!) ? " (arguments differ)" : "")}");
        if (outputChanged)
            parts.Add("output changed");
        return parts.Count == 0 ? null : string.Join("; ", parts);
    }

    private static string Sequence(IReadOnlyList<GoldenToolCall> calls) =>
        calls.Count == 0 ? "[none]" : "[" + string.Join(", ", calls.Select(c => c.Name)) + "]";

    private static bool SameNames(IReadOnlyList<GoldenToolCall> a, IReadOnlyList<GoldenToolCall> b) =>
        a.Select(c => c.Name).SequenceEqual(b.Select(c => c.Name), StringComparer.Ordinal);
}
