// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.CommandLine;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentEval.Cli.Infrastructure;
using AgentEval.Evals.Agentic.Calibration;
using AgentEval.Interop.AssertAi;
using Microsoft.Extensions.AI;

namespace AgentEval.Cli.Commands;

/// <summary>
/// <c>agenteval assert-ai</c>: work with Microsoft's ASSERT (<c>assert-ai</c>, formats of version 0.3.0).
/// <list type="bullet">
/// <item><c>serve</c>: serve a model as an ASSERT HTTP endpoint target;</item>
/// <item><c>import</c>: read an ASSERT run: its headline numbers, every case's verdict, the cases with no score row;</item>
/// <item><c>export</c>: write AgentEval's labelled golden cases as an ASSERT judge-only run;</item>
/// <item><c>calibrate</c>: compare ASSERT's verdicts on those cases with their labels.</item>
/// </list>
/// </summary>
internal static class AssertAiCommand
{
    private static readonly JsonSerializerOptions s_json = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static Command Create()
    {
        var cmd = new Command("assert-ai", "Interoperate with Microsoft's ASSERT (assert-ai 0.3): serve a target, import a run, export cases for its judge, calibrate its judge.");
        cmd.Add(CreateServe());
        cmd.Add(CreateImport());
        cmd.Add(CreateExport());
        cmd.Add(CreateCalibrate());
        return cmd;
    }

    // ---- serve ---------------------------------------------------------------------------------------------------

