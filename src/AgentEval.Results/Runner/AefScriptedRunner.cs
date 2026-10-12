// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AgentEval.Results.Json;
using AgentEval.Results.Runs;
using AgentEval.Results.Schemas;
using AgentEval.Results.Writing;

namespace AgentEval.Results.Runner;

/// <summary>Execution and analysis settings shared by <see cref="AefJobRunner"/> and its scripted adapter.</summary>
public sealed class AefJobOptions
{
    /// <summary>Optional caller-owned signer for every completed or aborted run seal. Defaults to unsigned seals.</summary>
    public AgentEval.Results.Signatures.IAefSigner? Signer { get; init; }

    /// <summary>
    /// The job clock's starting instant. Scripted jobs advance simulated time by preparation, case and closing
    /// durations (spec 09 §9.2.1); live jobs measure monotonic elapsed time from this instant.
    /// </summary>
    public required AefTime At { get; init; }

    /// <summary>
    /// The runner's environment: the value of a variable, or null when it is not set. A credential of scheme <c>env</c>
    /// resolves when the variable its <c>path</c> names is set ([PLAN-3]). The process's environment when not given.
    /// </summary>
    public Func<string, string?> Environment { get; init; } = System.Environment.GetEnvironmentVariable;

    /// <summary>The producer each run's run.json names; <see cref="AefScriptedRunner.DefaultProducer"/> when not given.</summary>
    public AefProducer? Producer { get; init; }

    /// <summary>The execution clock; defaults to simulated time for scripted targets and monotonic elapsed time otherwise.</summary>
    public IAefJobClock? Clock { get; init; }

    /// <summary>Optional progress destination. Receives the exact persisted NDJSON event bytes; remains caller-owned.</summary>
    public Stream? Progress { get; init; }

    /// <summary>Optional additional cost ceiling. May only lower the plan's cap; the supplied plan bytes remain unchanged.</summary>
    public double? MaxUsd { get; init; }

    /// <summary>Refuse before acceptance when the full estimated upper cost exceeds the effective cap. Off by default.</summary>
    public bool RefuseOverBudget { get; init; }
}

/// <summary>What a job wrote (<see cref="AefScriptedRunner.Run"/>).</summary>
/// <param name="JobId">The job's id, on every event.</param>
/// <param name="Events">The number of events in <c>events.ndjson</c>.</param>
/// <param name="Terminal">The terminal event's kind: <c>job.refused</c>, <c>job.sealed</c>, <c>job.cancelled</c> or <c>job.failed</c>.</param>
/// <param name="Limit">The plan limit the job stopped at (<c>maxUsd</c>, <c>cases</c>, <c>timeout</c>), or null.</param>
/// <param name="Reason">Why the plan was refused or the job stopped, or null for a sealed job.</param>
/// <param name="Runs">The runs it sealed, in the order announced.</param>
/// <param name="EndsAt">The terminal event's time.</param>
public sealed record AefJobResult(string JobId, int Events, string Terminal, string? Limit, string? Reason, IReadOnlyList<string> Runs, AefTime EndsAt);

