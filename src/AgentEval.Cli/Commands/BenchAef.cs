// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.Results.Adapters.Native;
using AgentEval.Results.Writing;

namespace AgentEval.Cli.Commands;

/// <summary>
/// Writes a composite benchmark run (bench gdpr, eu-ai-act, …) as sealed AEF evidence (S1 #5a): into <c>--aef</c>,
/// else the bench workspace's <c>.agenteval/aef</c>. The preset's definition gives each component's weight, whether it
/// is required and what its failure does; the result gives the rest.
/// </summary>
internal static class BenchAef
{
    /// <summary>
    /// Writes a red-team benchmark (bench owasp, mitre, nist) as its probes: the scan is the evidence, lane the family;
    /// the benchmark's own composite verdict goes in the summary. Returns true only when an explicit
    /// <paramref name="aef"/> could not be written.
    /// </summary>
    public static bool WriteScan(
        DirectoryInfo? aef, string agentEvalDir, string family, string preset, string subject, string? subjectVersion,
        AefTargetMode targetMode, string? judgeModel, AgentEval.RedTeam.RedTeamResult scan, EvalResult? verdict,
        int exitCode, JudgeCallLedger? judgeLedger, string? abortReason = null,
        Func<IReadOnlyList<AefComplianceArtifact>>? complianceArtifacts = null, AefStoreV1Link? storeV1 = null, string? endpoint = null, string? judgeProvider = null)
    {
        var root = aef?.FullName ?? Path.Combine(agentEvalDir, "aef");
        try
        {
            var (directory, runHash) = AefProbeRunWriter.Write(Path.Combine(root, "runs"), RedTeamAef.Lines(scan, AefCliOptions.ContentCapture == AefContentCapture.On), new AefProbeRunOptions
            {
                Config = new AefRunConfig { Settings = new System.Text.Json.Nodes.JsonObject { ["preset"] = preset, ["runs"] = 1 } },
                StoreV1 = storeV1,
                Endpoint = endpoint,
                Seal = AefCliOptions.Seal,
                Signer = AefCliOptions.Signer,
                ContentCapture = AefCliOptions.ContentCapture,
                ComplianceArtifacts = complianceArtifacts?.Invoke(),
                SubjectRef = AefEvalWriter.Ref("agent", subject),
                SubjectVersion = subjectVersion,
                JudgeModel = judgeModel,
                JudgeProvider = judgeProvider,
                TargetMode = targetMode,
                SuiteRef = AefEvalWriter.Ref("bench", $"{family}/{preset}"),
                ProbesNotRun = scan.SkippedProbes,
                Lane = family,
                BenchVerdict = verdict is null ? null : (verdict.Score.Label, verdict.Score.Value),
                Gate = abortReason is null && verdict is not null ? Gate(verdict.Score.Label, exitCode) : null,
                AbortReason = abortReason,
                JudgeUsage = judgeLedger is { ReportedUsage: true }
                    ? new AefUsage { Role = AefUsageRole.Judge, Model = judgeModel, InputTokens = judgeLedger.InputTokens, OutputTokens = judgeLedger.OutputTokens }
                    : null,
            }, scan.StartedAt, DateTimeOffset.UtcNow);
            Console.Error.WriteLine($"AEF run: {directory} ({(runHash is null ? "unsealed" : $"run hash {runHash[..12]}...")})");
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            Console.Error.WriteLine($"{(aef is null ? "Warning" : "Error")}: the AEF run was not written: {ex.Message}");
            return aef is not null;
        }
    }

    /// <summary>
    /// Writes a family's result that no <see cref="CompositeEval"/> definition describes (bench perf, trace-fidelity):
    /// its parts are the cases, or the result itself when it has none, each recorded beside its own verdict.
    /// </summary>
    public static bool WriteResult(
        DirectoryInfo? aef, string agentEvalDir, string family, string preset, string subject, string? subjectVersion,
        AefTargetMode targetMode, EvalResult result, DateTimeOffset startedAt, DateTimeOffset endedAt, string subjectKind = "agent",
        IReadOnlyDictionary<string, System.Text.Json.Nodes.JsonObject>? caseExt = null, int? exitCode = null,
        string? abortReason = null, bool emptyResults = false, AefStoreV1Link? storeV1 = null, string? endpoint = null) =>
        Write(aef, agentEvalDir, family, preset, subject, subjectVersion, targetMode, AefCompositeRunWriter.Undescribed, result, startedAt, endedAt, subjectKind, caseExt, exitCode,
            abortReason: abortReason, emptyResults: emptyResults, storeV1: storeV1, endpoint: endpoint);

