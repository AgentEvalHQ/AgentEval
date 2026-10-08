// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json.Nodes;

namespace AgentEval.Interop.AssertAi;

/// <summary>A test case ASSERT ran or planned that has no row in <c>scores.jsonl</c>, and why.</summary>
/// <param name="Key">The case.</param>
/// <param name="Reason">What its absence means, in ASSERT's terms.</param>
public sealed record AssertAiMissingCase(AssertAiCaseKey Key, string Reason);

/// <summary>Where the files of an ASSERT run are. Every path is optional except the run directory.</summary>
public sealed record AssertAiReadOptions
{
    /// <summary>The taxonomy to read. Default: <c>taxonomy.json</c> in the suite directory (the run's parent), which is
    /// the one ASSERT's own metrics read.</summary>
    public string? TaxonomyPath { get; init; }

    /// <summary>The test set to compare against. Default: the one the run's <c>manifest.json</c> names, else
    /// <c>test_set.jsonl</c> in the suite directory.</summary>
    public string? TestSetPath { get; init; }
}

/// <summary>
/// One ASSERT run, read from its directory (<c>&lt;results&gt;/&lt;suite&gt;/&lt;run&gt;/</c>): every score row, the
/// taxonomy, and the cases that were planned or run but have no score row. ASSERT writes no row when the target call
/// of a prompt case fails or when a judge call is stopped by a content filter, so a reader that only counts rows
/// would never see those cases.
/// </summary>
/// <param name="RunDirectory">The run directory.</param>
/// <param name="SuiteName">The suite (the parent directory's name).</param>
/// <param name="RunName">The run (the directory's name).</param>
/// <param name="Rows">The score rows, in file order.</param>
/// <param name="Taxonomy">The taxonomy, when one was found.</param>
/// <param name="TaxonomyPath">Where it was read from.</param>
/// <param name="TestSetPath">The test set compared against, when one was found.</param>
/// <param name="InferenceSetPath">The inference set compared against, when the run has one.</param>
/// <param name="Missing">Cases with no score row.</param>
/// <param name="ManifestStatus">The run's status in <c>manifest.json</c> (<c>completed</c>, <c>failed</c>,
/// <c>running</c>), when present.</param>
/// <param name="FinishedAt">When the run ended (<c>manifest.json</c>), else when <c>scores.jsonl</c> was last written.</param>
/// <param name="Warnings">Lines that were not JSON objects and were skipped, as ASSERT's own reader skips them.</param>
public sealed record AssertAiRun(
    string RunDirectory,
    string? SuiteName,
    string RunName,
    IReadOnlyList<AssertAiScoreRow> Rows,
    AssertAiTaxonomy? Taxonomy,
    string? TaxonomyPath,
    string? TestSetPath,
    string? InferenceSetPath,
    IReadOnlyList<AssertAiMissingCase> Missing,
    string? ManifestStatus,
    DateTimeOffset FinishedAt,
    IReadOnlyList<string> Warnings)
{
    /// <summary>True when the run's <c>manifest.json</c> says it completed. A run without a manifest is not known to
    /// have finished.</summary>
    public bool IsComplete => ManifestStatus == "completed";

    /// <summary>Reads an ASSERT run directory.</summary>
    /// <param name="runDirectory">The directory holding <c>scores.jsonl</c>.</param>
    /// <param name="options">Where the taxonomy and test set are, when not in the default places.</param>
    /// <exception cref="FileNotFoundException"><c>scores.jsonl</c> is missing.</exception>
    /// <exception cref="InvalidDataException">A file is not what ASSERT writes, or a case has two score rows.</exception>
    public static AssertAiRun Read(string runDirectory, AssertAiReadOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runDirectory);
        options ??= new AssertAiReadOptions();
        var run = Path.GetFullPath(runDirectory);
        var scoresPath = Path.Combine(run, "scores.jsonl");
        if (!File.Exists(scoresPath))
        {
            throw new FileNotFoundException($"No scores.jsonl in {run}: is this an ASSERT run directory (<results>/<suite>/<run>/)?", scoresPath);
        }

        var suite = Path.GetDirectoryName(run);
        var warnings = new List<string>();
        var rows = new List<AssertAiScoreRow>();
        var seen = new Dictionary<AssertAiCaseKey, int>();
        foreach (var (line, json) in AssertAiJson.ReadJsonLines(scoresPath, warnings))
        {
            var row = AssertAiScoreRows.Parse(json, line);
            if (seen.TryGetValue(row.Key, out var first))
            {
                throw new InvalidDataException($"{scoresPath}:{line}: a second row for {row.Key} (first at line {first}); ASSERT keeps one row per case.");
            }

            seen[row.Key] = line;
            rows.Add(row);
        }

        JsonObject? manifest = null;
        var manifestPath = Path.Combine(run, "manifest.json");
        if (File.Exists(manifestPath))
        {
            manifest = AssertAiJson.ReadObject(manifestPath);
        }

        var taxonomyPath = options.TaxonomyPath ?? (suite is null ? null : Path.Combine(suite, "taxonomy.json"));
        AssertAiTaxonomy? taxonomy = null;
        if (taxonomyPath is not null && File.Exists(taxonomyPath))
        {
            taxonomy = AssertAiTaxonomy.FromJson(AssertAiJson.ReadObject(taxonomyPath));
        }
        else if (options.TaxonomyPath is not null)
        {
            throw new FileNotFoundException($"The taxonomy {options.TaxonomyPath} does not exist.", options.TaxonomyPath);
        }

        var testSetPath = options.TestSetPath ?? ManifestTestSet(manifest, suite) ?? (suite is null ? null : Path.Combine(suite, "test_set.jsonl"));
        var planned = new List<AssertAiCaseKey>();
        if (testSetPath is not null && File.Exists(testSetPath))
        {
            planned.AddRange(Keys(testSetPath, warnings));
        }
        else
        {
            if (options.TestSetPath is not null)
            {
                throw new FileNotFoundException($"The test set {options.TestSetPath} does not exist.", options.TestSetPath);
            }

            testSetPath = null;
        }

        var inferencePath = Path.Combine(run, "inference_set.jsonl");
        var inferred = new HashSet<AssertAiCaseKey>();
        if (File.Exists(inferencePath))
        {
            inferred.UnionWith(Keys(inferencePath, warnings));
        }
        else
        {
            inferencePath = null;
        }

        var missing = new List<AssertAiMissingCase>();
        foreach (var key in inferred.Where(k => !seen.ContainsKey(k)).OrderBy(k => k.Type, StringComparer.Ordinal).ThenBy(k => k.TestCaseId, StringComparer.Ordinal))
        {
            missing.Add(new AssertAiMissingCase(key,
                "The case was run but has no score row: ASSERT writes none when the judge call is stopped by a content filter or fails after its retries."));
        }

        foreach (var key in planned.Where(k => !seen.ContainsKey(k) && !inferred.Contains(k)).Distinct())
        {
            missing.Add(new AssertAiMissingCase(key, inferencePath is null
                ? "The case is in the test set but has no score row (the run has no inference_set.jsonl to tell why)."
                : key.Type == "scenario"
                    ? "The case is in the test set but has no transcript: ASSERT records a scenario's target error as a row, so its worker failed (or the run stopped)."
                    : "The case is in the test set but has no transcript: ASSERT writes none when the target call of a prompt case fails (or the run stopped)."));
        }

        var finishedAt = manifest?["ended_at"] is { } ended && DateTimeOffset.TryParse(AssertAiJson.Str(ended), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var at)
            ? at
            : new DateTimeOffset(File.GetLastWriteTimeUtc(scoresPath), TimeSpan.Zero);

        return new AssertAiRun(
            run,
            suite is null ? null : Path.GetFileName(suite),
            Path.GetFileName(run),
            rows,
            taxonomy,
            taxonomy is null ? null : Path.GetFullPath(taxonomyPath!),
            testSetPath is null ? null : Path.GetFullPath(testSetPath),
            inferencePath,
            missing,
            manifest is null ? null : AssertAiJson.Str(manifest["status"]),
            finishedAt,
            warnings);
    }

    private static string? ManifestTestSet(JsonObject? manifest, string? suite)
    {
        // manifest.artifact_versions.test_set.path is relative to the suite, POSIX-style.
        if (suite is null || manifest?["artifact_versions"] is not JsonObject versions
            || versions["test_set"] is not JsonObject reference || AssertAiJson.Str(reference["path"]) is not { Length: > 0 } relative)
        {
            return null;
        }

        var path = Path.GetFullPath(Path.Combine(suite, relative.Replace('/', Path.DirectorySeparatorChar)));
        return File.Exists(path) ? path : null;
    }

    private static IEnumerable<AssertAiCaseKey> Keys(string path, List<string> warnings) =>
        AssertAiJson.ReadJsonLines(path, warnings)
            .Select(r => new AssertAiCaseKey(AssertAiJson.Str(r.Row["type"]) ?? string.Empty, AssertAiJson.Str(r.Row["test_case_id"]) ?? string.Empty))
            .Where(k => k.TestCaseId.Length > 0);
}
