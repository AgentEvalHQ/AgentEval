// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text;
using System.Text.Json.Nodes;
using AgentEval.Results.Integrity;
using AgentEval.Results.Writing;

namespace AgentEval.Results.Adapters.EvalPort;

/// <summary>Imports validated EvalPort observations as AEF evidence.</summary>
public interface IAefEvalPortImporter
{
    /// <summary>Writes a new run; refuses a nonempty destination. Caller supplies facts EvalPort does not establish.</summary>
    AefConversion Import(ReadOnlyMemory<byte> resultSet, string outputDirectory,
        AefEvalPortImportOptions options, CancellationToken cancellationToken = default);
}

/// <summary>
/// Preserves source root decisions using Own aggregation with zero-weight grader
/// children. Repeated-attempt AllPass rollups are explicitly converter-computed.
/// </summary>
public sealed class AefEvalPortImporter : IAefEvalPortImporter
{
    private const string RootPath = "evalport";
    private sealed record Observation(AefResult Root, IReadOnlyList<(AefResult Result, string? Reason)> Children, string? Output, JsonObject? Error);

    /// <inheritdoc/>
    public AefConversion Import(ReadOnlyMemory<byte> resultSet, string outputDirectory,
        AefEvalPortImportOptions options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var plan = EvalPortImportPlan.Create(EvalPortResultSet.Parse(resultSet.Span), options);
        var source = plan.Source.ToJson();
        var observations = PlanResults(source, options, cancellationToken);
        var notes = plan.Notes.Concat(new[]
        {
            "Root passed flags are source declarations: Own aggregation records grader children with zero weight; no source threshold is invented.",
            "Repeated attempts use converter-computed AllPass rollups; missing observations do not become passing evidence.",
            "Each observation records an aggregationCheck: verified means agreement with available policy inputs, not trusted execution; unverifiable means suite facts or policy semantics are unavailable.",
            "With capture-on, provider configuration, source summary and arbitrary metadata are retained as source declarations in supplemental document evidence; capture-off omits them. External signatures and trust are not transferred.",
            "Source error objects are retained as document evidence only with capture-on; capture-off retains declared error type/retryability and omits message/code content.",
        }).ToArray();
        var existed = Directory.Exists(outputDirectory);
        // Create owns only a new/empty destination. A refusal must never discard preexisting data.
        var writer = AefRunWriter.Create(outputDirectory, plan.Header);
        try
        {
            writer.SetMetrics([
                new() { Id = "evalport.score", Kind = AefMetricKind.Score, Direction = AefMetricDirection.None, Scale = AefScale.Unbounded },
                new() { Id = "evalport.pass", Kind = AefMetricKind.Rate, Direction = AefMetricDirection.HigherBetter, Scale = AefScale.Between(0, 1) },
            ]);
            plan.Source.Context.WriteTo(writer);
            if (options.ContentCapture == AefContentCapture.On) EvalPortSupplement.Write(writer, source);
            var evidenceIndex = 0;
            foreach (var observation in observations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var root = observation.Root;
                var evidenceIds = new List<string>();
                if (observation.Output is { } output && options.ContentCapture == AefContentCapture.On)
                {
                    var evidenceId = "E-output-" + (++evidenceIndex);
                    writer.AddEvidence(new() { EvidenceId = evidenceId, Kind = AefEvidenceKind.Output,
                        Link = AefEvidenceLink.ToBlob(writer.PutBlob(Encoding.UTF8.GetBytes(output))) });
                    evidenceIds.Add(evidenceId);
                }
                if (observation.Error is { } error && options.ContentCapture == AefContentCapture.On)
                {
                    var evidenceId = "E-error-" + (++evidenceIndex);
                    writer.AddEvidence(new() { EvidenceId = evidenceId, Kind = AefEvidenceKind.Document,
                        Description = "EvalPort source error object",
                        Link = AefEvidenceLink.ToBlob(writer.PutBlob(Encoding.UTF8.GetBytes(error.ToJsonString()))) });
                    evidenceIds.Add(evidenceId);
                    root.Ext!["evalport"]!["errorEvidence"] = evidenceId;
                }
                root = root with { Evidence = evidenceIds.Count == 0 ? null : evidenceIds };
                var parent = writer.AddResult(root);
                foreach (var (child, reason) in observation.Children)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    writer.AddResult(reason is not null && options.ContentCapture == AefContentCapture.On
                        ? child with { Reasoning = writer.PutBlob(Encoding.UTF8.GetBytes(reason)) } : child, parent);
                }
            }
            WriteRollups(writer, observations, cancellationToken);
            writer.SetSummary(new() { Lanes = [new("main", [new() { Metric = "evalport.pass", Path = RootPath }])] });
            cancellationToken.ThrowIfCancellationRequested();
            if (plan.EndedAt is { } ended)
                return AefConverter.Finish(writer, options, plan.Header.Imported!.From, plan.Header.Imported.Asserted,
                    notes, plan.Status, ended, plan.AbortReason);
            var verification = AefRunVerifier.Verify(writer.Directory);
            if (verification.Outcome == AefOutcome.Invalid) throw Refuse("running conversion failed AEF verification");
            return new(writer.Directory, writer.RunId, plan.Header.Imported!.From, plan.Header.Imported.Asserted, notes, verification, null);
        }
        catch { AefConverter.Discard(outputDirectory, existed); throw; }
    }

    private static List<Observation> PlanResults(JsonObject source, AefEvalPortImportOptions options, CancellationToken ct)
    {
        var results = source["results"]!.AsArray().Select(x => x!.AsObject()).ToArray();
        var repeated = results.GroupBy(x => x["test_case_id"]!.GetValue<string>(), StringComparer.Ordinal)
            .Where(g => g.Count() > 1 || g.Any(x => x.ContainsKey("attempt"))).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        var observations = new List<Observation>();
        foreach (var result in results)
        {
            ct.ThrowIfCancellationRequested();
            var caseId = result["test_case_id"]!.GetValue<string>();
            var attempt = result.ContainsKey("attempt") ? EvalPortResultSet.Integer(result["attempt"]!, 1) : 1;
            if (attempt > int.MaxValue) throw Refuse("attempt exceeds the AEF writer's supported trial range");
            int? trial = repeated.Contains(caseId) ? (int)attempt - 1 : null;
            var children = new List<(AefResult Result, string? Reason)>();
            foreach (var node in result["grader_results"]!.AsArray())
            {
                var grader = node!.AsObject(); var id = grader["grader_id"]!.GetValue<string>();
                var measured = grader["score"] is not null;
                var state = !measured ? AefState.NotMeasured : grader["passed"]!.GetValue<bool>() ? AefState.Passed : AefState.Failed;
                var normalized = measured ? grader["score"]!.GetValue<double>() : (double?)null;
                var raw = grader["metadata"]?["openeval.raw_score"];
                if (raw is not null && (raw is not JsonValue scalar || !scalar.TryGetValue<double>(out _))) throw Refuse("raw_score must be numeric");
                children.Add((new AefResult
                {
                    CaseId = caseId, Path = RootPath + "/g-" + AefConverter.Sha256Of(Encoding.UTF8.GetBytes(id))[7..],
                    Trial = trial, Evaluator = new("evalport:grader"), State = state,
                    Reason = measured ? null : "EvalPort grader recorded a null score.",
                    Scores = normalized is { } score ? [new() { Metric = "evalport.score", Value = raw?.GetValue<double>() ?? score, Normalized = score }] : null,
                    Component = new(0, false),
                    Ext = new() { ["evalport"] = new JsonObject { ["grader_id"] = id, ["type"] = grader["type"]!.DeepClone() } },
                }, grader["reason"]?.GetValue<string>()));
            }
            var isError = result["error"] is not null;
            var errorContext = isError ? new JsonObject() : null;
            if (result["error"] is JsonObject sourceError)
            {
                foreach (var key in new[] { "type", "retryable" })
                    if (sourceError.ContainsKey(key)) errorContext![key] = sourceError[key]!.DeepClone();
                if (options.ContentCapture == AefContentCapture.Off && (sourceError.ContainsKey("message") || sourceError.ContainsKey("code")))
                    errorContext!["contentOmitted"] = true;
            }
            var rootState = isError ? AefState.Error : children.All(c => !Measured(c.Result.State)) ? AefState.NotMeasured :
                result["passed"]!.GetValue<bool>() ? AefState.Passed : AefState.Failed;
            var root = new AefResult
            {
                CaseId = caseId, Path = RootPath, Trial = trial, Evaluator = new("evalport:result"), State = rootState,
                Reason = isError ? "EvalPort execution error" + (errorContext?["type"] is { } type ? ": " + type.GetValue<string>() : ".") : rootState == AefState.NotMeasured ? "EvalPort has no measured grader." : null,
                Scores = Measured(rootState) ? [new() { Metric = "evalport.pass", Value = rootState == AefState.Passed ? 1 : 0 }] : null,
                Aggregation = children.Count > 0 ? Own(children.Select(c => c.Result).ToArray()) : null,
                EndedAt = result["completed_at"] is { } time ? EvalPortTimestamp.Parse(time.GetValue<string>()) : null,
                DurationMs = result["duration_ms"] is { } duration ? ExactDuration(duration) : null,
                TraceLink = SourceTrace(result),
                Ext = new() { ["evalport"] = new JsonObject { ["passed"] = result["passed"]!.DeepClone(),
                    ["attemptDeclared"] = result.ContainsKey("attempt"), ["error"] = errorContext,
                    ["aggregationCheck"] = EvalPortAggregation.Assess(result, options.AggregationContext),
                    ["aggregation"] = options.ContentCapture == AefContentCapture.On ? result["metadata"]?["openeval.aggregation"]?.DeepClone() : null } },
            };
            observations.Add(new(root, children, result["actual_output"]?.GetValue<string>(), result["error"]?.DeepClone().AsObject()));
        }
        return observations;
    }

    private static double ExactDuration(JsonNode duration)
    {
        var value = EvalPortResultSet.Integer(duration, 0);
        if (value > 9_007_199_254_740_991L) throw Refuse("duration cannot be represented exactly by the AEF writer");
        return value;
    }

    private static AefTraceLink? SourceTrace(JsonObject result)
    {
        var metadata = result["metadata"] as JsonObject;
        if (metadata?.ContainsKey("openeval.trace_id") != true) return null;
        if (metadata["openeval.trace_id"] is not JsonValue value || !value.TryGetValue<string>(out var trace) ||
            trace.Length != 32 || trace.AsSpan().ContainsAnyExcept("0123456789abcdef"))
            throw Refuse("openeval.trace_id must be 32 lowercase hexadecimal characters");
        return new(trace);
    }

    private static void WriteRollups(AefRunWriter writer, List<Observation> observations, CancellationToken ct)
    {
        foreach (var group in observations.Where(x => x.Root.Trial is not null).GroupBy(x => x.Root.CaseId, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var children = group.SelectMany(x => x.Children).GroupBy(x => x.Result.Path, StringComparer.Ordinal)
                .Select(g => Rollup(g.Select(x => x.Result).ToArray()) with { Component = new(0, false) }).ToArray();
            var root = Rollup(group.Select(x => x.Root).ToArray()) with { Aggregation = children.Length > 0 ? Own(children) : null };
            var parent = writer.AddResult(root);
            foreach (var child in children) writer.AddResult(child, parent);
        }
    }

    private static AefResult Rollup(AefResult[] lines)
    {
        if (lines.Length > 1000) throw Refuse("more than 1000 trials at one case/path");
        var passed = lines.Count(x => x.State == AefState.Passed);
        var state = passed == lines.Length ? AefState.Passed : lines.Any(x => x.State == AefState.Failed) ? AefState.Failed :
            lines.All(x => !Measured(x.State)) ? AefState.NotMeasured : AefState.Inconclusive;
        return new()
        {
            CaseId = lines[0].CaseId, Path = lines[0].Path, Evaluator = new("evalport:import-rollup"), State = state,
            Trials = new(lines.Length, passed, AefTrialAggregation.AllPass, lines.Select(x => x.State).Distinct().Count() == 1),
            Reason = state == AefState.NotMeasured ? "No measured source trial at this path." : null,
            Scores = lines[0].Path == RootPath && Measured(state) ? [new() { Metric = "evalport.pass", Value = state == AefState.Passed ? 1 : 0 }] : null,
            Ext = new() { ["evalport"] = new JsonObject { ["converterComputed"] = true } },
        };
    }

    private static bool Measured(AefState state) => state is AefState.Passed or AefState.Failed or AefState.Inconclusive or AefState.Scored or AefState.Warn;
    private static AefAggregation Own(IReadOnlyList<AefResult> children) => new()
    {
        Strategy = AefAggregationStrategy.Own, RulePath = AefRulePath.Threshold,
        Measured = children.Count(c => Measured(c.State)), Total = children.Count,
        Unmeasured = new() { NotMeasured = children.Count(c => !Measured(c.State)) },
    };
    private static AefInputException Refuse(string actual) => new($"Expected: representable EvalPort evidence. Actual: {actual}. " +
        "Suggestions: correct or split the source observations. Because: conversion must preserve evidence without silent rounding.");
}
