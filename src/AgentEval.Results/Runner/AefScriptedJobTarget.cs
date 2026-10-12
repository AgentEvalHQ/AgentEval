// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Results.Writing;

namespace AgentEval.Results.Runner;

internal sealed class AefScriptedJobTarget(AefScriptedTarget target) : IAefJobTarget
{
    public AefTargetMode TargetMode => AefTargetMode.Scripted;
    public string Isolation => "process";
    public string PriceTable => target.PriceTable;
    public string Fingerprint => target.Fingerprint;
    public long CloseSeconds => target.CloseSeconds;
    public IReadOnlyCollection<string> CredentialPurposes => [];
    // Scripted conformance resolves every credential but deliberately delivers none (§9.2.1).
    public void SetCredentials(AefJobCredentials credentials) { }
    public IReadOnlyList<IAefJobSuite> Suites { get; } = target.Suites.Select(s => (IAefJobSuite)new Suite(s)).ToArray();

    private sealed class Suite(AefScriptedSuite suite) : IAefJobSuite
    {
        public string Ref => suite.Ref;
        public string Version => suite.Version;
        public string Digest => suite.Digest;
        public string CasePath => AefScriptedRunner.CasePath;
        public IReadOnlyList<IAefJobCase> Cases { get; } = suite.Cases.Select(c => (IAefJobCase)new Case(c)).ToArray();
        public IReadOnlyList<AefMetric> Metrics => [AefScriptedRunner.PassRate];
        public IReadOnlyList<AefSummaryEntry> SummaryEntries => [new() { Metric = AefScriptedRunner.PassRate.Id, Path = CasePath }];
    }

    private sealed class Case(AefScriptedCase value) : IAefJobCase
    {
        public string CaseId => value.CaseId;
        public double UsdLow => value.Usd;
        public double UsdBound => value.UsdBound;
        public long SecondsBound => value.SecondsBound;
        public double IncurredUsd { get; private set; }

        public ValueTask<AefJobCaseResult> RunAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IncurredUsd = value.Usd;
            return ValueTask.FromResult(new AefJobCaseResult(new AefResult
            {
                CaseId = CaseId,
                Path = AefScriptedRunner.CasePath,
                Evaluator = new AefEvaluator(AefScriptedRunner.EvaluatorId),
                State = value.State,
                Reason = value.State is AefState.NotMeasured or AefState.NotApplicable or AefState.Skipped or AefState.Error
                    ? $"the scripted target answers this case {AefNames.Of(value.State)}" : null,
                Severity = value.Severity,
            }, value.Usd, value.Seconds));
        }
    }
}
