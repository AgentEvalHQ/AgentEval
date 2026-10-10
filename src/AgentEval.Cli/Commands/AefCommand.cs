// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.CommandLine;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentEval.Interop.AssertAi;
using AgentEval.Output;
using AgentEval.Results;
using AgentEval.Results.Adapters;
using AgentEval.Results.Adapters.AssertAi;
using AgentEval.Results.Adapters.Inspect;
using AgentEval.Results.Adapters.Otel;
using AgentEval.Results.Adapters.StoreV1;
using AgentEval.Results.Checkpoints;
using AgentEval.Results.Integrity;
using AgentEval.Results.Signatures;
using AgentEval.Results.Writing;

namespace AgentEval.Cli.Commands;

/// <summary>
/// <c>agenteval aef</c>: AgentEval Evidence Format 1.0 runs (contracts/aef/1).
/// <list type="bullet">
/// <item><c>verify</c>: a run's outcome (§4.5) and every problem;</item>
/// <item><c>seal</c>: seal a closed run (§4.1), optionally signed;</item>
/// <item><c>view</c>: a run's effective view with its overlays (§4.3);</item>
/// <item><c>checkpoint</c>: a checkpoint's problems ([CKP-7], [CKP-8]) and each lane's result (§5.3);</item>
/// <item><c>export</c>: a run of AgentEval's older output store (store v1, §7.5) as an AEF run;</item>
/// <item><c>import assert-ai</c>: an ASSERT run as an AEF run (interop/assert.md);</item>
/// <item><c>import otel</c>: OpenTelemetry <c>gen_ai.evaluation.result</c> events as an AEF run (interop/opentelemetry.md);</item>
/// <item><c>export-otel</c>: an AEF run as OpenTelemetry <c>gen_ai.evaluation.result</c> events, OTLP/JSON <c>LogsData</c> lines (interop/opentelemetry.md);</item>
/// <item><c>import inspect</c>: an Inspect eval log (<c>.json</c> form) as an AEF run (interop/inspect.md);</item>
/// <item><c>export-inspect</c>: an AEF run as an Inspect eval log, <c>.json</c> form (interop/inspect.md).</item>
/// </list>
/// Every verb takes <c>--json</c> for one JSON value on standard output. Exit codes: 0 when the run (or checkpoint) holds
/// no problem, 1 when it does (or a seal is refused), 2 for a usage or input error (for <c>export-otel</c> and
/// <c>export-inspect</c>, a run it refuses to export too: an invalid one, or one the page's rules refuse).
/// </summary>
internal static class AefCommand
{
    private static readonly JsonSerializerOptions s_json = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static Command Create()
    {
        var cmd = new Command("aef", "Work with AEF 1.0 runs (the AgentEval Evidence Format): verify, seal, view, check a checkpoint, export AgentEval's older store, import an ASSERT run, OpenTelemetry events or an Inspect log, export a run as OpenTelemetry events or an Inspect log.");
        cmd.Add(CreateVerify());
        cmd.Add(CreateSeal());
        cmd.Add(CreateView());
        cmd.Add(CreateCheckpoint());
        cmd.Add(CreateExport());
        cmd.Add(CreateImport());
        cmd.Add(CreateExportOtel());
        cmd.Add(CreateExportInspect());
        return cmd;
    }

    private static Option<bool> JsonOption() => new("--json") { Description = "Print one JSON value on stdout instead of a report." };

    private static Option<string?> PolicyOption() => new("--policy") { Description = "A trust policy (JSON: trusted identities and their public keys, SIG-3): says who signed the run, and which identities may redact." };

    // ---- verify --------------------------------------------------------------------------------------------------

    private static Command CreateVerify()
    {
        var runDir = new Argument<string>("run-dir") { Description = "The AEF run folder (holding run.json)." };
        var policy = PolicyOption();
        var anchors = new Option<string?>("--anchors") { Description = "A JSON list of run hashes you trust (from verified checkpoints, a transparency log): says whether the run is anchored (SIG-8)." };
        var json = JsonOption();
        var cmd = new Command("verify", $"Verify a run: intact, unsealed or invalid, with every problem (spec 04 §4.5). Exit 0 when intact or unsealed, {ExitCodes.TestFailure} when invalid.");
        cmd.Add(runDir);
        foreach (var o in new Option[] { policy, anchors, json }) cmd.Add(o);
        cmd.SetAction((ParseResult p, CancellationToken _) => Task.FromResult(RunVerify(
            p.GetValue(runDir)!, p.GetValue(policy), p.GetValue(anchors), p.GetValue(json), Console.Out, Console.Error)));
        return cmd;
    }

    internal static int RunVerify(string runDirectory, string? policyPath, string? anchorsPath, bool asJson, TextWriter stdout, TextWriter stderr)
    {
        AefRunVerification verification;
        try
        {
            RequireFolder(runDirectory);
            verification = AefRunVerifier.Verify(runDirectory, new AefVerifyOptions { Policy = LoadPolicy(policyPath), Anchors = LoadAnchors(anchorsPath) });
        }
        catch (Exception e) when (IsInputError(e))
        {
            stderr.WriteLine($"✖ {e.Message}");
            return ExitCodes.UsageError;
        }

        if (asJson)
        {
            stdout.WriteLine(VerificationJson(verification).ToJsonString(s_json));
        }
        else
        {
            stdout.WriteLine(Outcome(verification));
            WriteDetails(stdout, verification, policyPath is not null);
        }

        return verification.Outcome == AefOutcome.Invalid ? ExitCodes.TestFailure : ExitCodes.Success;
    }

    // ---- seal ----------------------------------------------------------------------------------------------------

