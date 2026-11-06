// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentEval.Core;
using AgentEval.Models;

namespace AgentEval.Microsoft365Demo;

/// <summary>One benchmark case of the demo (the <c>m365-smoke.json</c> format).</summary>
/// <param name="Id">The case id (letters, digits, '.', '_' or '-'); also the evidence file name and the trace case id.</param>
/// <param name="Input">The prompt.</param>
/// <param name="Category">The optional category.</param>
/// <param name="Criteria">The judge criteria; empty when the case declares none.</param>
/// <param name="PassingScore">The judge score (0-100) the case needs.</param>
/// <param name="Tags">The optional tags.</param>
internal sealed record DemoCase(
    string Id,
    string Input,
    string? Category,
    IReadOnlyList<string> Criteria,
    int PassingScore,
    IReadOnlyList<string> Tags)
{
    /// <summary>The harness test case: name and id are the case id, criteria and passing score as declared.</summary>
    public TestCase ToTestCase() => new()
    {
        Id = Id,
        Name = Id,
        Input = Input,
        EvaluationCriteria = Criteria.Count == 0 ? null : Criteria,
        PassingScore = PassingScore,
        Tags = Tags.Count == 0 ? null : Tags,
    };
}

/// <summary>The cases the benchmark runs and where they came from.</summary>
/// <param name="Cases">The cases, in file order.</param>
/// <param name="Source">The file path, or the embedded default's description.</param>
/// <param name="IgnoredFieldsNote">
/// Which cases carry a field the demo does not run (<c>expected</c>, or a field it does not read), per field, and why;
/// null when none does. The banner and summary.md print it, so no field is dropped without a word.
/// </param>
internal sealed record DemoCaseSet(IReadOnlyList<DemoCase> Cases, string Source, string? IgnoredFieldsNote = null);

/// <summary>
/// Reads the benchmark cases: the embedded default (five read-only cases from <c>samples/datasets/m365-smoke.json</c>,
/// without their <c>expected</c> values) or a <c>--dataset</c> file in the same format. A file that does not fit is a
/// usage error with a message naming the case and the field, found before anything is sent. A field the demo does not
/// run (<c>expected</c>, or any field it does not read, such as a misspelt <c>evaluation_criteria</c>) is ignored, and
/// the banner and summary.md name it per case.
/// </summary>
/// <remarks>
/// AgentEval's <c>JsonDatasetLoader</c> reads the same format, but it cannot hold the demo to these rules: it reads only
/// from a file path (not the embedded default), fills a missing <c>input</c> with an empty string and a missing
/// <c>id</c> with <c>item_N</c>, accepts any <c>passing_score</c>, keeps unknown fields as metadata without a word, and
/// names a bad case by its index. The demo refuses the first four before a turn reaches a live tenant, and names every
/// field it ignores.
/// </remarks>
internal static partial class DemoDataset
{
    /// <summary>The manifest name of the embedded default cases.</summary>
    public const string EmbeddedResourceName = "AgentEval.Microsoft365Demo.m365-demo-cases.json";

    /// <summary>The case id the prompt step uses; a dataset case may not take it.</summary>
    public const string PromptCaseId = "prompt";

    /// <summary>How the embedded default is named in reports.</summary>
    public const string EmbeddedSource = "embedded: 5 read-only cases from samples/datasets/m365-smoke.json";

    /// <summary>The field of the shared dataset format the demo accepts and ignores (the CLI reads it).</summary>
    public const string IgnoredField = "expected";

    /// <summary>The fields the demo reads, in the order the messages list them.</summary>
    public static readonly IReadOnlyList<string> ReadFields = ["id", "input", "category", "evaluation_criteria", "passing_score", "tags"];

