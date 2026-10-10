// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AgentEval.Results.Integrity;
using AgentEval.Results.Json;
using AgentEval.Results.Runs;
using AgentEval.Results.Signatures;
using AgentEval.Results.Writing;

namespace AgentEval.Results.Conformance.Ops;

/// <summary>
/// <c>write-samples OUTDIR</c> (not an operation of spec 09 §9.3: the write side has none yet, findings D1): writes runs
/// with AgentEval.Results' writer, sealer and overlay writer into the empty folder OUTDIR, with a
/// trust policy for the keys that signed them (fresh P-256 test keys), and prints a manifest: for each check, the
/// driver arguments of a verifier operation (<c>run</c>, <c>seal</c>, <c>chain</c>, <c>view</c>), what the writer
/// meant (<c>want</c>: members the output must have), and the .NET verifier's output. writer-crosscheck.sh runs the
/// reference verifier (tools/aef_verify.py) on the same arguments and compares.
/// </summary>
internal static class WriterOps
{
    /// <summary>The operation.</summary>
    public static int WriteSamples(string[] args, TextWriter stdout)
    {
        DriverIO.Arguments(args, 1, 1, "write-samples OUTDIR");
        if (Directory.Exists(args[0]) && Directory.EnumerateFileSystemEntries(args[0]).Any())
        {
            throw new UsageException($"{args[0]}: not an empty folder");
        }

        return DriverIO.Print(stdout, WriterSamples.Write(args[0]));
    }
}

/// <summary>
/// Sample runs written by the AEF writer (<see cref="AefRunWriter"/>, <see cref="AefSealer"/>,
/// <see cref="AefOverlayWriter"/>), each with the checks a verifier must agree with: <c>rich</c> (every kind of line
/// and file, sealed and signed by the producer), <c>content-off</c> (<c>contentCapture: off</c>, sealed), <c>aborted</c>
/// (sealed and signed by a host on ingest), <c>unsealed</c> (closed, not sealed) and <c>overlays</c> (sealed and
/// signed, three overlay batches, the last signed by an identity the policy lets redact, and the redacted blob
/// deleted: intact, 1 withheld).
/// </summary>
internal static class WriterSamples
{
    public const string Alice = "git:alice@example.com";
    public const string Bob = "spiffe://example.com/reviewer/bob";

    /// <summary>The time the views are computed at.</summary>
    public const string ViewAt = "2026-10-03T00:00:00Z";

    public static readonly DateTimeOffset Start = new(2026, 10, 1, 10, 0, 0, TimeSpan.Zero);

    public static readonly byte[] Reasoning = Encoding.UTF8.GetBytes("The answer misses the refund policy: ünïcødé ✓, and a CRLF\r\n");

    private const string TraceId = "4bf92f3577b34da6a3ce929d0e0e4736"; // DevSkim: ignore DS173237 — W3C Trace Context example id
    private const string AgentSpan = "00f067aa0ba902b7";
    private const string JudgeSpan = "a3ce929d0e0e4736";

    // U+2028 inside a JSON string is text, not a line break ([ENC-6]).
    private static readonly string LineSeparator = char.ConvertFromUtf32(0x2028);