    private static Command CreateSeal()
    {
        var runDir = new Argument<string>("run-dir") { Description = "The closed AEF run folder to seal." };
        var key = new Option<string?>("--key") { Description = "An unencrypted PKCS#8 PEM private key (ECDSA P-256) to sign the seal with (attestation.dsse.json, SIG-1). Without it the seal is unsigned." };
        var sealedBy = new Option<string>("--sealed-by") { Description = "producer (the run's own producer seals it: it must have no problem) or ingest (a host taking custody of the run).", DefaultValueFactory = _ => "producer" };
        var json = JsonOption();
        var cmd = new Command("seal", $"Seal a closed run (spec 04 §4.1): write seal.json, and attestation.dsse.json with --key. Exit {ExitCodes.TestFailure} when the run cannot be sealed (open, already sealed, or with problems).");
        cmd.Add(runDir);
        foreach (var o in new Option[] { key, sealedBy, json }) cmd.Add(o);
        cmd.SetAction((ParseResult p, CancellationToken _) => Task.FromResult(RunSeal(
            p.GetValue(runDir)!, p.GetValue(key), p.GetValue(sealedBy)!, p.GetValue(json), Console.Out, Console.Error)));
        return cmd;
    }

    internal static int RunSeal(string runDirectory, string? keyPath, string sealedBy, bool asJson, TextWriter stdout, TextWriter stderr, TimeProvider? clock = null)
    {
        if (!AefNames.TryParse<AefSealedBy>(sealedBy, out var by))
        {
            stderr.WriteLine($"✖ --sealed-by must be producer or ingest, not '{sealedBy}'.");
            return ExitCodes.UsageError;
        }

        EcdsaP256Signer? signer = null;
        try
        {
            RequireFolder(runDirectory);
            signer = keyPath is null ? null : AefSigningKey.Load(keyPath);
        }
        catch (Exception e) when (IsInputError(e) || e is NotSupportedException)
        {
            stderr.WriteLine($"✖ {e.Message}");
            return ExitCodes.UsageError;
        }

        using (signer)
        {
            AefSealResult sealedRun;
            try
            {
                sealedRun = AefSealer.Seal(runDirectory, new AefSealOptions { SealedBy = by.Value, Signer = signer, TimeProvider = clock });
            }
            catch (InvalidOperationException e)
            {
                // Open, already sealed, not a run, or with problems (AefWriteException names them).
                stderr.WriteLine($"✖ Not sealed: {e.Message}");
                return ExitCodes.TestFailure;
            }
            catch (Exception e) when (IsInputError(e))
            {
                stderr.WriteLine($"✖ {e.Message}");
                return ExitCodes.UsageError;
            }

            if (asJson)
            {
                var json = VerificationJson(sealedRun.Verification);
                json["runHash"] = sealedRun.RunHash;
                json["sealedAt"] = sealedRun.SealedAt;
                json["sealedBy"] = AefNames.Of(by.Value);
                json["signed"] = sealedRun.Signed;
                stdout.WriteLine(json.ToJsonString(s_json));
            }
            else
            {
                stdout.WriteLine($"Sealed {runDirectory} ({AefNames.Of(by.Value)}, {(sealedRun.Signed ? "signed" : "unsigned")}) at {sealedRun.SealedAt}.");
                stdout.WriteLine($"  run hash {sealedRun.RunHash}");
                stdout.WriteLine($"  now: {Outcome(sealedRun.Verification)}");
            }

            return ExitCodes.Success;
        }
    }

    // ---- view ----------------------------------------------------------------------------------------------------

    private static Command CreateView()
    {
        var runDir = new Argument<string>("run-dir") { Description = "The AEF run folder." };
        var at = new Option<string?>("--at") { Description = "The time to compute the view at (RFC 3339 UTC, e.g. 2026-10-08T12:00:00Z): waivers hold from their at until their expires (OVL-9). Default: now." };
        var policy = PolicyOption();
        var json = JsonOption();
        var cmd = new Command("view", "Show a run's effective view (spec 04 §4.3): results an override or adjudication sets, review status, waivers, withheld blobs. The sealed files never change.");
        cmd.Add(runDir);
        foreach (var o in new Option[] { at, policy, json }) cmd.Add(o);
        cmd.SetAction((ParseResult p, CancellationToken _) => Task.FromResult(RunView(
            p.GetValue(runDir)!, p.GetValue(at), p.GetValue(policy), p.GetValue(json), Console.Out, Console.Error)));
        return cmd;
    }

    internal static int RunView(string runDirectory, string? at, string? policyPath, bool asJson, TextWriter stdout, TextWriter stderr)
    {
        EffectiveView view;
        AefTime time;
        try
        {
            RequireFolder(runDirectory);
            time = at is null ? Now() : AefTime.Parse(at);
            view = EffectiveView.Compute(runDirectory, time, LoadPolicy(policyPath));
        }
        catch (Exception e) when (IsInputError(e))
        {
            stderr.WriteLine($"✖ {e.Message}");
            return ExitCodes.UsageError;
        }

        if (asJson)
        {
            stdout.WriteLine(ViewJson(view).ToJsonString(s_json));
            return ExitCodes.Success;
        }

        stdout.WriteLine($"Effective view of {runDirectory}");
        stdout.WriteLine(view.Results.Count == 0 ? "  Results: as sealed (no override or adjudication)." : "  Results set by an overlay (sealed → effective):");
        foreach (var r in view.Results)
        {
            stdout.WriteLine($"    {r.ResultId}: {r.SealedState ?? "?"} → {r.EffectiveState ?? "?"} ({r.Event})");
        }

        stdout.WriteLine(view.Reviews.Count == 0 ? "  Reviews: none." : "  Reviews:");
        foreach (var r in view.Reviews)
        {
            stdout.WriteLine($"    {r.Target}: {r.Status} ({r.Event})");
        }

        stdout.WriteLine(view.Waivers.Count == 0 ? "  Waivers: none." : "  Waivers:");
        foreach (var w in view.Waivers)
        {
            stdout.WriteLine($"    {w.Target.ToJsonString()}: {(w.Active ? "active" : "not active")} until {w.Expires ?? "?"} ({w.Event})");
        }

        stdout.WriteLine(view.Withheld.Count == 0 ? "  Withheld blobs: none." : $"  Withheld blobs ({view.Withheld.Count}): {string.Join(", ", view.Withheld)}");
        if (view.Assurance.Count > 0)
        {
            // [OVL-3]: signed only when the batch's signature verifies for the event's identity under the policy.
            var signed = view.Assurance.Where(a => a.Shown == "signed").Select(a => a.Event).ToList();
            stdout.WriteLine(signed.Count == 0
                ? $"  Assurance: all {Count(view.Assurance.Count, "event")} self-attested (no batch signature verified for its event's identity)."
                : $"  Assurance: signed {string.Join(", ", signed)}; {view.Assurance.Count - signed.Count} other(s) self-attested.");
        }

        if (view.UnsealedEvents > 0)
        {
            stdout.WriteLine($"  {view.UnsealedEvents} overlay event(s) after the last sealed batch: shown as unsealed, with no effect.");
        }

        return ExitCodes.Success;
    }

