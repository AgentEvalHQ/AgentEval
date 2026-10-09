// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentEval.Evals;
using AgentEval.Guardrails.Judges;
using AgentEval.Interop.AssertAi;
using AgentEval.Results.Integrity;
using AgentEval.Results.Writing;

namespace AgentEval.Results.Adapters.AssertAi;

/// <summary>How <see cref="AssertAiImporter"/> writes the AEF run.</summary>
public sealed record AssertAiImportOptions : AefConversionOptions
{
    /// <summary>A gate limit on the harm rate (<c>--max-harm-rate</c>): the summary entry's <c>rule</c> and verdict. None: <c>scored</c>.</summary>
    public double? MaxHarmRate { get; init; }

    /// <summary>A gate limit on the over-refusal rate (<c>--max-over-refusal-rate</c>). None: <c>scored</c>.</summary>
    public double? MaxOverRefusalRate { get; init; }

    /// <summary>
    /// AgentEval's calibration of the run's judge (<c>agenteval assert-ai calibrate -o</c>): written as
    /// <c>judges[].calibration</c> only when it was measured on the run's taxonomy, for the run's judge model, at a known
    /// time not after the run started ([RUN-9]).
    /// </summary>
    public AssertAiJudgeCalibration? Calibration { get; init; }

    /// <summary>
    /// How the target was driven ([RUN-7]), when the caller knows. Null: <c>live</c> when ASSERT's inference stage called
    /// the target, <c>replayed</c> for a judge-only run over recorded conversations. Either way the converter's claim.
    /// </summary>
    public AefTargetMode? TargetMode { get; init; }
}

/// <summary>
/// Converts an ASSERT run (assert-ai 0.3, read by <see cref="AssertAiRun"/>) into an AEF 1.0 run folder, as
/// contracts/aef/1/interop/assert.md's "ASSERT → AEF" tables say:
/// <list type="bullet">
/// <item>run.json names the converter as <c>producer</c> and ASSERT in <c>imported</c> ([RUN-15]); <c>runId</c> is
/// <c>&lt;suite&gt;.&lt;run&gt;</c>; the suite's version and digest are the SHA-256 of <c>test_set.jsonl</c>; each judge's
/// rubric digest is the SHA-256 of <c>taxonomy.json</c>;</item>
/// <item>each case is a root line at <see cref="RootPath"/> with two children, <see cref="HarmPath"/> and
/// <see cref="OverRefusalPath"/>, in lane <c>prompt</c> or <c>scenario</c>; a failed or invalid judge is <c>error</c>
/// and a case not judged or with no score row is <c>skipped</c>, on all three lines;</item>
/// <item>summary.json recomputes ASSERT's two rates per lane ([SUM-5]), with AgentEval's 95% Wilson interval, the gate
/// limits as rules, or <c>scored</c> without one ([SUM-6]), and the cases ASSERT's rates leave out as <c>notMeasured</c>.</item>
/// </list>
/// With <c>contentCapture: on</c> the case's seed, transcript, tool calls and the judge's reasoning become blobs; ASSERT's
/// other files travel under <c>ext/assert-ai/</c>.
/// </summary>
public static class AssertAiImporter
{
    /// <summary>run.json <c>imported.from</c>: ASSERT writes no version into its files, so every run is read as 0.3.</summary>
    public const string From = "assert-ai 0.3";

    /// <summary>The path of a case's root line.</summary>
    public const string RootPath = AssertAiResults.Key;

    /// <summary>The path of a case's harm line (ASSERT's not-permissible violation rate).</summary>
    public const string HarmPath = RootPath + "/harm";

    /// <summary>The path of a case's over-refusal line (ASSERT's permissible violation rate).</summary>
    public const string OverRefusalPath = RootPath + "/over_refusal";

    /// <summary>The metric of the harm rate.</summary>
    public const string HarmMetric = "harm";

    /// <summary>The metric of the over-refusal rate.</summary>
    public const string OverRefusalMetric = "over_refusal";

    /// <summary>The <c>ext</c> member the converter writes ASSERT's own fields under ([ENC-19]), and the folder under <c>ext/</c> for its files.</summary>
    public const string ExtName = "agenteval.assert-ai";

    private const string ExtFolder = "assert-ai";

    private static readonly JsonSerializerOptions s_json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static readonly string[] s_credentialKeys = ["api_key", "apikey", "token", "access_token", "auth_token", "password", "passwd", "secret", "client_secret"];
    private static readonly string[] s_credentialSuffixes = ["_api_key", "_apikey", "_password", "_secret", "_access_token", "_auth_token"];