    private static Command CreateServe()
    {
        var fromEnv = new Option<bool>("--from-env") { Description = "Serve the model of the provider AI_INFERENCE_PROVIDER selects (its key, endpoint and model variables)." };
        var endpoint = new Option<string?>("--endpoint") { Description = "An OpenAI-compatible endpoint to serve instead (with --model; key from --api-key or OPENAI_API_KEY)." };
        var model = new Option<string?>("--model") { Description = "The model (with --endpoint), or a model override for --from-env." };
        var apiKey = new Option<string?>("--api-key") { Description = "The key for --endpoint (default: OPENAI_API_KEY)." };
        var systemPrompt = new Option<string?>("--system-prompt") { Description = "The target's system prompt. ASSERT never sends one to an endpoint." };
        var port = new Option<int>("--port") { Description = "The port.", DefaultValueFactory = _ => 8765 };
        var path = new Option<string>("--path") { Description = "The path.", DefaultValueFactory = _ => "/assert" };
        var host = new Option<string>("--host") { Description = "The host name to listen on. ASSERT accepts 'localhost' but refuses a literal 127.0.0.1 unless ASSERT_ALLOW_PRIVATE_ENDPOINTS=1. '+' answers every host name (ASSERT in a container calling host.docker.internal); on Windows that needs a URL reservation (netsh http add urlacl).", DefaultValueFactory = _ => "localhost" };

        var cmd = new Command("serve", "Serve a model as an ASSERT HTTP endpoint target (POST {message, history} → {response, events}) until Ctrl+C.");
        foreach (var o in new Option[] { fromEnv, endpoint, model, apiKey, systemPrompt, port, path, host }) cmd.Add(o);
        cmd.SetAction(async (ParseResult p, CancellationToken ct) =>
        {
            if (p.GetValue(port) is < 1 or > 65535)
            {
                Console.Error.WriteLine($"✖ --port must be between 1 and 65535, not {p.GetValue(port)}.");
                return ExitCodes.UsageError;
            }

            IChatClient client;
            string name;
            if (p.GetValue(fromEnv))
            {
                var (c, resolved, diagnostic) = ProviderChatClientFactory.TryCreate("agent", p.GetValue(model), generousTimeout: true);
                if (c is null)
                {
                    Console.Error.WriteLine($"✖ {diagnostic}");
                    return ExitCodes.UsageError;
                }

                (client, name) = (c, resolved!);
            }
            else if (p.GetValue(endpoint) is { } url && p.GetValue(model) is { } m)
            {
                (client, name) = (EndpointFactory.CreateOpenAICompatible(url, m, p.GetValue(apiKey)), m);
            }
            else
            {
                Console.Error.WriteLine("✖ Name the model to serve: --from-env, or --endpoint and --model.");
                return ExitCodes.UsageError;
            }

            AssertAiTargetServer server;
            try
            {
                server = AssertAiTargetServer.Start(new AssertAiTarget(client, systemPrompt: p.GetValue(systemPrompt)), p.GetValue(port), p.GetValue(path)!, p.GetValue(host)!, Console.Out);
            }
            catch (UriFormatException ex)
            {
                Console.Error.WriteLine($"✖ '{p.GetValue(host)}' is not a host name to listen on: {ex.Message}");
                return ExitCodes.UsageError;
            }
            catch (Exception ex) when (ex is System.Net.HttpListenerException or ArgumentException)
            {
                Console.Error.WriteLine($"✖ Could not listen: {ex.Message}"
                    + (OperatingSystem.IsWindows() && p.GetValue(host) is not "localhost" ? " (a host other than localhost needs a URL reservation: netsh http add urlacl url=http://+:PORT/ user=Everyone, as administrator)" : ""));
                return ExitCodes.RuntimeError;
            }

            await using (server)
            {
                Console.WriteLine($"▶ ASSERT target for {name} on {server.Endpoint}");
                Console.WriteLine("  In ASSERT's eval config:");
                Console.WriteLine("    pipeline:\n      inference:\n        target:");
                Console.WriteLine($"          endpoint: {server.Endpoint}");
                Console.WriteLine("  Ctrl+C stops it.");
                try
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }
                catch (OperationCanceledException)
                {
                }
            }

            Console.WriteLine("■ Stopped.");
            return ExitCodes.Success;
        });
        return cmd;
    }

    // ---- import --------------------------------------------------------------------------------------------------

    private static Command CreateImport()
    {
        var runDir = new Argument<string>("run-dir") { Description = "The ASSERT run directory (<results>/<suite>/<run>/, holding scores.jsonl)." };
        var taxonomy = new Option<string?>("--taxonomy") { Description = "The taxonomy (default: taxonomy.json in the suite directory, as ASSERT's metrics read it)." };
        var testSet = new Option<string?>("--test-set") { Description = "The test set to find unscored cases against (default: the one manifest.json names, else the suite's)." };
        var calibration = new Option<string?>("--calibration") { Description = "A judge calibration written by 'assert-ai calibrate -o', to show next to the verdicts." };
        var format = new Option<string>("--format") { Description = "text, json or markdown.", DefaultValueFactory = _ => "text" };
        var output = new Option<string?>("-o", "--output") { Description = "Write the report to this file instead of the console." };
        var maxHarm = new Option<double?>("--max-harm-rate") { Description = "Exit 1 when a harm rate (not-permissible violation rate) is above this (0-1); exit 11 when none could be measured." };
        var maxOverRefusal = new Option<double?>("--max-over-refusal-rate") { Description = "Exit 1 when an over-refusal rate (permissible violation rate) is above this (0-1); exit 11 when none could be measured." };
        var maxUnmeasured = new Option<double>("--max-unmeasured") { Description = "With a rate gate: the share of cases (0-1) that may go unmeasured (judge failed, not judged, no score row) before the gate cannot pass (exit 11). Default 0: a rate over fewer cases than ran is not a pass.", DefaultValueFactory = _ => 0.0 };

        var cmd = new Command("import", "Read an ASSERT run: its harm and over-refusal rates (as ASSERT computes them), every case's verdict, and the cases with no score row.");
        cmd.Add(runDir);
        foreach (var o in new Option[] { taxonomy, testSet, calibration, format, output, maxHarm, maxOverRefusal, maxUnmeasured }) cmd.Add(o);
        cmd.SetAction((ParseResult p, CancellationToken _) => Task.FromResult(RunImport(
            p.GetValue(runDir)!, p.GetValue(taxonomy), p.GetValue(testSet), p.GetValue(calibration), p.GetValue(format)!, p.GetValue(output),
            p.GetValue(maxHarm), p.GetValue(maxOverRefusal), Console.Out, Console.Error, p.GetValue(maxUnmeasured))));
        return cmd;
    }

    internal static int RunImport(
        string runDirectory, string? taxonomyPath, string? testSetPath, string? calibrationPath, string format, string? outputPath,
        double? maxHarm, double? maxOverRefusal, TextWriter stdout, TextWriter stderr, double maxUnmeasured = 0.0)
    {
        if (format is not ("text" or "json" or "markdown"))
        {
            stderr.WriteLine($"✖ --format must be text, json or markdown, not '{format}'.");
            return ExitCodes.UsageError;
        }

        foreach (var (name, value) in new[] { ("--max-harm-rate", maxHarm), ("--max-over-refusal-rate", maxOverRefusal), ("--max-unmeasured", (double?)maxUnmeasured) })
        {
            if (value is < 0 or > 1 || (value is { } v && !double.IsFinite(v)))
            {
                stderr.WriteLine($"✖ {name} is a rate between 0 and 1.");
                return ExitCodes.UsageError;
            }
        }

        AssertAiRun run;
        AssertAiJudgeCalibration? judgeCalibration = null;
        try
        {
            run = AssertAiRun.Read(runDirectory, new AssertAiReadOptions { TaxonomyPath = taxonomyPath, TestSetPath = testSetPath });
            if (calibrationPath is not null)
            {
                judgeCalibration = AssertAiJudgeCalibration.FromJson(
                    JsonNode.Parse(File.ReadAllText(calibrationPath)) as JsonObject ?? throw new InvalidDataException($"{calibrationPath} is not a calibration object."));
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException)
        {
            stderr.WriteLine($"✖ {ex.Message}");
            return ExitCodes.UsageError;
        }

        var headlines = AssertAiHeadline.Compute(run);
        var results = AssertAiResults.ToEvalResults(run, judgeCalibration);
        var report = format switch
        {
            "json" => ImportJson(run, headlines, results, judgeCalibration).ToJsonString(s_json),
            "markdown" => ImportMarkdown(run, headlines, results, judgeCalibration),
            _ => ImportText(run, headlines, results, judgeCalibration),
        };
        if (outputPath is not null)
        {
            File.WriteAllText(outputPath, report);
            stdout.WriteLine($"Report written to {outputPath}");
        }
        else
        {
            stdout.Write(report);
        }

        if (run.Taxonomy is null)
        {
            stderr.WriteLine("⚠ No taxonomy was found, so the harm and over-refusal rates cannot be computed (pass --taxonomy).");
        }

        foreach (var warning in run.Warnings)
        {
            stderr.WriteLine($"⚠ {warning}");
        }

        if (judgeCalibration is not null && !judgeCalibration.AppliesTo(run.Taxonomy))
        {
            stderr.WriteLine("⚠ The calibration was measured on another taxonomy than this run's, so it is not attached.");
        }

        var exit = ExitCodes.Success;
        var unmeasured = run.Rows.Count(r => r.Status != AssertAiJudgeStatus.Ok) + run.Missing.Count;
        var total = run.Rows.Count + run.Missing.Count;
        foreach (var (limit, pick, label) in new (double?, Func<AssertAiHeadline, AssertAiRate?>, string)[]
                 {
                     (maxHarm, h => h.NotPermissible, "harm"),
                     (maxOverRefusal, h => h.Permissible, "over-refusal"),
                 })
        {
            if (limit is not { } max)
            {
                continue;
            }

            var rates = headlines.Select(h => (h.Kind, Rate: pick(h)?.Rate)).ToList();
            if (rates.Any(r => r.Rate > max))
            {
                foreach (var r in rates.Where(r => r.Rate > max))
                {
                    stderr.WriteLine($"✖ {r.Kind} {label} rate {Percent(r.Rate!.Value)} is above {Percent(max)}.");
                }

                exit = ExitCodes.TestFailure;
            }
            else if (rates.All(r => r.Rate is null) && exit == ExitCodes.Success)
            {
                stderr.WriteLine($"⚠ No {label} rate could be measured, so --max-{label}-rate cannot pass.");
                exit = ExitCodes.GateIndeterminate;
            }
        }

        // A rate over the cases that happened to be judged is not a pass for the cases that were not: ASSERT drops the
        // ones its judge's content filter stopped, which are likely the most harmful.
        if ((maxHarm is not null || maxOverRefusal is not null) && exit == ExitCodes.Success)
        {
            if (total > 0 && (double)unmeasured / total > maxUnmeasured)
            {
                stderr.WriteLine($"⚠ {unmeasured} of {total} case(s) were not measured (judge failed, not judged, or no score row), more than --max-unmeasured {Percent(maxUnmeasured)} allows, so the gate cannot pass.");
                exit = ExitCodes.GateIndeterminate;
            }
            else if (run.ManifestStatus is { } status && !run.IsComplete)
            {
                stderr.WriteLine($"⚠ The run's manifest says '{status}', not 'completed': its scores may be partial, so the gate cannot pass.");
                exit = ExitCodes.GateIndeterminate;
            }
            else if (run.ManifestStatus is null)
            {
                stderr.WriteLine("⚠ The run has no manifest.json, so it is not known to have finished.");
            }
        }

        return exit;
    }

    private static string JudgeLine(AssertAiRun run, AssertAiJudgeCalibration? calibration)
    {
        var models = run.Rows.Select(r => r.JudgeModel).Where(m => m.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        var named = models.Count > 0 ? string.Join(", ", models) : "not recorded";
        return calibration is not null && models.Contains(calibration.JudgeModel, StringComparer.Ordinal)
            ? calibration.Describe()
            : $"Judge: {named}. Not calibrated: its agreement with labelled cases has not been measured ('agenteval assert-ai calibrate').";
    }

    private static string RateText(AssertAiRate? rate, string other)
    {
        if (rate is null)
        {
            return "not computed (no taxonomy)";
        }

        if (rate.Rate is not { } value)
        {
            return $"not measured (no judged case had a {other} category)";
        }

        var ci = rate.Interval!.Value;
        return $"{Percent(value)} ({rate.Flagged} of {rate.Count}; 95% CI {Percent(ci.Lower)}–{Percent(ci.Upper)}; {rate.NotApplicable} judged case(s) had no {other} category)";
    }

    private static string Percent(double share) => (share * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%";

    private static string ImportText(AssertAiRun run, IReadOnlyList<AssertAiHeadline> headlines, IReadOnlyList<(AssertAiCaseKey Key, Evals.EvalResult Result)> results, AssertAiJudgeCalibration? calibration)
    {
        var s = new StringBuilder();
        s.AppendLine($"ASSERT run {run.SuiteName}/{run.RunName} ({run.ManifestStatus ?? "no manifest.json: not known to have finished"})");
        var unmeasured = run.Rows.Count(r => r.Status != AssertAiJudgeStatus.Ok) + run.Missing.Count;
        s.AppendLine($"  {unmeasured} of {run.Rows.Count + run.Missing.Count} case(s) not measured (judge failed, not judged, or no score row): no rate covers them.");
        s.AppendLine($"  Read as assert-ai 0.3 (ASSERT records no version in its files). Taxonomy: {run.TaxonomyPath ?? "none"}");
        s.AppendLine($"  {JudgeLine(run, calibration)}");
        foreach (var h in headlines)
        {
            s.AppendLine();
            s.AppendLine($"{char.ToUpperInvariant(h.Kind[0])}{h.Kind[1..]} cases: {h.Rows} rows, {h.Scored} judged, {h.JudgeFailed} judge failure(s), {h.ScoringSkipped} not judged (refused input or target error)");
            s.AppendLine($"  Harm (not-permissible violation rate):     {RateText(h.NotPermissible, "not-permissible")}");
            s.AppendLine($"  Over-refusal (permissible violation rate): {RateText(h.Permissible, "permissible")}");
        }

        if (run.Missing.Count > 0)
        {
            s.AppendLine();
            s.AppendLine($"{run.Missing.Count} case(s) with no score row (in no rate; ASSERT's own numbers leave them out silently):");
            foreach (var m in run.Missing.Take(20)) s.AppendLine($"  {m.Key}: {m.Reason}");
            if (run.Missing.Count > 20) s.AppendLine($"  … and {run.Missing.Count - 20} more (--format json lists them all)");
        }

        s.AppendLine();
        s.AppendLine("Verdicts: " + string.Join(", ", results.GroupBy(r => r.Result.Score.Label).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Count()} {g.Key}")));
        foreach (var (key, result) in results.Where(r => r.Result.Score.Label == "fail").Take(20))
        {
            s.AppendLine($"  ✗ {key} [{result.Score.Severity}] {result.Details.Summary}");
        }

        return s.ToString();
    }

    private static string ImportMarkdown(AssertAiRun run, IReadOnlyList<AssertAiHeadline> headlines, IReadOnlyList<(AssertAiCaseKey Key, Evals.EvalResult Result)> results, AssertAiJudgeCalibration? calibration)
    {
        var s = new StringBuilder();
        s.AppendLine($"# ASSERT run `{run.SuiteName}/{run.RunName}`").AppendLine();
        s.AppendLine($"{JudgeLine(run, calibration)} Read as assert-ai 0.3 (ASSERT records no version in its files).").AppendLine();
        s.AppendLine("| Cases | Rows | Judged | Judge failures | Not judged | Harm | Over-refusal |");
        s.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var h in headlines)
        {
            s.AppendLine($"| {h.Kind} | {h.Rows} | {h.Scored} | {h.JudgeFailed} | {h.ScoringSkipped} | {RateText(h.NotPermissible, "not-permissible")} | {RateText(h.Permissible, "permissible")} |");
        }

        if (run.Missing.Count > 0)
        {
            s.AppendLine().AppendLine($"**{run.Missing.Count} case(s) with no score row:**").AppendLine();
            foreach (var m in run.Missing) s.AppendLine($"- `{m.Key}`: {m.Reason}");
        }

        s.AppendLine().AppendLine("| Case | Verdict | Severity | Why |").AppendLine("|---|---|---|---|");
        foreach (var (key, result) in results)
        {
            s.AppendLine($"| `{key}` | {result.Score.Label} | {result.Score.Severity} | {(result.Details.Summary ?? "").Replace("|", "\\|", StringComparison.Ordinal).ReplaceLineEndings(" ")} |");
        }

        return s.ToString();
    }

    private static JsonObject ImportJson(AssertAiRun run, IReadOnlyList<AssertAiHeadline> headlines, IReadOnlyList<(AssertAiCaseKey Key, Evals.EvalResult Result)> results, AssertAiJudgeCalibration? calibration)
    {
        static JsonNode? Rate(AssertAiRate? r) => r is null ? null : new JsonObject
        {
            ["rate"] = r.Rate,
            ["flagged"] = r.Flagged,
            ["count"] = r.Count,
            ["notApplicable"] = r.NotApplicable,
            ["ci95"] = r.Interval is { } ci ? new JsonArray(ci.Lower, ci.Upper) : null,
        };

        return new JsonObject
        {
            ["format"] = "assert-ai-0.3",
            ["suite"] = run.SuiteName,
            ["run"] = run.RunName,
            ["status"] = run.ManifestStatus,
            ["unmeasured"] = run.Rows.Count(r => r.Status != AssertAiJudgeStatus.Ok) + run.Missing.Count,
            ["caseCount"] = run.Rows.Count + run.Missing.Count,
            ["warnings"] = new JsonArray(run.Warnings.Select(w => (JsonNode)w).ToArray()),
            ["taxonomy"] = run.TaxonomyPath,
            ["judgeModels"] = new JsonArray(run.Rows.Select(r => r.JudgeModel).Where(m => m.Length > 0).Distinct(StringComparer.Ordinal).Select(m => (JsonNode)m).ToArray()),
            ["judgeCalibration"] = calibration?.ToJson(),
            ["headlines"] = new JsonArray(headlines.Select(h => (JsonNode)new JsonObject
            {
                ["kind"] = h.Kind,
                ["rows"] = h.Rows,
                ["judged"] = h.Scored,
                ["judgeFailed"] = h.JudgeFailed,
                ["notJudged"] = h.ScoringSkipped,
                ["harm"] = Rate(h.NotPermissible),
                ["overRefusal"] = Rate(h.Permissible),
            }).ToArray()),
            ["missing"] = new JsonArray(run.Missing.Select(m => (JsonNode)new JsonObject { ["case"] = m.Key.ToString(), ["reason"] = m.Reason }).ToArray()),
            ["cases"] = new JsonArray(results.Select(r => (JsonNode)new JsonObject
            {
                ["case"] = r.Key.ToString(),
                ["verdict"] = r.Result.Score.Label,
                ["severity"] = r.Result.Score.Severity,
                ["summary"] = r.Result.Details.Summary,
            }).ToArray()),
        };
    }

    // ---- export --------------------------------------------------------------------------------------------------

    private static Command CreateExport()
    {
        var golden = new Option<string[]>("--golden") { Description = "A labelled golden JSONL file (the agentic calibration format: scenarioId, input, agentResponse, expectedVerdict, …). Repeat for several.", Required = true, AllowMultipleArgumentsPerToken = true };
        var evaluator = new Option<string[]>("--evaluator") { Description = "Only cases of these evaluator keys.", AllowMultipleArgumentsPerToken = true };
        var taxonomy = new Option<string>("--taxonomy") { Description = "The taxonomy ASSERT's judge grades against (an ASSERT taxonomy.json). Required: ASSERT's judge cannot run without one.", Required = true };
        var judgeModel = new Option<string>("--judge-model") { Description = "The judge model as ASSERT names it (LiteLLM: azure/…, openai/…).", Required = true };
        var output = new Option<string>("--out") { Description = "The directory to write the ASSERT run into.", Required = true };
        var suite = new Option<string>("--suite") { Description = "ASSERT suite id.", DefaultValueFactory = _ => "agenteval" };
        var run = new Option<string>("--run") { Description = "ASSERT run id.", DefaultValueFactory = _ => "judge-1" };
        var assertRoot = new Option<string?>("--assert-root") { Description = "The output directory as ASSERT will see it, when ASSERT runs elsewhere (a container)." };

        var cmd = new Command("export", "Write labelled golden cases as an ASSERT judge-only run (inference_set.jsonl, taxonomy, config) for ASSERT's judge to grade.");
        foreach (var o in new Option[] { golden, evaluator, taxonomy, judgeModel, output, suite, run, assertRoot }) cmd.Add(o);
        cmd.SetAction(async (ParseResult p, CancellationToken ct) => await RunExportAsync(
            p.GetValue(golden)!, p.GetValue(evaluator) ?? [], p.GetValue(taxonomy)!, p.GetValue(judgeModel)!, p.GetValue(output)!,
            p.GetValue(suite)!, p.GetValue(run)!, p.GetValue(assertRoot), Console.Out, Console.Error, ct));
        return cmd;
    }

    internal static async Task<int> RunExportAsync(
        IReadOnlyList<string> goldenFiles, IReadOnlyList<string> evaluators, string taxonomyPath, string judgeModel, string outputDirectory,
        string suite, string run, string? assertRoot, TextWriter stdout, TextWriter stderr, CancellationToken ct = default)
    {
        var transcripts = new List<AssertAiTranscript>();
        var unlabelled = 0;
        JsonObject taxonomy;
        try
        {
            taxonomy = JsonNode.Parse(File.ReadAllText(taxonomyPath))?.AsObject() ?? throw new InvalidDataException($"{taxonomyPath} is not a JSON object.");
            var loader = new CalibrationDatasetLoader();
            foreach (var file in goldenFiles)
            {
                await using var stream = File.OpenRead(file);
                var dataset = await loader.LoadAsync(Path.GetFileNameWithoutExtension(file), stream, ct);
                foreach (var entry in dataset.Entries.Where(e => evaluators.Count == 0 || evaluators.Contains(e.EvaluatorKey, StringComparer.Ordinal)))
                {
                    var label = entry.ExpectedVerdict is "pass" or "fail" ? entry.ExpectedVerdict : null;
                    unlabelled += label is null ? 1 : 0;
                    transcripts.Add(AssertAiTranscript.FromEvalInput(
                        entry.ScenarioId,
                        new Evals.EvalInput(entry.Input, Response: entry.AgentResponse, ToolCalls: entry.ToolCalls),
                        label,
                        entry.ConversationHistory?.Select(t => (t.Role, t.Content))));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            stderr.WriteLine($"✖ {ex.Message}");
            return ExitCodes.UsageError;
        }

        if (transcripts.Count == 0)
        {
            stderr.WriteLine("✖ No cases to export (check --golden and --evaluator).");
            return ExitCodes.UsageError;
        }

        var duplicate = transcripts.GroupBy(t => t.CaseId, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            stderr.WriteLine($"✖ Case id '{duplicate.Key}' appears {duplicate.Count()} times; the case map needs one id per case.");
            return ExitCodes.UsageError;
        }

        try
        {
            AssertAiJudgeKit.Write(outputDirectory, transcripts, new AssertAiJudgeKitOptions
            {
                Taxonomy = taxonomy,
                JudgeModel = judgeModel,
                Suite = suite,
                Run = run,
                AssertRoot = assertRoot,
                LabelSet = string.Join(", ", goldenFiles.Select(Path.GetFileName)) + (evaluators.Count > 0 ? $" ({string.Join(", ", evaluators)})" : ""),
            });
        }
        catch (ArgumentException ex)
        {
            stderr.WriteLine($"✖ {ex.Message}");
            return ExitCodes.UsageError;
        }

        var runDirectory = Path.Combine(Path.GetFullPath(outputDirectory), "results", suite, run);
        stdout.WriteLine($"✔ {transcripts.Count} case(s) written as an ASSERT run in {runDirectory}{(unlabelled > 0 ? $" ({unlabelled} without a pass/fail label)" : "")}.");
        stdout.WriteLine("  Context and RAG documents of a case are not sent: ASSERT's transcripts have no slot for them.");
        stdout.WriteLine("  What an earlier ASSERT run left in that run directory was removed, so old verdicts cannot meet new cases.");
        stdout.WriteLine("  Next:");
        stdout.WriteLine($"    assert-ai run --config {Path.Combine(Path.GetFullPath(outputDirectory), AssertAiJudgeKit.ConfigFileName)}");
        stdout.WriteLine($"    agenteval assert-ai calibrate \"{runDirectory}\" --cases \"{Path.Combine(Path.GetFullPath(outputDirectory), AssertAiCaseMap.FileName)}\"");
        return ExitCodes.Success;
    }

    // ---- calibrate -----------------------------------------------------------------------------------------------

    private static Command CreateCalibrate()
    {
        var runDir = new Argument<string>("run-dir") { Description = "The ASSERT run that judged the exported cases." };
        var cases = new Option<string>("--cases") { Description = "The case map written by 'assert-ai export' (agenteval-cases.json).", Required = true };
        var format = new Option<string>("--format") { Description = "text or json.", DefaultValueFactory = _ => "text" };
        var output = new Option<string?>("-o", "--output") { Description = "Also write the calibration for 'assert-ai import --calibration'." };

        var cmd = new Command("calibrate", "Compare ASSERT's verdicts on exported golden cases with their labels: accuracy, Cohen's κ, dangerous errors.");
        cmd.Add(runDir);
        foreach (var o in new Option[] { cases, format, output }) cmd.Add(o);
        cmd.SetAction((ParseResult p, CancellationToken _) => Task.FromResult(RunCalibrate(
            p.GetValue(runDir)!, p.GetValue(cases)!, p.GetValue(format)!, p.GetValue(output), Console.Out, Console.Error)));
        return cmd;
    }

    internal static int RunCalibrate(string runDirectory, string casesPath, string format, string? outputPath, TextWriter stdout, TextWriter stderr)
    {
        if (format is not ("text" or "json"))
        {
            stderr.WriteLine($"✖ --format must be text or json, not '{format}'.");
            return ExitCodes.UsageError;
        }

        AssertAiCalibrationReport report;
        try
        {
            report = AssertAiCalibration.Measure(AssertAiRun.Read(runDirectory), AssertAiCaseMap.Read(casesPath));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            stderr.WriteLine($"✖ {ex.Message}");
            return ExitCodes.UsageError;
        }

        // When it was measured travels with it: an AEF import records a judge's calibration only with that time (RUN-9).
        var calibration = report.ToCalibration() with { MeasuredAt = DateTimeOffset.UtcNow };
        if (outputPath is not null)
        {
            File.WriteAllText(outputPath, calibration.ToJson().ToJsonString(s_json));
        }

        if (format == "json")
        {
            var json = calibration.ToJson();
            json["agreed"] = report.Agreed;
            json["falseAlarms"] = report.FalseAlarms;
            json["noRelevantCategory"] = report.NoRelevantCategory;
            json["accuracyCi95"] = report.AccuracyInterval is { } ci ? new JsonArray(ci.Lower, ci.Upper) : null;
            json["cases"] = new JsonArray(report.Cases.Select(c => (JsonNode)new JsonObject
            {
                ["caseId"] = c.CaseId, ["case"] = c.Key.ToString(), ["expected"] = c.Expected, ["actual"] = c.Actual, ["notMeasured"] = c.NotMeasuredReason,
            }).ToArray());
            stdout.WriteLine(json.ToJsonString(s_json));
        }
        else
        {
            stdout.WriteLine(report.ToCalibration().Describe());
            if (report.AccuracyInterval is { } ci)
            {
                stdout.WriteLine($"  Accuracy 95% CI {Percent(ci.Lower)}–{Percent(ci.Upper)}; {report.FalseAlarms} false alarm(s) (a labelled pass failed); {report.NoRelevantCategory} case(s) the judge found no category relevant to, counted as not flagged.");
            }

            foreach (var c in report.Cases.Where(c => c.Actual is not null && c.Actual != c.Expected))
            {
                stdout.WriteLine($"  ✗ {c.CaseId} ({c.Key}): labelled {c.Expected}, judged {c.Actual}");
            }

            foreach (var c in report.Cases.Where(c => c.Actual is null).Take(10))
            {
                stdout.WriteLine($"  ? {c.CaseId} ({c.Key}): not measured, {c.NotMeasuredReason}");
            }

            if (outputPath is not null)
            {
                stdout.WriteLine($"Calibration written to {outputPath} (use with 'assert-ai import --calibration').");
            }
        }

        return report.Decided > 0 ? ExitCodes.Success : ExitCodes.GateIndeterminate;
    }
}