    /// <summary>Writes the samples into <paramref name="root"/> and returns the manifest.</summary>
    public static JsonObject Write(string root)
    {
        Directory.CreateDirectory(root);
        using var alice = EcdsaP256Signer.Generate();
        using var bob = EcdsaP256Signer.Generate();
        var policy = new TrustPolicy([new TrustedKey(Alice, alice.PublicKey), new TrustedKey(Bob, bob.PublicKey, [TrustPolicy.Redact])]);
        var policyPath = Path.Combine(root, "policy.json");
        File.WriteAllBytes(policyPath, AefJsonWriter.Document(new JsonObject
        {
            ["keys"] = new JsonArray(
                new JsonObject { ["identity"] = Alice, ["publicKey"] = alice.PublicKey.ToPem() },
                new JsonObject { ["identity"] = Bob, ["publicKey"] = bob.PublicKey.ToPem(), ["may"] = new JsonArray("redact") }),
        }));

        var checks = new JsonArray();
        void Check(JsonObject want, params string[] arguments)
        {
            var output = new StringWriter();
            var errors = new StringWriter();
            if (Program.Dispatch(arguments, output, errors) != 0)
            {
                throw new InvalidOperationException($"{string.Join(' ', arguments)}: {errors}");
            }

            checks.Add(new JsonObject
            {
                ["args"] = new JsonArray([.. arguments.Select(a => (JsonNode?)a)]),
                ["want"] = want.DeepClone(),
                ["dotnet"] = JsonNode.Parse(output.ToString()),
            });
        }

        // §4.5: intact, with no problem but a withheld blob ("intact, 1 withheld"), signed by these identities.
        JsonObject Intact(string? withheldBlob, params string[] signedBy)
        {
            var want = new JsonObject { ["outcome"] = "intact", ["problems"] = Withheld(withheldBlob), ["signedBy"] = new JsonArray([.. signedBy.Select(s => (JsonNode?)s)]) };
            if (withheldBlob is not null)
            {
                want["withheld"] = 1;
            }

            return want;
        }

        static JsonArray Withheld(string? blob) => blob is null ? [] : [new JsonArray(AefRunFolder.BlobPath(blob), "withheld")];

        var none = new JsonObject { ["problems"] = new JsonArray() };

        // rich: sealed and signed by the producer (alice).
        var rich = Path.Combine(root, "rich");
        WriteRich(rich, "run-rich-1", AefContentCapture.On);
        AefSealer.Seal(rich, new AefSealOptions { Signer = alice, TimeProvider = new FixedTime(Start.AddMinutes(6)) });
        Check(Intact(null, Alice), "run", rich, "--policy", policyPath);
        Check(none, "seal", rich, "--policy", policyPath);
        Check(none, "chain", rich);

        // content-off: sealed by the producer, unsigned.
        var off = Path.Combine(root, "content-off");
        WriteRich(off, "run-off-1", AefContentCapture.Off);
        AefSealer.Seal(off, new AefSealOptions { TimeProvider = new FixedTime(Start.AddMinutes(6)) });
        Check(Intact(null), "run", off, "--policy", policyPath);
        Check(none, "seal", off);

        // aborted: sealed on ingest by a host (bob), signed.
        var aborted = Path.Combine(root, "aborted");
        WriteAborted(aborted);
        AefSealer.Seal(aborted, new AefSealOptions { SealedBy = AefSealedBy.Ingest, Signer = bob, TimeProvider = new FixedTime(Start.AddHours(1)) });
        Check(Intact(null, Bob), "run", aborted, "--policy", policyPath);
        Check(none, "seal", aborted);

        // unsealed: closed, never sealed.
        var unsealed = Path.Combine(root, "unsealed");
        WriteRich(unsealed, "run-unsealed-1", AefContentCapture.On);
        Check(new JsonObject { ["outcome"] = "unsealed", ["problems"] = new JsonArray() }, "run", unsealed);
        Check(none, "seal", unsealed);

        // overlays: sealed and signed; three batches; the redacted blob deleted.
        var overlays = Path.Combine(root, "overlays");
        WriteRich(overlays, "run-overlays-1", AefContentCapture.On);
        AefSealer.Seal(overlays, new AefSealOptions { Signer = alice, TimeProvider = new FixedTime(Start.AddMinutes(6)) });
        var (k1, k2, k3) = (Id("run-overlays-1", "k1", "q"), Id("run-overlays-1", "k2", "q"), Id("run-overlays-1", "k3", "q"));
        var overlay = AefOverlayWriter.Open(overlays);
        overlay.Append(Event("ov_approve1", AefOverlayKind.Approve, AefOverlayTarget.TheRun, Alice, 1));
        overlay.Append(Event("ov_override1", AefOverlayKind.Override, new AefOverlayTarget { Result = k2 }, Alice, 1, AefState.Failed, "the label was wrong"));
        overlay.Append(Event("ov_waive1", AefOverlayKind.Waive, new AefOverlayTarget { Result = k1 }, Alice, 1, reason: "known issue, fix in 2026.11", expires: Start.AddDays(30)));
        overlay.SealBatch();
        overlay.Append(Event("ov_note1", AefOverlayKind.Annotate, new AefOverlayTarget { Result = k1 }, Alice, 1, reason: "see the trace"));
        overlay.Append(Event("ov_adjudicate1", AefOverlayKind.Adjudicate, new AefOverlayTarget { Result = k3 }, Alice, 1, AefState.Passed, "the panel split; a person decided"));
        overlay.SealBatch(alice);
        var blob = Convert.ToHexString(SHA256.HashData(Reasoning)).ToLowerInvariant();
        overlay.Append(Event("ov_redact1", AefOverlayKind.Redact, new AefOverlayTarget { Blob = blob }, Bob, 2, reason: "personal data in the reasoning"));
        overlay.SealBatch(bob);
        AefOverlayWriter.DeleteRedactedBlob(overlays, blob, policy);
        Check(Intact(blob, Alice), "run", overlays, "--policy", policyPath);
        Check(new JsonObject { ["problems"] = Withheld(blob) }, "seal", overlays, "--policy", policyPath);
        Check(none, "chain", overlays);

        // The effective view (§4.3): the override and the adjudication in results.ndjson order, the run approved, the
        // waiver active at ViewAt, the blob withheld.
        Check(
            new JsonObject
            {
                ["results"] = new JsonArray(
                    new JsonObject { ["resultId"] = k2, ["sealedState"] = "passed", ["effectiveState"] = "failed", ["event"] = "ov_override1" },
                    new JsonObject { ["resultId"] = k3, ["sealedState"] = "passed", ["effectiveState"] = "passed", ["event"] = "ov_adjudicate1" }),
                ["reviews"] = new JsonArray(new JsonObject { ["target"] = "run", ["status"] = "approve", ["event"] = "ov_approve1" }),
                ["waivers"] = new JsonArray(new JsonObject
                {
                    ["target"] = new JsonObject { ["result"] = k1 },
                    ["expires"] = "2026-10-31T10:00:00Z",
                    ["active"] = true,
                    ["event"] = "ov_waive1",
                }),
                ["withheld"] = new JsonArray(blob),
                ["unsealedEvents"] = 0,
            },
            "view", overlays, "--at", ViewAt, "--policy", policyPath);

        return new JsonObject { ["policy"] = policyPath, ["checks"] = checks };
    }

