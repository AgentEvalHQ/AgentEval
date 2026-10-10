// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentEval.Results.Runs;
using AgentEval.Results.Signatures;
using AgentEval.Results.Writing;

namespace AgentEval.Results.Conformance.Ops;

/// <summary>
/// The write-side operations of spec 09 §9.3, which the conformance runner judges rather than compares: <c>summarize</c>
/// (a Producer computes a summary.json, [SUM-2]–[SUM-9]), <c>seal-write</c> (a Sealer seals a closed run,
/// [SEAL-1]–[SEAL-5]) and <c>sign</c> (a Sealer signs a file in a DSSE envelope, [SIG-1]–[SIG-3]). Each runs
/// AgentEval.Results' writer: <see cref="AefSummaryWriter"/>, <see cref="AefSealer"/>, <see cref="AefSigningKey"/> with
/// <see cref="DsseEnvelope"/>. Anything the writer refuses is an input error (exit code 2).
/// </summary>
internal static class WriteSideOps
{
    /// <summary>
    /// <c>summarize DIR REQUEST</c>: the summary.json document of the run in DIR (run.json, results.ndjson and
    /// metrics.json) for the entries of REQUEST, <c>{"lanes": [{"lane", "metrics": [{"metric", "path", "aggregate"?,
    /// "rule"?, "verdict"?, "value"?}]}]}</c> (spec 09 §9.2.1). A metric metrics.json does not declare, a lane named
    /// twice, one lane, metric and path twice, a verdict or a metric kind AEF does not define, or an aggregate method
    /// of the producer's without its value when lines were measured, is an input error.
    /// </summary>
    public static int Summarize(string[] args, TextWriter stdout)
    {
        DriverIO.Arguments(args, 2, 2, "summarize DIR REQUEST");
        var request = DriverIO.Document(args[1]);
        var (runId, results, metrics) = ReadRun(args[0]);
        var summary = Request(request);
        try
        {
            return DriverIO.Print(stdout, AefSummaryWriter.Build(runId, results, metrics, summary));
        }
        catch (Exception e) when (DriverIO.IsInputError(e))
        {
            throw new UsageException(e.Message);
        }
    }

    /// <summary>
    /// <c>seal-write DIR --sealed-by B --sealed-at T</c>: writes <c>DIR/seal.json</c> ([SEAL-5]), <c>sealedBy</c> B
    /// (<c>producer</c> or <c>ingest</c>) and <c>sealedAt</c> T as written, and prints <c>{"runHash": hex}</c>. An open
    /// run, a T before the run's end, a run already sealed, or a run the sealer does not seal (a producer's with a
    /// problem, a host's the reader schemas refuse) is an input error, and nothing is written.
    /// </summary>
    public static int SealWrite(string[] args, TextWriter stdout)
    {
        const string usage = "usage: seal-write DIR --sealed-by producer|ingest --sealed-at TIME";
        var (paths, options) = Options(args, usage, "--sealed-by", "--sealed-at");
        if (paths.Count != 1 || !options.TryGetValue("--sealed-by", out var by) || !options.TryGetValue("--sealed-at", out var at))
        {
            throw new UsageException(usage);
        }

        if (!AefNames.TryParse<AefSealedBy>(by, out var sealedBy))
        {
            throw new UsageException($"--sealed-by is producer or ingest, not '{by}' ([SEAL-5]); {usage}");
        }

        if (!Directory.Exists(paths[0]))
        {
            throw new UsageException($"{paths[0]}: not a folder");
        }

        try
        {
            var sealedRun = AefSealer.Seal(paths[0], new AefSealOptions { SealedBy = sealedBy.Value, SealedAt = at });
            return DriverIO.Print(stdout, new JsonObject { ["runHash"] = sealedRun.RunHash });
        }
        catch (Exception e) when (DriverIO.IsInputError(e))
        {
            throw new UsageException(e.Message);
        }
    }

    /// <summary>
    /// <c>sign FILE KEY --payload-type T</c>: the DSSE envelope over FILE's bytes ([SIG-1]) with one signature by KEY, an
    /// unencrypted PKCS#8 PEM private key, under its key id ([SIG-3]). AgentEval signs with ECDSA P-256 only ([SIG-2]: a
    /// signer uses one of the two): an Ed25519 key is an input error, "algorithm not supported for signing".
    /// </summary>
    public static int Sign(string[] args, TextWriter stdout)
    {
        const string usage = "usage: sign FILE KEY --payload-type TYPE";
        var (paths, options) = Options(args, usage, "--payload-type");
        if (paths.Count != 2 || !options.TryGetValue("--payload-type", out var payloadType))
        {
            throw new UsageException(usage);
        }

        var file = DriverIO.Bytes(paths[0]);
        string pem;
        try
        {
            pem = new UTF8Encoding(false, true).GetString(DriverIO.Bytes(paths[1]));
        }
        catch (DecoderFallbackException)
        {
            throw new UsageException($"{paths[1]}: not a PEM file (UTF-8 text)");
        }

        try
        {
            using var signer = AefSigningKey.FromPkcs8Pem(pem);
            var envelope = DsseEnvelope.Create(payloadType, file, signer).ToJson();
            return DriverIO.Print(stdout, JsonNode.Parse(envelope));
        }
        catch (NotSupportedException e)
        {
            throw new UsageException($"{paths[1]}: {e.Message}");   // "…: algorithm not supported for signing …"
        }
        catch (ArgumentException e)
        {
            throw new UsageException($"{paths[1]}: {e.Message}");
        }
    }

