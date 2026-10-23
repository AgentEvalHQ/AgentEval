// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AgentEval.Output;
using AgentEval.Results.Adapters.StoreV1;
using AgentEval.Results.Ingestion;
using AgentEval.Results.Integrity;
using AgentEval.Results.Runs;

namespace AgentEval.Results.Adapters.Comparison;

/// <summary>Verified, detached comparison inputs. Import custody is distinct from execution custody.</summary>
public sealed record AefComparisonRun(
    IReadOnlyList<ScenarioResult> Scenarios, string Integrity, string? RunHash, string Status, bool Imported,
    IReadOnlyDictionary<string, AefComparisonMetric> Metrics)
{
    /// <summary>Header fields supplied by the importer rather than recorded by the original execution.</summary>
    public IReadOnlyList<string> Asserted { get; init; } = [];

    /// <summary>The recorded abort reason, including unknown completion for partial legacy imports.</summary>
    public string? AbortReason { get; init; }

    /// <summary>Compares recorded scenario facts and the numeric meaning of each selected metric.</summary>
    public RunComparison CompareTo(AefComparisonRun candidate, bool strict = false)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var comparison = RunComparison.Of(Scenarios, candidate.Scenarios, strict);
        return comparison with
        {
            Scenarios = comparison.Scenarios.Select(s =>
            {
                Metrics.TryGetValue(s.ScenarioId, out var left);
                candidate.Metrics.TryGetValue(s.ScenarioId, out var right);
                var state = left is null || right is null ? ComparabilityAxisState.Unrecorded
                    : left == right ? ComparabilityAxisState.Match : ComparabilityAxisState.Mismatch;
                var axis = new ComparabilityAxis("metric", state,
                    left is null ? null : JsonSerializer.Serialize(left),
                    right is null ? null : JsonSerializer.Serialize(right),
                    "A score delta requires the same recorded metric identity, kind, direction, scale and unit.");
                return s with { Axes = [.. s.Axes, axis] };
            }).ToList(),
        };
    }
}

/// <summary>The numeric meaning of a selected AEF score; absent bounds denote the declared unbounded scale.</summary>
public sealed record AefComparisonMetric(string Id, string Kind, string Direction, double? Minimum, double? Maximum, string? Unit);

/// <summary>Reads comparison observations from AEF evidence after protocol verification.</summary>
public interface IAefComparisonReader
{
    /// <summary>Reads an owned snapshot; refuses invalid evidence and never changes the source.</summary>
    AefComparisonRun Read(string directory);
}

