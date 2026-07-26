// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Buffers;
using System.Text;
using System.Text.Json;

namespace AgentEval.MAF.Gatekeeper;

/// <summary>Deterministic action totals for one replay configuration.</summary>
public sealed record GateReplayActionCounts(int Allow, int Block, int Mutate)
{
    internal static GateReplayActionCounts From(IEnumerable<ToolGateAction> actions)
    {
        var allow = 0;
        var block = 0;
        var mutate = 0;
        foreach (var action in actions)
        {
            switch (action)
            {
                case ToolGateAction.Allow: allow++; break;
                case ToolGateAction.Block: block++; break;
                case ToolGateAction.Mutate: mutate++; break;
                default: throw new ArgumentOutOfRangeException(nameof(actions), action, "Unknown tool-gate action.");
            }
        }

        return new GateReplayActionCounts(allow, block, mutate);
    }
}

/// <summary>
/// Secret-minimizing per-call replay output. It intentionally has no arguments, mutations, verdict reason, or
/// message history.
/// </summary>
public sealed record GateReplayReportRow(
    string Id,
    string FunctionName,
    ToolGateAction Baseline,
    string BaselinePolicy,
    ToolGateAction Candidate,
    string CandidatePolicy,
    bool Diverged);

/// <summary>A deterministic, machine-readable shadow-validation result.</summary>
public sealed class GateReplayReport
{
    internal GateReplayReport(
        string corpusId,
        string baselineConfigId,
        string candidateConfigId,
        IEnumerable<GateReplayReportRow> rows)
    {
        CorpusId = corpusId;
        BaselineConfigId = baselineConfigId;
        CandidateConfigId = candidateConfigId;
        Rows = Array.AsReadOnly(rows.ToArray());
        Total = Rows.Count;
        Diverged = Rows.Count(static row => row.Diverged);
        BaselineActions = GateReplayActionCounts.From(Rows.Select(static row => row.Baseline));
        CandidateActions = GateReplayActionCounts.From(Rows.Select(static row => row.Candidate));
    }

    /// <summary>Opaque identity of the corpus that was evaluated.</summary>
    public string CorpusId { get; }

    /// <summary>Explicit id or stable gate-list fingerprint for the baseline configuration.</summary>
    public string BaselineConfigId { get; }

    /// <summary>Explicit id or stable gate-list fingerprint for the candidate configuration.</summary>
    public string CandidateConfigId { get; }

    /// <summary>Number of evaluated calls.</summary>
    public int Total { get; }

    /// <summary>Number of calls whose effective baseline and candidate actions differ.</summary>
    public int Diverged { get; }

    /// <summary>Baseline Allow/Block/Mutate totals.</summary>
    public GateReplayActionCounts BaselineActions { get; }

    /// <summary>Candidate Allow/Block/Mutate totals.</summary>
    public GateReplayActionCounts CandidateActions { get; }

    /// <summary>Per-call secret-minimizing rows in corpus order.</summary>
    public IReadOnlyList<GateReplayReportRow> Rows { get; }
}

/// <summary>Runs real tool-gate objects against a replay corpus using the live-parity <see cref="GateReplayer"/>.</summary>
public static class GateReplayCorpusRunner
{
    /// <summary>
    /// Evaluates the baseline and candidate independently. Omitted configuration ids are stable fingerprints of
    /// the ordered gate lists.
    /// </summary>
    public static async Task<GateReplayReport> RunAsync(
        GateReplayCorpus corpus,
        IReadOnlyList<IToolGate> baseline,
        IReadOnlyList<IToolGate> candidate,
        string? baselineConfigId = null,
        string? candidateConfigId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(corpus);
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(candidate);

        baselineConfigId ??= GateConfigFingerprint.Compute(toolGates: baseline);
        candidateConfigId ??= GateConfigFingerprint.Compute(toolGates: candidate);
        ValidateConfigId(baselineConfigId, nameof(baselineConfigId));
        ValidateConfigId(candidateConfigId, nameof(candidateConfigId));

        var calls = corpus.Fixtures.Select(static fixture => fixture.Call).ToArray();
        var comparison = await GateReplayer.CompareAsync(
            calls,
            baseline,
            candidate,
            cancellationToken).ConfigureAwait(false);

        var rows = new GateReplayReportRow[comparison.Rows.Count];
        for (var i = 0; i < rows.Length; i++)
        {
            var fixture = corpus.Fixtures[i];
            var row = comparison.Rows[i];
            rows[i] = new GateReplayReportRow(
                fixture.Id,
                fixture.Call.FunctionName,
                row.Baseline.Action,
                row.Baseline.PolicyName,
                row.Candidate.Action,
                row.Candidate.PolicyName,
                row.Diverged);
        }

        return new GateReplayReport(
            corpus.CorpusId,
            baselineConfigId,
            candidateConfigId,
            rows);
    }

    private static void ValidateConfigId(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Any(char.IsControl))
        {
            throw new ArgumentException(
                "Expected a non-empty configuration id of at most 256 characters with no control characters. " +
                "Actual: invalid id. Suggestions: use a release id or GateConfigFingerprint.",
                parameterName);
        }
    }
}