    /// <summary>The definition of <paramref name="eval"/> as AEF needs it beside its result.</summary>
    public static AefComponentShape Shape(IEval eval, double weight = 1, bool required = true, ComponentFailureEffect onFailure = ComponentFailureEffect.Averaged) =>
        new(weight, required, onFailure.ToString(), eval is CompositeEval composite
            ? composite.Components.Select(c => Shape(c.Eval, c.Weight, c.Required, c.OnFailure)).ToList()
            : []);

    /// <summary>
    /// Writes the run; returns true only when an explicit <paramref name="aef"/> could not be written (the command then
    /// fails, exit 3). Without <paramref name="aef"/> a failure is a warning.
    /// </summary>
    public static bool Write(
        DirectoryInfo? aef, string agentEvalDir, string family, string preset, string subject, string? subjectVersion,
        AefTargetMode targetMode, CompositeEval benchmark, EvalResult result, DateTimeOffset startedAt, DateTimeOffset endedAt,
        int? exitCode = null, IReadOnlyList<EvalResult>? trials = null, string? abortReason = null, int? requestedTrials = null,
        bool trialAggregationCompleted = false, bool emptyResults = false,
        Func<IReadOnlyList<AefComplianceArtifact>>? complianceArtifacts = null, AefStoreV1Link? storeV1 = null, string? endpoint = null, string? budgetTier = null) =>
        Write(aef, agentEvalDir, family, preset, subject, subjectVersion, targetMode, Shape(benchmark), result, startedAt, endedAt, "agent", null, exitCode, trials, abortReason, requestedTrials, trialAggregationCompleted, emptyResults, complianceArtifacts, storeV1, endpoint, budgetTier);

    /// <summary>
    /// Writes a family's result as a tree whose shape the caller gives (bench memory builds both from its categories).
    /// Returns true only when an explicit <paramref name="aef"/> could not be written.
    /// </summary>
    public static bool WriteTree(
        DirectoryInfo? aef, string agentEvalDir, string family, string preset, string subject, string? subjectVersion,
        AefTargetMode targetMode, AefComponentShape shape, EvalResult result, DateTimeOffset startedAt, DateTimeOffset endedAt, int? exitCode = null,
        string? abortReason = null, AefStoreV1Link? storeV1 = null, string? endpoint = null) =>
        Write(aef, agentEvalDir, family, preset, subject, subjectVersion, targetMode, shape, result, startedAt, endedAt, "agent", null, exitCode, abortReason: abortReason, storeV1: storeV1, endpoint: endpoint);

    /// <summary>The bench command's own decision; a warning never authorizes shipping (D-6).</summary>
    internal static AefCommandGate Gate(string label, int exitCode) => new()
    {
        GateId = "gate:bench",
        Rule = "label:" + label.ToLowerInvariant(),
        ExitCode = exitCode,
        Outcome = exitCode switch
        {
            ExitCodes.Success => AefGateOutcome.Ship,
            ExitCodes.GateFailed or ExitCodes.GateWarning => AefGateOutcome.NoShip,
            _ => AefGateOutcome.Inconclusive,
        },
    };

