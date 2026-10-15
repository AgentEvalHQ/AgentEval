// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Models;
using AgentEval.Core;
using System.Text.Json.Nodes;
using AgentEval.Results.Suites;
using AgentEval.Results.Writing;

namespace AgentEval.Results.Adapters.Native;

public static partial class AefEvalWriter
{
    /// <summary>Starts a running eval folder, ready to append cases or trials as they finish.</summary>
    public static Session Start(string runsRoot, AefEvalRunOptions options, DateTimeOffset startedAt,
        int? plannedTrials = null, int? requirePasses = null) => new(runsRoot, options, startedAt, plannedTrials, requirePasses);

    /// <summary>
    /// One incremental eval run. Add writes results immediately; Complete writes its summary and seal. Dispose
    /// releases the folder reservation without deleting evidence or pretending an unfinished run completed.
    /// Calls must be serialized, as with AefRunWriter.
    /// </summary>
    public sealed class Session : IDisposable
    {
        private readonly AefRunFolder _folder;
        private readonly AefRunWriter _writer;
        private readonly AefEvalRunOptions _options;
        private readonly int? _plannedTrials;
        private readonly int? _requirePasses;
        private readonly List<TestResult> _all = [];
        private readonly List<(string ResultId, AefState State)> _roots = [];
        private readonly Dictionary<string, List<TestResult>> _trials = new(StringComparer.Ordinal);
        private readonly HashSet<string> _rolledUp = new(StringComparer.Ordinal);
        private readonly Dictionary<string, (AefResultHandle Handle, AefResult Root, int? PassingScore)> _cases = new(StringComparer.Ordinal);
        private readonly HashSet<(string CaseId, string Metric)> _caseMetrics = [];
        private readonly List<AefMetric> _metrics = [];
        private readonly List<AefJudge> _judges = [];
        private bool _disposed;

        internal Session(string runsRoot, AefEvalRunOptions options, DateTimeOffset startedAt, int? plannedTrials, int? requirePasses)
        {
            ArgumentNullException.ThrowIfNull(options);
            AefNativeFinalization.Validate(options.Seal, options.Signer);
            if ((plannedTrials is null) != (requirePasses is null)
                || plannedTrials is < 1 or > 1000 || requirePasses is < 1 || requirePasses > plannedTrials)
                throw new ArgumentException("A trial run requires 1 to 1000 planned trials and a pass count within that range.");
            _options = options;
            _plannedTrials = plannedTrials;
            _requirePasses = requirePasses;
            var digest = DatasetDigest(options.DatasetBytes);
            var suiteRef = AefConverter.TypedRef("dataset", options.DatasetName);
            var definition = options.Definition;
            if (definition is not null && (definition.Kind != "dataset" || definition.SuiteRef != suiteRef
                || definition.ToJson()["inputs"]?["dataset"]?.GetValue<string>() != SuiteDefinitionManifest.Hash(options.DatasetBytes)))
                throw new ArgumentException("Expected: dataset definition bound to this suite and exact snapshot. Actual: definition binding differs. Suggestions: build the definition from the captured dataset bytes. Because: evidence must identify what was executed.", nameof(options));
            _folder = AefRunFolder.Reserve(runsRoot, startedAt, "eval");
            try
            {
                var runId = _folder.RunId;
                var directory = _folder.Directory;
                var ext = AefCliProvenance.Extension(options.Cli) ?? new JsonObject();
                ext["agenteval.suiteIdentity"] = definition?.Metadata() ?? new JsonObject { ["scope"] = "dataset", ["digest"] = digest };
                var endpoint = options.Endpoint is null ? null : AefConverter.Endpoint(AefConverter.WithoutCredentials(options.Endpoint));
                var header = new AefRunHeader
                {
                    RunId = runId,
                    Ext = ext,
                    Producer = AefConverter.DefaultProducer,
                    Config = options.Config,
                    CostPolicy = options.CostPolicy,
                    Subject = new AefSubject { Ref = options.SubjectRef, Kind = options.SubjectKind, Version = options.SubjectVersion },
                    Deployment = endpoint is null ? null : new AefDeployment { Ref = AefConverter.TypedRef("endpoint", endpoint), Endpoint = endpoint },
                    Suite = new AefSuite
                    {
                        Ref = suiteRef,
                        Version = definition?.DefinitionVersion ?? "sha256-" + digest["sha256:".Length..][..12],
                        Digest = definition is null ? digest : definition.SuiteContentDigest,
                        Frozen = definition?.Frozen ?? true,
                        ExecutionPolicy = requirePasses is { } k
                            ? new AefExecutionPolicy { TrialsPerCase = plannedTrials!.Value, RequirePasses = k, Aggregation = AefTrialAggregation.AtLeast }
                            : null,
                    },
                    StartedAt = AefTime.FromDateTimeOffset(startedAt),
                    ContentCapture = options.ContentCapture,
                    Execution = new AefExecution { TargetMode = options.TargetMode, Stimulus = AefStimulus.Suite },
                };

                _writer = AefRunWriter.Create(directory, header);
                if (definition is not null) _writer.PutExtFile(SuiteDefinitionManifest.ExtensionFile, definition.ToBytes());
                _metrics.AddRange(
                [
                    new AefMetric
                    {
                        Id = PassMetric, Kind = AefMetricKind.Rate, Direction = AefMetricDirection.HigherBetter, Scale = AefScale.Between(0, 1),
                        Description = "A case counts 1 when it passed (SUM-4).",
                    },
                    new AefMetric
                    {
                        Id = ScoreMetric, Kind = AefMetricKind.Score, Direction = AefMetricDirection.HigherBetter, Scale = AefScale.Between(0, 100),
                        Description = "The case's score from AgentEval's evaluation harness (0 to 100); for a case run in trials, the mean over its measured trials.",
                    },
                ]);
                _writer.SetMetrics(_metrics);

            }
            catch
            {
                _folder.Dispose();
                AefConverter.Discard(_folder.Directory, existed: false);
                throw;
            }
        }

