// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text;
using System.Text.Json.Nodes;
using AgentEval.Results.Ingestion;
using AgentEval.Results.Integrity;
using AgentEval.Results.Interop;
using AgentEval.Results.Json;
using AgentEval.Results.Runs;
using AgentEval.Results.Signatures;

namespace AgentEval.Results.Adapters.EvalPort;

/// <summary>Explicit selection and trust inputs for exporting base evidence.</summary>
public sealed record AefEvalPortExportOptions
{
    /// <summary>Caller-owned policy, including any authorized withholding.</summary>
    public TrustPolicy? Policy { get; init; }
    /// <summary>Optional exact selected sealed run hash.</summary>
    public string? ExpectedRunHash { get; init; }
    /// <summary>Explicitly export base evidence when overlays exist; never applies their effective view.</summary>
    public bool IgnoreOverlays { get; init; }
    /// <summary>Suite identifier when the AEF run does not declare one.</summary>
    public string? SuiteId { get; init; }
}

/// <summary>An exported ResultSet and explicit semantic losses; the AEF signature does not sign these bytes.</summary>
public sealed record AefEvalPortExport(EvalPortResultSet ResultSet, string RunHash, IReadOnlyList<string> Notes);

/// <summary>Exports verified base-run evidence as EvalPort.</summary>
public interface IAefEvalPortExporter
{
    /// <summary>Captures and verifies a bounded run before exporting detached results.</summary>
    AefEvalPortExport Export(string directory, AefEvalPortExportOptions? options = null, CancellationToken cancellationToken = default);
}

