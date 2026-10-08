// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text.Json.Nodes;
using AgentEval.Results.Runner;
using AgentEval.Results.Schemas;

namespace AgentEval.Results.Conformance.Ops;

/// <summary>
/// The runner protocol (spec 06): <c>match</c> ([PLAN-7], the <c>matching</c> vectors) and <c>stream</c> ([STRM-3],
/// the <c>stream</c> vectors), through <see cref="RunnerEventStream"/>.
/// </summary>
internal static class ProtocolOps
{
    /// <summary><c>match PLAN RUNNER</c>: <c>{"matches": true|false}</c>, whether the runner can take the plan.</summary>
    public static int Match(string[] args, TextWriter stdout)
    {
        DriverIO.Arguments(args, 2, 2, "match PLAN RUNNER");
        var plan = Accepted(DriverIO.Document(args[0]), "run-plan", args[0]);
        var runner = Accepted(DriverIO.Document(args[1]), "runner", args[1]);
        return DriverIO.Print(stdout, new JsonObject { ["matches"] = RunnerEventStream.Matches(plan, runner) });
    }

    /// <summary>
    /// <c>stream EVENTS PLAN</c>: <c>{"problems": [[where, problem], …]}</c>, the problems of [STRM-3] against the
    /// plan and the SHA-256 of its bytes. A last line without LF is still being written and is not read ([STRM-2]); a
    /// line that is not an I-JSON object valid against the reader runner-event schema is <c>event-invalid</c>, and
    /// finished lines not framed as [ENC-5] requires are <c>encoding</c> at <c>stream</c>. A plan the reader refuses is
    /// an input error (spec 09 §9.3).
    /// </summary>
    public static int Stream(string[] args, TextWriter stdout)
    {
        DriverIO.Arguments(args, 2, 2, "stream EVENTS PLAN");
        var (plan, planDigest) = Plan(args[1]);
        var problems = RunnerEventStream.Verify(RunnerEventStream.Read(DriverIO.Bytes(args[0])), plan, planDigest);
        return DriverIO.Print(stdout, new JsonObject { ["problems"] = DriverIO.Pairs(problems.Select(p => (p.Where, p.Problem))) });
    }

    /// <summary>A plan file the reader run-plan schema accepts, and the SHA-256 of its bytes; anything else is an input error.</summary>
    public static (JsonObject Plan, string Digest) Plan(string path)
    {
        var bytes = DriverIO.Bytes(path);
        var plan = Accepted(DriverIO.Document(bytes, path), "run-plan", path);
        return (plan, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }

    // A document the reader schema accepts, else an input error.
    private static JsonObject Accepted(JsonObject document, string schema, string where) =>
        AefSchemas.Reader.Validate(schema, document) is { } why
            ? throw new UsageException($"{where}: not valid against the reader {schema} schema: {why}")
            : document;
}