        /// <summary>The run folder, whether running, closed or sealed.</summary>
        public string Directory => _folder.Directory;

        /// <summary>Appends a completed case, or the next completed trial of that case, to disk.</summary>
        public void Add(TestResult result, string caseId, int? passingScore = null, AefNativeContent? content = null) =>
            Add(result, caseId, passingScore, content, null);

        /// <summary>Appends a case or trial with observed usage replacing legacy totals for the observed roles.</summary>
        public void Add(TestResult result, string caseId, int? passingScore, AefNativeContent? content, IReadOnlyList<AefUsage>? usage)
        {
            EnsureOpen();
            ArgumentNullException.ThrowIfNull(result);
            if (_options.Definition is { Frozen: true } definition && !definition.CaseIds.Contains(caseId, StringComparer.Ordinal))
                throw new ArgumentException("Expected: a case in the frozen planned roster. Actual: undeclared case. Suggestions: declare the complete roster before execution. Because: observed outcomes cannot change suite identity.", nameof(caseId));
            content = _options.ContentCapture == AefContentCapture.On
                ? (content ?? new AefNativeContent()) with
                {
                    Output = result.ActualOutput,
                    JudgeReasoning = Graded(result) && result.Details.Length > 0 ? result.Details : content?.JudgeReasoning,
                    ToolCalls = result.ToolUsage?.Calls.Select(call => System.Text.Json.JsonSerializer.Serialize(new
                    {
                        call.Name, call.CallId, call.Arguments, call.Result, call.WasExecuted,
                        call.ApprovalState, call.Order, call.ExecutorId,
                    })).ToArray() ?? content?.ToolCalls,
                }
                : null;
            if (_requirePasses is null)
            {
                var entry = AddCase(_writer, result, caseId, passingScore, content, _options.JudgeModel);
                _roots.Add((entry.Handle.ResultId, entry.Root.State));
                _cases.Add(caseId, (entry.Handle, entry.Root, passingScore));
                UpdateCaseUsage(caseId, usage);
                if (result.MetricResults is { Count: > 0 } metrics) AddMetrics(caseId, metrics);
            }
            else
            {
                if (!_trials.TryGetValue(caseId, out var trials)) _trials.Add(caseId, trials = []);
                if (trials.Count >= _plannedTrials) throw new ArgumentException("A case cannot exceed its planned trial count.", nameof(caseId));
                var line = Line(result, caseId, trials.Count, _options.JudgeModel);
                _writer.AddResult(AefNativeContent.Attach(_writer, line with { Usage = AefNativeUsage.Merge(usage, line.Usage) }, content));
                trials.Add(result);
                if (trials.Count == _plannedTrials) AddRollup(caseId, trials);
            }
            _all.Add(result);
            if (_options.JudgeModel is { } judge && Graded(result)) RecordJudge(judge, _options.JudgeProvider, result.JudgeRubricDigest);
        }