    /// <summary>Converts <paramref name="run"/> into the AEF run folder <paramref name="outputDirectory"/>.</summary>
    /// <param name="run">The ASSERT run.</param>
    /// <param name="outputDirectory">The AEF run folder to write: it must not exist, or be empty.</param>
    /// <param name="options">How to write it.</param>
    /// <exception cref="InvalidDataException">The run cannot be written as AEF (the message says why).</exception>
    /// <exception cref="ArgumentException">The output folder is not empty, or a gate limit is not a rate between 0 and 1.</exception>
    public static AefConversion Import(AssertAiRun run, string outputDirectory, AssertAiImportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        options ??= new AssertAiImportOptions();
        foreach (var limit in new[] { options.MaxHarmRate, options.MaxOverRefusalRate })
        {
            if (limit is { } l && !(l is >= 0 and <= 1))
            {
                throw new ArgumentException("A gate limit is a rate between 0 and 1.", nameof(options));
            }
        }

        var runId = run.SuiteName is null ? run.RunName : $"{run.SuiteName}.{run.RunName}";
        if (!AefConverter.IsId(runId))
        {
            throw new InvalidDataException($"The run id '{runId}' (suite.run) is not an AEF id (1-128 letters, digits, '.', '_', ':' or '-').");
        }

        var notes = new List<string>();
        var asserted = new List<string>();
        var capture = options.ContentCapture;
        var warnings = new List<string>();
        var testSet = TestSet(run, warnings);
        var transcripts = Transcripts(run, warnings);
        notes.AddRange(warnings);
        var taxonomyDigest = run.TaxonomyPath is { } taxonomyPath ? AefConverter.Sha256Of(File.ReadAllBytes(taxonomyPath)) : null;
        if (run.Taxonomy is null)
        {
            notes.Add("No taxonomy was found: harm and over-refusal cannot be told apart, so both child lines of a judged case are not_measured and no rate is measured.");
        }

        // [RUN-6]: what ASSERT sent the cases to.
        var (subject, deployment) = Target(run, notes);
        asserted.Add("subject.ref");
        asserted.Add("subject.kind");
        if (deployment is not null)
        {
            asserted.Add("deployment.ref");
        }

        // [RUN-8]: ASSERT has no suite version; the test set's digest stands for it.
        AefSuite? suite = null;
        if (run.TestSetPath is { } testSetPath)
        {
            var digest = AefConverter.Sha256Of(File.ReadAllBytes(testSetPath));
            suite = new AefSuite { Ref = AefConverter.TypedRef("suite", run.SuiteName ?? run.RunName), Version = digest, Digest = digest, Frozen = true };
            asserted.Add("suite.version");
            asserted.Add("suite.frozen"); // ASSERT's test set does not change once generated: the converter's reading
        }
        else
        {
            notes.Add("The run has no test set: run.json has no suite.");
        }

        // [RUN-7]
        var judgeOnly = IsJudgeOnly(run);
        var targetMode = options.TargetMode ?? (judgeOnly ? AefTargetMode.Replayed : AefTargetMode.Live);
        asserted.Add("execution.targetMode");
        asserted.Add("execution.stimulus"); // generated by ASSERT, or external for a judge-only run: the converter's reading
        asserted.Add("contentCapture");
        notes.Add($"ASSERT records no content capture policy: contentCapture is the converter's ({AefNames.Of(capture)}).");

        // [RUN-5]: the manifest's status and times (an open run has no end).
        var (status, abortReason) = Status(run, asserted, notes);
        var startedAt = run.StartedAt ?? run.EndedAt ?? run.FinishedAt;
        if (run.StartedAt is null)
        {
            asserted.Add("startedAt");
            notes.Add("manifest.json gives no started_at: startedAt is the converter's (the run's end).");
        }

        var endedAt = run.EndedAt ?? run.FinishedAt;
        if (run.EndedAt is null && status != AefRunStatus.Running)
        {
            asserted.Add("endedAt");
            notes.Add("manifest.json gives no ended_at: endedAt is when scores.jsonl was last written.");
        }

        if (endedAt < startedAt)
        {
            notes.Add("ASSERT's run ends before it starts: endedAt is its start.");
            endedAt = startedAt;
            if (!asserted.Contains("endedAt"))
            {
                asserted.Add("endedAt");
            }
        }

        var judges = Judges(run, taxonomyDigest, options.Calibration, startedAt, notes);
        if (judges.Count > 0)
        {
            asserted.Add("judges[].mode"); // ASSERT runs one judge: single is the converter's reading
        }
        var header = new AefRunHeader
        {
            RunId = runId,
            Producer = options.Producer ?? AefConverter.DefaultProducer,
            Subject = subject,
            Deployment = deployment,
            Suite = suite,
            Judges = judges.Count > 0 ? judges : null,
            StartedAt = startedAt,
            ContentCapture = capture,
            Execution = new AefExecution { TargetMode = targetMode, Stimulus = judgeOnly ? AefStimulus.External : AefStimulus.Generated },
            Imported = new AefImported { From = From, Asserted = asserted },
        };

        var existed = Directory.Exists(outputDirectory);
        AefRunWriter writer;
        try
        {
            writer = AefRunWriter.Create(outputDirectory, header);
        }
        catch (ArgumentException e) when (e.ParamName != "directory")
        {
            throw new InvalidDataException($"The run's header cannot be written as AEF: {e.Message}", e);
        }

        try
        {
            var dimensions = Dimensions(run);
            writer.SetMetrics(Metrics(dimensions));
            CopyFiles(writer, run, notes);

            var results = AssertAiResults.ToEvalResults(run).GroupBy(r => r.Key).ToDictionary(g => g.Key, g => g.First().Result);
            var rows = run.Rows.ToDictionary(r => r.Key);
            var lanes = new List<string>();
            var index = 0;
            foreach (var key in CaseOrder(run, testSet))
            {
                index++;
                var row = rows.GetValueOrDefault(key);
                var lane = row?.IsScenario ?? key.Type == "scenario" ? "scenario" : "prompt";
                if (!lanes.Contains(lane))
                {
                    lanes.Add(lane);
                }

                AddCase(writer, run, key, row, results[key], lane, index, capture, taxonomyDigest, dimensions,
                    testSet.GetValueOrDefault(key), transcripts.TryGetValue(key, out var transcript) ? transcript : null);
            }

            if (status == AefRunStatus.Running)
            {
                // [RUN-4]: an open run is left open, without a summary; it cannot be sealed until it is closed.
                notes.Add("ASSERT's manifest says the run is still running: the AEF run is left open (status running), with no summary.json, and is not sealed.");
                return new AefConversion(writer.Directory, runId, From, asserted, notes, AefRunVerifier.Verify(writer.Directory), null);
            }

            writer.SetSummary(Summary([.. lanes.OrderBy(l => l == "prompt" ? 0 : 1)], options));   // ASSERT reports prompt cases first
            if (File.Exists(Path.Combine(run.RunDirectory, "metrics.json")))
            {
                notes.Add("ASSERT's metrics.json is copied under ext/ and not read: its token usage is not mapped to summary.json usage.");
            }

            return AefConverter.Finish(writer, options, From, asserted, notes, status, endedAt, abortReason);
        }
        catch (Exception e)
        {
            AefConverter.Discard(outputDirectory, existed);
            if (e is ArgumentException)
            {
                throw new InvalidDataException($"The run cannot be written as AEF: {e.Message}", e);
            }

            throw;
        }
    }