/// <summary>Bounded AEF-to-EvalPort projection; omitted rollups and metadata-only states are reported.</summary>
public sealed class AefEvalPortExporter : IAefEvalPortExporter
{
    /// <inheritdoc/>
    public AefEvalPortExport Export(string directory, AefEvalPortExportOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new(); cancellationToken.ThrowIfCancellationRequested();
        using var snapshot = AefOwnedSnapshot.Capture(directory, new(), cancellationToken);
        var folder = AefRunFolder.Open(snapshot.FullName);
        var verification = AefRunVerifier.Verify(folder, new() { Policy = options.Policy });
        if (verification.Outcome != AefOutcome.Intact || verification.RunHash is not { Sealed: true } hash)
            throw Refuse("export requires intact sealed evidence");
        if (options.ExpectedRunHash is not null && options.ExpectedRunHash != hash.Value) throw Refuse("selected run hash mismatch");
        var notes = new HashSet<string>(StringComparer.Ordinal);
        if (folder.Has("overlays/events.ndjson"))
        {
            if (!options.IgnoreOverlays) throw Refuse("overlays require explicit base-evidence selection");
            notes.Add("Overlays are not applied; exported values are base evidence.");
        }
        var docs = AefRunDocuments.Read(folder); var run = docs.Run!;
        var origin = Origin(run);
        var suite = origin?["suite_id"]?.GetValue<string>() ?? options.SuiteId ?? run["suite"]?["ref"]?.GetValue<string>();
        if (string.IsNullOrEmpty(suite)) throw Refuse("no suite identity; supply one explicitly");
        var results = new JsonArray();
        var document = new JsonObject
        {
            ["version"] = "1.0.0-rc.5", ["suite_id"] = suite,
            ["run_id"] = origin?["run_id"]?.DeepClone() ?? run["runId"]!.DeepClone(),
            ["started_at"] = run["startedAt"]!.DeepClone(), ["results"] = results,
            ["metadata"] = new JsonObject { ["aef"] = new JsonObject { ["runHash"] = hash.Value, ["subject"] = run["subject"]!.DeepClone() } },
        };
        if (run["suite"]?["version"] is { } version) document["suite_version"] = version.DeepClone();
        if (run["endedAt"] is { } ended) document["completed_at"] = ended.DeepClone();
        if (run["status"]?.GetValue<string>() == "aborted") document["metadata"]!["openeval.partial"] = true;
        document["runner"] = origin?["runner"]?.DeepClone() ?? new JsonObject
        { ["name"] = run["producer"]!["name"]!.DeepClone(), ["version"] = run["producer"]!["version"]!.DeepClone() };
        if (run["subject"]?["kind"]?.GetValue<string>() == "model")
            document["provider"] = new JsonObject { ["model"] = origin?["providerModel"]?.DeepClone() ?? run["subject"]!["ref"]!.DeepClone() };
        var contextPath = "ext/" + EvalPortRunContext.ExtensionFile;
        if (folder.Has(contextPath)) document = EvalPortRunContext.Parse(folder.Read(contextPath, EvalPortRunContext.MaxBytes)).ApplyTo(document);
        results = document["results"]!.AsArray();

        var lines = docs.Results.Objects.Select(x => x.Value).ToArray();
        var children = lines.Where(x => x["parentResultId"] is not null).ToLookup(x => Text(x["parentResultId"]), StringComparer.Ordinal);
        var evidence = docs.Evidence.Objects.ToDictionary(x => Text(x.Value["evidenceId"]), x => x.Value, StringComparer.Ordinal);
        var capture = Text(run["contentCapture"]) == "on";
        var blobBudget = AefLimits.MaxJsonBytes;
        var rowBytes = 0;
        var roots = lines.Where(x => x["parentResultId"] is null && x["trials"] is null).ToArray();
        var splitCases = roots.GroupBy(x => Text(x["caseId"]), StringComparer.Ordinal)
            .Where(g => g.Select(x => Text(x["path"])).Distinct(StringComparer.Ordinal).Count() > 1)
            .Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        var reservedCases = roots.Select(x => Text(x["caseId"])).ToHashSet(StringComparer.Ordinal);
        var caseMapping = new Dictionary<(string Case, string Path), string>();
        foreach (var key in roots.Select(x => (Case: Text(x["caseId"]), Path: Text(x["path"])))
            .Distinct().OrderBy(x => x.Case, StringComparer.Ordinal).ThenBy(x => x.Path, StringComparer.Ordinal))
        {
            if (!splitCases.Contains(key.Case)) continue;
            var stem = "aef-root-" + AefConverter.Sha256Of(Encoding.UTF8.GetBytes(new JsonArray(key.Case, key.Path).ToJsonString()))[7..];
            var candidate = stem; var suffix = 0;
            while (!reservedCases.Add(candidate)) candidate = stem + "-" + (++suffix);
            caseMapping.Add(key, candidate);
        }
        if (caseMapping.Count > 0) notes.Add("Cases with multiple root paths use distinct deterministic test_case_id values; metadata.aef.caseId/path preserve original identity.");
        if (lines.Any(x => x["trials"] is not null)) notes.Add("AEF trial rollups are omitted; individual attempts remain.");
        foreach (var root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ext = Origin(root);
            var graders = new JsonArray();
            var caseId = Text(root["caseId"]);
            var row = new JsonObject { ["test_case_id"] = caseMapping.GetValueOrDefault((caseId, Text(root["path"])), caseId),
                ["passed"] = Text(root["state"]) == "passed", ["grader_results"] = graders,
                ["metadata"] = new JsonObject { ["aef"] = new JsonObject { ["state"] = root["state"]!.DeepClone(),
                    ["caseId"] = caseId, ["path"] = root["path"]!.DeepClone() } } };
            if (root["trial"] is { } trial && ext?["attemptDeclared"]?.GetValue<bool>() != false)
            {
                var value = EvalPortResultSet.Integer(trial, 0);
                if (value == long.MaxValue) throw Refuse("trial cannot be incremented without overflow");
                row["attempt"] = value + 1;
            }
            if (root["endedAt"] is { } completed) row["completed_at"] = completed.DeepClone();
            if (root["durationMs"] is { } duration)
            {
                try { row["duration_ms"] = EvalPortResultSet.Integer(duration, 0); }
                catch (AefInputException) { notes.Add("A non-integral or out-of-range AEF duration was omitted."); }
            }
            var stack = new Stack<JsonObject>(); stack.Push(root);
            while (stack.TryPop(out var line))
            {
                var descendants = children[Text(line["resultId"])].Where(x => x["trials"] is null).ToArray();
                if (descendants.Length > 0) { foreach (var child in Enumerable.Reverse(descendants)) stack.Push(child); continue; }
                var leafExt = Origin(line);
                var state = Text(line["state"]);
                var score = (line["scores"] as JsonArray)?.FirstOrDefault();
                JsonNode? normalized = score?["normalized"] ?? score?["value"];
                var verdictProjection = score is null && state is "passed" or "failed";
                if (verdictProjection)
                {
                    normalized = JsonValue.Create(state == "passed" ? 1 : 0);
                    notes.Add("Scoreless pass/fail assertions use a declared Boolean score projection, not an observed numeric metric.");
                }
                else if (normalized is not null)
                {
                    if (normalized is not JsonValue numeric || !numeric.TryGetValue<double>(out var number) || !double.IsFinite(number))
                        throw Refuse("AEF score is not a finite numeric value");
                    else if (number is < 0 or > 1) throw Refuse("score outside [0,1] has no normalized value");
                }
                var grader = new JsonObject
                {
                    ["grader_id"] = leafExt?["grader_id"]?.DeepClone() ?? line["path"]!.DeepClone(),
                    ["type"] = leafExt?["type"]?.DeepClone() ?? JsonValue.Create("aef"),
                    ["score"] = normalized?.DeepClone(), ["passed"] = normalized is not null && state == "passed",
                    ["metadata"] = new JsonObject { ["aef"] = new JsonObject { ["state"] = state, ["path"] = line["path"]!.DeepClone() } },
                };
                if (line["scores"] is { } sourceScores) grader["metadata"]!["aef"]!["scores"] = sourceScores.DeepClone();
                if (verdictProjection) grader["metadata"]!["aef"]!["scoreProjection"] = "verdict";
                if (score?["normalized"] is not null && score["value"] is JsonValue raw && raw.TryGetValue<double>(out var rawNumber) && double.IsFinite(rawNumber))
                    grader["metadata"]!["openeval.raw_score"] = raw.DeepClone();
                if (capture && line["reasoning"]?["blob"] is { } reasonDigest && BlobText(reasonDigest.GetValue<string>()) is { } reason)
                    grader["reason"] = reason;
                if (state is not ("passed" or "failed")) notes.Add("AEF states without EvalPort equivalents survive only in metadata.aef.state.");
                if ((line["scores"] as JsonArray)?.Count > 1) notes.Add("Only the first score per leaf is projected.");
                graders.Add(grader);
            }
            if (graders.All(g => g!["score"] is null)) row["passed"] = false;
            if (capture && root["evidence"] is JsonArray ids)
            {
                foreach (var id in ids)
                    if (evidence.TryGetValue(id!.GetValue<string>(), out var record) && record["link"]?["blob"] is { } digest)
                    {
                        if (Text(record["kind"]) == "output")
                        {
                            if (row.ContainsKey("actual_output")) throw Refuse("multiple outputs cannot be mapped to one actual_output");
                            if (BlobText(digest.GetValue<string>()) is { } output) row["actual_output"] = output;
                        }
                        if (id.GetValue<string>() == Text(ext?["errorEvidence"]))
                        {
                            var text = BlobText(digest.GetValue<string>());
                            if (text is not null) row["error"] = AefJsonReader.ParseDocument(Encoding.UTF8.GetBytes(text));
                        }
                    }
            }
            if (Text(root["state"]) == "error" && !row.ContainsKey("error"))
            {
                row["error"] = new JsonObject { ["type"] = ext?["error"]?["type"]?.DeepClone() ?? JsonValue.Create("runner_error") };
                if (ext?["error"]?["retryable"] is { } retry) row["error"]!["retryable"] = retry.DeepClone();
                notes.Add("Error message/code content was unavailable; only classification is exported.");
            }
            rowBytes += Encoding.UTF8.GetByteCount(row.ToJsonString());
            if (rowBytes > AefLimits.MaxJsonBytes) throw Refuse("export exceeds 4 MiB");
            results.Add(row);
        }
        notes.Add("AEF evidence trees are flattened to root results and leaf graders; gates, effective overlays and signatures are not transferred.");
        var supplementPath = "ext/" + EvalPortSupplement.FileName;
        if (capture && folder.Has(supplementPath))
        {
            try
            {
                var pointer = AefJsonReader.ParseDocument(folder.Read(supplementPath, 1024));
                if (pointer["schemaVersion"]?.GetValue<string>() != "1.0") throw Refuse("unsupported supplement version");
                if (BlobText(pointer["blob"]!.GetValue<string>()) is { } text)
                    EvalPortSupplement.Restore(document, AefJsonReader.ParseDocument(Encoding.UTF8.GetBytes(text)).AsObject(), notes);
            }
            catch (Exception e) when (e is not AefInputException && e is (InvalidOperationException or NullReferenceException or ArgumentException))
            { throw Refuse("invalid source supplement shape or identity mapping"); }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(EvalPortResultSet.Parse(Encoding.UTF8.GetBytes(document.ToJsonString())), hash.Value, notes.Order(StringComparer.Ordinal).ToArray());

        string? BlobText(string digest)
        {
            if (!digest.StartsWith("sha256:", StringComparison.Ordinal) || digest.Length != 71 ||
                digest.AsSpan(7).ContainsAnyExcept("0123456789abcdef")) throw Refuse("invalid blob reference");
            var path = AefRunFolder.BlobPath(digest[7..]);
            if (!folder.Has(path)) { notes.Add("Authorized withheld content is omitted."); return null; }
            var bytes = folder.Read(path, blobBudget); blobBudget -= bytes.Length;
            try { return new UTF8Encoding(false, true).GetString(bytes); }
            catch (DecoderFallbackException) { throw Refuse("captured content is not UTF-8 text"); }
        }
    }

    private static JsonObject? Origin(JsonObject document)
    {
        var node = document["ext"]?["evalport"];
        if (node is null) return null;
        if (node is not JsonObject origin) throw Refuse("evalport origin extension must be an object");
        foreach (var field in new[] { "suite_id", "run_id", "providerModel", "grader_id", "type", "errorEvidence" })
            if (origin[field] is { } value && (value is not JsonValue scalar || !scalar.TryGetValue<string>(out _)))
                throw Refuse("evalport origin identifier must be a string");
        if (origin["attemptDeclared"] is { } attempt && (attempt is not JsonValue flag || !flag.TryGetValue<bool>(out _)))
            throw Refuse("evalport attemptDeclared must be boolean");
        if (origin["error"] is { } error && error is not JsonObject) throw Refuse("evalport error classification must be an object");
        return origin;
    }

    private static string Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";
    private static AefInputException Refuse(string actual) => new($"Expected: representable verified AEF evidence. Actual: {actual}. " +
        "Suggestions: select compatible evidence or inspect the explicit conversion losses. Because: export must not invent evidence or silently round it.");
}
