// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json.Nodes;

namespace AgentEval.Results.Schemas;

/// <summary>
/// How a reader takes one value of a §7.3 field: the concrete <paramref name="Field"/> path (dots, and <c>[i]</c> for an
/// array item: <c>judges[0].mode</c>; an object member by its name: <c>config.thresholds.m.op</c>), the value
/// <paramref name="Written"/>, the value it is <paramref name="Read"/> as, and whether this version knows the written
/// value (<paramref name="Known"/>: the writer schema accepts it there).
/// </summary>
public sealed record AefReading(string Field, JsonNode? Written, JsonNode? Read, bool Known);

/// <summary>
/// Reading a value this version does not know (contracts/aef/1/spec/07-versioning.md, §7.3, [VER-8]): a later minor may
/// add enum values, and a reader reads each as the table of §7.3 says, never as a pass or as better evidence than it
/// is. What this version "knows" is what its writer schema accepts at that field, so the sets can never drift from
/// the schemas. Only documents the reader schema accepts are read.
/// </summary>
public static class AefReadings
{
    // §7.3, row by row. Known: the writer subschema(s) that accept a known value ('*' is every item of a oneOf). When:
    // the field is read only when the object holding it has this kind (a rule's max is a severity rule's).
    private static readonly Rule[] Rules =
    [
        new("result", "severity", "result#/properties/severity", How.Fallback, "critical"),
        new("run", "execution.targetMode", "run#/properties/execution/properties/targetMode", How.Fallback, "mocked"),
        new("run", "execution.stimulus", "run#/properties/execution/properties/stimulus", How.Fallback, "other"),
        new("run", "contentCapture", "run#/properties/contentCapture", How.Fallback, "on"),

        // "other", shown as written.
        new("run", "subject.kind", "run#/properties/subject/properties/kind", How.Fallback, "other"),
        new("evidence", "kind", "evidence#/properties/kind", How.Fallback, "other"),
        new("run", "judges[*].mode", "run#/properties/judges/items/properties/mode", How.Fallback, "other"),
        new("result", "annotator.kind", "result#/properties/annotator/properties/kind", How.Fallback, "OTHER"),
        new("result", "usage[*].role", "result#/properties/usage/items/properties/role", How.Fallback, "other"),
        new("summary", "usage[*].role", "summary#/properties/usage/items/properties/role", How.Fallback, "other"),
        new("result", "attack.taxonomy[*].scheme", "result#/properties/attack/properties/taxonomy/items/properties/scheme", How.Fallback, "other"),

        new("summary", "lanes[*].metrics[*].verdict", "summary#/properties/lanes/items/properties/metrics/items/properties/verdict", How.Fallback, "inconclusive"),

        // Descriptive: shown as written.
        new("result", "trials.aggregation", "result#/properties/trials/properties/aggregation", How.AsWritten),
        new("run", "suite.executionPolicy.aggregation", "run#/properties/suite/properties/executionPolicy/properties/aggregation", How.AsWritten),
        new("run", "config.thresholds{*}.op", "run#/properties/config/properties/thresholds/additionalProperties/properties/op", How.AsWritten),
        new("result", "aggregation.strategy", "result#/properties/aggregation/properties/strategy", How.AsWritten),
        new("result", "aggregation.rulePath", "result#/properties/aggregation/properties/rulePath", How.AsWritten),

        new("metrics", "metrics[*].direction", "metrics#/properties/metrics/items/properties/direction", How.Fallback, "none"),
        new("gate-decision", "outcome", "gate-decision#/properties/outcome", How.Fallback, "inconclusive"),
        new("gate-decision", "comparability", "gate-decision#/properties/comparability", How.Fallback, "incomparable"),
        new("overlay-event", "kind", "overlay-event#/properties/kind", How.Fallback, "annotate"),

        // [OVL-3]: an assurance is shown only as far as it was verified, and a document read alone verifies nothing.
        new("overlay-event", "by.assurance", "common#/$defs/trustedIdentity/properties/assurance", How.Always, "self-attested"),

        new("seal", "predicate.sealedBy", "seal#/properties/predicate/properties/sealedBy", How.Fallback, "ingest"),
        new("checkpoint", "state", "checkpoint#/properties/state", How.Fallback, "unverifiable"),
        new("checkpoint", "outcome", "checkpoint#/properties/outcome", How.Fallback, "unverifiable"),
        new("checkpoint", "lanes[*].rule.kind", "checkpoint#/$defs/laneRule/oneOf/*/properties/kind", How.Fallback, "not_measured"),
        new("checkpoint", "lanes[*].rule.max", "checkpoint#/$defs/laneRule/oneOf/*/properties/max", How.Fallback, "not_measured", When: "severity"),
        new("checkpoint", "lanes[*].rule.axes[*]", "checkpoint#/$defs/laneRule/oneOf/*/properties/axes/items", How.Fallback, "incomparable", When: "comparison"),
        new("checkpoint", "lanes[*].rule.op", "checkpoint#/$defs/laneRule/oneOf/*/properties/op", How.Fallback, "not_measured", When: "threshold"),
        new("checkpoint", "budget.approvedBy.assurance", "common#/$defs/trustedIdentity/properties/assurance", How.Always, "self-attested"),
        new("checkpoint", "decisionInput.exceptions[*].by.assurance", "common#/$defs/trustedIdentity/properties/assurance", How.Always, "self-attested"),

        // A decision input's results as [DEC-2] reads them; a decision document cannot be recomputed ([CKP-7]).
        new("decision#/$defs/input", "lanes[*].result.status", "decision#/$defs/input/properties/lanes/items/properties/result/anyOf/*/properties/status", How.Fallback, "not_measured"),
        new("decision#/$defs/input", "exceptions[*].by.assurance", "common#/$defs/trustedIdentity/properties/assurance", How.Always, "self-attested"),
        new("decision", "outcome", "decision#/properties/outcome", How.Fallback, "unverifiable"),
        new("decision", "lanes[*].status", "decision#/$defs/laneStatus", How.Fallback, "unverifiable"),

        new("runner-event", "kind", "runner-event#/oneOf/*/properties/kind", How.Fallback, "skipped"),
        new("runner-event", "status", "runner-event#/oneOf/*/properties/status", How.Fallback, "not_measured", When: "lane.completed"),
        new("runner-event", "limit", "runner-event#/oneOf/*/properties/limit", How.AsWritten, When: "job.failed"),

        // [PLAN-7]: a runner refuses a plan whose provider, isolation, content capture, target mode, credential scheme or
        // purpose it does not know.
        new("run-plan", "provider", "run-plan#/properties/provider", How.Fallback, "refused"),
        new("run-plan", "isolation", "run-plan#/properties/isolation", How.Fallback, "refused"),
        new("run-plan", "contentCapture", "run-plan#/properties/contentCapture", How.Fallback, "refused"),
        new("run-plan", "targetMode", "run-plan#/properties/targetMode", How.Fallback, "refused"),
        new("run-plan", "credentialRefs[*].scheme", "run-plan#/properties/credentialRefs/items/properties/scheme", How.Fallback, "refused"),
        new("run-plan", "credentialRefs[*].purpose", "run-plan#/properties/credentialRefs/items/properties/purpose", How.Fallback, "refused"),

        // Shown as written; takes no part in matching.
        new("runner", "kind", "runner#/properties/kind", How.AsWritten),
        new("runner", "os", "runner#/properties/os", How.AsWritten),
    ];