    /// <summary>The result id of a line of a run (<see cref="AefRunWriter.ResultIdOf"/>, without a writer).</summary>
    public static string Id(string runId, string caseId, string path, int? trial = null) => AefResultId.Compute(runId, caseId, path, trial);

    /// <summary>A header of a live run of the triage agent, with every optional member run.json has.</summary>
    public static AefRunHeader Header(string runId, AefContentCapture capture) => new()
    {
        RunId = runId,
        Producer = new AefProducer { Name = "agenteval", Version = "1.0.0-test", Runtime = new AefRuntime { Name = ".NET", Version = "10.0" } },
        Subject = new AefSubject
        {
            Ref = "agent:support/triage",
            Kind = AefSubjectKind.Agent,
            Version = "2026.10.1",
            Environment = "staging",
            ExternalIds = new Dictionary<string, string?> { ["github"] = "example/triage", ["jira"] = null },
            Telemetry = new AefSubjectTelemetry { AgentId = "triage", ServiceName = "support-api" },
        },
        Deployment = new AefDeployment { Ref = "deployment:staging-eu", Environment = "staging", Endpoint = "https://agents.example.com/triage" },
        Suite = new AefSuite
        {
            Ref = "suite:support/triage",
            Version = "3",
            Digest = "sha256:" + new string('a', 64),
            Frozen = true,
            ExecutionPolicy = new AefExecutionPolicy { TrialsPerCase = 2, RequirePasses = 1, Aggregation = AefTrialAggregation.AnyPass },
        },
        Judges =
        [
            new AefJudge
            {
                Model = "judge-model-1",
                Provider = "example",
                Mode = AefJudgeMode.Single,
                RubricDigest = "sha256:" + new string('b', 64),
                Calibration = new AefCalibration { LabelSet = "labels:triage-v1", N = 200, Accuracy = 0.93, Kappa = 0.81, DangerousErrors = 2, MeasuredAt = Start.AddDays(-3) },
            },
        ],
        Config = new AefRunConfig
        {
            Thresholds = new Dictionary<string, AefThreshold> { ["m"] = new(AefThresholdOp.AtLeast, 0.8) },
            Settings = new JsonObject { ["temperature"] = 0, ["seed"] = 7 },
        },
        StartedAt = Start,
        Otel = new AefOtel { SemconvVersion = "1.37.0", Dialects = ["gen_ai"], SchemaUrls = ["https://opentelemetry.io/schemas/1.37.0"] },
        ContentCapture = capture,
        CostPolicy = new AefCostPolicy { MaxUsd = 5, PriceTable = "example-2026-10" },
        Ext = new JsonObject { ["agenteval.note"] = "ünïcødé ✓ " + LineSeparator + " kept as text" },
        Execution = new AefExecution { TargetMode = AefTargetMode.Live, Stimulus = AefStimulus.Suite },
    };

