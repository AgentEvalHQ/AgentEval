// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Results.Writing;
using AgentEval.Results.Suites;

namespace AgentEval.Results.Runner;

/// <summary>A target whose suites and execution bounds are known before a job is accepted.</summary>
public interface IAefJobTarget
{
    /// <summary>How this target is actually driven.</summary>
    AefTargetMode TargetMode { get; }
    /// <summary>The isolation this target can provide.</summary>
    string Isolation { get; }
    /// <summary>The price table used for all cost figures.</summary>
    string PriceTable { get; }
    /// <summary>A stable, non-secret fingerprint of the target configuration.</summary>
    string Fingerprint { get; }
    /// <summary>Time reserved for closing and sealing one run, in seconds.</summary>
    long CloseSeconds { get; }
    /// <summary>The suites resolved without executing the target.</summary>
    IReadOnlyList<IAefJobSuite> Suites { get; }
    /// <summary>The purposes to which this target can deliver credentials, such as subject or judge.</summary>
    IReadOnlyCollection<string> CredentialPurposes { get; }
    /// <summary>A content-free reason resolution failed; checked before acceptance or credential delivery.</summary>
    string? RefusalReason => null;
    /// <summary>Installs purpose-scoped credentials without network calls or process environment changes.</summary>
    void SetCredentials(AefJobCredentials credentials);
}

/// <summary>A resolved suite with stable identities, declared metrics and bounded cases.</summary>
public interface IAefJobSuite
{
    /// <summary>The suite's typed reference.</summary>
    string Ref { get; }
    /// <summary>The suite's version.</summary>
    string Version { get; }
    /// <summary>The digest of the exact suite content.</summary>
    string Digest { get; }
    /// <summary>Optional full declared definition; when supplied its content digest must equal Digest and its roster must match Cases.</summary>
    SuiteDefinitionManifest? Definition => null;
    /// <summary>The root result path, also used for infrastructure errors.</summary>
    string CasePath { get; }
    /// <summary>The cases in execution order.</summary>
    IReadOnlyList<IAefJobCase> Cases { get; }
    /// <summary>The metrics this suite may emit.</summary>
    IReadOnlyList<AefMetric> Metrics { get; }
    /// <summary>The entries summarized in the lane assigned by the plan.</summary>
    IReadOnlyList<AefSummaryEntry> SummaryEntries { get; }
    /// <summary>Complete per-role run usage, including preparation and interrupted calls; null when unavailable.</summary>
    IReadOnlyList<AefUsage>? SummaryUsage => null;
    /// <summary>Known lower cost estimate for preparing this run.</summary>
    double PreparationUsdLow => 0;
    /// <summary>Upper cost bound for preparing this run, charged separately from cases.</summary>
    double PreparationUsdBound => 0;
    /// <summary>Upper time bound for preparation in seconds; zero means no asynchronous preparation.</summary>
    long PreparationSecondsBound => 0;
    /// <summary>Preparation cost incurred so far, including on interruption.</summary>
    double PreparationIncurredUsd => 0;
    /// <summary>Prepares the run after its first case and preparation jointly fit the remaining limits.</summary>
    ValueTask<AefJobPreparationResult> PrepareAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(new AefJobPreparationResult(true, 0, 0));
    /// <summary>Releases run resources after sealing or failure. Must honor cancellation and make no charged model calls.</summary>
    ValueTask CloseAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

/// <summary>Readiness and cost of preparing one run; preparation does not count as a case.</summary>
/// <param name="Ready">Whether the target is ready to execute cases.</param>
/// <param name="Usd">The cost incurred by preparation.</param>
/// <param name="Seconds">Preparation duration on a simulated clock.</param>
public sealed record AefJobPreparationResult(bool Ready, double Usd, long Seconds);

/// <summary>A case executed only after its cost and time bounds fit the remaining job budget.</summary>
public interface IAefJobCase
{
    /// <summary>The original case id; the runner never rewrites it.</summary>
    string CaseId { get; }
    /// <summary>A known lower estimate of the case cost.</summary>
    double UsdLow { get; }
    /// <summary>An upper bound on the case cost.</summary>
    double UsdBound { get; }
    /// <summary>An upper bound on execution time in seconds.</summary>
    long SecondsBound { get; }
    /// <summary>Cost incurred so far, including on cancellation or an exception.</summary>
    double IncurredUsd { get; }
    /// <summary>Executes this case once, respecting cancellation and the declared bounds.</summary>
    ValueTask<AefJobCaseResult> RunAsync(CancellationToken cancellationToken);
}

/// <summary>A case's evidence and cost. Seconds advances a simulated clock; real clocks measure elapsed time.</summary>
/// <param name="Root">The case's root result, before the runner supplies its lane and timestamps.</param>
/// <param name="Usd">The cost incurred by this case.</param>
/// <param name="Seconds">The duration on a simulated clock, in seconds.</param>
/// <param name="Children">Optional child evidence.</param>
/// <param name="Judges">Models that actually graded this case.</param>
public sealed record AefJobCaseResult(AefResult Root, double Usd, long Seconds,
    IReadOnlyList<AefJobCaseNode>? Children = null, IReadOnlyList<AefJudge>? Judges = null)
{
    /// <summary>Observed root content; ignored by the writer when the plan disables capture.</summary>
    public IReadOnlyList<AefTextEvidence>? Content { get; init; }
}

/// <summary>A result tree below a case root.</summary>
/// <param name="Result">The child result.</param>
/// <param name="Children">Its children, if any.</param>
public sealed record AefJobCaseNode(AefResult Result, IReadOnlyList<AefJobCaseNode>? Children = null)
{
    /// <summary>Observed child content; ignored by the writer when the plan disables capture.</summary>
    public IReadOnlyList<AefTextEvidence>? Content { get; init; }
}

/// <summary>The job clock; simulated clocks advance explicitly, real clocks measure elapsed execution.</summary>
public interface IAefJobClock
{
    /// <summary>The current job time.</summary>
    AefTime Now { get; }
    /// <summary>Accounts for simulated elapsed seconds. A real clock need not advance explicitly.</summary>
    void Advance(long seconds);
}

internal sealed class AefSimulatedJobClock(AefTime start) : IAefJobClock
{
    public AefTime Now { get; private set; } = start;
    public void Advance(long seconds) => Now = Now.AddSeconds(seconds);
}