    /// <summary>
    /// bench memory's result as a tree: a case per category, weighted as the overall score weighs them (WeightedSum),
    /// each with the command's own label (pass at 70 and above, warn from 50, fail below), a skipped category skipped and
    /// an errored one error. The one model is the agent and its judge.
    /// </summary>
    public static (EvalResult Result, AefComponentShape Shape) MemoryTree(
        AgentEval.Memory.Models.MemoryBenchmarkResult result, string preset, string verdict, string? judgeModel)
    {
        var categories = result.CategoryResults.Select(c =>
        {
            var label = c.Errored ? "error" : c.Skipped ? "skipped" : c.Score >= 70 ? "pass" : c.Score >= 50 ? "warn" : "fail";
            return new EvalResult(
                new EvalMetadata("memory." + c.CategoryName.ToLowerInvariant().Replace(' ', '-'), c.CategoryName, "memory", "1.0.0"),
                // No severity: the benchmark grades a score, it does not say how bad a low one is.
                new EvalScore(Math.Clamp(c.Score / 100.0, 0.0, 1.0), null, label, label == "pass", 0.7, label == "pass" ? "none" : "", null),
                new EvalDetails(null, null, null, null, null),
                new EvalProvenance(judgeModel is null ? "atomic-code" : "atomic-llm", judgeModel, null, null, null, 0, false),
                DateTimeOffset.UtcNow);
        }).ToList();
        // One case, the benchmark, with its categories as weighted parts below it: the weights reach the run as each
        // part's component weight and the case's aggregation is WeightedSum, as the overall score is computed. An
        // incomplete run withholds its verdict: a warn, not measured, as the store records it.
        var incomplete = verdict == "INCOMPLETE";
        var benchmark = new EvalResult(
            new EvalMetadata("memory." + preset.ToLowerInvariant(), "Memory " + preset, "memory", "1.0.0"),
            new EvalScore(Math.Clamp(result.OverallScore / 100.0, 0.0, 1.0), null, incomplete ? "warn" : verdict.ToLowerInvariant(), verdict == "PASS", 0.7, "none", null)
            {
                Measurement = incomplete ? AgentEval.Evals.Meta.MeasurementState.NotMeasured : AgentEval.Evals.Meta.MeasurementState.Measured,
            },
            new EvalDetails(null, null, null, categories, "WeightedSum"),
            new EvalProvenance("composite", null, null, null, null, 0, false),
            DateTimeOffset.UtcNow);
        var root = new EvalResult(benchmark.Metric, benchmark.Score, new EvalDetails(null, null, null, [benchmark], null), benchmark.Provenance, DateTimeOffset.UtcNow);
        return (root, new AefComponentShape(1, true, "Averaged",
        [
            new AefComponentShape(1, true, "Averaged", result.CategoryResults.Select(c => new AefComponentShape(c.Weight, true, "Averaged", [])).ToList()),
        ]));
    }

    /// <summary>
    /// An external memory benchmark's result (bench longmemeval) as a tree: a case per question, case id the question's
    /// id, recorded beside the benchmark's verdict. Correct is passed and wrong failed; a question the judge could not
    /// score is not measured and one the agent did not answer an error: typed absences, never failures. No question,
    /// answer or response is written. The one model is the agent and its judge.
    /// </summary>
    public static EvalResult QuestionTree(
        AgentEval.Memory.External.Models.ExternalBenchmarkResult result, string family, string preset, string verdict, string? judgeModel)
    {
        var questions = result.QuestionResults.Select(q =>
        {
            var agentFailed = q.ExecutionStatus != AgentEval.Memory.External.Models.QuestionExecutionStatus.Completed;
            var label = agentFailed ? "error" : q.Correct is true ? "pass" : q.Correct is false ? "fail" : "inconclusive";
            return new EvalResult(
                new EvalMetadata(q.QuestionId, q.QuestionId, q.QuestionType, "1.0.0"),
                new EvalScore(q.Correct is true ? 1 : 0, null, label, q.Correct is true, null, label == "pass" ? "none" : "", null)
                {
                    // An unscored judgment is a typed absence: not measured, never a failure.
                    Measurement = label == "inconclusive" ? AgentEval.Evals.Meta.MeasurementState.NotMeasured : AgentEval.Evals.Meta.MeasurementState.Measured,
                },
                new EvalDetails(null, null, null, null, null),
                new EvalProvenance(judgeModel is null ? "atomic-code" : "atomic-llm", judgeModel, null, null, null, 0, false),
                DateTimeOffset.UtcNow);
        }).ToList();
        return new EvalResult(
            new EvalMetadata($"{family}.{preset.ToLowerInvariant()}", $"{family} {preset}", family, "1.0.0"),
            new EvalScore((result.OverallAccuracy ?? 0) / 100.0, null, verdict.ToLowerInvariant(), verdict == "PASS", null, "none", null),   // a percentage
            new EvalDetails(null, null, null, questions, null),
            new EvalProvenance("composite", null, null, null, null, 0, false),
            DateTimeOffset.UtcNow);
    }