    /// <summary>
    /// A completed run with every kind of line and file: a composite with three children (one not applicable), a
    /// plain leaf, a case in two trials and its rollup, an attack, a scored line, a skipped one; evidence by blob, span
    /// and URI; traces and logs; a gate decision; four metrics; two lanes with a mean, a rate, a median and a
    /// producer's aggregate; an ext file. Under <c>contentCapture: off</c>, no reasoning, prompt hash, content evidence
    /// or log body.
    /// </summary>
    public static AefRunVerification WriteRich(string dir, string runId, AefContentCapture capture)
    {
        var on = capture == AefContentCapture.On;
        var writer = AefRunWriter.Create(dir, Header(runId, capture));
        writer.SetMetrics(
        [
            new AefMetric { Id = "m", Kind = AefMetricKind.Score, Direction = AefMetricDirection.HigherBetter, Scale = AefScale.Between(0, 1), Description = "answer quality" },
            new AefMetric { Id = "pass", Kind = AefMetricKind.Rate, Direction = AefMetricDirection.HigherBetter, Scale = AefScale.Between(0, 1) },
            new AefMetric { Id = "tokens", Kind = AefMetricKind.Count, Direction = AefMetricDirection.LowerBetter, Scale = AefScale.Unbounded, Unit = "tokens" },
            new AefMetric { Id = "latency", Kind = AefMetricKind.Duration, Direction = AefMetricDirection.None, Scale = AefScale.Unbounded, Unit = "ms" },
        ]);

        writer.AddTraces(Traces(on));
        writer.AddLogs(Logs(on));
        var reasoning = on ? writer.PutBlob(Reasoning) : null;
        if (reasoning is not null)
        {
            writer.AddEvidence(new AefEvidence { EvidenceId = "E-reasoning", Kind = AefEvidenceKind.JudgeReasoning, Link = AefEvidenceLink.ToBlob(reasoning), Description = "the judge's reasoning" });
        }

        writer.AddEvidence(new AefEvidence { EvidenceId = "E-judge-span", Kind = AefEvidenceKind.Span, Link = AefEvidenceLink.ToSpan(TraceId, JudgeSpan) });
        writer.AddEvidence(new AefEvidence
        {
            EvidenceId = "E-policy",
            Kind = AefEvidenceKind.ComplianceArtifact,
            Link = AefEvidenceLink.ToUri("https://example.com/policies/refunds-v4.pdf", "sha256:" + new string('c', 64)),
        });

        var composite = writer.AddResult(new AefResult
        {
            CaseId = "k1",
            Path = "q",
            Lane = "main",
            Evaluator = new AefEvaluator("composite:q", "1"),
            State = AefState.Failed,
            Severity = AefSeverity.Medium,
            Scores = [new AefScore { Metric = "m", Value = 0.4, Normalized = 0.4 }, new AefScore { Metric = "tokens", Value = 1530 }, new AefScore { Metric = "latency", Value = 812.5 }],
            Aggregation = new AefAggregation
            {
                Strategy = AefAggregationStrategy.Min,
                Threshold = 0.5,
                Score = 0.4,
                RulePath = AefRulePath.Threshold,
                Measured = 2,
                Total = 3,
                MinimumMeasuredShare = 0.5,
                Unmeasured = new AefUnmeasured { NotMeasured = 0, NotApplicable = 1, Skipped = 0, Error = 0 },
                Decisive = [writer.ResultIdOf("k1", "q/b")],
            },
            TraceLink = new AefTraceLink(TraceId, AgentSpan),
            Evidence = ["E-judge-span"],
            Usage =
            [
                new AefUsage { Role = AefUsageRole.Agent, InputTokens = 1200, OutputTokens = 210, CacheReadInputTokens = 300, CostUsd = 0.0042, CostSource = "example-2026-10" },
                new AefUsage { Role = AefUsageRole.Judge, InputTokens = 100, OutputTokens = 20, ReasoningOutputTokens = 12 },
            ],
            StartedAt = Start.AddSeconds(1),
            EndedAt = Start.AddSeconds(2.5),
            DurationMs = 1500,
            Turns = 3,
        });
        composite.AddChild(new AefResult
        {
            CaseId = "k1", Path = "q/a", Lane = "main", Evaluator = new AefEvaluator("code:a"), State = AefState.Passed,
            Scores = [new AefScore { Metric = "m", Value = 1.0 }], Component = new AefComponent(1, true),
            Annotator = new AefAnnotator { Kind = AefAnnotatorKind.Code },
        });
        composite.AddChild(new AefResult
        {
            CaseId = "k1", Path = "q/b", Lane = "main", Evaluator = new AefEvaluator("llm:b", "2"), State = AefState.Failed, Severity = AefSeverity.Medium,
            Scores = [new AefScore { Metric = "m", Value = 0.4, Label = "partial" }], Component = new AefComponent(2, true),
            Reasoning = reasoning,
            Annotator = new AefAnnotator
            {
                Kind = AefAnnotatorKind.Llm,
                Model = "judge-model-1",
                PromptHash = on ? "sha256:" + new string('d', 64) : null,
                RubricDigest = "sha256:" + new string('b', 64),
                Panel = new AefPanel(2, 3),
            },
            VerdictRule = new AefVerdictRule("m >= 0.5", 0.5, "config.thresholds"),
            Evidence = reasoning is null ? null : ["E-reasoning"],
        });
        composite.AddChild(new AefResult
        {
            CaseId = "k1", Path = "q/c", Lane = "main", Evaluator = new AefEvaluator("code:c"), State = AefState.NotApplicable,
            Reason = "the case makes no tool call", Component = new AefComponent(0.5, false),
        });

        writer.AddResult(new AefResult
        {
            CaseId = "k2", Path = "q", Lane = "main", Evaluator = new AefEvaluator("composite:q", "1"), State = AefState.Passed,
            Scores = [new AefScore { Metric = "m", Value = 0.9 }, new AefScore { Metric = "tokens", Value = 980 }],
            VerdictRule = new AefVerdictRule("m >= 0.8", 0.8),
        });

        // k3: two trials and the rollup, which is the case's result at q ([RES-8]).
        writer.AddResult(new AefResult
        {
            CaseId = "k3", Path = "q", Trial = 0, Lane = "main", Evaluator = new AefEvaluator("composite:q", "1"), State = AefState.Passed,
            Scores = [new AefScore { Metric = "m", Value = 0.85 }],
        });
        writer.AddResult(new AefResult
        {
            CaseId = "k3", Path = "q", Trial = 1, Lane = "main", Evaluator = new AefEvaluator("composite:q", "1"), State = AefState.Failed, Severity = AefSeverity.Low,
            Scores = [new AefScore { Metric = "m", Value = 0.6 }],
        });
        writer.AddResult(new AefResult
        {
            CaseId = "k3", Path = "q", Lane = "main", Evaluator = new AefEvaluator("composite:q", "1"), State = AefState.Passed,
            Trials = new AefTrials(2, 1, AefTrialAggregation.AnyPass, false),
            Scores = [new AefScore { Metric = "m", Value = 0.725 }],
        });

        writer.AddResult(new AefResult
        {
            CaseId = "k4", Path = "q", Lane = "security", Evaluator = new AefEvaluator("attack:injection"), State = AefState.Failed, Severity = AefSeverity.High,
            Scores = [new AefScore { Metric = "m", Value = 0.1 }],
            Attack = new AefAttack { Technique = "indirect prompt injection", Taxonomy = [new AefTaxonomyId(AefTaxonomyScheme.OwaspLlm, "LLM01")], Success = true },
        });
        writer.AddResult(new AefResult
        {
            CaseId = "k5", Path = "q", Lane = "security", Evaluator = new AefEvaluator("score:q"), State = AefState.Scored,
            Scores = [new AefScore { Metric = "m", Value = 0.5 }],
            Uncertainty = new AefUncertainty(0.05, new AefInterval(0.4, 0.6, 0.95, "bootstrap")),
        });
        writer.AddResult(new AefResult
        {
            CaseId = "k6", Path = "q", Lane = "main", Evaluator = new AefEvaluator("composite:q", "1"), State = AefState.Skipped, Reason = "rate limited",
        });

        writer.AddGate(new AefGateDecision
        {
            GateId = "gate:ci",
            DecisionId = "d-1",
            Rule = new AefGateRule("fail-on", ["severity>=medium"]),
            Inputs = new AefGateInputs { Results = [writer.ResultIdOf("k1", "q"), writer.ResultIdOf("k2", "q")], BaselineRun = new AefRunPointer("run-0", new string('e', 64)) },
            Comparability = AefComparability.Comparable,
            Outcome = AefGateOutcome.NoShip,
            ExitCode = 1,
            Decisive = [writer.ResultIdOf("k1", "q")],
            DecidedAt = Start.AddMinutes(5),
        });

        writer.SetSummary(new AefSummary
        {
            Lanes =
            [
                new AefSummaryLane("main",
                [
                    new AefSummaryEntry { Metric = "m", Path = "q", Rule = "mean m >= 0.8", Decide = f => new AefSummaryDecision(f.Value >= 0.8 ? AefSummaryVerdict.Passed : AefSummaryVerdict.Failed, Stderr: 0.1) },
                    new AefSummaryEntry { Metric = "pass", Path = "q", Rule = "pass rate >= 0.9", Decide = f => new AefSummaryDecision(f.Value >= 0.9 ? AefSummaryVerdict.Passed : AefSummaryVerdict.Failed) },
                    new AefSummaryEntry { Metric = "tokens", Path = "q" },
                    new AefSummaryEntry { Metric = "m", Path = "q/a", Aggregate = new AefAggregate("median") },
                    new AefSummaryEntry { Metric = "latency", Path = "q" },
                ]),
                new AefSummaryLane("security",
                [
                    new AefSummaryEntry { Metric = "m", Path = "q", Aggregate = new AefAggregate("min"), Decide = f => new AefSummaryDecision(AefSummaryVerdict.Warn, Ci: new AefInterval(0.1, 0.5, 0.9)) },
                    new AefSummaryEntry { Metric = "pass", Path = "q", Aggregate = new AefAggregate("pass@k", 2), Decide = f => new AefSummaryDecision(AefSummaryVerdict.Inconclusive, Value: 0.5) },
                ]),
            ],
            Cost = new AefCost(0.0042, "example-2026-10"),
            Usage =
            [
                new AefUsage { Role = AefUsageRole.Agent, Model = "agent-model", InputTokens = 1200, OutputTokens = 210 },
                new AefUsage { Role = AefUsageRole.Judge, Model = "judge-model-1", InputTokens = 100, OutputTokens = 20 },
                new AefUsage { Role = AefUsageRole.Judge, InputTokens = 5 },
            ],
            Ext = new JsonObject { ["agenteval.summaryNote"] = "computed by the writer" },
        });
        writer.PutExtFile("agenteval/notes.txt", Encoding.UTF8.GetBytes("Notes kept by the producer.\n"));
        return writer.Close(AefRunStatus.Completed, Start.AddMinutes(5));
    }

