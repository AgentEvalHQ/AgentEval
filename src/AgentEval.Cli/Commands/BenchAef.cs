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

    /// <summary>
    /// Writes a family's result as a tree whose shape the caller gives (bench memory builds both from its categories).
    /// Returns true only when an explicit <paramref name="aef"/> could not be written.
    /// </summary>
    public static bool WriteTree(
        DirectoryInfo? aef, string agentEvalDir, string family, string preset, string subject, string? subjectVersion,
        AefTargetMode targetMode, AefComponentShape shape, EvalResult result, DateTimeOffset startedAt, DateTimeOffset endedAt) =>
        Write(aef, agentEvalDir, family, preset, subject, subjectVersion, targetMode, shape, result, startedAt, endedAt, "agent");

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
        var root = new EvalResult(
            new EvalMetadata("memory." + preset.ToLowerInvariant(), "Memory " + preset, "memory", "1.0.0"),
            new EvalScore(Math.Clamp(result.OverallScore / 100.0, 0.0, 1.0), null, verdict.ToLowerInvariant(), verdict == "PASS", 0.7, "none", null),
            new EvalDetails(null, null, null, categories, "WeightedSum"),
            new EvalProvenance("composite", null, null, null, null, 0, false),
            DateTimeOffset.UtcNow);
        return (root, new AefComponentShape(1, true, "Averaged",
            result.CategoryResults.Select(c => new AefComponentShape(c.Weight, true, "Averaged", [])).ToList()));
    }

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
