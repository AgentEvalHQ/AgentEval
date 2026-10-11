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
        AefTargetMode targetMode, CompositeEval benchmark, EvalResult result, DateTimeOffset startedAt, DateTimeOffset endedAt)
    {
        var root = aef?.FullName ?? Path.Combine(agentEvalDir, "aef");
        try
        {
            var (directory, runHash) = AefCompositeRunWriter.Write(Path.Combine(root, "runs"), result, Shape(benchmark), new AefCompositeRunOptions
            {
                SubjectRef = AefEvalWriter.Ref("agent", subject),
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