    /// <summary>An aborted run: one error, one skipped, summary entries with nothing measured.</summary>
    public static AefRunVerification WriteAborted(string dir)
    {
        var writer = AefRunWriter.Create(dir, Header("run-aborted-1", AefContentCapture.On));
        writer.SetMetrics([new AefMetric { Id = "m", Kind = AefMetricKind.Score, Direction = AefMetricDirection.HigherBetter, Scale = AefScale.Between(0, 1) }]);
        writer.AddResult(new AefResult { CaseId = "k1", Path = "q", Evaluator = new AefEvaluator("composite:q"), State = AefState.Error, Reason = "the subject timed out" });
        writer.AddResult(new AefResult { CaseId = "k2", Path = "q", Evaluator = new AefEvaluator("composite:q"), State = AefState.Skipped, Reason = "the run was aborted before it ran" });
        writer.SetSummary(new AefSummary { Lanes = [new AefSummaryLane("main", [new AefSummaryEntry { Metric = "m", Path = "q", Decide = _ => throw new InvalidOperationException("n is 0") }])] });
        return writer.Close(AefRunStatus.Aborted, Start.AddMinutes(2), "the budget ran out");
    }

    /// <summary>A TracesData object with the agent's span and the judge's.</summary>
    public static JsonObject Traces(bool content)
    {
        var agentAttributes = new JsonArray(Attribute("gen_ai.operation.name", "invoke_agent"), Attribute("gen_ai.agent.id", "triage"));
        if (content)
        {
            agentAttributes.Add(Attribute("gen_ai.input.messages", "[{\"role\":\"user\",\"parts\":[{\"type\":\"text\",\"content\":\"Refund?\"}]}]"));
        }

        return new JsonObject
        {
            ["resourceSpans"] = new JsonArray(new JsonObject
            {
                ["resource"] = new JsonObject { ["attributes"] = new JsonArray(Attribute("service.name", "support-api")) },
                ["scopeSpans"] = new JsonArray(new JsonObject
                {
                    ["scope"] = new JsonObject { ["name"] = "agenteval" },
                    ["spans"] = new JsonArray(
                        new JsonObject
                        {
                            ["traceId"] = TraceId, ["spanId"] = AgentSpan, ["name"] = "invoke_agent triage", ["kind"] = 1,
                            ["startTimeUnixNano"] = "1790848801000000000", ["endTimeUnixNano"] = "1790848802500000000", ["attributes"] = agentAttributes,
                        },
                        new JsonObject
                        {
                            ["traceId"] = TraceId, ["spanId"] = JudgeSpan, ["parentSpanId"] = AgentSpan, ["name"] = "chat judge-model-1", ["kind"] = 3,
                            ["startTimeUnixNano"] = "1790848802000000000", ["endTimeUnixNano"] = "1790848802400000000",
                            ["attributes"] = new JsonArray(Attribute("gen_ai.operation.name", "chat")),
                        }),
                }),
            }),
        };
    }