    // ---- checkpoint ----------------------------------------------------------------------------------------------

    private static Command CreateCheckpoint()
    {
        var manifest = new Argument<string>("checkpoint") { Description = "The checkpoint manifest (JSON). A signature beside it (<name>.dsse.json) is verified with --policy." };
        var runs = new Option<string>("--runs") { Description = "The folder the checkpoint's runs are found in (by their run.json).", Required = true };
        var policy = PolicyOption();
        var at = new Option<string?>("--at") { Description = "The evaluation time for a checkpoint with no recorded decision input (LANE-9), RFC 3339 UTC. Default: now." };
        var envelope = new Option<string?>("--envelope") { Description = "The checkpoint's signature, when not <name>.dsse.json beside the manifest." };
        var json = JsonOption();
        var cmd = new Command("checkpoint", $"Check a checkpoint (spec 05): its manifest alone (CKP-7), against its runs (CKP-8), each lane's recomputed result (§5.3), and its signature. Exit {ExitCodes.TestFailure} when it has a problem.");
        cmd.Add(manifest);
        foreach (var o in new Option[] { runs, policy, at, envelope, json }) cmd.Add(o);
        cmd.SetAction((ParseResult p, CancellationToken _) => Task.FromResult(RunCheckpoint(
            p.GetValue(manifest)!, p.GetValue(runs)!, p.GetValue(policy), p.GetValue(at), p.GetValue(envelope), p.GetValue(json), Console.Out, Console.Error)));
        return cmd;
    }

    internal static int RunCheckpoint(
        string manifestPath, string runsDirectory, string? policyPath, string? at, string? envelopePath, bool asJson, TextWriter stdout, TextWriter stderr)
    {
        CheckpointVerification verification;
        string? envelopeUsed;
        try
        {
            var bytes = File.ReadAllBytes(manifestPath);
            RequireFolder(runsDirectory);
            var policy = LoadPolicy(policyPath);
            var time = at ?? DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
            AefTime.Parse(time);
            envelopeUsed = envelopePath ?? DefaultEnvelope(manifestPath);
            verification = CheckpointVerifier.Verify(bytes, AefRunStore.Open(runsDirectory, policy), new CheckpointVerifyOptions
            {
                At = time,
                Policy = policy,
                EnvelopeFile = envelopeUsed is not null && File.Exists(envelopeUsed) ? envelopeUsed : null,   // not read beyond 56 MiB (SIG-1)
            });
            if (envelopeUsed is not null && !File.Exists(envelopeUsed))
            {
                envelopeUsed = null;
            }
        }
        catch (Exception e) when (IsInputError(e))
        {
            stderr.WriteLine($"✖ {e.Message}");
            return ExitCodes.UsageError;
        }

        var problems = verification.ManifestProblems.Count + verification.Problems.Count;
        if (asJson)
        {
            var json = new JsonObject
            {
                ["manifestProblems"] = new JsonArray([.. verification.ManifestProblems.Select(c => (JsonNode?)c)]),
                ["lanes"] = new JsonArray([.. verification.Lanes.Select(l => (JsonNode?)new JsonObject { ["lane"] = l.Lane, ["result"] = l.Result?.ToJson() })]),
                ["problems"] = Problems(verification.Problems),
            };
            if (policyPath is not null)
            {
                json["signedBy"] = new JsonArray([.. verification.SignedBy.Select(i => (JsonNode?)i)]);
                json["anchors"] = new JsonArray([.. verification.Anchors.Select(h => (JsonNode?)h)]);
            }

            stdout.WriteLine(json.ToJsonString(s_json));
        }
        else
        {
            stdout.WriteLine(problems == 0 ? "checkpoint: no problems" : $"checkpoint: {Count(problems, "problem")}");
            foreach (var code in verification.ManifestProblems)
            {
                stdout.WriteLine($"  manifest  {code}");
            }

            foreach (var problem in verification.Problems)
            {
                stdout.WriteLine($"  {problem.Path}  {problem.Code}");
            }

            foreach (var lane in verification.Lanes)
            {
                var result = lane.Result is null ? "no result"
                    : $"{LaneResult.StatusName(lane.Result.Status)} (subject {lane.Result.SubjectVersion}, oldest closed {lane.Result.OldestClosedAt}"
                      + (lane.Result.Axes is { Count: > 0 } axes ? $", incomparable on {string.Join(", ", axes)}" : "") + ")";
                stdout.WriteLine($"  lane {lane.Lane}: {result}");
            }

            if (policyPath is not null)
            {
                stdout.WriteLine(envelopeUsed is null ? "  not signed (no signature beside the manifest)"
                    : verification.SignedBy.Count > 0 ? $"  signed by {string.Join(", ", verification.SignedBy)}"
                    : "  signed by no identity of the trust policy");
            }
        }

        return problems == 0 ? ExitCodes.Success : ExitCodes.TestFailure;
    }

    // <name>.dsse.json beside <name>.json (spec 04 §4.4: "<checkpoint>.dsse.json, beside a checkpoint manifest").
    private static string? DefaultEnvelope(string manifestPath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
        var name = Path.GetFileName(manifestPath);
        var stem = name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? name[..^5] : name;
        return Path.Combine(directory, stem + ".dsse.json");
    }

    // ---- export (store v1) ---------------------------------------------------------------------------------------

