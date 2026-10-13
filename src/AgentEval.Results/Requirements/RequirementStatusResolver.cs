// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Results.Runs;

namespace AgentEval.Results.Requirements;

/// <summary>A selected observation, retaining sealed/effective states and the exact result and metric identity.</summary>
public sealed record RequirementObservation(string RunHash, string ResultId, string Lane, string Metric, string Path,
    string SealedState, string EffectiveState, string? OverrideEvent, double? Value, string EndedAt);

/// <summary>A review remains separate from both sealed and effective measurements.</summary>
public sealed record RequirementReview(string RunHash, string Target, string Status, string Event);

/// <summary>Sealed and overlay identities needed to reproduce the selected evidence view.</summary>
public sealed record RequirementEvidenceIdentity(string RunId, string RunHash, string OverlayDigest);

/// <summary>One computed requirement, including the sealed base, optional effective result and scoped waiver history.</summary>
public sealed record RequirementStatus(string Id, string BaseStatus, string EffectiveStatus, string Status,
    IReadOnlyList<RequirementObservation> Observations, IReadOnlyList<RequirementWaiver> Waivers,
    IReadOnlyList<RequirementReview> Reviews, IReadOnlyList<string> Diagnostics);

/// <summary>Reproducibility inputs and computed statuses, without writing a producer result or checkpoint.</summary>
public sealed record RequirementStatusReport(string CatalogueDigest, string PolicyDigest, string At,
    RequirementSubject Subject, IReadOnlyList<string> SelectedRunHashes, IReadOnlyList<RequirementEvidenceIdentity> Evidence,
    IReadOnlyList<RequirementStatus> Requirements);

/// <summary>Pure status computation over detached, verified evidence snapshots.</summary>
public interface IRequirementStatusResolver
{
    /// <summary>Computes statuses for the exact binding; no I/O, implicit run discovery or producer-side recomputation.</summary>
    RequirementStatusReport Resolve(RequirementCatalogue catalogue, RequirementSubject subject,
        IReadOnlyList<RequirementEvidence> evidence, AefTime at, string policyDigest);
}

/// <summary>Deterministic external requirement status. Missing observations take precedence over stale evidence.</summary>
public sealed class RequirementStatusResolver : IRequirementStatusResolver
{
    /// <inheritdoc />
    public RequirementStatusReport Resolve(RequirementCatalogue catalogue, RequirementSubject subject,
        IReadOnlyList<RequirementEvidence> evidence, AefTime at, string policyDigest)
    {
        ArgumentNullException.ThrowIfNull(catalogue);
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(evidence);
        if (string.IsNullOrWhiteSpace(subject.Ref) || string.IsNullOrWhiteSpace(subject.Version) || !at.IsValid
            || string.IsNullOrWhiteSpace(policyDigest) || evidence.Count > 256
            || evidence.Any(e => e.At != at || e.PolicyDigest != policyDigest))
            throw RequirementEvidenceReader.Error("exact subject/version, at most 256 runs and consistent view time/policy",
                "inconsistent status inputs", "Read all selected evidence with the same view time and trust policy.");
        if (evidence.GroupBy(e => e.RunHash).Any(g => g.Select(e => e.OverlayDigest).Distinct().Skip(1).Any()))
            throw RequirementEvidenceReader.Error("one overlay view for each selected sealed run", "conflicting overlay snapshots",
                "Select one explicit snapshot per run hash.");
        // Hash identity, not a path alias or duplicated CLI argument, defines one run.
        var runs = evidence.DistinctBy(e => e.RunHash).OrderBy(e => e.RunHash, StringComparer.Ordinal).ToArray();
        return new(catalogue.Digest, policyDigest, at.ToString(), subject,
            Array.AsReadOnly(runs.Select(r => r.RunHash).ToArray()),
            Array.AsReadOnly(runs.Select(r => new RequirementEvidenceIdentity(r.RunId, r.RunHash, r.OverlayDigest)).ToArray()),
            Array.AsReadOnly(catalogue.Requirements.Select(rule => Evaluate(rule, subject, runs, at)).ToArray()));
    }