    /// <summary>A LogsData object with a <c>gen_ai.evaluation.result</c> event (a body only when content is kept).</summary>
    public static JsonObject Logs(bool content)
    {
        var record = new JsonObject
        {
            ["timeUnixNano"] = "1790848802500000000",
            ["eventName"] = "gen_ai.evaluation.result",
            ["traceId"] = TraceId,
            ["spanId"] = AgentSpan,
            ["attributes"] = new JsonArray(Attribute("gen_ai.evaluation.name", "m"), Attribute("gen_ai.evaluation.score.label", "failed")),
        };
        if (content)
        {
            record["body"] = new JsonObject { ["stringValue"] = "The answer misses the refund policy." };
        }

        return new JsonObject
        {
            ["resourceLogs"] = new JsonArray(new JsonObject
            {
                ["scopeLogs"] = new JsonArray(new JsonObject { ["logRecords"] = new JsonArray(record) }),
            }),
        };
    }

    /// <summary>An overlay event at 2026-10-01 plus <paramref name="day"/> days, claiming the assurance it has (self-attested until a signature verifies).</summary>
    public static AefOverlayEvent Event(
        string id, AefOverlayKind kind, AefOverlayTarget target, string by, int day, AefState? state = null, string? reason = null, DateTimeOffset? expires = null) => new()
    {
        EventId = id,
        Kind = kind,
        Target = target,
        State = state,
        Reason = reason,
        Expires = expires,
        By = new AefIdentity(by, AefAssurance.SelfAttested),
        At = Start.AddDays(day),
    };

    private static JsonObject Attribute(string key, string value) => new() { ["key"] = key, ["value"] = new JsonObject { ["stringValue"] = value } };

    /// <summary>A clock that always says <paramref name="now"/>.</summary>
    public sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        /// <inheritdoc/>
        public override DateTimeOffset GetUtcNow() => now;
    }
}
