// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text;
using System.Text.Json.Nodes;
using AgentEval.Results.Json;
using AgentEval.Results.Writing;

namespace AgentEval.Results.Adapters.EvalPort;

// Only source declarations without native equivalents are stored here. Observations,
// verdicts, output and reasoning continue through their native AEF mappings.
internal static class EvalPortSupplement
{
    internal const string FileName = "evalport-supplement.json";

    internal static void Write(AefRunWriter writer, JsonObject source)
    {
        var supplement = new JsonObject { ["schemaVersion"] = "1.0" };
        foreach (var field in new[] { "provider", "summary", "metadata" })
            if (source.ContainsKey(field)) supplement[field] = source[field]!.DeepClone();
        var rows = new JsonArray();
        foreach (var row in source["results"]!.AsArray())
        {
            var graders = new JsonArray();
            foreach (var grader in row!["grader_results"]!.AsArray())
                if (grader!.AsObject().ContainsKey("metadata")) graders.Add(new JsonObject
                { ["grader_id"] = grader["grader_id"]!.DeepClone(), ["metadata"] = grader["metadata"]!.DeepClone() });
            if (!row.AsObject().ContainsKey("metadata") && graders.Count == 0) continue;
            var item = new JsonObject { ["test_case_id"] = row["test_case_id"]!.DeepClone(),
                ["attempt"] = row["attempt"]?.DeepClone() ?? JsonValue.Create(1), ["graders"] = graders };
            if (row.AsObject().ContainsKey("metadata")) item["metadata"] = row["metadata"]!.DeepClone();
            rows.Add(item);
        }
        supplement["results"] = rows;
        var bytes = Encoding.UTF8.GetBytes(supplement.ToJsonString());
        // Serialization can expand escaping. Apply the same document bound before writing.
        AefJsonReader.ParseDocument(bytes);
        var blob = writer.PutBlob(bytes);
        writer.AddEvidence(new() { EvidenceId = "E-evalport-supplement", Kind = AefEvidenceKind.Document,
            Description = "EvalPort source declarations; summary and metadata are not recomputed AEF conclusions.",
            Link = AefEvidenceLink.ToBlob(blob) });
        writer.PutExtFile(FileName, Encoding.UTF8.GetBytes(new JsonObject
        { ["schemaVersion"] = "1.0", ["blob"] = blob.Digest }.ToJsonString()));
    }

    internal static void Restore(JsonObject document, JsonObject supplement, ISet<string> notes)
    {
        if (supplement["schemaVersion"]?.GetValue<string>() != "1.0") throw Refuse();
        foreach (var field in new[] { "provider", "summary" })
            if (supplement.ContainsKey(field)) document[field] = supplement[field]!.DeepClone();
        MergeMetadata(document, supplement, notes);
        var rows = document["results"]!.AsArray().ToDictionary(x => Key(x!), x => x!.AsObject());
        var seen = new HashSet<(string, long)>();
        foreach (var item in supplement["results"]!.AsArray())
        {
            var key = Key(item!);
            if (!seen.Add(key) || !rows.TryGetValue(key, out var row)) throw Refuse();
            MergeMetadata(row, item!.AsObject(), notes);
            var graders = row["grader_results"]!.AsArray().ToDictionary(x => x!["grader_id"]!.GetValue<string>(), x => x!.AsObject(), StringComparer.Ordinal);
            var seenGraders = new HashSet<string>(StringComparer.Ordinal);
            foreach (var grader in item["graders"]!.AsArray())
            {
                var id = grader!["grader_id"]!.GetValue<string>();
                if (!seenGraders.Add(id) || !graders.TryGetValue(id, out var target)) throw Refuse();
                MergeMetadata(target, grader.AsObject(), notes);
            }
        }
        notes.Add("Captured provider configuration, summary and metadata are restored as source declarations, not independently validated conclusions or transferred trust.");
    }

    private static (string, long) Key(JsonNode row) => (row["test_case_id"]!.GetValue<string>(),
        row["attempt"] is { } attempt ? EvalPortResultSet.Integer(attempt, 1) : 1);

    private static void MergeMetadata(JsonObject target, JsonObject source, ISet<string> notes)
    {
        if (!source.ContainsKey("metadata")) return;
        if (source["metadata"] is not JsonObject metadata) throw Refuse();
        var result = target["metadata"]!.AsObject();
        foreach (var item in metadata)
        {
            if (result.ContainsKey(item.Key) && !JsonNode.DeepEquals(result[item.Key], item.Value))
                notes.Add("Source metadata takes precedence over generated metadata on key collisions; source metadata is a declaration, not exporter attestation.");
            result[item.Key] = item.Value?.DeepClone();
        }
    }

    private static AefInputException Refuse() => new("Expected: a supported EvalPort supplement bound to exported observation identities. Actual: invalid version or identity mapping. Suggestions: inspect the captured source declarations. Because: metadata must not be attached to a different observation.");
}