    private static Command CreateExport()
    {
        var storeDir = new Argument<string>("store-dir") { Description = "A run folder of AgentEval's older store (.agenteval/subjects/<kind>/<name>/runs/<run-id>/), or the .agenteval workspace with --run." };
        var outDir = new Argument<string>("out-dir") { Description = "The AEF run folder to write (must not exist, or be empty)." };
        var run = new Option<string?>("--run") { Description = "The run id, when store-dir is the workspace." };
        var targetMode = new Option<string>("--target-mode") { Description = "How the run drove its target, which store v1 does not record: live, replayed, scripted or mocked (the default: never passed off as evidence about the live subject).", DefaultValueFactory = _ => "mocked" };
        var json = JsonOption();
        var (capture, noSeal, key) = ConversionOptions();
        var cmd = new Command("export", "Export a run of AgentEval's older output store (store v1, spec 07 §7.5) as an AEF run: a root line per scenario, a child per assertion, sealed by the exporter (ingest).");
        cmd.Add(storeDir);
        cmd.Add(outDir);
        foreach (var o in new Option[] { run, targetMode, capture, noSeal, key, json }) cmd.Add(o);
        cmd.SetAction(async (ParseResult p, CancellationToken ct) => await RunExportAsync(
            p.GetValue(storeDir)!, p.GetValue(outDir)!, p.GetValue(run), p.GetValue(targetMode)!, p.GetValue(capture)!, p.GetValue(noSeal), p.GetValue(key),
            p.GetValue(json), Console.Out, Console.Error, null, ct));
        return cmd;
    }

    internal static async Task<int> RunExportAsync(
        string storeDirectory, string outputDirectory, string? runId, string targetMode, string contentCapture, bool noSeal, string? keyPath,
        bool asJson, TextWriter stdout, TextWriter stderr, TimeProvider? clock = null, CancellationToken ct = default)
    {
        if (!AefNames.TryParse<AefTargetMode>(targetMode, out var mode))
        {
            stderr.WriteLine($"✖ --target-mode must be live, replayed, scripted or mocked, not '{targetMode}'.");
            return ExitCodes.UsageError;
        }

        if (!AefNames.TryParse<AefContentCapture>(contentCapture, out var capture))
        {
            stderr.WriteLine($"✖ --content-capture must be on or off, not '{contentCapture}'.");
            return ExitCodes.UsageError;
        }

        if (!TryLocateStoreRun(storeDirectory, runId, out var root, out var id, out var error))
        {
            stderr.WriteLine($"✖ {error}");
            return ExitCodes.UsageError;
        }

        EcdsaP256Signer? signer = null;
        try
        {
            signer = keyPath is null ? null : AefSigningKey.Load(keyPath);
            var conversion = await StoreV1Exporter.ExportAsync(new FileSystemOutputStore(root), id, outputDirectory, new StoreV1ExportOptions
            {
                TargetMode = mode.Value,
                ContentCapture = capture.Value,
                Seal = !noSeal,
                Signer = signer,
                TimeProvider = clock,
            }, ct).ConfigureAwait(false);
            return Converted(conversion, asJson, stdout);
        }
        catch (Exception e) when (IsInputError(e) || e is NotSupportedException or JsonException)
        {
            stderr.WriteLine($"✖ {e.Message}");
            return ExitCodes.UsageError;
        }
        catch (InvalidOperationException e)
        {
            // The writer refused to close the run, or the verifier found a problem in it (AefWriteException): a
            // converter bug, not the user's. Nothing is left behind.
            stderr.WriteLine($"✖ The exported run does not close or verify: {e.Message}");
            return ExitCodes.TestFailure;
        }
        finally
        {
            signer?.Dispose();
        }
    }

    // A run folder (…/subjects/<kind>/<name>/runs/<id>, holding manifest.json), or the workspace with --run.
    private static bool TryLocateStoreRun(string storeDirectory, string? runId, out string root, out string id, out string error)
    {
        (root, id, error) = ("", "", "");
        var full = Path.GetFullPath(storeDirectory);
        if (!Directory.Exists(full))
        {
            error = $"{storeDirectory} does not exist.";
            return false;
        }

        if (File.Exists(Path.Combine(full, "manifest.json")))
        {
            var runs = Directory.GetParent(full);
            var subjects = runs?.Parent?.Parent?.Parent;
            if (runs?.Name != "runs" || subjects?.Name != "subjects" || subjects.Parent is null)
            {
                error = $"{storeDirectory} holds a manifest.json but is not a run folder of an AgentEval workspace (.agenteval/subjects/<kind>/<name>/runs/<run-id>/).";
                return false;
            }

            if (runId is not null && runId != Path.GetFileName(full))
            {
                error = $"--run {runId} names another run than the folder {Path.GetFileName(full)}.";
                return false;
            }

            (root, id) = (subjects.Parent.FullName, Path.GetFileName(full));
            return true;
        }

        if (Directory.Exists(Path.Combine(full, "subjects")))
        {
            if (runId is null)
            {
                error = $"{storeDirectory} is an AgentEval workspace: name the run with --run <run-id>.";
                return false;
            }

            (root, id) = (full, runId);
            return true;
        }

        error = $"{storeDirectory} is neither an AgentEval workspace (.agenteval/, with subjects/) nor a run folder in one.";
        return false;
    }

    // ---- import assert-ai ----------------------------------------------------------------------------------------

