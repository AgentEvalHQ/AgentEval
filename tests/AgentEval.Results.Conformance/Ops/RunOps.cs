// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Nodes;
using AgentEval.Results.Integrity;
using AgentEval.Results.Runs;
using AgentEval.Results.Signatures;

namespace AgentEval.Results.Conformance.Ops;

/// <summary>
/// A run's verification (spec 04; spec 09 §9.3): <c>run</c> (§4.5: the outcome and every problem of §3.9 and §4.1),
/// <c>seal</c> (§4.1: the manifest, the run hash and the seal's problems), <c>chain</c> (§4.2: the overlay chain's
/// problems) and <c>view</c> (§4.3: the effective view). A trust policy [SIG-3] refuses, an anchors file that is not a
/// JSON list of strings, a time that is not an AEF time, or a run folder that does not exist is an input error (exit
/// code 2).
/// </summary>
internal static class RunOps
{
    /// <summary>
    /// <c>run DIR [--policy P] [--anchors A]</c>: <c>{"outcome", "problems"}</c>, with <c>withheld</c> (a count) when not
    /// 0, <c>signedBy</c> (identities, in policy order) with <c>--policy</c>, and <c>anchored</c> with <c>--anchors</c>.
    /// </summary>
    public static int Run(string[] args, TextWriter stdout)
    {
        var (paths, options) = Parse(args, "run DIR [--policy P] [--anchors A]", "--policy", "--anchors");
        var verification = Guard(() => AefRunVerifier.Verify(paths[0], new AefVerifyOptions
        {
            Policy = Policy(options),
            Anchors = Anchors(options),
        }));

        var output = new JsonObject
        {
            ["outcome"] = AefRunVerification.Name(verification.Outcome),
            ["problems"] = Problems(verification.Problems),
        };
        if (verification.Withheld > 0)
        {
            output["withheld"] = verification.Withheld;
        }

        if (verification.SignedBy is { } signedBy)
        {
            output["signedBy"] = new JsonArray([.. signedBy.Select(i => (JsonNode?)i)]);
        }

        if (verification.Anchored is { } anchored)
        {
            output["anchored"] = anchored;
        }

        return DriverIO.Print(stdout, output);
    }

    /// <summary><c>seal DIR [--policy P]</c>: <c>{"manifest": text, "runHash": hex, "problems"}</c> (§4.1 only).</summary>
    public static int Seal(string[] args, TextWriter stdout)
    {
        var (paths, options) = Parse(args, "seal DIR [--policy P]", "--policy");
        var policy = Policy(options);
        var (manifest, problems) = Guard(() =>
        {
            var folder = AefRunFolder.Open(paths[0]);
            var documents = AefRunDocuments.Read(folder);
            var chain = OverlayChain.Verify(folder, documents);
            var withheld = EffectiveView.AuthorizedRedactions(folder, chain, policy).ToHashSet(StringComparer.Ordinal);
            return (folder.Manifest(), SealVerifier.Verify(folder, documents.Run, withheld).Problems);
        });

        return DriverIO.Print(stdout, new JsonObject
        {
            ["manifest"] = manifest.Text,
            ["runHash"] = manifest.RunHash,
            ["problems"] = Problems(problems),
        });
    }

    /// <summary><c>chain DIR</c>: <c>{"problems"}</c> (§4.2 only).</summary>
    public static int Chain(string[] args, TextWriter stdout)
    {
        var (paths, _) = Parse(args, "chain DIR");
        var chain = Guard(() =>
        {
            var folder = AefRunFolder.Open(paths[0]);
            return OverlayChain.Verify(folder, AefRunDocuments.Read(folder));
        });

        return DriverIO.Print(stdout, new JsonObject { ["problems"] = Problems(chain.Problems) });
    }

    /// <summary>
    /// <c>view DIR --at T [--policy P]</c>: the effective view of spec 09 §9.2.1, <c>{"results", "reviews", "waivers",
    /// "withheld", "unsealedEvents", "assurance"}</c>. <c>--at</c> defaults to the time of the call (§9.3).
    /// </summary>
    public static int View(string[] args, TextWriter stdout)
    {
        var (paths, options) = Parse(args, "view DIR [--at T] [--policy P]", "--at", "--policy");
        AefTime at;
        if (options.TryGetValue("--at", out var text))
        {
            try
            {
                at = AefTime.Parse(text);
            }
            catch (FormatException e)
            {
                throw new UsageException($"--at: {e.Message}");
            }
        }
        else
        {
            var now = DateTimeOffset.UtcNow;
            at = new AefTime(now.ToUnixTimeSeconds(), (int)(now.Ticks % TimeSpan.TicksPerSecond * 100));
        }

        var policy = Policy(options);
        var view = Guard(() => EffectiveView.Compute(paths[0], at, policy));
        return DriverIO.Print(stdout, new JsonObject
        {
            ["results"] = new JsonArray([.. view.Results.Select(r => (JsonNode?)new JsonObject
            {
                ["resultId"] = r.ResultId,
                ["sealedState"] = r.SealedState,
                ["effectiveState"] = r.EffectiveState,
                ["event"] = r.Event,
            })]),
            ["reviews"] = new JsonArray([.. view.Reviews.Select(r => (JsonNode?)new JsonObject
            {
                ["target"] = r.Target,
                ["status"] = r.Status,
                ["event"] = r.Event,
            })]),
            ["waivers"] = new JsonArray([.. view.Waivers.Select(w => (JsonNode?)new JsonObject
            {
                ["target"] = w.Target.DeepClone(),
                ["expires"] = w.Expires,
                ["active"] = w.Active,
                ["event"] = w.Event,
            })]),
            ["withheld"] = new JsonArray([.. view.Withheld.Select(b => (JsonNode?)b)]),
            ["unsealedEvents"] = view.UnsealedEvents,
            ["assurance"] = new JsonArray([.. view.Assurance.Select(a => (JsonNode?)new JsonObject
            {
                ["event"] = a.Event,
                ["shown"] = a.Shown,
            })]),
        });
    }

    // One folder argument and the named options (each at most once, each with a value), else the usage.
    private static (List<string> Paths, Dictionary<string, string> Options) Parse(string[] args, string usage, params string[] known)
    {
        var paths = new List<string>(1);
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

        return paths.Count == 1 ? (paths, options) : throw new UsageException($"usage: {usage}");
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

    // --anchors: a file holding a JSON list of run hashes.
    private static IReadOnlyCollection<string>? Anchors(Dictionary<string, string> options)
    {
        if (!options.TryGetValue("--anchors", out var path))
        {
            return null;
        }

        return DriverIO.Value(path) is JsonArray list && list.All(h => h is JsonValue v && v.GetValueKind() == JsonValueKind.String)
            ? [.. list.Select(h => h!.GetValue<string>())]
            : throw new UsageException($"{path}: not a JSON list of run hashes");
    }

    private static JsonArray Problems(IEnumerable<AefProblem> problems) => DriverIO.Pairs(problems.Select(p => (p.Path, p.Code)));

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
    }
}