    /// <summary>Loads the embedded default cases.</summary>
    /// <exception cref="InvalidDataException">The resource is missing or malformed (a build defect).</exception>
    public static DemoCaseSet LoadDefault()
    {
        using var stream = typeof(DemoDataset).Assembly.GetManifestResourceStream(EmbeddedResourceName)
            ?? throw new InvalidDataException($"The embedded default dataset '{EmbeddedResourceName}' is missing from the demo assembly.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd(), "the embedded default dataset", EmbeddedSource);
    }

    /// <summary>Loads a <c>--dataset</c> file.</summary>
    /// <param name="file">The file.</param>
    /// <exception cref="InvalidDataException">The file cannot be read or does not fit the format; the message says where.</exception>
    public static DemoCaseSet Load(FileInfo file)
    {
        ArgumentNullException.ThrowIfNull(file);
        string json;
        try
        {
            json = File.ReadAllText(file.FullName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException($"--dataset {file.FullName} could not be read: {ex.Message}", ex);
        }

        return Parse(json, "--dataset " + file.FullName, file.FullName);
    }

    /// <summary>Parses the case array.</summary>
    /// <param name="json">The JSON text.</param>
    /// <param name="sourceName">How the source is named in errors.</param>
    /// <param name="source">How the source is named in reports (<see cref="DemoCaseSet.Source"/>).</param>
    /// <exception cref="InvalidDataException">The text does not fit the format.</exception>
    internal static DemoCaseSet Parse(string json, string sourceName, string source)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{sourceName} is not valid JSON: {ex.Message}", ex);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() == 0)
            {
                throw new InvalidDataException($"{sourceName} must be a non-empty JSON array of cases (the format of samples/datasets/m365-smoke.json).");
            }

            var cases = new List<DemoCase>();
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var ignored = new List<(string Field, List<string> CaseIds)>();
            var index = 0;
            foreach (var item in document.RootElement.EnumerateArray())
            {
                index++;
                var where = $"{sourceName}, case {index.ToString(CultureInfo.InvariantCulture)}";
                if (item.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidDataException($"{where}: each case must be a JSON object.");
                }

                var id = RequiredString(item, "id", where);
                if (!CaseIdPattern().IsMatch(id))
                {
                    throw new InvalidDataException($"{where}: id '{id}' must be 1-64 letters, digits, '.', '_' or '-', starting with a letter or digit (it names the evidence file).");
                }

                if (string.Equals(id, PromptCaseId, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"{where}: id '{PromptCaseId}' is reserved for the demo's prompt step.");
                }

                if (!ids.Add(id))
                {
                    throw new InvalidDataException($"{where}: id '{id}' appears more than once.");
                }

                where = $"{sourceName}, case '{id}'";
                foreach (var property in item.EnumerateObject())
                {
                    if (ReadFields.Contains(property.Name, StringComparer.Ordinal))
                    {
                        continue;
                    }

                    var entry = ignored.FindIndex(e => string.Equals(e.Field, property.Name, StringComparison.Ordinal));
                    if (entry < 0)
                    {
                        ignored.Add((property.Name, [id]));
                    }
                    else if (!ignored[entry].CaseIds.Contains(id, StringComparer.Ordinal))
                    {
                        ignored[entry].CaseIds.Add(id);
                    }
                }

                var input = RequiredString(item, "input", where);
                var category = OptionalString(item, "category", where);
                var criteria = OptionalStrings(item, "evaluation_criteria", where);
                var tags = OptionalStrings(item, "tags", where);
                var passingScore = EvaluationDefaults.DefaultPassingScore;
                if (item.TryGetProperty("passing_score", out var score) && score.ValueKind != JsonValueKind.Null)
                {
                    if (score.ValueKind != JsonValueKind.Number || !score.TryGetInt32(out passingScore) || passingScore is < 0 or > 100)
                    {
                        throw new InvalidDataException($"{where}: passing_score must be a whole number from 0 to 100.");
                    }
                }

                cases.Add(new DemoCase(id, input, category, criteria, passingScore, tags));
            }

            return new DemoCaseSet(cases, source, IgnoredNote(ignored));
        }
    }

    /// <summary>The note on the fields the demo ignores, one part per field with the cases that carry it; null when none does.</summary>
    private static string? IgnoredNote(IReadOnlyList<(string Field, List<string> CaseIds)> ignored) => ignored.Count == 0
        ? null
        : string.Join("; ", ignored.Select(e =>
            $"'{e.Field}' is ignored in {e.CaseIds.Count.ToString(CultureInfo.InvariantCulture)} case(s) ({string.Join(", ", e.CaseIds)}): " +
            (e.Field == IgnoredField
                ? "the demo grades each case's criteria with the judge only; the agenteval CLI uses 'expected' as a case-insensitive substring check when a case is not graded by a judge"
                : $"the demo does not read it (it reads {string.Join(", ", ReadFields)}, names exactly as written)")));

    private static string RequiredString(JsonElement item, string name, string where)
    {
        if (!item.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidDataException($"{where}: '{name}' is required and must be a non-blank string.");
        }

        return value.GetString()!.Trim();
    }

    private static string? OptionalString(JsonElement item, string name, string where)
    {
        if (!item.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException($"{where}: '{name}' must be a string.");
        }

        return string.IsNullOrWhiteSpace(value.GetString()) ? null : value.GetString()!.Trim();
    }

    private static IReadOnlyList<string> OptionalStrings(JsonElement item, string name, string where)
    {
        if (!item.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return [];
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"{where}: '{name}' must be an array of strings.");
        }

        var list = new List<string>();
        foreach (var entry in value.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(entry.GetString()))
            {
                throw new InvalidDataException($"{where}: every entry of '{name}' must be a non-blank string.");
            }

            list.Add(entry.GetString()!.Trim());
        }

        return list;
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex CaseIdPattern();
}