        /// <summary>Updates the single-run case root with cumulative case usage; child results do not repeat it.</summary>
        public void UpdateCaseUsage(string caseId, IReadOnlyList<AefUsage>? usage)
        {
            EnsureOpen();
            if (usage is null) return;
            if (!_cases.TryGetValue(caseId, out var entry))
                throw new ArgumentException("Usage requires an already recorded single-run case.", nameof(caseId));
            var root = entry.Root with { Usage = AefNativeUsage.Merge(usage, entry.Root.Usage) };
            _writer.UpdateResult(entry.Handle, root);
            _cases[caseId] = (entry.Handle, root, entry.PassingScore);
        }

        private void RecordJudge(string model, string? provider, string? rubricDigest = null)
        {
            var judge = new AefJudge { Model = model, Provider = provider, RubricDigest = rubricDigest, Mode = AefJudgeMode.Single };
            if (_judges.Contains(judge)) return;
            _judges.Add(judge);
            _writer.SetJudges(_judges.ToArray());
        }

        /// <summary>Appends named metric results for an already recorded single-run case, preserving its own verdict.</summary>
        public void AddMetrics(string caseId, IReadOnlyList<MetricResult> metrics)
        {
            EnsureOpen();
            ArgumentNullException.ThrowIfNull(metrics);
            if (!_cases.TryGetValue(caseId, out var entry))
                throw new ArgumentException("Named metrics require an already recorded single-run case.", nameof(caseId));
            foreach (var metric in metrics)
            {
                if (_caseMetrics.Contains((caseId, metric.MetricName)))
                    throw new ArgumentException("A named metric can be recorded only once per case.", nameof(metrics));
                var metricId = metric.MetricName;
                ValidateMetricName(metricId, nameof(metrics));
                if (!_metrics.Any(m => m.Id == metricId))
                {
                    _metrics.Add(NamedMetric(metricId));
                    _writer.SetMetrics(_metrics);
                }
                var child = MetricChild(caseId, metric, _options.MetricJudgeModel ?? _options.JudgeModel);
                if (_options.ContentCapture == AefContentCapture.On && metric.Explanation is { } explanation)
                    child = AefNativeContent.Attach(_writer, child,
                        metric.Measured && metric.ModelGraded != false && metricId.StartsWith("llm_", StringComparison.Ordinal)
                            ? new AefNativeContent { JudgeReasoning = explanation }
                            : new AefNativeContent { Document = explanation });
                _writer.AddResult(child, entry.Handle);
                var root = IncludeMetric(entry.Root, metric, entry.PassingScore);
                _writer.UpdateResult(entry.Handle, root);
                entry = (entry.Handle, root, entry.PassingScore);
                _cases[caseId] = entry;
                _caseMetrics.Add((caseId, metricId));
                if (metric.Measured && metric.ModelGraded != false && metricId.StartsWith("llm_", StringComparison.Ordinal)
                    && (_options.MetricJudgeModel ?? _options.JudgeModel) is { } model)
                    RecordJudge(model, _options.MetricJudgeModel is null ? _options.JudgeProvider : _options.MetricJudgeProvider, metric.RubricDigest);
            }
        }

        private void AddRollup(string caseId, IReadOnlyList<TestResult> trials)
        {
            var rollup = Rollup(trials, caseId, _requirePasses!.Value, _plannedTrials);
            _roots.Add((_writer.AddResult(rollup).ResultId, rollup.State));
            _rolledUp.Add(caseId);
        }