/// <summary>One bounded AEF reading path for native evidence and converted legacy evidence.</summary>
public sealed class AefComparisonReader : IAefComparisonReader
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <inheritdoc />
    public AefComparisonRun Read(string directory)
    {
        using var snapshot = AefOwnedSnapshot.Capture(Path.GetFullPath(directory), new AefStagingOptions
        {
            MaxFiles = 100_000, MaxBytes = 1L << 30, MaxFileBytes = 1L << 30,
        }, CancellationToken.None);
        var folder = AefRunFolder.Open(snapshot.FullName);
        var verification = AefRunVerifier.Verify(folder);
        if (verification.Outcome == AefOutcome.Invalid)
            throw Bad("invalid AEF evidence: " + string.Join(", ", verification.Problems.Select(p => $"{p.Path}: {p.Code}")));
        // These are the same invocation-owned bytes that were verified, not another read of the caller's directory.
        var documents = AefRunDocuments.Read(folder);
        var final = documents.Results.Objects.Select(x => x.Value).Where(x => x["trial"] is null).ToList();
        var hasSelection = final.Any(x => (x["ext"] as JsonObject)?.ContainsKey(AefComparisonMetadata.Extension) == true);
        if (hasSelection) ValidateSelection(final, documents);
        var selected = hasSelection ? final.Where(x => ComparisonMetadata(x)?["selected"]?.GetValue<bool>() == true)
            : final.Where(x => x["parentResultId"] is null);
        var selectedLines = selected.ToList();
        var scenarios = selectedLines.Select(Project).ToList();
        if (scenarios.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != scenarios.Count)
            throw Bad("multiple final roots for one case; the comparison unit is ambiguous");
        if (scenarios.Count == 0) throw Bad("no final comparison results were recorded");
        var definitions = AefNode.Objects(documents.Metrics?["metrics"]).ToDictionary(m => (string)m["id"]!, StringComparer.Ordinal);
        var metrics = new Dictionary<string, AefComparisonMetric>(StringComparer.Ordinal);
        for (var i = 0; i < selectedLines.Count; i++)
        {
            var id = (string?)ComparisonMetadata(selectedLines[i])?["metric"] ?? StoreV1Exporter.ScoreMetric;
            if (!definitions.TryGetValue(id, out var metric)) throw Bad("comparison metric is not declared");
            var scale = metric["scale"] as JsonObject;
            metrics.Add(scenarios[i].Id, new(id, (string)metric["kind"]!, (string)metric["direction"]!,
                AefNode.Number(scale?["min"]), AefNode.Number(scale?["max"]), AefNode.String(metric["unit"])));
        }
        return new(scenarios, AefRunVerification.Name(verification.Outcome), verification.RunHash?.Value,
            documents.Status ?? "unknown", documents.Run?["imported"] is not null, metrics)
        {
            Asserted = (documents.Run?["imported"]?["asserted"] as JsonArray)?.Select(x => x!.GetValue<string>()).ToArray() ?? [],
            AbortReason = (string?)documents.Run?["abortReason"],
        };
    }

    private static ScenarioResult Project(JsonObject line)
    {
        var metadata = ComparisonMetadata(line);
        if (metadata is not null && metadata["version"]?.GetValue<int>() != 1)
            throw Bad("unsupported comparison metadata version");
        var id = (string?)metadata?["id"] ?? (string)line["caseId"]!;
        var state = (string)line["state"]!;
        var measured = state is "passed" or "failed" or "warn" or "scored" or "inconclusive";
        var legacy = (line["ext"] as JsonObject)?[StoreV1Exporter.ExtName] as JsonObject;
        ComparabilityFacts? facts = null;
        try
        {
            facts = (metadata?["facts"] ?? legacy?["comparability"])?.Deserialize<ComparabilityFacts>(Json);
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException)
        {
            throw Bad("unreadable recorded comparison facts", error);
        }
        // Typed AEF state and scores are authoritative. Extension placeholders must not revive an absent measurement.
        var metric = (string?)metadata?["metric"] ?? StoreV1Exporter.ScoreMetric;
        var score = measured ? (line["scores"] as JsonArray)?.OfType<JsonObject>()
            .SingleOrDefault(s => (string?)s["metric"] == metric)?["value"]?.GetValue<double>() : null;
        return new ScenarioResult(id, (string?)legacy?["name"] ?? id, "", "", state == "passed", score,
            new Dictionary<string, double>(), [], null, null)
        {
            Comparability = facts,
            StimulusHash = (string?)metadata?["stimulusHash"] ?? (string?)legacy?["stimulusHash"],
            Measurement = measured ? null : state == "not_applicable" ? ScenarioMeasurement.NotApplicable : ScenarioMeasurement.NotMeasured,
        };
    }

    private static JsonObject? ComparisonMetadata(JsonObject line) =>
        (line["ext"] as JsonObject)?[AefComparisonMetadata.Extension] as JsonObject;

    private static void ValidateSelection(IReadOnlyList<JsonObject> lines, AefRunDocuments documents)
    {
        var metrics = AefNode.Objects(documents.Metrics?["metrics"]).Select(m => (string)m["id"]!).ToHashSet(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            var metadata = ComparisonMetadata(line) ?? throw Bad("comparison metadata is missing or is not an object");
            if (metadata["version"] is not JsonValue version || !version.TryGetValue<int>(out var number) || number != 1)
                throw Bad("unsupported comparison metadata version");
            if (metadata["selected"] is not JsonValue selection || !selection.TryGetValue<bool>(out _))
                throw Bad("comparison selection is not a boolean");
            if (AefNode.String(metadata["id"]) is not { Length: > 0 } id || string.IsNullOrWhiteSpace(id))
                throw Bad("comparison identity is missing");
            if (AefNode.String(metadata["metric"]) is not { } metric || !metrics.Contains(metric))
                throw Bad("comparison metric is not declared");
            if (metadata["stimulusHash"] is not null && AefNode.String(metadata["stimulusHash"]) is null)
                throw Bad("comparison stimulus hash is not a string");
            ComparabilityFacts facts;
            try
            {
                facts = metadata["facts"]?.Deserialize<ComparabilityFacts>(Json) ?? throw Bad("comparison facts are missing");
            }
            catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException)
            {
                throw Bad("unreadable comparison facts", error);
            }
            if (facts.EvalKey != AefNode.String(line["evaluator"]?["id"]) ||
                facts.EvalVersion != AefNode.String(line["evaluator"]?["version"]))
                throw Bad("comparison evaluator identity differs from the recorded evaluator");
            var annotator = line["annotator"];
            if (!Enum.IsDefined(facts.EffectiveInstrumentKind) ||
                facts.Judge is not null && facts.EffectiveInstrumentKind != EvaluationInstrumentKind.Model ||
                facts.Judge?.RubricDigest is { } namedRubric && facts.RubricDigest is { } unnamedRubric && namedRubric != unnamedRubric)
                throw Bad("comparison instrument facts are inconsistent");
            if (AefNode.String(annotator?["kind"]) == "CODE" && facts.EffectiveInstrumentKind != EvaluationInstrumentKind.Deterministic ||
                AefNode.String(annotator?["kind"]) == "LLM" &&
                (facts.EffectiveInstrumentKind != EvaluationInstrumentKind.Model || facts.Judge?.ModelId != AefNode.String(annotator?["model"]) ||
                 AefNode.String(annotator?["rubricDigest"]) is { } rubric && rubric != facts.EffectiveRubricDigest))
                throw Bad("comparison judge differs from the recorded annotator");
        }

        // Selection is a partition of the final result tree. Each terminal result belongs to exactly one
        // selected observation (itself or an ancestor); selecting nothing or both parent and child is not a partition.
        var byId = lines.ToDictionary(x => (string)x["resultId"]!, StringComparer.Ordinal);
        var parents = lines.Select(x => (string?)x["parentResultId"]).OfType<string>().ToHashSet(StringComparer.Ordinal);
        foreach (var leaf in lines.Where(x => !parents.Contains((string)x["resultId"]!)))
        {
            var selected = 0;
            JsonObject? current = leaf;
            while (current is not null)
            {
                if (ComparisonMetadata(current)!["selected"]!.GetValue<bool>()) selected++;
                current = (string?)current["parentResultId"] is { } parent ? byId[parent] : null;
            }
            if (selected != 1) throw Bad("comparison selection omits or overlaps recorded results");
        }
    }

    private static InvalidDataException Bad(string actual, Exception? inner = null) => new(
        $"Expected: valid AEF evidence with unambiguous comparison observations. Actual: {actual}. " +
        "Suggestions: verify the selected run and record its comparison facts. Because: invalid or missing evidence cannot establish a delta.", inner);
}
