// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using AgentEval.Results.Runner;

namespace AgentEval.Results.Conformance.Ops;

/// <summary>
/// <c>job PLAN RUNNER TARGET OUT --at T</c> (spec 09 §9.3, the <c>job</c> vectors): runs the plan as the runner the
/// manifest describes ([PLAN-7]), against the scripted target (§9.2.1), on the clock that starts at T, with
/// <see cref="AefScriptedRunner"/>; writes <c>OUT/events.ndjson</c> and <c>OUT/runs/&lt;runId&gt;/</c> for each run it
/// seals, and nothing else; prints <c>{"events": the number of events}</c>. Exit 0 whatever the job's end: accepted or
/// refused, sealed or failed. Input errors (exit 2), with nothing written: a PLAN that does not read or names no
/// <c>planId</c> a <c>job.refused</c> can carry, a RUNNER the reader refuses, a TARGET not of §9.2.1's shape, an OUT
/// that is not empty, a T that is not a time.
/// </summary>
internal static class JobOps
{
    private const string Usage = "job PLAN RUNNER TARGET OUT --at T";

    /// <summary>
    /// The operation. The credentials' variables are read from this process's environment ([PLAN-3]). It exits 0 when the
    /// job ran, whatever its end, and 2 on every input or usage error §9.3 lists, each found before anything is written
    /// ([CONF-3]). Anything else that fails is no input error: it is not caught here, and the driver exits 1
    /// (<see cref="Program.Main"/>).
    /// </summary>
    public static int Job(string[] args, TextWriter stdout)
    {
        if (args.Length != 6 || args[4] != "--at" || args[..4].Any(a => a.StartsWith("--", StringComparison.Ordinal)))
        {
            throw new UsageException($"usage: {Usage}");
        }

        var (planPath, runnerPath, targetPath, output) = (args[0], args[1], args[2], args[3]);
        AefTime at;
        try
        {
            at = AefTime.Parse(args[5]);
        }
        catch (FormatException e)
        {
            throw new UsageException($"--at {args[5]}: {e.Message}");
        }

        if (File.Exists(output) || (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any()))
        {
            throw new UsageException($"{output}: OUT is a folder that does not exist yet or is empty (spec 09 §9.3)");
        }

        var plan = DriverIO.Bytes(planPath);
        var runner = DriverIO.Document(runnerPath);
        AefScriptedTarget target;
        try
        {
            target = AefScriptedTarget.Read(DriverIO.Bytes(targetPath));
        }
        catch (FormatException e)
        {
            throw new UsageException($"{targetPath}: not a scripted target (spec 09 §9.2.1): {e.Message}");
        }

        AefJobResult result;
        try
        {
            result = AefScriptedRunner.Run(plan, runner, target, output, new AefJobOptions { At = at });
        }
        catch (FormatException e)
        {
            // The runner's input errors (a plan that does not read or names no planId, a manifest the reader refuses, a
            // clock that would leave [ENC-8]'s years), found before it writes anything.
            throw new UsageException(e.Message);
        }

        return DriverIO.Print(stdout, new JsonObject { ["events"] = result.Events });
    }
}