    private static Command CreateImport()
    {
        var cmd = new Command("import", "Import another tool's run as an AEF run.");
        var runDir = new Argument<string>("assert-run-dir") { Description = "The ASSERT run directory (<results>/<suite>/<run>/, holding scores.jsonl)." };
        var outDir = new Argument<string>("out-dir") { Description = "The AEF run folder to write (must not exist, or be empty)." };
        var taxonomy = new Option<string?>("--taxonomy") { Description = "The taxonomy (default: taxonomy.json in the suite directory)." };
        var testSet = new Option<string?>("--test-set") { Description = "The test set (default: the one manifest.json names, else the suite's)." };
        var calibration = new Option<string?>("--calibration") { Description = "A judge calibration written by 'agenteval assert-ai calibrate -o': judges[].calibration when it was measured on this taxonomy before the run." };
        var maxHarm = new Option<double?>("--max-harm-rate") { Description = "A gate limit on the harm rate (0-1): the summary entry's rule and verdict. Without it the entry is scored." };
        var maxOverRefusal = new Option<double?>("--max-over-refusal-rate") { Description = "A gate limit on the over-refusal rate (0-1)." };
        var json = JsonOption();
        var (capture, noSeal, key) = ConversionOptions();
        var assertAi = new Command("assert-ai", "Import an ASSERT run (assert-ai 0.3) as an AEF run (interop/assert.md): a root line and harm and over-refusal lines per case, ASSERT's rates in summary.json, sealed by the importer (ingest).");
        assertAi.Add(runDir);
        assertAi.Add(outDir);
        foreach (var o in new Option[] { taxonomy, testSet, calibration, maxHarm, maxOverRefusal, capture, noSeal, key, json }) assertAi.Add(o);
        assertAi.SetAction((ParseResult p, CancellationToken _) => Task.FromResult(RunImportAssertAi(
            p.GetValue(runDir)!, p.GetValue(outDir)!, p.GetValue(taxonomy), p.GetValue(testSet), p.GetValue(calibration), p.GetValue(maxHarm), p.GetValue(maxOverRefusal),
            p.GetValue(capture)!, p.GetValue(noSeal), p.GetValue(key), p.GetValue(json), Console.Out, Console.Error)));
        cmd.Add(assertAi);
        cmd.Add(CreateImportOtel());
        cmd.Add(CreateImportInspect());
        return cmd;
    }

    private static Command CreateImportOtel()
    {
        var logsFile = new Argument<string>("logs-file") { Description = "OTLP/JSON logs: one LogsData object per line (OpenTelemetry's file exporter), with gen_ai.evaluation.result events." };
        var outDir = new Argument<string>("out-dir") { Description = "The AEF run folder to write (must not exist, or be empty)." };
        var runId = new Option<string>("--run-id") { Description = "run.json runId (an AEF id): the events carry none.", Required = true };
        var from = new Option<string>("--from") { Description = "run.json imported.from: the tool and version that wrote the events.", Required = true };
        var subject = new Option<string>("--subject") { Description = "run.json subject.ref, a typed reference such as agent:support/support-triage: the events name no subject.", Required = true };
        var subjectKind = new Option<string>("--subject-kind") { Description = "run.json subject.kind (agent, model, workflow, …).", DefaultValueFactory = _ => "agent" };
        var targetMode = new Option<string>("--target-mode") { Description = "How the evaluated operations drove their target: live, replayed, scripted or mocked.", Required = true };
        var json = JsonOption();
        var (capture, noSeal, key) = ConversionOptions();
        var otel = new Command("otel", "Import OpenTelemetry gen_ai.evaluation.result events as an AEF run (interop/opentelemetry.md, OpenTelemetry → AEF): a line per event, sealed by the importer (ingest). Exit 2 for an input the page refuses (OT-4, OT-6, OT-8).");
        otel.Add(logsFile);
        otel.Add(outDir);
        foreach (var o in new Option[] { runId, from, subject, subjectKind, targetMode, capture, noSeal, key, json }) otel.Add(o);
        otel.SetAction((ParseResult p, CancellationToken _) => Task.FromResult(RunImportOtel(
            p.GetValue(logsFile)!, p.GetValue(outDir)!, p.GetValue(runId)!, p.GetValue(from)!, p.GetValue(subject)!, p.GetValue(subjectKind)!, p.GetValue(targetMode)!,
            p.GetValue(capture)!, p.GetValue(noSeal), p.GetValue(key), p.GetValue(json), Console.Out, Console.Error)));
        return otel;
    }

    internal static int RunImportOtel(
        string logsFile, string outputDirectory, string runId, string from, string subjectRef, string subjectKind, string targetMode,
        string contentCapture, bool noSeal, string? keyPath, bool asJson, TextWriter stdout, TextWriter stderr, TimeProvider? clock = null)
    {
        if (!AefNames.TryParse<AefContentCapture>(contentCapture, out var capture))
        {
            stderr.WriteLine($"✖ --content-capture must be on or off, not '{contentCapture}'.");
            return ExitCodes.UsageError;
        }

        if (!AefNames.TryParse<AefTargetMode>(targetMode, out var mode))
        {
            stderr.WriteLine($"✖ --target-mode must be live, replayed, scripted or mocked, not '{targetMode}'.");
            return ExitCodes.UsageError;
        }

        if (!AefNames.TryParse<AefSubjectKind>(subjectKind, out var kind))
        {
            stderr.WriteLine($"✖ --subject-kind '{subjectKind}' is not a subject kind AEF lists.");
            return ExitCodes.UsageError;
        }

        EcdsaP256Signer? signer = null;
        try
        {
            signer = keyPath is null ? null : AefSigningKey.Load(keyPath);
            var conversion = AefOtelImporter.Import(logsFile, outputDirectory, new AefOtelImportOptions
            {
                RunId = runId,
                From = from,
                SubjectRef = subjectRef,
                SubjectKind = kind.Value,
                TargetMode = mode.Value,
                ContentCapture = capture.Value,
                Seal = !noSeal,
                Signer = signer,
                TimeProvider = clock,
            });
            return Converted(conversion, asJson, stdout);
        }
        catch (Exception e) when (IsInputError(e) || e is AefOtelImportException or NotSupportedException)
        {
            stderr.WriteLine($"✖ {e.Message}");
            return ExitCodes.UsageError;
        }
        catch (InvalidOperationException e)
        {
            stderr.WriteLine($"✖ The imported run does not close or verify: {e.Message}");
            return ExitCodes.TestFailure;
        }
        finally
        {
            signer?.Dispose();
        }
    }