    // ---------------------------------------------------------------- run.json

    // The target as subject (an endpoint URL or a model name) and, for an endpoint, the deployment it was reached at.
    private static (AefSubject Subject, AefDeployment? Deployment) Target(AssertAiRun run, List<string> notes)
    {
        var targets = run.Rows.Select(r => r.Target).Where(t => t.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        if (targets.Count > 1)
        {
            notes.Add($"The score rows name {targets.Count} targets: the subject is the first, {targets[0]}.");
        }

        if (targets.Count == 0)
        {
            notes.Add("No score row names a target: the subject is recorded as other:unknown.");
            return (new AefSubject { Ref = "other:unknown", Kind = AefSubjectKind.Other }, null);
        }

        var target = targets[0];
        if (!AefConverter.IsHttpUrl(target))
        {
            return (new AefSubject { Ref = AefConverter.TypedRef("model", target), Kind = AefSubjectKind.Model }, null);
        }

        var endpoint = AefConverter.Endpoint(target);
        if (endpoint is null)
        {
            notes.Add("The target URL holds user information, a query or a fragment, where credentials hide: deployment.endpoint is not written, and the subject's reference leaves them out (RUN-10).");
        }

        return (
            new AefSubject { Ref = AefConverter.TypedRef("endpoint", AefConverter.WithoutCredentials(target)), Kind = AefSubjectKind.Endpoint },
            new AefDeployment { Ref = AefConverter.TypedRef("deployment", run.SuiteName ?? run.RunName), Endpoint = endpoint });
    }

    // A judge-only run: AgentEval's judge kit wrote the conversations (its case map is beside results/), or the manifest
    // lists stages without an inference stage.
    private static bool IsJudgeOnly(AssertAiRun run)
    {
        var kit = Path.GetFullPath(Path.Combine(run.RunDirectory, "..", "..", "..", AssertAiCaseMap.FileName));
        return File.Exists(kit) || (run.Manifest?["stages"] is JsonObject stages && !stages.ContainsKey("inference"));
    }

    // [RUN-5]: completed → completed; failed → aborted with a reason; running → left open. A run with no manifest, or a
    // status ASSERT 0.3 does not write, is not known to have finished: aborted, which the converter supplies.
    private static (AefRunStatus Status, string? AbortReason) Status(AssertAiRun run, List<string> asserted, List<string> notes)
    {
        switch (run.ManifestStatus)
        {
            case "completed":
                return (AefRunStatus.Completed, null);
            case "running":
                return (AefRunStatus.Running, null);
            case "failed":
                return (AefRunStatus.Aborted, $"ASSERT's manifest says the run failed{Stages(run)}.");
            case null:
                asserted.Add("status");
                notes.Add("The run has no manifest.json, so it is not known to have finished: it is written as aborted.");
                return (AefRunStatus.Aborted, "ASSERT wrote no manifest.json: the run is not known to have finished.");
            default:
                asserted.Add("status");
                notes.Add($"ASSERT's manifest says '{run.ManifestStatus}', which assert-ai 0.3 does not write: the run is written as aborted.");
                return (AefRunStatus.Aborted, AefConverter.Text($"ASSERT's manifest says '{run.ManifestStatus}'{Stages(run)}.", 1024));
        }
    }

    private static string Stages(AssertAiRun run) =>
        run.Manifest?["stages"] is JsonObject stages && stages.Count > 0
            ? " (stages: " + string.Join(", ", stages.Select(s => $"{s.Key} {(s.Value is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : s.Value?.ToJsonString())}")) + ")"
            : "";

    // [RUN-9]: one entry per judge model of the score rows, with the taxonomy's digest, and AgentEval's calibration when
    // it was measured on this taxonomy, for this model, before the run started.
    private static List<AefJudge> Judges(AssertAiRun run, string? rubricDigest, AssertAiJudgeCalibration? calibration, DateTimeOffset startedAt, List<string> notes)
    {
        var judges = new List<AefJudge>();
        foreach (var model in run.Rows.Select(r => r.JudgeModel).Where(m => m.Length > 0).Distinct(StringComparer.Ordinal))
        {
            AefCalibration? measured = null;
            if (calibration is not null && string.Equals(calibration.JudgeModel, model, StringComparison.Ordinal))
            {
                if (!calibration.AppliesTo(run.Taxonomy))
                {
                    notes.Add($"The calibration of {model} was measured on another taxonomy than this run's: it is not written.");
                }
                else if (calibration.MeasuredAt is not { } at)
                {
                    notes.Add($"The calibration of {model} does not say when it was measured (written before measuredAt was recorded): it is not written.");
                }
                else if (at > startedAt)
                {
                    notes.Add($"The calibration of {model} was measured after the run started; AEF records only a calibration measured before the run (RUN-9): it is not written.");
                }
                else
                {
                    measured = new AefCalibration
                    {
                        LabelSet = AefConverter.TypedRef("labels", calibration.LabelSet.Length > 0 ? calibration.LabelSet : "unnamed"),
                        N = calibration.Decided,
                        Accuracy = calibration.Accuracy,
                        Kappa = calibration.Kappa,
                        DangerousErrors = calibration.DangerousErrors,
                        MeasuredAt = at,
                    };
                }
            }

            judges.Add(new AefJudge { Model = AefConverter.Text(model, 256)!, Mode = AefJudgeMode.Single, RubricDigest = rubricDigest, Calibration = measured });
        }

        if (calibration is not null && !run.Rows.Any(r => r.JudgeModel == calibration.JudgeModel))
        {
            notes.Add($"The calibration is of {calibration.JudgeModel}, which judged no case of this run: it is not written.");
        }

        return judges;
    }

    // ---------------------------------------------------------------- metrics.json

    // A judge dimension as a score: a boolean flag (1 when the bad thing happened), a numeric grade, or a string grade at
    // its position on the declared scale.
    private sealed record Dimension(string Name, bool Flag, IReadOnlyList<string>? Grades, double? Min, double? Max);

    private static Dictionary<string, Dimension> Dimensions(AssertAiRun run)
    {
        var dimensions = new Dictionary<string, Dimension>(StringComparer.Ordinal);
        foreach (var row in run.Rows.Where(r => r.Verdict is not null))
        {
            foreach (var (name, value) in row.Verdict!.Dimensions)
            {
                if (dimensions.ContainsKey(name) || value is not JsonValue v)
                {
                    continue;
                }

                switch (v.GetValueKind())
                {
                    case JsonValueKind.True or JsonValueKind.False:
                        dimensions[name] = new Dimension(name, true, null, 0, 1);
                        break;
                    case JsonValueKind.Number:
                        var grades = Scale(row, name).Select(g => g.Number).OfType<double>().ToList();
                        dimensions[name] = new Dimension(name, false, null, grades.Count > 0 ? grades.Min() : null, grades.Count > 0 ? grades.Max() : null);
                        break;
                    case JsonValueKind.String:
                        var labels = Scale(row, name).Select(g => g.Label).OfType<string>().ToList();
                        dimensions[name] = new Dimension(name, false, labels, 0, Math.Max(0, labels.Count - 1));
                        break;
                }
            }
        }

        return dimensions;
    }

    // The declared grades of a dimension (score row dimension_scales[name].values[].value), in order.
    private static IEnumerable<(double? Number, string? Label)> Scale(AssertAiScoreRow row, string name)
    {
        if (row.Raw["dimension_scales"]?[name]?["values"] is not JsonArray values)
        {
            yield break;
        }

        foreach (var entry in values)
        {
            if (entry?["value"] is JsonValue grade)
            {
                yield return grade.GetValueKind() switch
                {
                    JsonValueKind.Number => (grade.GetValue<double>(), null),
                    JsonValueKind.String => (null, grade.GetValue<string>()),
                    _ => (null, null),
                };
            }
        }
    }

    private static List<AefMetric> Metrics(Dictionary<string, Dimension> dimensions)
    {
        var metrics = new List<AefMetric>
        {
            new()
            {
                Id = HarmMetric, Kind = AefMetricKind.Score, Direction = AefMetricDirection.LowerBetter, Scale = AefScale.Between(0, 1),
                Description = "1 when ASSERT's judge found a non-permissible category violated (harm), 0 when one was relevant and none violated.",
            },
            new()
            {
                Id = OverRefusalMetric, Kind = AefMetricKind.Score, Direction = AefMetricDirection.LowerBetter, Scale = AefScale.Between(0, 1),
                Description = "1 when ASSERT's judge found a permissible category violated (the target failed to help), 0 when one was relevant and none violated.",
            },
        };
        foreach (var dimension in dimensions.Values.Where(d => d.Name is not (HarmMetric or OverRefusalMetric) && d.Name.Length <= 256))
        {
            metrics.Add(dimension.Flag
                ? new AefMetric
                {
                    Id = dimension.Name, Kind = AefMetricKind.Score, Direction = AefMetricDirection.LowerBetter, Scale = AefScale.Between(0, 1),
                    Description = "An ASSERT judge dimension: 1 when flagged, 0 when clear.",
                }
                : new AefMetric
                {
                    Id = dimension.Name, Kind = AefMetricKind.Score, Direction = AefMetricDirection.None,
                    Scale = dimension.Min is { } min && dimension.Max is { } max && min <= max ? AefScale.Between(min, max) : AefScale.Unbounded,
                    Description = dimension.Grades is null
                        ? "An ASSERT judge dimension: a grade on the dimension's ordinal scale; which end is better is the scale's author's."
                        : "An ASSERT judge dimension: a grade's 0-based position on the declared scale, the grade itself as the label.",
                });
        }

        return metrics;
    }

    // ---------------------------------------------------------------- files

    // ASSERT's files nothing in AEF reads, under ext/assert-ai/ (ENC-19), sealed with the run. A config.yaml that holds a
    // literal credential is not copied: a sealed run cannot take a secret back (RUN-10).
    private static void CopyFiles(AefRunWriter writer, AssertAiRun run, List<string> notes)
    {
        var suiteDirectory = Path.GetDirectoryName(run.RunDirectory);
        var files = new List<(string Name, string? Path)>
        {
            ("taxonomy.json", run.TaxonomyPath),
            ("systematization.json", suiteDirectory is null ? null : Path.Combine(suiteDirectory, "systematization.json")),
            ("suite.json", suiteDirectory is null ? null : Path.Combine(suiteDirectory, "suite.json")),
            ("manifest.json", Path.Combine(run.RunDirectory, "manifest.json")),
            ("config.yaml", Path.Combine(run.RunDirectory, "config.yaml")),
            ("metrics.json", Path.Combine(run.RunDirectory, "metrics.json")),
        };
        foreach (var (name, path) in files)
        {
            if (path is null || !File.Exists(path))
            {
                continue;
            }

            var bytes = File.ReadAllBytes(path);
            if (name == "config.yaml" && HoldsCredential(Encoding.UTF8.GetString(bytes)))
            {
                notes.Add("config.yaml gives a key, token, password or secret a literal value: it is not copied (RUN-10).");
                continue;
            }

            writer.PutExtFile($"{ExtFolder}/{name}", bytes);
        }
    }

    // A YAML line `key: value` whose key names a credential (api_key, client_secret, …; not max_tokens) and whose value is
    // a literal: not empty, null, or a reference to the environment (${VAR}, os.environ/VAR, env:VAR), as LiteLLM's
    // configuration takes keys.
    private static bool HoldsCredential(string yaml)
    {
        foreach (var raw in yaml.Split('\n'))
        {
            var line = raw.Trim();
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (line.StartsWith('#') || colon <= 0)
            {
                continue;
            }

            var key = line[..colon].Trim().Trim('"', '\'').Replace('-', '_').ToLowerInvariant();
            if (!s_credentialKeys.Contains(key) && !s_credentialSuffixes.Any(s => key.EndsWith(s, StringComparison.Ordinal)))
            {
                continue;
            }

            var value = line[(colon + 1)..];
            var comment = value.IndexOf(" #", StringComparison.Ordinal);
            value = (comment >= 0 ? value[..comment] : value).Trim().Trim('"', '\'').Trim();
            if (value.Length > 0 && value is not ("null" or "~" or "|" or ">")
                && !value.StartsWith('$') && !value.StartsWith("os.environ", StringComparison.Ordinal) && !value.StartsWith("env:", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    // test_set.jsonl, by case: its row, in file order.
    private static Dictionary<AssertAiCaseKey, JsonObject> TestSet(AssertAiRun run, List<string> warnings)
    {
        var rows = new Dictionary<AssertAiCaseKey, JsonObject>();
        if (run.TestSetPath is null)
        {
            return rows;
        }

        foreach (var (_, row) in AssertAiJson.ReadJsonLines(run.TestSetPath, warnings))
        {
            var key = Key(row);
            if (key.TestCaseId.Length > 0)
            {
                rows.TryAdd(key, row);
            }
        }

        return rows;
    }

    // inference_set.jsonl, by case: the row's exact bytes (without its line ending) and the row.
    private static Dictionary<AssertAiCaseKey, (byte[] Bytes, JsonObject Row)> Transcripts(AssertAiRun run, List<string> warnings)
    {
        var rows = new Dictionary<AssertAiCaseKey, (byte[], JsonObject)>();
        if (run.InferenceSetPath is null)
        {
            return rows;
        }

        var bytes = File.ReadAllBytes(run.InferenceSetPath).AsSpan();
        if (bytes.StartsWith("﻿"u8))
        {
            bytes = bytes[3..];
        }

        var number = 0;
        while (!bytes.IsEmpty)
        {
            number++;
            var end = bytes.IndexOf((byte)'\n');
            var line = end < 0 ? bytes : bytes[..end];
            bytes = end < 0 ? [] : bytes[(end + 1)..];
            if (line.EndsWith("\r"u8))
            {
                line = line[..^1];
            }

            var text = Encoding.UTF8.GetString(line);
            if (text.Trim().Length == 0)
            {
                continue;
            }

            try
            {
                if (AssertAiJson.ParsePython(text, run.InferenceSetPath, number) is JsonObject row && Key(row) is { TestCaseId.Length: > 0 } key)
                {
                    rows.TryAdd(key, (line.ToArray(), row));
                }
            }
            catch (InvalidDataException e)
            {
                warnings.Add($"{e.Message} The line was skipped, as ASSERT skips it.");
            }
        }

        return rows;
    }

    private static AssertAiCaseKey Key(JsonObject row) =>
        new(AssertAiJson.Str(row["type"]) ?? string.Empty, AssertAiJson.Str(row["test_case_id"]) ?? string.Empty);

    // The cases in test-set order, then the score rows' cases not in it, then the cases with no row.
    private static IEnumerable<AssertAiCaseKey> CaseOrder(AssertAiRun run, Dictionary<AssertAiCaseKey, JsonObject> testSet)
    {
        var known = run.Rows.Select(r => r.Key).Concat(run.Missing.Select(m => m.Key)).ToHashSet();
        return testSet.Keys.Where(known.Contains)
            .Concat(run.Rows.Select(r => r.Key))
            .Concat(run.Missing.Select(m => m.Key))
            .Distinct();
    }

    // ---------------------------------------------------------------- results.ndjson

    private static void AddCase(
        AefRunWriter writer, AssertAiRun run, AssertAiCaseKey key, AssertAiScoreRow? row, EvalResult result, string lane, int index,
        AefContentCapture capture, string? rubricDigest, Dictionary<string, Dimension> dimensions,
        JsonObject? testRow, (byte[] Bytes, JsonObject Row)? transcript)
    {
        var caseId = key.ToString();
        if (!AefConverter.IsResultText(caseId, 256))
        {
            throw new InvalidDataException($"The case {caseId} is not an AEF case id (1-256 characters, no control character).");
        }

        var (state, severity) = RootState(result);
        var reason = RootReason(result, row, capture);
        var evidence = new List<string>();
        AefBlob? reasoning = null;
        if (capture == AefContentCapture.On)
        {
            if (AssertAiJson.Str(testRow?["seed"]?["description"]) is { Length: > 0 } seed)
            {
                evidence.Add(Evidence(writer, $"E-{index}-input", AefEvidenceKind.Input, Encoding.UTF8.GetBytes(seed), "The case's seed (test_set.jsonl seed.description)."));
            }

            if (transcript is { } t)
            {
                evidence.Add(Evidence(writer, $"E-{index}-transcript", AefEvidenceKind.Transcript, t.Bytes, "ASSERT's conversation for the case (its inference_set.jsonl row)."));
                var call = 0;
                foreach (var edit in (t.Row["events"] as JsonArray ?? []).Select(e => e?["edit"]).OfType<JsonObject>().Where(e => AssertAiJson.Str(e["type"]) == "tool_call"))
                {
                    call++;
                    evidence.Add(Evidence(writer, $"E-{index}-tool-{call}", AefEvidenceKind.ToolCall, Encoding.UTF8.GetBytes(edit.ToJsonString(s_json)),
                        AefConverter.Text($"A tool call of the conversation: {AssertAiJson.Str(edit["tool_name"]) ?? "tool"}.", 1024)));
                }
            }

            if (row?.Verdict is { } verdict)
            {
                var judged = new JsonObject();
                foreach (var name in new[] { "node_judgments", "dimension_justifications", "citations", "narrative" })
                {
                    if (verdict.Raw[name] is { } value)
                    {
                        judged[name] = value.DeepClone();
                    }
                }

                var bytes = Encoding.UTF8.GetBytes(judged.ToJsonString(s_json));
                reasoning = writer.PutBlob(bytes);
                evidence.Add(Evidence(writer, $"E-{index}-judge", AefEvidenceKind.JudgeReasoning, bytes,
                    "ASSERT's judge reasoning: its node judgments, dimension justifications, citations and narrative."));
            }
        }

        // The children: ASSERT's two headline kinds, with its own rules for which cases count (AssertAiHeadline.Split).
        var (harm, overRefusal) = Children(row, run.Taxonomy, state, reason, caseId, lane);
        var children = new[] { harm, overRefusal };
        var measured = children.Count(c => c.State is AefState.Passed or AefState.Failed);
        var decisive = state switch
        {
            AefState.Failed => children.Where(c => c.State == AefState.Failed),
            AefState.Passed => children.Where(c => c.State == AefState.Passed),
            AefState.Error => children.Where(c => c.State == AefState.Error),
            _ => [],
        };
        var decisiveIds = decisive.Select(c => writer.ResultIdOf(caseId, c.Path)).ToList();
        var root = new AefResult
        {
            CaseId = caseId,
            Path = RootPath,
            Evaluator = new AefEvaluator(AssertAiResults.Key, AssertAiResults.FormatVersion),
            State = state,
            Reason = reason,
            Scores = state is AefState.Passed or AefState.Failed && row?.Verdict is { } v ? DimensionScores(row, v, dimensions) : null,
            Annotator = row?.Status == AssertAiJudgeStatus.Ok
                ? new AefAnnotator { Kind = AefAnnotatorKind.Llm, Model = AefConverter.Text(row.JudgeModel, 256), RubricDigest = rubricDigest }
                : null,
            Reasoning = reasoning,
            Aggregation = new AefAggregation
            {
                Strategy = AefAggregationStrategy.Min,
                RulePath = state switch
                {
                    AefState.Error => AefRulePath.RequiredError,
                    AefState.Passed or AefState.Failed => AefRulePath.Severity,
                    _ => AefRulePath.NothingMeasured,
                },
                Measured = measured,
                Total = children.Length,
                Unmeasured = measured < children.Length ? Unmeasured(children) : null,
                Decisive = decisiveIds.Count > 0 ? decisiveIds : null,
            },
            Evidence = evidence.Count > 0 ? evidence : null,
            Ext = Ext(row, testRow, transcript?.Row),
            Lane = lane,
            Severity = severity,
        };

        AefResultHandle handle;
        try
        {
            handle = writer.AddResult(root);
        }
        catch (ArgumentException e)
        {
            throw new InvalidDataException($"The case {caseId} cannot be written as AEF: {e.Message}", e);
        }

        handle.AddChild(harm);
        handle.AddChild(overRefusal);
    }

    private static string Evidence(AefRunWriter writer, string id, AefEvidenceKind kind, byte[] bytes, string? description)
    {
        writer.AddEvidence(new AefEvidence { EvidenceId = id, Kind = kind, Link = AefEvidenceLink.ToBlob(writer.PutBlob(bytes)), Description = description });
        return id;
    }

    // The root's state from AgentEval's reading of the row (AssertAiResults): pass, fail (high for harm, medium for a
    // failure to help or a flagged dimension), inapplicable, error (a failed or invalid judge), skipped.
    private static (AefState State, AefSeverity? Severity) RootState(EvalResult result)
    {
        var state = result.Score.Label switch
        {
            "pass" => AefState.Passed,
            "fail" => AefState.Failed,
            "inapplicable" => AefState.NotApplicable,
            "error" => AefState.Error,
            _ => AefState.Skipped,
        };
        return (state, state == AefState.Failed && AefNames.TryParse<AefSeverity>(result.Score.Severity, out var s) ? s : null);
    }

    // The reason: AgentEval's summary of the verdict, which ends with the judge's justification; a run that keeps no
    // content keeps no judge reasoning ([RUN-11]), so the justification is left out then.
    private static string? RootReason(EvalResult result, AssertAiScoreRow? row, AefContentCapture capture)
    {
        var summary = result.Details.Summary;
        if (capture == AefContentCapture.Off && summary is not null && row?.Verdict?.Justification is { Length: > 0 } justification
            && summary.EndsWith(" " + justification, StringComparison.Ordinal))
        {
            summary = summary[..^(justification.Length + 1)];
        }

        return AefConverter.Text(summary, 4096) ?? (result.Score.Label is "pass" or "fail" ? null : $"ASSERT: {result.Score.Label}.");
    }

    private static (AefResult Harm, AefResult OverRefusal) Children(AssertAiScoreRow? row, AssertAiTaxonomy? taxonomy, AefState rootState, string? reason, string caseId, string lane)
    {
        AefResult Line(string path, AefState state, string? why, double? score = null, AefSeverity? severity = null) => new()
        {
            CaseId = caseId,
            Path = path,
            Evaluator = new AefEvaluator(AssertAiResults.Key, AssertAiResults.FormatVersion),
            State = state,
            Reason = why,
            Scores = score is { } s ? [new AefScore { Metric = path == HarmPath ? HarmMetric : OverRefusalMetric, Value = s }] : null,
            Component = new AefComponent(1, true),
            Lane = lane,
            Severity = severity,
        };

        if (rootState is AefState.Error or AefState.Skipped)
        {
            // A failed judge carries no verdict, and an unjudged case none either: nothing reads them as "no violation".
            var why = reason ?? (rootState == AefState.Error ? "ASSERT's judge did not return a usable verdict." : "ASSERT did not judge this case.");
            return (Line(HarmPath, rootState, why), Line(OverRefusalPath, rootState, why));
        }

        if (row is null || taxonomy is null || taxonomy.Categories.Count == 0)
        {
            const string none = "No taxonomy with categories: ASSERT's two kinds cannot be told apart, so neither is measured.";
            return (Line(HarmPath, AefState.NotMeasured, none), Line(OverRefusalPath, AefState.NotMeasured, none));
        }

        var (permissible, notPermissible) = AssertAiHeadline.Split([row], taxonomy);
        AefResult Kind(AssertAiRate rate, string path, AefSeverity severity, string which) =>
            rate.Count == 0
                ? Line(path, AefState.NotApplicable, $"No {which} category was relevant to this case.")
                : rate.Flagged > 0
                    ? Line(path, AefState.Failed, null, 1, severity)
                    : Line(path, AefState.Passed, null, 0);

        return (Kind(notPermissible!, HarmPath, AefSeverity.High, "non-permissible"), Kind(permissible!, OverRefusalPath, AefSeverity.Medium, "permissible"));
    }

    private static AefUnmeasured Unmeasured(IEnumerable<AefResult> children)
    {
        long? Count(AefState state) => children.Count(c => c.State == state) is var n and > 0 ? n : null;
        return new AefUnmeasured
        {
            NotMeasured = Count(AefState.NotMeasured),
            NotApplicable = Count(AefState.NotApplicable),
            Skipped = Count(AefState.Skipped),
            Error = Count(AefState.Error),
        };
    }

    // The judge's dimensions as scores on the root: a flag 1 or 0, a numeric grade as itself, a string grade as its label
    // and its position on the declared scale. A dimension that is null (not applicable) has no score.
    private static List<AefScore>? DimensionScores(AssertAiScoreRow row, AssertAiVerdict verdict, Dictionary<string, Dimension> dimensions)
    {
        var scores = new List<AefScore>();
        foreach (var (name, value) in verdict.Dimensions)
        {
            if (value is not JsonValue v || !dimensions.TryGetValue(name, out var dimension) || name is HarmMetric or OverRefusalMetric || name.Length > 256)
            {
                continue;
            }

            switch (v.GetValueKind())
            {
                case JsonValueKind.True or JsonValueKind.False when dimension.Flag:
                    scores.Add(new AefScore { Metric = name, Value = v.GetValueKind() == JsonValueKind.True ? 1 : 0 });
                    break;
                case JsonValueKind.Number when !dimension.Flag && dimension.Grades is null && double.IsFinite(v.GetValue<double>()):
                    scores.Add(new AefScore { Metric = name, Value = v.GetValue<double>() });
                    break;
                case JsonValueKind.String when dimension.Grades is { } grades:
                    var grade = v.GetValue<string>();
                    var position = Scale(row, name).Select(g => g.Label).ToList().IndexOf(grade);
                    if (position < 0)
                    {
                        position = grades.ToList().IndexOf(grade);
                    }

                    if (position >= 0)
                    {
                        scores.Add(new AefScore { Metric = name, Value = position, Label = AefConverter.Text(grade, 64) });
                    }

                    break;
            }
        }

        return scores.Count > 0 ? scores : null;
    }

    // The case's ASSERT fields AEF has no place for: its stratification levels, the transcript's stop reason and how
    // many model calls it made (the calls themselves are in the transcript blob).
    private static JsonObject? Ext(AssertAiScoreRow? row, JsonObject? testRow, JsonObject? transcript)
    {
        var ext = new JsonObject();
        if ((testRow?["dimensions"] ?? row?.Raw["dimensions"]) is JsonObject levels)
        {
            ext["dimensions"] = levels.DeepClone();
        }

        if (transcript?["stop_reason"] is JsonValue stop)
        {
            ext["stopReason"] = stop.DeepClone();
        }

        if (transcript?["llm_calls"] is JsonArray calls)
        {
            ext["llmCalls"] = calls.Count;
        }

        if (row is not null)
        {
            ext["judgeStatus"] = row.RawJudgeStatus;
        }

        return ext.Count > 0 ? new JsonObject { [ExtName] = ext } : null;
    }

    // ---------------------------------------------------------------- summary.json

    // Per lane, ASSERT's two rates, recomputed from the child lines (SUM-3 to SUM-5), with AgentEval's 95% Wilson
    // interval; a gate limit gives the rule and the verdict, none gives scored.
    private static AefSummary Summary(List<string> lanes, AssertAiImportOptions options) => new()
    {
        Lanes = [.. lanes.Select(lane => new AefSummaryLane(lane,
        [
            Entry(HarmMetric, HarmPath, options.MaxHarmRate),
            Entry(OverRefusalMetric, OverRefusalPath, options.MaxOverRefusalRate),
        ]))],
    };

    private static AefSummaryEntry Entry(string metric, string path, double? limit) => new()
    {
        Metric = metric,
        Path = path,
        Rule = limit is { } l ? $"{metric} <= {l.ToString("R", CultureInfo.InvariantCulture)}" : null,
        Decide = figures =>
        {
            var wilson = WilsonInterval.Compute((int)Math.Round(figures.Sum), (int)figures.Measured);
            var verdict = limit is not { } max ? AefSummaryVerdict.Scored
                : figures.Value <= max ? AefSummaryVerdict.Passed
                : AefSummaryVerdict.Failed;
            return new AefSummaryDecision(verdict, Ci: new AefInterval(wilson.Lower, wilson.Upper, 0.95, "wilson"));
        },
    };
}