/// <summary>Serializes replay reports with fixed property ordering and no sensitive call payloads.</summary>
public static class GateReplayReportSerializer
{
    /// <summary>The only report schema emitted by this implementation.</summary>
    public const string SchemaVersion = "gatekeeper.replay-report/1";

    /// <summary>Returns deterministic compact JSON.</summary>
    public static string Serialize(GateReplayReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            Write(writer, report);
            writer.Flush();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>Writes deterministic compact JSON to a writable stream.</summary>
    public static async Task WriteAsync(
        Stream destination,
        GateReplayReport report,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(report);
        if (!destination.CanWrite)
        {
            throw new ArgumentException(
                "Expected a writable stream. Actual: CanWrite is false. Suggestions: open the destination for writing.",
                nameof(destination));
        }

        var bytes = Encoding.UTF8.GetBytes(Serialize(report));
        await destination.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    private static void Write(Utf8JsonWriter writer, GateReplayReport report)
    {
        writer.WriteStartObject();
        writer.WriteString("schema", SchemaVersion);
        writer.WriteString("corpusId", report.CorpusId);
        writer.WriteString("baselineConfigId", report.BaselineConfigId);
        writer.WriteString("candidateConfigId", report.CandidateConfigId);
        writer.WriteNumber("total", report.Total);
        writer.WriteNumber("diverged", report.Diverged);
        writer.WritePropertyName("baselineActions");
        WriteCounts(writer, report.BaselineActions);
        writer.WritePropertyName("candidateActions");
        WriteCounts(writer, report.CandidateActions);
        writer.WritePropertyName("calls");
        writer.WriteStartArray();
        foreach (var row in report.Rows)
        {
            writer.WriteStartObject();
            writer.WriteString("id", row.Id);
            writer.WriteString("function", row.FunctionName);
            writer.WriteString("baseline", row.Baseline.ToString());
            writer.WriteString("baselinePolicy", row.BaselinePolicy);
            writer.WriteString("candidate", row.Candidate.ToString());
            writer.WriteString("candidatePolicy", row.CandidatePolicy);
            writer.WriteBoolean("diverged", row.Diverged);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteCounts(Utf8JsonWriter writer, GateReplayActionCounts counts)
    {
        writer.WriteStartObject();
        writer.WriteNumber("allow", counts.Allow);
        writer.WriteNumber("block", counts.Block);
        writer.WriteNumber("mutate", counts.Mutate);
        writer.WriteEndObject();
    }
}

/// <summary>Maximum acceptable candidate deltas for a replay promotion check.</summary>
public sealed record GateReplayThresholds(int MaxDiverged, int MaxCandidateBlocks, int MaxCandidateMutations)
{
    /// <summary>Validates that every configured maximum is non-negative.</summary>
    public void Validate()
    {
        if (MaxDiverged < 0 || MaxCandidateBlocks < 0 || MaxCandidateMutations < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(GateReplayThresholds),
                "Expected non-negative replay thresholds. Actual: at least one threshold is negative. " +
                "Suggestions: use zero for a strict no-change check.");
        }
    }
}

/// <summary>One exceeded replay threshold.</summary>
public sealed record GateReplayThresholdViolation(string Metric, int Actual, int Maximum);

/// <summary>Structured promotion-check outcome.</summary>
public sealed class GateReplayThresholdResult
{
    internal GateReplayThresholdResult(IEnumerable<GateReplayThresholdViolation> violations)
    {
        Violations = Array.AsReadOnly(violations.ToArray());
    }

    /// <summary>True when no configured maximum was exceeded.</summary>
    public bool Passed => Violations.Count == 0;

    /// <summary>Every exceeded threshold, in stable metric order.</summary>
    public IReadOnlyList<GateReplayThresholdViolation> Violations { get; }
}

/// <summary>Evaluates promotion thresholds without coupling gate construction to a CLI.</summary>
public static class GateReplayThresholdEvaluator
{
    /// <summary>Compares report totals with the supplied maximums.</summary>
    public static GateReplayThresholdResult Evaluate(
        GateReplayReport report,
        GateReplayThresholds thresholds)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(thresholds);
        thresholds.Validate();

        var violations = new List<GateReplayThresholdViolation>(3);
        AddIfExceeded(violations, "diverged", report.Diverged, thresholds.MaxDiverged);
        AddIfExceeded(
            violations,
            "candidateBlocks",
            report.CandidateActions.Block,
            thresholds.MaxCandidateBlocks);
        AddIfExceeded(
            violations,
            "candidateMutations",
            report.CandidateActions.Mutate,
            thresholds.MaxCandidateMutations);
        return new GateReplayThresholdResult(violations);
    }

    private static void AddIfExceeded(
        ICollection<GateReplayThresholdViolation> violations,
        string metric,
        int actual,
        int maximum)
    {
        if (actual > maximum)
        {
            violations.Add(new GateReplayThresholdViolation(metric, actual, maximum));
        }
    }
}

/// <summary>Minimal test/build adapter for mapping a structured threshold result to a process exit code.</summary>
public static class GateReplayBuildAdapter
{
    /// <summary>Returns zero on pass and one on failure.</summary>
    public static int GetExitCode(GateReplayThresholdResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Passed ? 0 : 1;
    }
}