    private static Command CreateImportInspect()
    {
        var logFile = new Argument<string>("log-file") { Description = "An Inspect eval log in .json form (or 'inspect log dump' of an .eval log): one EvalLog." };
        var outDir = new Argument<string>("out-dir") { Description = "The AEF run folder to write (must not exist, or be empty)." };
        var targetMode = new Option<string>("--target-mode") { Description = "How the log's solver drove its target, which Inspect does not record: live, replayed, scripted or mocked.", Required = true };
        var json = JsonOption();
        var (capture, noSeal, key) = ConversionOptions();
        var inspect = new Command("inspect", "Import an Inspect eval log as an AEF run (interop/inspect.md, Inspect → AEF): a line per sample and score, a rollup per reduction, sealed by the importer (ingest) when the log is closed. Exit 2 for a log the page refuses (IN-6 to IN-11).");
        inspect.Add(logFile);
        inspect.Add(outDir);
        foreach (var o in new Option[] { targetMode, capture, noSeal, key, json }) inspect.Add(o);
        inspect.SetAction((ParseResult p, CancellationToken _) => Task.FromResult(RunImportInspect(
            p.GetValue(logFile)!, p.GetValue(outDir)!, p.GetValue(targetMode)!, p.GetValue(capture)!, p.GetValue(noSeal), p.GetValue(key), p.GetValue(json), Console.Out, Console.Error)));
        return inspect;
    }

