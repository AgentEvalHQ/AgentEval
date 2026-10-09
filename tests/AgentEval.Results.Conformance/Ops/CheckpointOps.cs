// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Globalization;
using System.Text.Json.Nodes;
using AgentEval.Results.Checkpoints;
using AgentEval.Results.Json;
using AgentEval.Results.Runner;
using AgentEval.Results.Schemas;
using AgentEval.Results.Signatures;

namespace AgentEval.Results.Conformance.Ops;

/// <summary>
/// Checkpoints (spec 05) and the runs a job produced (spec 06 [STRM-4]): <c>checkpoint</c> ([CKP-7], the
/// <c>checkpoint</c> vectors), <c>lanes</c> (§5.3 and [CKP-8], the <c>lane</c> vectors) and <c>conform</c> ([STRM-4],
/// the <c>plan-conformance</c> vectors). A manifest or a plan the reader refuses (for <c>lanes</c> and <c>conform</c>), a
/// trust policy [SIG-3] refuses, a time that is not an AEF time, or a folder that does not exist is an input error (exit
/// code 2).
/// </summary>
internal static class CheckpointOps
{
    /// <summary>
    /// <c>checkpoint FILE</c>: <c>{"writer": "valid"|"invalid", "reader": …, "problems": [code, …] | null}</c>: the
    /// manifest's schema verdicts and, when the reader accepts it, the problems of [CKP-7] in code order. Bytes that are
    /// not an I-JSON document are invalid on both sides.
    /// </summary>
    public static int Checkpoint(string[] args, TextWriter stdout)
    {
        DriverIO.Arguments(args, 1, 1, "checkpoint FILE");
        JsonObject? document;
        try
        {
            document = AefJsonReader.ParseDocument(DriverIO.Bytes(args[0]));
        }
        catch (AefReadException)
        {
            document = null;
        }

        var writer = document is not null && AefSchemas.Writer.IsValid("checkpoint", document);
        var reader = document is not null && AefSchemas.Reader.IsValid("checkpoint", document);
        return DriverIO.Print(stdout, new JsonObject
        {
            ["writer"] = writer ? "valid" : "invalid",
            ["reader"] = reader ? "valid" : "invalid",
            ["problems"] = reader ? new JsonArray([.. CheckpointManifest.Verify(document!).Select(c => (JsonNode?)c)]) : null,
        });
    }

    /// <summary>
    /// <c>lanes CHECKPOINT --runs DIR [--at T] [--policy P] [--envelope E]</c>: <c>{"lanes": [{"lane", "result"}],
    /// "problems", "anchors"}</c>, each lane's recomputed result (§5.3; <c>null</c> for none) in manifest order, the
    /// problems of [CKP-8] as <c>[path, code]</c> pairs, and the run hashes the checkpoint anchors ([CKP-9], [SIG-8]: its
    /// lanes' runs and comparison baselines, in byte order) when it has no problem of [CKP-7] or [CKP-8] and E holds a
    /// signature verified for an identity of P, otherwise none. <c>--at</c> (the evaluation time for a checkpoint with no
    /// recorded input, [LANE-9]) defaults to the time of the call.
    /// </summary>
    public static int Lanes(string[] args, TextWriter stdout)
    {
        const string usage = "lanes CHECKPOINT --runs DIR [--at T] [--policy P] [--envelope E]";
        var (paths, options) = Parse(args, usage, 1, "--runs", "--at", "--policy", "--envelope");
        if (!options.TryGetValue("--runs", out var runs))
        {
            throw new UsageException($"usage: {usage}");
        }

        var at = options.TryGetValue("--at", out var given) ? Time(given) : Now();
        var manifest = DriverIO.Bytes(paths[0]);
        try
        {
            CheckpointVerifier.Read(manifest);
        }
        catch (FormatException e)
        {
            throw new UsageException($"{paths[0]}: {e.Message}");
        }

        var policy = Policy(options);
        var envelope = options.TryGetValue("--envelope", out var envelopePath) ? DriverIO.Bytes(envelopePath) : null;
        var verification = Guard(() => CheckpointVerifier.Verify(
            manifest, AefRunStore.Open(runs, policy), new CheckpointVerifyOptions { At = at, Policy = policy, Envelope = envelope }));
        return DriverIO.Print(stdout, new JsonObject
        {
            ["lanes"] = new JsonArray([.. verification.Lanes.Select(l => (JsonNode?)new JsonObject { ["lane"] = l.Lane, ["result"] = l.Result?.ToJson() })]),
            ["problems"] = DriverIO.Pairs(verification.Problems.Select(p => (p.Path, p.Code))),
            ["anchors"] = new JsonArray([.. verification.Anchors.Select(a => (JsonNode?)a)]),
        });
    }

    /// <summary>
    /// <c>conform EVENTS PLAN RUNS [--policy P]</c>: <c>{"problems"}</c>, the problems of [STRM-4] at
    /// <c>run:&lt;runId&gt;</c> and <c>job</c> as <c>[path, code]</c> pairs, ordered by path and then code (their bytes).
    /// Only the events [STRM-3] reads take part.
    /// </summary>
    public static int Conform(string[] args, TextWriter stdout)
    {
        var (paths, options) = Parse(args, "conform EVENTS PLAN RUNS [--policy P]", 3, "--policy");
        var (plan, _) = ProtocolOps.Plan(paths[1]);
        var stream = RunnerEventStream.Read(DriverIO.Bytes(paths[0]));
        var policy = Policy(options);
        var problems = Guard(() => RunnerEventStream.Conform(stream, plan, AefRunStore.Open(paths[2], policy)));
        return DriverIO.Print(stdout, new JsonObject { ["problems"] = DriverIO.Pairs(problems.Select(p => (p.Where, p.Problem))) });
    }

    // The positional arguments (exactly `count`) and the named options (each at most once, each with a value).
    private static (List<string> Paths, Dictionary<string, string> Options) Parse(string[] args, string usage, int count, params string[] known)
    {
        var paths = new List<string>(count);
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                if (!known.Contains(args[i], StringComparer.Ordinal) || options.ContainsKey(args[i]) || i + 1 >= args.Length)
                {
                    throw new UsageException($"usage: {usage}");
                }

                options[args[i]] = args[++i];
            }
            else
            {
                paths.Add(args[i]);
            }
        }

        return paths.Count == count ? (paths, options) : throw new UsageException($"usage: {usage}");
    }

    // --policy: a trust policy file; one [SIG-3] refuses is an input error.
    private static TrustPolicy? Policy(Dictionary<string, string> options)
    {
        if (!options.TryGetValue("--policy", out var path))
        {
            return null;
        }

        try
        {
            return TrustPolicy.Parse(DriverIO.Bytes(path));
        }
        catch (TrustPolicyException e)
        {
            throw new UsageException($"{path}: {e.Message}");
        }
    }

    // An AEF time as given (it is written into a lane's result as it is, [LANE-9]).
    private static string Time(string text)
    {
        try
        {
            AefTime.Parse(text);
            return text;
        }
        catch (FormatException e)
        {
            throw new UsageException($"--at: {e.Message}");
        }
    }

    // The time of the call, as an AEF time ([ENC-8]).
    private static string Now() => DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    // A folder that does not exist or a file that cannot be read is an input error.
    private static T Guard<T>(Func<T> operation)
    {
        try
        {
            return operation();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new UsageException(e.Message);
        }
        catch (FormatException e)
        {
            throw new UsageException(e.Message);
        }
    }
}