/// <summary>
/// A runner of AEF jobs against a scripted target (contracts/aef/1/spec/06-runners.md §6, and spec 09 §9.2.1 "A
/// scripted target" and "The job's clock"): given a run plan, the capability manifest it acts as, and an
/// <see cref="AefScriptedTarget"/>, it decides whether it takes the plan ([PLAN-7]), runs the plan's suites as runs
/// ([PLAN-8]) within its limits ([PLAN-9]), writes and seals each run with AgentEval.Results' writer
/// (<see cref="AefRunWriter"/>) and sealer (<see cref="AefSealer"/>), and reports the job as an event stream ([STRM-1],
/// §6.4) in <c>events.ndjson</c>. It gives the target mode <c>scripted</c> only ([RUN-7]).
/// </summary>
/// <remarks>
/// <para>
/// Before <c>job.accepted</c> it refuses (<c>job.refused</c>, one event) a plan the reader schema refuses (its
/// <c>planId</c> readable), a plan the manifest does not take (<see cref="RunnerEventStream.WhyNot"/>), a target mode
/// other than <c>scripted</c>, an isolation other than <c>process</c> (it runs the target in its own process), a suite
/// named twice, a suite the target does not have or whose content does not have the plan's digest ([PLAN-8]), and a
/// credential it cannot resolve ([PLAN-3]: <c>env</c> when its variable is set and not empty; it reaches no keychain and
/// no vault). It resolves every credential the plan names, though the target needs none, and writes no value and no
/// reference's path anywhere.
/// </para>
/// <para>
/// Running, it opens one run per suite before the suite's first case, in the plan's order, and runs the suite's cases in
/// the target's order. Before each case it checks the plan's limits in [PLAN-2]'s order against what the case could
/// take: <c>maxUsd</c> against the spend so far plus the case's cost bound, summed both ways a Stream verifier sums a
/// job (its cases, [STRM-3]; its runs' totals, [STRM-4]), each exactly and rounded once ([SUM-5]);
/// <c>cases</c> against the cases completed; <c>timeout</c> against the time since <c>job.accepted</c> plus the case's
/// time bound plus <c>closeSeconds</c>. At the first it could pass it stops: an open run is closed <c>aborted</c>, sealed
/// and announced, and <c>job.failed</c> names the limit and the runs sealed. A suite whose first case a limit stops gets
/// no run. A run whose suite's last case has run is closed <c>completed</c> and sealed before the next check. Each run
/// declares <see cref="PassRate"/>; a run of a suite the plan gives a lane names it on every line and summarises
/// <c>pass-rate</c> at <c>check</c> in that lane ([PLAN-8]); a case's severity is the target's.
/// </para>
/// <para>
/// Every id and time derives from the inputs (the plan's bytes, the manifest, the target, the clock's start): the same
/// job run twice writes the same bytes.
/// </para>
/// </remarks>
public static class AefScriptedRunner
{
    /// <summary>The path of each case's line: the case's root ([RES-5]), as §9.2.1 asks.</summary>
    public const string CasePath = "check";

    /// <summary>The evaluator each line names ([RES-11]): the scripted target's fixed answer.</summary>
    public const string EvaluatorId = "aef-scripted-target";

    /// <summary>run.json <c>producer.name</c> of a run this runner writes.</summary>
    public const string ProducerName = "agenteval-results";

    /// <summary>
    /// The metric every run declares (§9.2.1): <c>pass-rate</c>, a <c>rate</c> (a line counts 1 when <c>passed</c>, 0
    /// otherwise, [SUM-4]), higher is better, from 0 to 1; a suite's lane summarises it at <see cref="CasePath"/>.
    /// </summary>
    public static AefMetric PassRate { get; } = new()
    {
        Id = "pass-rate", Kind = AefMetricKind.Rate, Direction = AefMetricDirection.HigherBetter, Scale = AefScale.Between(0, 1),
    };

    /// <summary>This runner as a producer: <see cref="ProducerName"/> at AgentEval.Results' release version.</summary>
    public static AefProducer DefaultProducer { get; } = new() { Name = ProducerName, Version = ReadVersion() };

    /// <summary>
    /// Runs the plan <paramref name="plan"/> (its exact bytes) as the runner <paramref name="runner"/> describes, against
    /// <paramref name="target"/>, writing into <paramref name="output"/> (which does not exist yet or is empty) the event
    /// stream <c>events.ndjson</c> and <c>runs/&lt;runId&gt;/</c> for each run it seals, and nothing else.
    /// </summary>
    /// <returns>What the job wrote: refused, sealed or failed, all of them a job that ran.</returns>
    /// <exception cref="AefInputException">
    /// An input error, with nothing written: a plan that does not read as an I-JSON document or names no <c>planId</c> a
    /// <c>job.refused</c> can carry (a valid id), a manifest the reader <c>runner</c> schema refuses, or a start from
    /// which the job's clock, moved by every case's time bound and <c>closeSeconds</c> once per suite of the target,
    /// would leave the years 0001 to 9999 ([ENC-8]).
    /// </exception>
    /// <exception cref="AefInputException"><paramref name="output"/> is a file, or a folder that is not empty.</exception>
    /// <exception cref="IOException">The output cannot be written.</exception>
    public static AefJobResult Run(byte[] plan, JsonObject runner, AefScriptedTarget target, string output, AefJobOptions options) =>
        AefJobRunner.RunAsync(plan, runner, new AefScriptedJobTarget(target ?? throw new ArgumentNullException(nameof(target))), output, options)
            .GetAwaiter().GetResult();