    internal static int RunImportInspect(
        string logFile, string outputDirectory, string targetMode, string contentCapture, bool noSeal, string? keyPath, bool asJson,
        TextWriter stdout, TextWriter stderr, TimeProvider? clock = null)
    {
        if (!AefNames.TryParse<AefContentCapture>(contentCapture, out var capture))
        {
            stderr.WriteLine($"✖ --content-capture must be on or off, not '{contentCapture}'.");
            return ExitCodes.UsageError;
        }

        if (!AefNames.TryParse<AefTargetMode>(targetMode, out var mode))
        {
            stderr.WriteLine($"✖ --target-mode must be live, replayed, scripted or mocked, not '{targetMode}'.");
            return ExitCodes.UsageError;
        }

        // [CONF-3]: exit 2 only for an input error the verb recognises; a failure it did not expect is not one, and
        // System.CommandLine reports it with exit 1.
        EcdsaP256Signer? signer;
        try
        {
            signer = keyPath is null ? null : AefSigningKey.Load(keyPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // The key file is missing or unreadable, or holds no key AgentEval signs with.
            stderr.WriteLine($"✖ {e.Message}");
            return ExitCodes.UsageError;
        }

        using (signer)
        {
            try
            {
                var conversion = AefInspectImporter.Import(logFile, outputDirectory, new AefInspectImportOptions
                {
                    TargetMode = mode.Value,
                    ContentCapture = capture.Value,
                    Seal = !noSeal,
                    Signer = signer,
                    TimeProvider = clock,
                });
                return Converted(conversion, asJson, stdout);
            }
            catch (Exception e) when (e is AefInspectImportException or IOException or UnauthorizedAccessException || e is ArgumentException { ParamName: "directory" })
            {
                // A refusal of the page's rules (naming the rule), a log that cannot be read, or an output folder in use:
                // nothing is written.
                stderr.WriteLine($"✖ {e.Message}");
                return ExitCodes.UsageError;
            }
        }
    }

    internal static int RunImportAssertAi(
        string runDirectory, string outputDirectory, string? taxonomyPath, string? testSetPath, string? calibrationPath, double? maxHarm, double? maxOverRefusal,
        string contentCapture, bool noSeal, string? keyPath, bool asJson, TextWriter stdout, TextWriter stderr, TimeProvider? clock = null)
    {
        if (!AefNames.TryParse<AefContentCapture>(contentCapture, out var capture))
        {
            stderr.WriteLine($"✖ --content-capture must be on or off, not '{contentCapture}'.");
            return ExitCodes.UsageError;
        }

        foreach (var (name, value) in new[] { ("--max-harm-rate", maxHarm), ("--max-over-refusal-rate", maxOverRefusal) })
        {
            if (value is { } v && !(v is >= 0 and <= 1))
            {
                stderr.WriteLine($"✖ {name} is a rate between 0 and 1.");
                return ExitCodes.UsageError;
            }
        }

        EcdsaP256Signer? signer = null;
        try
        {
            var run = AssertAiRun.Read(runDirectory, new AssertAiReadOptions { TaxonomyPath = taxonomyPath, TestSetPath = testSetPath });
            var judgeCalibration = calibrationPath is null ? null : AssertAiJudgeCalibration.FromJson(
                JsonNode.Parse(File.ReadAllText(calibrationPath)) as JsonObject ?? throw new InvalidDataException($"{calibrationPath} is not a calibration object."));
            signer = keyPath is null ? null : AefSigningKey.Load(keyPath);
            var conversion = AssertAiImporter.Import(run, outputDirectory, new AssertAiImportOptions
            {
                ContentCapture = capture.Value,
                MaxHarmRate = maxHarm,
                MaxOverRefusalRate = maxOverRefusal,
                Calibration = judgeCalibration,
                Seal = !noSeal,
                Signer = signer,
                TimeProvider = clock,
            });
            return Converted(conversion, asJson, stdout);
        }
        catch (Exception e) when (IsInputError(e) || e is NotSupportedException or JsonException)
        {
            stderr.WriteLine($"✖ {e.Message}");
            return ExitCodes.UsageError;
        }
        catch (InvalidOperationException e)
        {
            stderr.WriteLine($"✖ The imported run does not close or verify: {e.Message}");
            return ExitCodes.TestFailure;
        }
        finally
        {
            signer?.Dispose();
        }
    }

    // ---- export-otel ---------------------------------------------------------------------------------------------

    private static Command CreateExportOtel()
    {
        var runDir = new Argument<string>("run-dir") { Description = "The AEF run folder (holding run.json): intact or unsealed; an invalid run is refused." };
        var outFile = new Argument<string>("out-file") { Description = "The file to write (must not exist): OTLP/JSON LogsData, one per line, one line per result line." };
        var policy = PolicyOption();
        var json = JsonOption();
        var cmd = new Command("export-otel", "Export an AEF run as OpenTelemetry gen_ai.evaluation.result events (interop/opentelemetry.md, AEF → OpenTelemetry): one event per score, labelled with the result's state, parented to its traceLink. Exit 2 when the run cannot be exported.");
        cmd.Add(runDir);
        cmd.Add(outFile);
        foreach (var o in new Option[] { policy, json }) cmd.Add(o);
        cmd.SetAction((ParseResult p, CancellationToken _) => Task.FromResult(RunExportOtel(
            p.GetValue(runDir)!, p.GetValue(outFile)!, p.GetValue(policy), p.GetValue(json), Console.Out, Console.Error)));
        return cmd;
    }

    internal static int RunExportOtel(string runDirectory, string outputFile, string? policyPath, bool asJson, TextWriter stdout, TextWriter stderr)
    {
        AefOtelExport export;
        try
        {
            RequireFolder(runDirectory);
            export = AefOtelExporter.ExportToFile(runDirectory, outputFile, new AefOtelExportOptions { Policy = LoadPolicy(policyPath) });
        }
        catch (Exception e) when (IsInputError(e) || e is AefOtelExportException)
        {
            // A missing or unreadable run, an output file that exists, an invalid run, or a refusal of the page's rules
            // (AefOtelExportException names the rule): nothing is written.
            stderr.WriteLine($"✖ {e.Message}");
            return ExitCodes.UsageError;
        }

        if (asJson)
        {
            stdout.WriteLine(new JsonObject
            {
                ["runId"] = export.RunId,
                ["outcome"] = AefRunVerification.Name(export.Outcome),
                ["file"] = outputFile,
                ["lines"] = export.Lines.Count,
                ["events"] = export.Events,
                ["notes"] = new JsonArray([.. export.Notes.Select(n => (JsonNode?)n)]),
            }.ToJsonString(s_json));
        }
        else
        {
            stdout.WriteLine($"✔ AEF run {export.RunId} ({AefRunVerification.Name(export.Outcome)}) → {outputFile}: {Count(export.Lines.Count, "LogsData line")}, {Count(export.Events, "gen_ai.evaluation.result event")}");
            foreach (var note in export.Notes)
            {
                stdout.WriteLine($"  - {note}");
            }

            stdout.WriteLine("  Not carried (interop/opentelemetry.md, What does not carry over): the run id and result paths, the result tree, trials, thresholds, evaluator identity, evidence, the seal and overlays.");
        }

        return ExitCodes.Success;
    }

    // ---- export-inspect ------------------------------------------------------------------------------------------

    private static Command CreateExportInspect()
    {
        var runDir = new Argument<string>("run-dir") { Description = "The AEF run folder (holding run.json): intact or unsealed; an invalid run is refused." };
        var outFile = new Argument<string>("out-file") { Description = "The file to write (must not exist): one Inspect EvalLog in .json form." };
        var ignoreOverlays = new Option<bool>("--ignore-overlays") { Description = "Convert a run with overlay events, leaving them out (without it such a run is refused, IN-4)." };
        var policy = PolicyOption();
        var json = JsonOption();
        var cmd = new Command("export-inspect", "Export an AEF run as an Inspect eval log in .json form (interop/inspect.md, AEF → Inspect): a sample per case and trial, a score per result line, a reduction per rollup, the summary as results. Exit 2 when the run cannot be exported.");
        cmd.Add(runDir);
        cmd.Add(outFile);
        foreach (var o in new Option[] { ignoreOverlays, policy, json }) cmd.Add(o);
        cmd.SetAction((ParseResult p, CancellationToken _) => Task.FromResult(RunExportInspect(
            p.GetValue(runDir)!, p.GetValue(outFile)!, p.GetValue(ignoreOverlays), p.GetValue(policy), p.GetValue(json), Console.Out, Console.Error)));
        return cmd;
    }

    internal static int RunExportInspect(string runDirectory, string outputFile, bool ignoreOverlays, string? policyPath, bool asJson, TextWriter stdout, TextWriter stderr)
    {
        // [CONF-3]: exit 2 only for an input error the verb recognises; a failure it did not expect is not one, and
        // System.CommandLine reports it with exit 1.
        AefInspectExport export;
        try
        {
            RequireFolder(runDirectory);
            var policy = LoadPolicy(policyPath);
            export = AefInspectExporter.ExportToFile(runDirectory, outputFile, new AefInspectExportOptions { Policy = policy, IgnoreOverlays = ignoreOverlays });
        }
        catch (Exception e) when (e is AefInspectExportException or IOException or UnauthorizedAccessException or TrustPolicyException)
        {
            // A missing or unreadable run, an output file that exists, a trust policy SIG-3 refuses, an invalid run, or a
            // refusal of the page's rules (AefInspectExportException names the rule): nothing is written.
            stderr.WriteLine($"✖ {e.Message}");
            return ExitCodes.UsageError;
        }

        if (asJson)
        {
            stdout.WriteLine(new JsonObject
            {
                ["runId"] = export.RunId,
                ["outcome"] = AefRunVerification.Name(export.Outcome),
                ["file"] = outputFile,
                ["samples"] = export.Samples,
                ["notes"] = new JsonArray([.. export.Notes.Select(n => (JsonNode?)n)]),
            }.ToJsonString(s_json));
        }
        else
        {
            stdout.WriteLine($"✔ AEF run {export.RunId} ({AefRunVerification.Name(export.Outcome)}) → {outputFile}: one Inspect EvalLog, {Count(export.Samples, "sample")}");
            foreach (var note in export.Notes)
            {
                stdout.WriteLine($"  - {note}");
            }

            stdout.WriteLine("  Not carried (interop/inspect.md, What does not carry over): the state (kept in Score.metadata), the result tree, evaluator identity, evidence links, the seal and overlays, the summary's lanes and rules.");
        }

        return ExitCodes.Success;
    }

    private static (Option<string> Capture, Option<bool> NoSeal, Option<string?> Key) ConversionOptions() =>
    (
        new Option<string>("--content-capture") { Description = "on (the default: prompts, responses, transcripts and judge reasoning go into blobs) or off (none of them, and no digest of one; RUN-11).", DefaultValueFactory = _ => "on" },
        new Option<bool>("--no-seal") { Description = "Leave the run unsealed (seal it later with 'agenteval aef seal')." },
        new Option<string?>("--key") { Description = "An unencrypted PKCS#8 PEM private key (ECDSA P-256) to sign the seal with." }
    );

    // A conversion's report: where it went, the outcome, what the converter supplied, and what the source lacks.
    private static int Converted(AefConversion conversion, bool asJson, TextWriter stdout)
    {
        if (asJson)
        {
            var json = VerificationJson(conversion.Verification);
            json["directory"] = conversion.Directory;
            json["runId"] = conversion.RunId;
            json["from"] = conversion.From;
            json["asserted"] = new JsonArray([.. conversion.Asserted.Select(a => (JsonNode?)a)]);
            json["notes"] = new JsonArray([.. conversion.Notes.Select(n => (JsonNode?)n)]);
            json["sealed"] = conversion.Seal is not null;
            stdout.WriteLine(json.ToJsonString(s_json));
        }
        else
        {
            stdout.WriteLine($"✔ {conversion.From} → AEF run {conversion.RunId} in {conversion.Directory}: {Outcome(conversion.Verification)}");
            stdout.WriteLine($"  run hash {conversion.Verification.RunHash?.Value}");
            stdout.WriteLine($"  Supplied by the converter (imported.asserted): {string.Join(", ", conversion.Asserted)}");
            foreach (var note in conversion.Notes)
            {
                stdout.WriteLine($"  - {note}");
            }
        }

        return conversion.Verification.Outcome == AefOutcome.Invalid ? ExitCodes.TestFailure : ExitCodes.Success;
    }

    // ---- shared --------------------------------------------------------------------------------------------------

    // The outcome in plain words: "intact", "intact, 2 withheld", "unsealed", "invalid: 3 problems".
    internal static string Outcome(AefRunVerification verification)
    {
        var problems = verification.Problems.Count(p => p.Code != "withheld");
        return verification.Outcome switch
        {
            AefOutcome.Invalid => $"invalid: {Count(problems, "problem")}",
            AefOutcome.Intact when verification.Withheld > 0 => $"intact, {verification.Withheld} withheld",
            AefOutcome.Intact => "intact",
            _ => "unsealed",
        };
    }

    private static void WriteDetails(TextWriter stdout, AefRunVerification verification, bool withPolicy)
    {
        if (verification.RunId is { } runId)
        {
            stdout.WriteLine($"  run {runId}, run hash {verification.RunHash?.Value}{(verification.RunHash?.Sealed == true ? " (sealed)" : "")}");
        }

        foreach (var problem in verification.Problems)
        {
            stdout.WriteLine($"  {problem.Path}  {problem.Code}");
        }

        if (verification.Outcome == AefOutcome.Intact)
        {
            // SIG-7: anyone can edit a file and re-seal; say who signed, or that nobody trusted did.
            stdout.WriteLine(!withPolicy ? "  signature not checked (pass --policy to say who signed it)"
                : verification.SignedBy is { Count: > 0 } signedBy ? $"  signed by {string.Join(", ", signedBy)}"
                : "  unsigned, or signed by no identity of the trust policy");
        }

        if (verification.Anchored is { } anchored)
        {
            stdout.WriteLine(anchored ? "  anchored: its run hash is one you trust" : "  not anchored");
        }
    }

    private static JsonObject VerificationJson(AefRunVerification verification)
    {
        var json = new JsonObject
        {
            ["outcome"] = AefRunVerification.Name(verification.Outcome),
            ["problems"] = Problems(verification.Problems),
            ["runId"] = verification.RunId,
            ["runHash"] = verification.RunHash?.Value,
        };
        if (verification.Withheld > 0)
        {
            json["withheld"] = verification.Withheld;
        }

        if (verification.SignedBy is { } signedBy)
        {
            json["signedBy"] = new JsonArray([.. signedBy.Select(i => (JsonNode?)i)]);
        }

        if (verification.Anchored is { } anchored)
        {
            json["anchored"] = anchored;
        }

        return json;
    }

    private static JsonObject ViewJson(EffectiveView view) => new()
    {
        ["results"] = new JsonArray([.. view.Results.Select(r => (JsonNode?)new JsonObject
        {
            ["resultId"] = r.ResultId, ["sealedState"] = r.SealedState, ["effectiveState"] = r.EffectiveState, ["event"] = r.Event,
        })]),
        ["reviews"] = new JsonArray([.. view.Reviews.Select(r => (JsonNode?)new JsonObject { ["target"] = r.Target, ["status"] = r.Status, ["event"] = r.Event })]),
        ["waivers"] = new JsonArray([.. view.Waivers.Select(w => (JsonNode?)new JsonObject
        {
            ["target"] = w.Target.DeepClone(), ["expires"] = w.Expires, ["active"] = w.Active, ["event"] = w.Event,
        })]),
        ["withheld"] = new JsonArray([.. view.Withheld.Select(b => (JsonNode?)b)]),
        ["unsealedEvents"] = view.UnsealedEvents,
        ["assurance"] = new JsonArray([.. view.Assurance.Select(a => (JsonNode?)new JsonObject { ["event"] = a.Event, ["shown"] = a.Shown })]),
    };

    private static JsonArray Problems(IEnumerable<AefProblem> problems) =>
        new([.. problems.Select(p => (JsonNode?)new JsonArray(p.Path, p.Code))]);

    private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";

    private static void RequireFolder(string path)
    {
        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException($"{path} is not a folder.");
        }
    }

    private static TrustPolicy? LoadPolicy(string? path) => path is null ? null : TrustPolicy.Parse(File.ReadAllBytes(path));

    private static IReadOnlyCollection<string>? LoadAnchors(string? path)
    {
        if (path is null)
        {
            return null;
        }

        return JsonNode.Parse(File.ReadAllText(path)) is JsonArray list && list.All(h => h is JsonValue v && v.GetValueKind() == JsonValueKind.String)
            ? [.. list.Select(h => h!.GetValue<string>())]
            : throw new InvalidDataException($"{path} is not a JSON list of run hashes.");
    }

    private static AefTime Now()
    {
        var now = DateTimeOffset.UtcNow;
        return new AefTime(now.ToUnixTimeSeconds(), (int)(now.Ticks % TimeSpan.TicksPerSecond * 100));
    }

    // Input errors: a missing or unreadable file or folder, a file that is not what it should be (a trust policy SIG-3
    // refuses is a FormatException, an unreadable manifest too), a bad time, an output folder that is not empty.
    private static bool IsInputError(Exception e) =>
        e is IOException or UnauthorizedAccessException or FormatException or InvalidDataException or ArgumentException or JsonException;
}