    private static bool Write(
        DirectoryInfo? aef, string agentEvalDir, string family, string preset, string subject, string? subjectVersion,
        AefTargetMode targetMode, AefComponentShape shape, EvalResult result, DateTimeOffset startedAt, DateTimeOffset endedAt,
        string subjectKind, IReadOnlyDictionary<string, System.Text.Json.Nodes.JsonObject>? caseExt, int? exitCode,
        IReadOnlyList<EvalResult>? trials = null, string? abortReason = null, int? requestedTrials = null,
        bool trialAggregationCompleted = false, bool emptyResults = false,
        Func<IReadOnlyList<AefComplianceArtifact>>? complianceArtifacts = null, AefStoreV1Link? storeV1 = null, string? endpoint = null, string? budgetTier = null)
    {
        var root = aef?.FullName ?? Path.Combine(agentEvalDir, "aef");
        try
        {
            var settings = new System.Text.Json.Nodes.JsonObject { ["preset"] = preset, ["runs"] = requestedTrials ?? trials?.Count ?? 1 };
            if (budgetTier is not null) settings["budgetTier"] = budgetTier;
            var options = new AefCompositeRunOptions
            {
                Config = new AefRunConfig { Settings = settings },
                StoreV1 = storeV1,
                Endpoint = endpoint,
                Seal = AefCliOptions.Seal,
                Signer = AefCliOptions.Signer,
                ContentCapture = AefCliOptions.ContentCapture,
                ComplianceArtifacts = complianceArtifacts?.Invoke(),
                SubjectRef = AefEvalWriter.Ref(subjectKind, subject),   // the bench's own subject kind
                SubjectVersion = subjectVersion,
                TargetMode = targetMode,
                Lane = family,
                SuiteRef = AefEvalWriter.Ref("bench", $"{family}/{preset}"),
                CaseExt = caseExt,
                Gate = exitCode is { } code ? Gate(result.Score.Label, code) : null,
                AbortReason = abortReason,
                ExecutionPolicy = requestedTrials is not null || trials is not null
                    ? new AefExecutionPolicy { TrialsPerCase = requestedTrials ?? trials!.Count, Aggregation = AefTrialAggregation.MajorityVote }
                    : null,
            };
            var (directory, runHash) = emptyResults
                ? AefCompositeRunWriter.WriteAbortedEmpty(Path.Combine(root, "runs"), result, options, startedAt, endedAt)
                : trials is null
                ? AefCompositeRunWriter.Write(Path.Combine(root, "runs"), result, shape, options, startedAt, endedAt)
                : abortReason is not null && !trialAggregationCompleted
                    ? AefCompositeRunWriter.WriteAbortedTrials(Path.Combine(root, "runs"), trials, shape, options, startedAt, endedAt)
                    : AefCompositeRunWriter.WriteTrials(Path.Combine(root, "runs"), result, trials,
                        shape, options, startedAt, endedAt);
            Console.Error.WriteLine($"AEF run: {directory} ({(runHash is null ? "unsealed" : $"run hash {runHash[..12]}...")})");
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            Console.Error.WriteLine($"{(aef is null ? "Warning" : "Error")}: the AEF run was not written: {ex.Message}");
            return aef is not null;
        }
    }

    /// <summary>
    /// One model's auto-audit as its own AEF run (a run has one subject; the ranking compares runs and stays in the
    /// command's report): two cases, both scored with no verdict, since the audit ranks and publishes no pass mark.
    /// <c>fidelity</c> is the Trace Fidelity score (the tool loop's account against the chat boundary); <c>safety</c> is
    /// 1, or 0 when protected data reached the caller past the gate, with the gate's blocks, the leak, tokens and latency
    /// beside it. A model that did not complete the task has two error lines.
    /// </summary>
    public static (EvalResult Result, IReadOnlyDictionary<string, System.Text.Json.Nodes.JsonObject> CaseExt) AutoAuditTree(
        AgentEval.Benchmarks.AutoAuditEndpointResult result)
    {
        var label = result.Completed ? "scored" : "error";
        EvalResult Leaf(string key, double value) => new(
            new EvalMetadata(key, key, "autoaudit", "1.0.0"),
            new EvalScore(value, null, label, false, null, "none", null),
            new EvalDetails(null, null, null, null, null),
            new EvalProvenance("atomic-code", null, null, null, null, 0, false),
            DateTimeOffset.UtcNow);
        var root = new EvalResult(
            new EvalMetadata("autoaudit.support-task", "Auto-audit", "autoaudit", "1.0.0"),
            new EvalScore(result.FidelityScore, null, label, false, null, "none", null),
            new EvalDetails(null, null, null, [Leaf("fidelity", result.FidelityScore), Leaf("safety", result.LeakedPastGate ? 0 : 1)], null),
            new EvalProvenance("composite", null, null, null, null, 0, false),
            DateTimeOffset.UtcNow);
        var ext = new Dictionary<string, System.Text.Json.Nodes.JsonObject>(StringComparer.Ordinal);
        if (result.Completed)
        {
            ext["safety"] = new System.Text.Json.Nodes.JsonObject
            {
                ["agenteval.autoaudit"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["gateBlocks"] = result.GateBlocks,
                    ["leakedPastGate"] = result.LeakedPastGate,
                    ["promptTokens"] = result.PromptTokens,
                    ["completionTokens"] = result.CompletionTokens,
                    ["latencyMs"] = result.LatencyMs,
                },
            };
        }

        return (root, ext);
    }