    // The writer subschemas each rule's Known names, '*' expanded.
    private static readonly Lazy<Dictionary<Rule, string[]>> KnownSchemas = new(() =>
        Rules.ToDictionary(r => r, r => Expand(r.Known).ToArray()));

    /// <summary>The schema names that have §7.3 fields (<c>run</c>, <c>decision#/$defs/input</c>, …).</summary>
    public static IReadOnlyList<string> Schemas { get; } = [.. Rules.Select(r => r.Schema).Distinct()];

    /// <summary>
    /// Every §7.3 field <paramref name="document"/> holds, in the order of §7.3's table and then of the document, with
    /// the value a reader takes. <paramref name="schema"/> is the name the document was validated against (<c>run</c>,
    /// <c>decision#/$defs/input</c>); a schema without §7.3 fields has none.
    /// </summary>
    /// <param name="schema">The schema name.</param>
    /// <param name="document">A document the reader schema accepts.</param>
    public static IReadOnlyList<AefReading> Read(string schema, JsonNode? document)
    {
        ArgumentNullException.ThrowIfNull(schema);
        var readings = new List<AefReading>();
        foreach (var rule in Rules.Where(r => r.Schema == schema))
        {
            foreach (var (field, holder, written) in Values(document, rule.Field.Split('.'), "", null))
            {
                if (rule.When is { } kind && (holder?["kind"] is not JsonValue k || !k.TryGetValue<string>(out var held) || held != kind))
                {
                    continue;
                }

                var known = KnownSchemas.Value[rule].Any(s => AefSchemas.Writer.IsValid(s, written));
                var read = rule.How switch
                {
                    How.AsWritten => written?.DeepClone(),
                    How.Always => JsonValue.Create(rule.Value),
                    _ => known ? written?.DeepClone() : JsonValue.Create(rule.Value),
                };
                readings.Add(new AefReading(field, written?.DeepClone(), read, known));
            }
        }

        return readings;
    }