    private static RequirementStatus Evaluate(RequirementDefinition rule, RequirementSubject subject,
        IReadOnlyList<RequirementEvidence> runs, AefTime at)
    {
        var observations = new List<RequirementObservation>();
        var waivers = new List<RequirementWaiver>();
        var reviews = new List<RequirementReview>();
        var diagnostics = new List<string>();
        var eligible = new List<string>();
        var missing = false;
        var stale = false;
        foreach (var evidence in runs)
        {
            var docs = evidence.Documents;
            var header = docs.Run!;
            var selector = rule.Selector;
            string? Binding(string key, string member) => AefNode.String(header[key]?[member]);
            var exclusions = new List<string>();
            if (Binding("subject", "ref") != subject.Ref) exclusions.Add("subject.ref mismatch");
            if (Binding("subject", "version") != subject.Version) exclusions.Add("subject.version mismatch");
            if (selector.Deployment is { } deployment && Binding("deployment", "ref") != deployment) exclusions.Add("deployment mismatch");
            if (selector.SuiteRef is { } suite && Binding("suite", "ref") != suite) exclusions.Add("suite.ref mismatch");
            if (selector.SuiteDigest is { } digest && Binding("suite", "digest") != digest) exclusions.Add("suite.digest mismatch");
            if (AefNode.String(header["status"]) != "completed") exclusions.Add("run not completed");
            if (Binding("execution", "targetMode") != "live") exclusions.Add("targetMode is not live");
            var ended = AefNode.Time(header["endedAt"]);
            if (ended is null || ended > at) exclusions.Add("endedAt absent or after view time");
            if (exclusions.Count > 0)
            {
                diagnostics.Add($"Excluded {evidence.RunHash}: {string.Join(", ", exclusions)}.");
                continue;
            }
            eligible.Add(evidence.RunHash);
            diagnostics.AddRange(evidence.Diagnostics.Select(d => $"{evidence.RunHash}: {d}"));
            waivers.AddRange(evidence.Waivers.Where(w => w.Requirement == rule.Id));
            reviews.AddRange(evidence.View.Reviews.Select(r => new RequirementReview(evidence.RunHash, r.Target, r.Status, r.Event)));
            if (at > ended!.Value.AddSeconds(rule.FreshnessSeconds))
            {
                stale = true;
                diagnostics.Add($"Stale {evidence.RunHash}: endedAt {ended}, freshnessSeconds {rule.FreshnessSeconds}, view {at}.");
            }
            var lanes = AefNode.Objects(docs.Summary!["lanes"]).ToArray();
            var laneNames = lanes.Select(l => AefNode.String(l["lane"])!).ToArray();
            var entries = lanes.Where(l => AefNode.String(l["lane"]) == selector.Lane)
                .SelectMany(l => AefNode.Objects(l["metrics"]))
                .Where(m => AefNode.String(m["metric"]) == selector.Metric && AefNode.String(m["path"]) == selector.Path).ToArray();
            if (entries.Length != 1)
            {
                missing = true;
                diagnostics.Add($"Missing exact lane/metric/path binding {selector.Lane}/{selector.Metric}/{selector.Path} in {evidence.RunHash}.");
                continue;
            }
            var metric = AefNode.Objects(docs.Metrics!["metrics"]).SingleOrDefault(m => AefNode.String(m["id"]) == selector.Metric);
            var kind = AefNode.String(metric?["kind"]);
            if (kind is not ("score" or "rate" or "verdict" or "count" or "duration" or "cost"))
                throw RequirementEvidenceReader.Error("a supported metric kind", kind ?? "missing metric",
                    "Use a known metric definition; do not infer an unknown metric's meaning.");
            var effects = evidence.View.Results.ToDictionary(r => r.ResultId);
            var count = observations.Count;
            foreach (var (_, line) in docs.Results.Objects)
            {
                if (line.ContainsKey("trial") || AefNode.String(line["path"]) != selector.Path
                    || !AefSummaryCalculator.Belongs(line, selector.Lane, laneNames)) continue;
                var id = AefNode.String(line["resultId"])!;
                var state = AefNode.String(line["state"])!;
                effects.TryGetValue(id, out var effect);
                var (_, value) = AefSummaryCalculator.ValueOf(line, selector.Metric, kind);
                observations.Add(new(evidence.RunHash, id, selector.Lane, selector.Metric, selector.Path,
                    state, effect?.EffectiveState ?? state, effect?.Event, value, ended.Value.ToString()));
            }
            if (count == observations.Count)
            {
                missing = true;
                diagnostics.Add($"No matching case observations in {evidence.RunHash}.");
            }
        }
        if (eligible.Count == 0) diagnostics.Add("No eligible selected evidence matches the requested subject/version and selector.");
        string Compute(bool effective)
        {
            var values = observations.Where(o => (effective ? o.EffectiveState : o.SealedState) != "not_applicable").ToArray();
            bool Measured(RequirementObservation o) => rule.Acceptance.Kind == "threshold"
                ? o.Value is { } v && double.IsFinite(v)
                : (effective ? o.EffectiveState : o.SealedState) is "passed" or "failed" or "warn" or "scored";
            if (eligible.Count == 0 || missing || values.Length == 0 || values.Any(o => !Measured(o))
                || eligible.Any(hash => !values.Any(o => o.RunHash == hash))) return "not_measured";
            if (stale) return "stale";
            bool Accepted(RequirementObservation o) => rule.Acceptance.Kind == "state"
                ? rule.Acceptance.States.Contains(effective ? o.EffectiveState : o.SealedState, StringComparer.Ordinal)
                : rule.Acceptance.Operator switch
                {
                    ">" => o.Value > rule.Acceptance.Value, ">=" => o.Value >= rule.Acceptance.Value,
                    "<" => o.Value < rule.Acceptance.Value, "<=" => o.Value <= rule.Acceptance.Value,
                    "==" => o.Value == rule.Acceptance.Value, "!=" => o.Value != rule.Acceptance.Value,
                    _ => throw new InvalidOperationException("A validated catalogue has a known comparison operator."),
                };
            var passed = values.Count(Accepted);
            var met = rule.Aggregation.Kind switch
            {
                "all" => passed == values.Length,
                "any" => passed > 0,
                "fraction" => (double)passed / values.Length >= rule.Aggregation.Minimum,
                _ => throw new InvalidOperationException("A validated catalogue has a known aggregation."),
            };
            return met ? "met" : "not_met";
        }
        var baseStatus = Compute(false);
        var effectiveStatus = rule.AllowEffectiveOverrides ? Compute(true) : baseStatus;
        if (baseStatus == "not_measured") diagnostics.Add("A required observation is absent or has no measured value/state; see observation identities and sealed states.");
        // A waiver for one run cannot cover another selected run. Every eligible run needs its own scoped authority.
        var waived = eligible.Count > 0 && eligible.All(hash => waivers.Any(w => w.RunHash == hash && w.Active && w.Authorized));
        return new(rule.Id, baseStatus, effectiveStatus, effectiveStatus != "met" && waived ? "waived" : effectiveStatus,
            observations.AsReadOnly(), waivers.AsReadOnly(), reviews.AsReadOnly(), diagnostics.AsReadOnly());
    }
}