        /// <summary>Closes the run and seals it by default, with final command decision and judge usage if known. An unsealed run returns no hash.</summary>
        public (string Directory, string? RunHash) Complete(DateTimeOffset endedAt, AefCommandGate? gate = null,
            string? abortReason = null, AefUsage? judgeUsage = null) =>
            Complete(endedAt, gate, abortReason, judgeUsage, null);

        /// <summary>Closes the run with observed usage; metered roles replace legacy accounting for those roles.</summary>
        public (string Directory, string? RunHash) Complete(DateTimeOffset endedAt, AefCommandGate? gate,
            string? abortReason, AefUsage? judgeUsage, IReadOnlyList<AefUsage>? usage)
        {
            EnsureOpen();
            var options = _options with
            {
                Gate = gate ?? _options.Gate,
                AbortReason = abortReason ?? _options.AbortReason,
                JudgeUsage = judgeUsage ?? _options.JudgeUsage,
                Usage = usage ?? _options.Usage,
            };
            if (options.AbortReason is null && _trials.Any(t => t.Value.Count != _plannedTrials))
                throw new InvalidOperationException("An incomplete trial run must close aborted.");
            if (options.AbortReason is null && options.Definition is { Frozen: true } definition
                && !definition.CaseIds.ToHashSet(StringComparer.Ordinal).SetEquals(_cases.Keys.Concat(_trials.Keys)))
                throw new InvalidOperationException("Expected: every frozen planned case observed before completed status. Actual: incomplete roster. Suggestions: finish the remaining cases or close aborted. Because: partial execution must not claim complete evidence.");
            foreach (var pair in _trials)
                if (!_rolledUp.Contains(pair.Key)) AddRollup(pair.Key, pair.Value);
            if (options.AbortReason is not null)
            {
                // No final gate decision was taken for an interrupted evaluation.
            }
            else if (options.Gate is { } decision)
            {
                decision.Write(_writer, _folder.RunId, endedAt, _roots.Select(r => r.ResultId).ToList());
            }
            else if (options.GateRule is { } rule)
            {
                // The command's own gate: every case passed, or it does not ship (exit 1, as the command exits).
                var notPassed = _roots.Where(r => r.State != AefState.Passed).Select(r => r.ResultId).ToList();
                _writer.AddGate(new AefGateDecision
                {
                    GateId = "gate:eval",
                    DecisionId = _folder.RunId + "-gate",
                    Rule = new AefGateRule(rule),
                    Inputs = new AefGateInputs { Results = _roots.Select(r => r.ResultId).ToList() },
                    Comparability = AefComparability.NotApplicable,
                    Outcome = notPassed.Count == 0 ? AefGateOutcome.Ship : AefGateOutcome.NoShip,
                    ExitCode = notPassed.Count == 0 ? 0 : 1,
                    Decisive = notPassed.Count > 0 ? notPassed : null,
                    DecidedAt = AefTime.FromDateTimeOffset(endedAt),
                });
            }

            var totals = AefNativeUsage.Merge(options.Usage, Totals(_all, options.JudgeUsage));
            _writer.SetSummary(new AefSummary
            {
                Lanes = [new AefSummaryLane(Lane, [new AefSummaryEntry { Metric = PassMetric, Path = CasePath }, new AefSummaryEntry { Metric = ScoreMetric, Path = CasePath }])],
                Cost = AefNativeUsage.Cost(totals),
                Usage = totals,
                Ext = new JsonObject { ["agenteval.observedRoster"] = new JsonObject
                {
                    ["digest"] = AefProbeRunWriter.RosterDigest(_cases.Keys.Concat(_trials.Keys)),
                    ["count"] = _cases.Keys.Concat(_trials.Keys).Distinct(StringComparer.Ordinal).Count(),
                } },
            });
            _writer.Close(options.AbortReason is null ? AefRunStatus.Completed : AefRunStatus.Aborted,
                AefTime.FromDateTimeOffset(endedAt), options.AbortReason);
            return (_folder.Directory, AefNativeFinalization.Complete(_folder.Directory, options.Seal, options.Signer));
        }

        private void EnsureOpen()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_writer.Status != AefRunStatus.Running) throw new InvalidOperationException("The eval run is already closed.");
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (_disposed) return;
            _folder.Dispose();
            _disposed = true;
        }
    }
}