    // run.json's runId, the result lines (each read and valid against the reader result schema), and metrics.json's
    // metrics by id with their kinds (each declared once, with a kind AEF 1.0 defines).
    private static (string RunId, IReadOnlyList<JsonObject> Results, IReadOnlyDictionary<string, AefMetricKind> Metrics) ReadRun(string directory)
    {
        AefRunDocuments documents;
        try
        {
            documents = AefRunDocuments.Read(AefRunFolder.Open(directory));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new UsageException(e.Message);
        }

        // What summarize reads must read; a closed run has no summary.json yet, which is what is asked for.
        var unread = documents.Problems.Where(p => p.Path is "run.json" or "metrics.json" or "results.ndjson" || p.Path.StartsWith("results.ndjson:", StringComparison.Ordinal)).ToList();
        if (unread.Count > 0 || documents.RunId is not { } runId || documents.Metrics is not { } metricsJson)
        {
            throw new UsageException($"{directory}: run.json, metrics.json and results.ndjson do not all read: {string.Join(", ", unread.Select(p => $"{p.Path} {p.Code}"))}");
        }

        var metrics = new Dictionary<string, AefMetricKind>(StringComparer.Ordinal);
        foreach (var metric in (metricsJson["metrics"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var id = Text(metric["id"]);
            if (id is null || !AefNames.TryParse<AefMetricKind>(Text(metric["kind"]), out var kind) || !metrics.TryAdd(id, kind.Value))
            {
                throw new UsageException($"metrics.json: {metric.ToJsonString()} is not a metric declared once with a kind AEF 1.0 defines ([SUM-1])");
            }
        }

        return (runId, [.. documents.Results.Objects.Select(o => o.Value)], metrics);
    }

    // The request (spec 09 §9.2.1) as what the summary reports: the producer's verdict and, for its own aggregate
    // method, its value; null when it gives neither (the verdict is then scored, [SUM-6]). Also a produce scenario's
    // summary (ProduceOps).
    internal static AefSummary Request(JsonObject request)
    {
        const string shape = "the request is {\"lanes\": [{\"lane\": name, \"metrics\": [{\"metric\", \"path\", \"aggregate\"?, \"rule\"?, \"verdict\"?, \"value\"?}]}]}";
        if (request["lanes"] is not JsonArray lanes)
        {
            throw new UsageException(shape);
        }

        var result = new List<AefSummaryLane>();
        foreach (var lane in lanes)
        {
            if (lane is not JsonObject laneJson || Text(laneJson["lane"]) is not { } name || laneJson["metrics"] is not JsonArray entries)
            {
                throw new UsageException(shape);
            }

            var list = new List<AefSummaryEntry>();
            foreach (var entry in entries)
            {
                if (entry is not JsonObject e || Text(e["metric"]) is not { } metric || Text(e["path"]) is not { } path)
                {
                    throw new UsageException($"lane {name}: {shape}");
                }

                AefAggregate? aggregate = null;
                if (e.ContainsKey("aggregate"))
                {
                    if (e["aggregate"] is not JsonObject a || Text(a["method"]) is not { } method || (a.ContainsKey("k") && Integer(a["k"]) is null))
                    {
                        throw new UsageException($"{name}/{metric}/{path}: aggregate is an object with a method and an optional integer k ([SUM-8])");
                    }

                    aggregate = new AefAggregate(method, Integer(a["k"]));
                }

                AefSummaryVerdict? verdict = null;
                if (e.ContainsKey("verdict") && !AefNames.TryParse<AefSummaryVerdict>(Text(e["verdict"]), out verdict))
                {
                    throw new UsageException($"{name}/{metric}/{path}: {e["verdict"]?.ToJsonString()} is not a summary verdict ([SUM-6])");
                }

                double? value = null;
                if (e.ContainsKey("value") && (value = Number(e["value"])) is null)
                {
                    throw new UsageException($"{name}/{metric}/{path}: value is a number ([SUM-8])");
                }

                if (e.ContainsKey("rule") && Text(e["rule"]) is null)
                {
                    throw new UsageException($"{name}/{metric}/{path}: rule is a string");
                }

                list.Add(new AefSummaryEntry
                {
                    Metric = metric,
                    Path = path,
                    Aggregate = aggregate,
                    Rule = Text(e["rule"]),
                    Decide = verdict is null && value is null ? null : _ => new AefSummaryDecision(verdict ?? AefSummaryVerdict.Scored, value),
                });
            }

            result.Add(new AefSummaryLane(name, list));
        }

        return new AefSummary { Lanes = result };
    }

    // One or more paths and the named options (each at most once, each with a value), else the usage.
    private static (List<string> Paths, Dictionary<string, string> Options) Options(string[] args, string usage, params string[] known)
    {
        var paths = new List<string>();
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                if (!known.Contains(args[i], StringComparer.Ordinal) || options.ContainsKey(args[i]) || i + 1 >= args.Length)
                {
                    throw new UsageException(usage);
                }

                options[args[i]] = args[++i];
            }
            else
            {
                paths.Add(args[i]);
            }
        }

        return (paths, options);
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    private static double? Number(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.Number ? v.GetValue<double>() : null;

    private static long? Integer(JsonNode? node) =>
        Number(node) is { } n && Math.Floor(n) == n && Math.Abs(n) <= 9_007_199_254_740_991 ? (long)n : null;
}
