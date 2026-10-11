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
        AefTargetMode targetMode, string? judgeModel, AgentEval.RedTeam.RedTeamResult scan, EvalResult verdict)
    {
        var root = aef?.FullName ?? Path.Combine(agentEvalDir, "aef");
        try
        {
            var (directory, runHash) = AefProbeRunWriter.Write(Path.Combine(root, "runs"), RedTeamAef.Lines(scan), new AefProbeRunOptions
            {
                SubjectRef = AefEvalWriter.Ref("agent", subject),
                SubjectVersion = subjectVersion,
                JudgeModel = judgeModel,
                TargetMode = targetMode,
                SuiteRef = AefEvalWriter.Ref("bench", $"{family}/{preset}"),
                ProbesNotRun = scan.SkippedProbes,
                Lane = family,
                BenchVerdict = (verdict.Score.Label, verdict.Score.Value),
            }, scan.StartedAt, scan.CompletedAt);
            Console.Error.WriteLine($"AEF run: {directory} (run hash {runHash[..12]}...)");
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
        AefTargetMode targetMode, EvalResult result, DateTimeOffset startedAt, DateTimeOffset endedAt, string subjectKind = "agent") =>
        Write(aef, agentEvalDir, family, preset, subject, subjectVersion, targetMode, AefCompositeRunWriter.Undescribed, result, startedAt, endedAt, subjectKind);

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
        AefTargetMode targetMode, CompositeEval benchmark, EvalResult result, DateTimeOffset startedAt, DateTimeOffset endedAt) =>
        Write(aef, agentEvalDir, family, preset, subject, subjectVersion, targetMode, Shape(benchmark), result, startedAt, endedAt, "agent");

    private static bool Write(
        DirectoryInfo? aef, string agentEvalDir, string family, string preset, string subject, string? subjectVersion,
        AefTargetMode targetMode, AefComponentShape shape, EvalResult result, DateTimeOffset startedAt, DateTimeOffset endedAt,
        string subjectKind)
    {
        var root = aef?.FullName ?? Path.Combine(agentEvalDir, "aef");
        try
        {
            var (directory, runHash) = AefCompositeRunWriter.Write(Path.Combine(root, "runs"), result, shape, new AefCompositeRunOptions
            {
                SubjectRef = AefEvalWriter.Ref(subjectKind, subject),   // the bench's own subject kind
                SubjectVersion = subjectVersion,
                TargetMode = targetMode,
                Lane = family,
                SuiteRef = AefEvalWriter.Ref("bench", $"{family}/{preset}"),
            }, startedAt, endedAt);
            Console.Error.WriteLine($"AEF run: {directory} (run hash {runHash[..12]}...)");
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            Console.Error.WriteLine($"{(aef is null ? "Warning" : "Error")}: the AEF run was not written: {ex.Message}");
            return aef is not null;
        }
    }
}