    private static string ReadVersion()
    {
        var assembly = typeof(AefScriptedRunner).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (version is not null && version.IndexOf('+', StringComparison.Ordinal) is var plus and >= 0)
        {
            version = version[..plus];
        }

        return version is { Length: > 0 and <= 128 } && version.All(c => c is >= '!' and <= '~')
            ? version
            : assembly.GetName().Version?.ToString() ?? "0.0.0";
    }

    internal static string CutReason(string reason) => AefJobRunner.CutReason(reason);
}

/// <summary>
/// What a run plan does not say and a runner derives ([PLAN-10]), so that two runners given one plan write the same
/// values: <c>subject.kind</c> and <c>deployment.ref</c>.
/// </summary>
public static class AefPlanDefaults
{
    /// <summary>
    /// <c>subject.kind</c>: the kind of the plan's <c>subject.ref</c> (the part before its colon) when it is one of
    /// run.json's <c>subject.kind</c> values, and <c>other</c> otherwise.
    /// </summary>
    public static AefSubjectKind SubjectKind(JsonNode plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var subjectRef = AefNode.String(AefNode.At(plan, "subject", "ref")) ?? "";
        var colon = subjectRef.IndexOf(':', StringComparison.Ordinal);
        return colon > 0 && AefNames.TryParse<AefSubjectKind>(subjectRef[..colon], out var kind) ? kind.Value : AefSubjectKind.Other;
    }

    /// <summary>
    /// <c>deployment.ref</c>: the plan's <c>subject.deployment</c>; when it names none but names an <c>endpoint</c>,
    /// <c>endpoint:</c> followed by the endpoint encoded as [ENC-13] encodes a name (<see cref="EncodeName"/>); null when
    /// it names neither.
    /// </summary>
    public static string? DeploymentRef(JsonNode plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (AefNode.String(AefNode.At(plan, "subject", "deployment")) is { } deployment)
        {
            return deployment;
        }

        return AefNode.String(AefNode.At(plan, "subject", "endpoint")) is { } endpoint ? "endpoint:" + EncodeName(endpoint) : null;
    }

    /// <summary>
    /// A typed reference's name derived from free text ([ENC-13]): each UTF-8 byte from <c>!</c> to <c>~</c> but
    /// <c>%</c> kept, every other byte, and <c>%</c>, as <c>%</c> and two upper-case hex digits; an empty name is
    /// <c>-</c>, and a name that is exactly <c>-</c> is <c>%2D</c>; a result longer than 256 characters is its first 239,
    /// <c>~</c>, and the first 16 lower-case hex characters of the SHA-256 of the text's UTF-8 bytes.
    /// </summary>
    public static string EncodeName(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var bytes = Encoding.UTF8.GetBytes(text);
        var name = new StringBuilder(bytes.Length);
        foreach (var b in bytes)
        {
            if (b is >= 0x21 and <= 0x7E && b != (byte)'%')
            {
                name.Append((char)b);
            }
            else
            {
                name.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        var encoded = name.ToString() switch
        {
            "" => "-",
            "-" => "%2D",
            var written => written,
        };
        return encoded.Length <= 256
            ? encoded
            : encoded[..239] + "~" + Convert.ToHexString(SHA256.HashData(bytes), 0, 8).ToLowerInvariant();
    }
}