    /// <summary>The writer subschema names every rule takes its known values from, for a test that they all exist.</summary>
    internal static IEnumerable<(string Schema, string Field, IReadOnlyList<string> Known)> KnownSchemaNames() =>
        Rules.Select(r => (r.Schema, r.Field, (IReadOnlyList<string>)KnownSchemas.Value[r]));

    // Every concrete (path, the object holding the value, value) the dotted field reaches; name[*] is every item of an
    // array, name{*} every member of an object.
    private static IEnumerable<(string Path, JsonObject? Holder, JsonNode? Value)> Values(
        JsonNode? node, string[] steps, string prefix, JsonObject? holder)
    {
        if (steps.Length == 0)
        {
            yield return (prefix, holder, node);
            yield break;
        }

        var step = steps[0];
        var items = step.EndsWith("[*]", StringComparison.Ordinal);
        var members = step.EndsWith("{*}", StringComparison.Ordinal);
        var name = items || members ? step[..^3] : step;
        if (node is not JsonObject obj || !obj.TryGetPropertyValue(name, out var child))
        {
            yield break;
        }

        var here = prefix.Length == 0 ? name : $"{prefix}.{name}";
        if (items && child is JsonArray array)
        {
            for (var i = 0; i < array.Count; i++)
            {
                foreach (var found in Values(array[i], steps[1..], $"{here}[{i}]", obj)) yield return found;
            }
        }
        else if (members && child is JsonObject map)
        {
            foreach (var (key, value) in map)
            {
                foreach (var found in Values(value, steps[1..], $"{here}.{key}", map)) yield return found;
            }
        }
        else if (!items && !members)
        {
            foreach (var found in Values(child, steps[1..], here, obj)) yield return found;
        }
    }

    // "checkpoint#/$defs/laneRule/oneOf/*/properties/kind" → each existing "…/oneOf/<i>/properties/kind".
    private static IEnumerable<string> Expand(string name)
    {
        var star = name.IndexOf('*', StringComparison.Ordinal);
        if (star < 0)
        {
            return AefSchemas.Writer.Has(name) ? [name] : [];
        }

        var found = new List<string>();
        for (var i = 0; AefSchemas.Writer.Has($"{name[..star]}{i}"); i++)
        {
            found.AddRange(Expand($"{name[..star]}{i}{name[(star + 1)..]}"));
        }

        return found;
    }

    private enum How
    {
        Fallback,    // a known value as written, an unknown one as Value
        AsWritten,   // descriptive: every value as written
        Always,      // every value as Value, known or not
    }

    private sealed record Rule(string Schema, string Field, string Known, How How, string? Value = null, string? When = null);
}