    /// <summary>
    /// A TypedMemEval vertical's result: a case per question, <c>scored</c> (measured, no verdict: the benchmark publishes
    /// no pass mark and gates on nothing), score 1 for a correct answer and 0 otherwise, so the summary's mean is the
    /// accuracy, and the typed outcome (correct, wrong, abstained, missed, premature) in the line's ext. A judgment that
    /// could not be made is not measured, a question that did not run skipped, an agent failure an error. No question,
    /// answer or evidence text is written.
    /// </summary>
    public static (EvalResult Result, IReadOnlyDictionary<string, System.Text.Json.Nodes.JsonObject> CaseExt) TypedTree(
        AgentEval.Memory.External.Models.ExternalBenchmarkResult result, string vertical, string? judgeModel) =>
        TypedTree(result.QuestionResults, (result.OverallAccuracy ?? 0) / 100.0, vertical, judgeModel);

    /// <summary>Maps observed questions; a null overall score withholds a partial benchmark's aggregate.</summary>
    public static (EvalResult Result, IReadOnlyDictionary<string, System.Text.Json.Nodes.JsonObject> CaseExt) TypedTree(
        IReadOnlyList<AgentEval.Memory.External.Models.QuestionResult> observed, double? overallScore, string vertical, string? judgeModel)
    {
        var ext = new Dictionary<string, System.Text.Json.Nodes.JsonObject>(StringComparer.Ordinal);
        var questions = observed.Select(q =>
        {
            var outcome = q.TypedOutcome?.Outcome;
            // Unrun first: the runner marks a question it did not run as Unrun with an AgentError status.
            var label = outcome == AgentEval.Memory.External.Models.TypedMemEvalOutcome.Unrun ? "skipped"
                : q.ExecutionStatus != AgentEval.Memory.External.Models.QuestionExecutionStatus.Completed ? "error"
                : outcome is null or AgentEval.Memory.External.Models.TypedMemEvalOutcome.Inconclusive ? "inconclusive"
                : "scored";
            if (label == "scored")
            {
                ext[q.QuestionId] = new System.Text.Json.Nodes.JsonObject
                {
                    ["agenteval.typedmemeval"] = new System.Text.Json.Nodes.JsonObject { ["outcome"] = outcome!.Value.ToString().ToLowerInvariant() },
                };
            }

            var correct = outcome == AgentEval.Memory.External.Models.TypedMemEvalOutcome.Correct;
            return new EvalResult(
                new EvalMetadata(q.QuestionId, q.QuestionId, q.QuestionType, "1.0.0"),
                new EvalScore(correct ? 1 : 0, null, label, false, null, "none", null)
                {
                    Measurement = label == "inconclusive" ? AgentEval.Evals.Meta.MeasurementState.NotMeasured : AgentEval.Evals.Meta.MeasurementState.Measured,
                },
                new EvalDetails(null, null, null, null, null),
                new EvalProvenance(judgeModel is null ? "atomic-code" : "atomic-llm", judgeModel, null, null, null, 0, false),
                DateTimeOffset.UtcNow);
        }).ToList();
        var root = new EvalResult(
            new EvalMetadata($"typedmemeval.{vertical}", $"TypedMemEval {vertical}", "typedmemeval", "1.0.0"),
            new EvalScore(overallScore ?? 0, null, "scored", false, null, "none", null)
            {
                Measurement = overallScore is null ? AgentEval.Evals.Meta.MeasurementState.NotMeasured : AgentEval.Evals.Meta.MeasurementState.Measured,
            },
            new EvalDetails(null, null, null, questions, null),
            new EvalProvenance("composite", null, null, null, null, 0, false),
            DateTimeOffset.UtcNow);
        return (root, ext);
    }
}
