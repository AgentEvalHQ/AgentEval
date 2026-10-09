// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using AgentEval.Results.Runs;

namespace AgentEval.Results.Writing;

// The plain types a producer gives AefRunWriter (contracts/aef/1/spec/03-run.md), shaped after the writer schemas
// (contracts/aef/1/schemas/writer): one property per schema member, required members required, optional ones nullable
// (an absent value is not written; the writer writes no null the schema does not require). What the writer computes is
// not here: schemaVersion (its own, 1.0, [VER-2]), status and endedAt (Close), resultId and parentResultId ([RES-4]),
// a blob's digest and size (PutBlob), the summary's figures ([SUM-3]–[SUM-8]). Each type writes its members in the order
// of its schema's properties.

#region Enums: the writer schemas' values ([VER-2]: a writer writes only known values)

/// <summary>run.json <c>status</c> ([RUN-5]); closed for major 1 ([VER-9]).</summary>
public enum AefRunStatus
{
    /// <summary>The producer is writing the run; its files may still change ([RUN-4]).</summary>
    [AefName("running")] Running,

    /// <summary>Closed after running to its end.</summary>
    [AefName("completed")] Completed,

    /// <summary>Closed before its end, with an <c>abortReason</c>.</summary>
    [AefName("aborted")] Aborted,
}

/// <summary>What the subject is ([RUN-6]).</summary>
public enum AefSubjectKind
{
    /// <summary><c>agent</c>.</summary>
    [AefName("agent")] Agent,

    /// <summary><c>workflow</c>.</summary>
    [AefName("workflow")] Workflow,

    /// <summary><c>model</c>.</summary>
    [AefName("model")] Model,

    /// <summary><c>endpoint</c>.</summary>
    [AefName("endpoint")] Endpoint,

    /// <summary><c>mcp-server</c>: an MCP server.</summary>
    [AefName("mcp-server")] McpServer,

    /// <summary><c>other</c>.</summary>
    [AefName("other")] Other,
}

/// <summary>How a judge was used ([RUN-9]).</summary>
public enum AefJudgeMode
{
    /// <summary><c>single</c>: alone.</summary>
    [AefName("single")] Single,

    /// <summary><c>panel</c>: one judge of a panel.</summary>
    [AefName("panel")] Panel,

    /// <summary><c>primary</c>: its grade counts.</summary>
    [AefName("primary")] Primary,

    /// <summary><c>shadow</c>: it grades alongside, without counting.</summary>
    [AefName("shadow")] Shadow,

    /// <summary><c>other</c>.</summary>
    [AefName("other")] Other,
}

/// <summary>How a measured value is compared with a threshold (run.json <c>config.thresholds</c>).</summary>
public enum AefThresholdOp
{
    /// <summary><c>&gt;=</c>.</summary>
    [AefName(">=")] AtLeast,

    /// <summary><c>&gt;</c>.</summary>
    [AefName(">")] Above,

    /// <summary><c>&lt;=</c>.</summary>
    [AefName("<=")] AtMost,

    /// <summary><c>&lt;</c>.</summary>
    [AefName("<")] Below,

    /// <summary><c>==</c>.</summary>
    [AefName("==")] EqualTo,
}

/// <summary>What text a run keeps ([RUN-11], [SEC-3]).</summary>
public enum AefContentCapture
{
    /// <summary><c>off</c>: no prompt, response, tool argument or judge reasoning is kept, and no digest of one.</summary>
    [AefName("off")] Off,

    /// <summary><c>on</c>: they may be kept, in blobs.</summary>
    [AefName("on")] On,
}

/// <summary>How the target was driven ([RUN-7]).</summary>
public enum AefTargetMode
{
    /// <summary><c>live</c>: the real subject, answering at run time.</summary>
    [AefName("live")] Live,

    /// <summary><c>replayed</c>: recorded answers of the real subject, played back.</summary>
    [AefName("replayed")] Replayed,

    /// <summary><c>scripted</c>: a scripted stand-in that follows a fixed script.</summary>
    [AefName("scripted")] Scripted,

    /// <summary><c>mocked</c>: a stand-in that is not the subject.</summary>
    [AefName("mocked")] Mocked,
}

/// <summary>Where the cases' inputs came from ([RUN-7]).</summary>
public enum AefStimulus
{
    /// <summary><c>suite</c>: a fixed suite.</summary>
    [AefName("suite")] Suite,

    /// <summary><c>generated</c>: generated at run time.</summary>
    [AefName("generated")] Generated,

    /// <summary><c>external</c>: another tool's cases.</summary>
    [AefName("external")] External,

    /// <summary><c>other</c>.</summary>
    [AefName("other")] Other,
}

/// <summary>How a case's trials are combined ([RES-8]; run.json <c>executionPolicy.aggregation</c> and a rollup's <c>trials.aggregation</c>).</summary>
public enum AefTrialAggregation
{
    /// <summary><c>MajorityVote</c>.</summary>
    [AefName("MajorityVote")] MajorityVote,

    /// <summary><c>AllPass</c>.</summary>
    [AefName("AllPass")] AllPass,

    /// <summary><c>AnyPass</c>.</summary>
    [AefName("AnyPass")] AnyPass,

    /// <summary><c>Mean</c>.</summary>
    [AefName("Mean")] Mean,

    /// <summary><c>Median</c>.</summary>
    [AefName("Median")] Median,

    /// <summary><c>Max</c>.</summary>
    [AefName("Max")] Max,

    /// <summary><c>PassAtK</c> (with <c>k</c>).</summary>
    [AefName("PassAtK")] PassAtK,
}

/// <summary>A result line's state ([RES-1]); closed for major 1 ([VER-9]).</summary>
public enum AefState
{
    /// <summary><c>passed</c>: measured, and the rule was met (the only pass).</summary>
    [AefName("passed")] Passed,

    /// <summary><c>failed</c>: measured, and the rule was not met.</summary>
    [AefName("failed")] Failed,

    /// <summary><c>warn</c>: measured; a soft failure.</summary>
    [AefName("warn")] Warn,

    /// <summary><c>inconclusive</c>: measured, but the evidence does not decide.</summary>
    [AefName("inconclusive")] Inconclusive,

    /// <summary><c>scored</c>: measured, with no pass/fail rule applied.</summary>
    [AefName("scored")] Scored,

    /// <summary><c>not_measured</c>: a typed absence (an input was not there).</summary>
    [AefName("not_measured")] NotMeasured,

    /// <summary><c>not_applicable</c>: a typed absence (the evaluator does not apply).</summary>
    [AefName("not_applicable")] NotApplicable,

    /// <summary><c>skipped</c>: a typed absence (not run).</summary>
    [AefName("skipped")] Skipped,

    /// <summary><c>error</c>: a typed absence (did not complete).</summary>
    [AefName("error")] Error,

    /// <summary><c>pending</c>: a typed absence (not finished; only in an open run, [RES-3]).</summary>
    [AefName("pending")] Pending,
}

/// <summary>How bad a failure is ([RES-9]).</summary>
public enum AefSeverity
{
    /// <summary><c>none</c>.</summary>
    [AefName("none")] None,

    /// <summary><c>low</c>.</summary>
    [AefName("low")] Low,

    /// <summary><c>medium</c>.</summary>
    [AefName("medium")] Medium,

    /// <summary><c>high</c>.</summary>
    [AefName("high")] High,

    /// <summary><c>critical</c>.</summary>
    [AefName("critical")] Critical,
}

/// <summary>How the producer combined a composite's children ([RES-6], for display).</summary>
public enum AefAggregationStrategy
{
    /// <summary><c>WeightedSum</c>.</summary>
    [AefName("WeightedSum")] WeightedSum,

    /// <summary><c>Min</c>.</summary>
    [AefName("Min")] Min,

    /// <summary><c>WeightedMedian</c>.</summary>
    [AefName("WeightedMedian")] WeightedMedian,

    /// <summary><c>CapByWorst</c>.</summary>
    [AefName("CapByWorst")] CapByWorst,

    /// <summary><c>MajorityVote</c>.</summary>
    [AefName("MajorityVote")] MajorityVote,

    /// <summary><c>Own</c>: the node's own verdict; its children are recorded beside it, with weight 0.</summary>
    [AefName("Own")] Own,
}

/// <summary>Which branch of the producer's verdict rules decided a composite's state ([RES-6], for display).</summary>
public enum AefRulePath
{
    /// <summary><c>required-error</c>.</summary>
    [AefName("required-error")] RequiredError,

    /// <summary><c>nothing-measured</c>.</summary>
    [AefName("nothing-measured")] NothingMeasured,

    /// <summary><c>threshold</c>.</summary>
    [AefName("threshold")] Threshold,

    /// <summary><c>severity</c>.</summary>
    [AefName("severity")] Severity,

    /// <summary><c>under-covered</c>.</summary>
    [AefName("under-covered")] UnderCovered,
}

/// <summary>Who graded a node ([RES-10]).</summary>
public enum AefAnnotatorKind
{
    /// <summary><c>CODE</c>.</summary>
    [AefName("CODE")] Code,

    /// <summary><c>LLM</c>.</summary>
    [AefName("LLM")] Llm,

    /// <summary><c>HUMAN</c>.</summary>
    [AefName("HUMAN")] Human,

    /// <summary><c>HYBRID</c>.</summary>
    [AefName("HYBRID")] Hybrid,

    /// <summary><c>OTHER</c>.</summary>
    [AefName("OTHER")] Other,
}

/// <summary>Whose usage an entry is ([RES-10], [SUM-7]).</summary>
public enum AefUsageRole
{
    /// <summary><c>agent</c>: the subject.</summary>
    [AefName("agent")] Agent,

    /// <summary><c>judge</c>.</summary>
    [AefName("judge")] Judge,

    /// <summary><c>attacker</c>: an attacker model.</summary>
    [AefName("attacker")] Attacker,

    /// <summary><c>other</c>.</summary>
    [AefName("other")] Other,
}

/// <summary>A public taxonomy of attack techniques ([RES-10]).</summary>
public enum AefTaxonomyScheme
{
    /// <summary><c>owasp-llm</c>.</summary>
    [AefName("owasp-llm")] OwaspLlm,

    /// <summary><c>owasp-agentic</c>.</summary>
    [AefName("owasp-agentic")] OwaspAgentic,

    /// <summary><c>mitre-atlas</c>.</summary>
    [AefName("mitre-atlas")] MitreAtlas,

    /// <summary><c>nist-ai-rmf</c>.</summary>
    [AefName("nist-ai-rmf")] NistAiRmf,

    /// <summary><c>other</c>.</summary>
    [AefName("other")] Other,
}

/// <summary>An evidence record's kind ([EVD-1]); the content kinds are not written under <c>contentCapture: off</c> ([RUN-11]).</summary>
public enum AefEvidenceKind
{
    /// <summary><c>span</c>.</summary>
    [AefName("span")] Span,

    /// <summary><c>tool_call</c> (content).</summary>
    [AefName("tool_call")] ToolCall,

    /// <summary><c>judge_reasoning</c> (content).</summary>
    [AefName("judge_reasoning")] JudgeReasoning,

    /// <summary><c>compliance_artifact</c>.</summary>
    [AefName("compliance_artifact")] ComplianceArtifact,

    /// <summary><c>document</c> (content).</summary>
    [AefName("document")] Document,

    /// <summary><c>input</c> (content).</summary>
    [AefName("input")] Input,

    /// <summary><c>expected</c> (content).</summary>
    [AefName("expected")] Expected,

    /// <summary><c>output</c> (content).</summary>
    [AefName("output")] Output,

    /// <summary><c>transcript</c> (content).</summary>
    [AefName("transcript")] Transcript,

    /// <summary><c>other</c>.</summary>
    [AefName("other")] Other,
}

/// <summary>Whether a comparison a gate decision needed was shown comparable ([GATE-1]).</summary>
public enum AefComparability
{
    /// <summary><c>comparable</c>.</summary>
    [AefName("comparable")] Comparable,

    /// <summary><c>incomparable</c>: never yields <c>ship</c> ([GATE-2]).</summary>
    [AefName("incomparable")] Incomparable,

    /// <summary><c>not_applicable</c>: no comparison was needed.</summary>
    [AefName("not_applicable")] NotApplicable,
}

/// <summary>A gate decision's outcome ([GATE-1]).</summary>
public enum AefGateOutcome
{
    /// <summary><c>ship</c>.</summary>
    [AefName("ship")] Ship,

    /// <summary><c>no_ship</c>.</summary>
    [AefName("no_ship")] NoShip,

    /// <summary><c>inconclusive</c>.</summary>
    [AefName("inconclusive")] Inconclusive,
}

/// <summary>A metric's kind ([SUM-1]); closed for major 1 ([VER-9]).</summary>
public enum AefMetricKind
{
    /// <summary><c>score</c>.</summary>
    [AefName("score")] Score,

    /// <summary><c>rate</c>: a summary counts 1 for <c>passed</c>, 0 otherwise ([SUM-4]).</summary>
    [AefName("rate")] Rate,

    /// <summary><c>count</c>: a summary's value is the sum ([SUM-5]).</summary>
    [AefName("count")] Count,

    /// <summary><c>duration</c>.</summary>
    [AefName("duration")] Duration,

    /// <summary><c>cost</c>.</summary>
    [AefName("cost")] Cost,

    /// <summary><c>verdict</c>: as <c>rate</c> ([SUM-4]).</summary>
    [AefName("verdict")] Verdict,
}

/// <summary>Which way a metric is better ([SUM-1]).</summary>
public enum AefMetricDirection
{
    /// <summary><c>higher_better</c>.</summary>
    [AefName("higher_better")] HigherBetter,

    /// <summary><c>lower_better</c>.</summary>
    [AefName("lower_better")] LowerBetter,

    /// <summary><c>none</c>: shown, never coloured as better or worse.</summary>
    [AefName("none")] None,
}

/// <summary>The producer's verdict on a summary entry ([SUM-6]).</summary>
public enum AefSummaryVerdict
{
    /// <summary><c>passed</c>.</summary>
    [AefName("passed")] Passed,

    /// <summary><c>failed</c>.</summary>
    [AefName("failed")] Failed,

    /// <summary><c>warn</c>.</summary>
    [AefName("warn")] Warn,

    /// <summary><c>inconclusive</c>.</summary>
    [AefName("inconclusive")] Inconclusive,

    /// <summary><c>not_measured</c>: the verdict of an entry whose <c>n</c> is 0.</summary>
    [AefName("not_measured")] NotMeasured,

    /// <summary><c>scored</c>: no rule applied (a measurement only).</summary>
    [AefName("scored")] Scored,
}

#endregion

#region run.json

/// <summary>
/// What a producer says about a run when it starts it (run.json, [RUN-5]–[RUN-15]). The writer adds
/// <c>schemaVersion</c>, <c>status</c> (<c>running</c>, then the status <see cref="AefRunWriter.Close"/> gives), and,
/// at close, <c>endedAt</c> and <c>abortReason</c>.
/// </summary>
public sealed record AefRunHeader
{
    /// <summary>The run's id: never reused for a different run ([RUN-13]).</summary>
    public required string RunId { get; init; }

    /// <summary>The tool that writes the run ([RUN-15]: for an imported run, the converter).</summary>
    public required AefProducer Producer { get; init; }

    /// <summary>What was evaluated ([RUN-6]).</summary>
    public required AefSubject Subject { get; init; }

    /// <summary>Where the subject ran ([RUN-6]).</summary>
    public AefDeployment? Deployment { get; init; }

    /// <summary>The cases that ran ([RUN-8]).</summary>
    public AefSuite? Suite { get; init; }

    /// <summary>The models that graded results, in order ([RUN-9]).</summary>
    public IReadOnlyList<AefJudge>? Judges { get; init; }

    /// <summary>The run's configuration as the producer applied it.</summary>
    public AefRunConfig? Config { get; init; }

    /// <summary>When the run started ([RUN-5]), at the precision given ([ENC-8]: up to nanoseconds; a <see cref="DateTimeOffset"/> converts).</summary>
    public required AefTime StartedAt { get; init; }

    /// <summary>The OpenTelemetry conventions the run's traces follow ([RUN-14]).</summary>
    public AefOtel? Otel { get; init; }

    /// <summary>
    /// What text the run keeps ([RUN-11]). Always written (a producer SHOULD write it; a reader treats a run without it
    /// as <c>on</c>). With <see cref="AefContentCapture.Off"/> the writer refuses content ([RUN-11], [SEC-6]).
    /// </summary>
    public required AefContentCapture ContentCapture { get; init; }

    /// <summary>The spending limit and the price table the producer used.</summary>
    public AefCostPolicy? CostPolicy { get; init; }

    /// <summary>Producer extensions ([ENC-19]); never a credential ([RUN-10]).</summary>
    public JsonObject? Ext { get; init; }

    /// <summary>The plan, job and runner that produced the run, written by a runner ([RUN-12]).</summary>
    public AefProvenance? Provenance { get; init; }

    /// <summary>How the target was driven ([RUN-7]).</summary>
    public required AefExecution Execution { get; init; }

    /// <summary>For a run converted from another tool's output: the tool, and the fields the converter supplied ([RUN-15]).</summary>
    public AefImported? Imported { get; init; }

    /// <summary>run.json in the schema's member order, for a status and, for a closed run, its end.</summary>
    internal JsonObject ToJson(AefRunStatus status, string? endedAt, string? abortReason)
    {
        var json = new JsonObject
        {
            ["schemaVersion"] = AefRunWriter.SchemaVersion,
            ["runId"] = RunId,
            ["status"] = AefWire.Node(status),
        };
        json.Put("abortReason", abortReason);
        json["producer"] = Producer.ToJson();
        json["subject"] = Subject.ToJson();
        json.Put("deployment", Deployment?.ToJson());
        json.Put("suite", Suite?.ToJson());
        json.Put("judges", Judges is null ? null : AefWire.Array(Judges, j => j.ToJson()));
        json.Put("config", Config?.ToJson());
        json["startedAt"] = AefWire.Time(StartedAt);
        json.Put("endedAt", endedAt);
        json.Put("otel", Otel?.ToJson());
        json["contentCapture"] = AefWire.Node(ContentCapture);
        json.Put("costPolicy", CostPolicy?.ToJson());
        json.Put("ext", AefWire.Ext(Ext));
        json.Put("provenance", Provenance?.ToJson());
        json["execution"] = Execution.ToJson();
        json.Put("imported", Imported?.ToJson());
        return json;
    }
}

/// <summary>run.json <c>producer</c>: the tool that wrote the run.</summary>
public sealed record AefProducer
{
    /// <summary>The producer's name (1–128 characters).</summary>
    public required string Name { get; init; }

    /// <summary>The producer's exact version ([ENC-10]).</summary>
    public required string Version { get; init; }

    /// <summary>The runtime the producer ran on.</summary>
    public AefRuntime? Runtime { get; init; }

    internal JsonObject ToJson()
    {
        var json = new JsonObject { ["name"] = Name, ["version"] = Version };
        json.Put("runtime", Runtime?.ToJson());
        return json;
    }
}

/// <summary>run.json <c>producer.runtime</c>.</summary>
public sealed record AefRuntime
{
    /// <summary>The runtime's name.</summary>
    public required string Name { get; init; }

    /// <summary>The runtime's version.</summary>
    public string? Version { get; init; }

    internal JsonObject ToJson()
    {
        var json = new JsonObject { ["name"] = Name };
        json.Put("version", Version);
        return json;
    }
}

/// <summary>run.json <c>subject</c>: what was evaluated ([RUN-6]).</summary>
public sealed record AefSubject
{
    /// <summary>A typed reference, <c>kind:name</c>.</summary>
    public required string Ref { get; init; }

    /// <summary>What the subject is.</summary>
    public required AefSubjectKind Kind { get; init; }

    /// <summary>The subject's exact version ([ENC-10]); required when the run serves a checkpoint.</summary>
    public string? Version { get; init; }

    /// <summary>The environment the subject ran in, as the producer names it.</summary>
    public string? Environment { get; init; }

    /// <summary>The subject's ids in other systems, by system name.</summary>
    public IReadOnlyDictionary<string, string?>? ExternalIds { get; init; }

    /// <summary>The keys that join the subject to OpenTelemetry.</summary>
    public AefSubjectTelemetry? Telemetry { get; init; }

    internal JsonObject ToJson()
    {
        var json = new JsonObject { ["ref"] = Ref, ["kind"] = AefWire.Node(Kind) };
        json.Put("version", Version);
        json.Put("environment", Environment);
        json.Put("externalIds", ExternalIdsJson(ExternalIds));
        json.Put("telemetry", Telemetry?.ToJson());
        return json;
    }

    internal static JsonObject? ExternalIdsJson(IReadOnlyDictionary<string, string?>? ids)
    {
        if (ids is null)
        {
            return null;
        }

        var json = new JsonObject();
        foreach (var (system, id) in ids)
        {
            json[system] = id;   // null: the system has no id for it (the schema allows a string or null)
        }

        return json;
    }
}

/// <summary>run.json <c>subject.telemetry</c>.</summary>
public sealed record AefSubjectTelemetry
{
    /// <summary>The subject's <c>gen_ai.agent.id</c>.</summary>
    public string? AgentId { get; init; }

    /// <summary>The subject's <c>service.name</c>.</summary>
    public string? ServiceName { get; init; }

    internal JsonObject ToJson()
    {
        var json = new JsonObject();
        json.Put("agentId", AgentId);
        json.Put("serviceName", ServiceName);
        return json;
    }
}

/// <summary>run.json <c>deployment</c>: where the subject ran ([RUN-6]).</summary>
public sealed record AefDeployment
{
    /// <summary>A typed reference.</summary>
    public required string Ref { get; init; }

    /// <summary>The deployment's environment.</summary>
    public string? Environment { get; init; }

    /// <summary>Scheme, host and path only: no user information, query or fragment ([RUN-10]).</summary>
    public string? Endpoint { get; init; }

    /// <summary>The deployment's ids in other systems.</summary>
    public IReadOnlyDictionary<string, string?>? ExternalIds { get; init; }

    internal JsonObject ToJson()
    {
        var json = new JsonObject { ["ref"] = Ref };
        json.Put("environment", Environment);
        json.Put("endpoint", Endpoint);
        json.Put("externalIds", AefSubject.ExternalIdsJson(ExternalIds));
        return json;
    }
}

/// <summary>run.json <c>suite</c> ([RUN-8]).</summary>
public sealed record AefSuite
{
    /// <summary>A typed reference.</summary>
    public required string Ref { get; init; }

    /// <summary>The suite's exact version ([ENC-10]).</summary>
    public required string Version { get; init; }

    /// <summary>The SHA-256 of the suite's content (<c>sha256:</c> and 64 hex characters), when it is frozen.</summary>
    public string? Digest { get; init; }

    /// <summary>Whether the content is frozen.</summary>
    public bool? Frozen { get; init; }

    /// <summary>How many trials each case had.</summary>
    public AefExecutionPolicy? ExecutionPolicy { get; init; }

    internal JsonObject ToJson()
    {
        var json = new JsonObject { ["ref"] = Ref, ["version"] = Version };
        json.Put("digest", Digest);
        json.Put("frozen", Frozen);
        json.Put("executionPolicy", ExecutionPolicy?.ToJson());
        return json;
    }
}

/// <summary>run.json <c>suite.executionPolicy</c> ([RUN-8]).</summary>
public sealed record AefExecutionPolicy
{
    /// <summary>Trials per case, 1–1000.</summary>
    public required int TrialsPerCase { get; init; }

    /// <summary>How many trials must pass for the case to pass; never above <see cref="TrialsPerCase"/> (§3.9 <c>execution-policy</c>).</summary>
    public int? RequirePasses { get; init; }

    /// <summary>How a case's trials are combined.</summary>
    public AefTrialAggregation? Aggregation { get; init; }

    /// <summary>k, for <see cref="AefTrialAggregation.PassAtK"/>.</summary>
    public long? K { get; init; }

    internal JsonObject ToJson()
    {
        var json = new JsonObject { ["trialsPerCase"] = TrialsPerCase };
        json.Put("requirePasses", RequirePasses);
        json.Put("aggregation", Aggregation is { } a ? AefWire.Node(a) : null);
        json.Put("k", K);
        return json;
    }
}

/// <summary>One of run.json's <c>judges</c> ([RUN-9]).</summary>
public sealed record AefJudge
{
    /// <summary>The judge model, as its provider names it.</summary>
    public required string Model { get; init; }

    /// <summary>Who serves it.</summary>
    public string? Provider { get; init; }

    /// <summary>How the judge was used.</summary>
    public AefJudgeMode? Mode { get; init; }

    /// <summary>How many judges the panel had, when <see cref="Mode"/> is panel.</summary>
    public int? PanelSize { get; init; }

    /// <summary>The SHA-256 of the rubric (<c>sha256:</c> and 64 hex characters).</summary>
    public string? RubricDigest { get; init; }

    /// <summary>The producer's claim about how far the judge agreed with labelled cases before the run.</summary>
    public AefCalibration? Calibration { get; init; }

    internal JsonObject ToJson()
    {
        var json = new JsonObject { ["model"] = Model };
        json.Put("provider", Provider);
        json.Put("mode", Mode is { } m ? AefWire.Node(m) : null);
        json.Put("panelSize", PanelSize);
        json.Put("rubricDigest", RubricDigest);
        json.Put("calibration", Calibration?.ToJson());
        return json;
    }
}

/// <summary>A judge's <c>calibration</c> ([RUN-9]): measured before the run.</summary>
public sealed record AefCalibration
{
    /// <summary>The labelled cases, as a typed reference.</summary>
    public required string LabelSet { get; init; }

    /// <summary>Labelled cases the judge decided.</summary>
    public required long N { get; init; }

    /// <summary>Share of decided cases where the judge agreed with the label, 0–1.</summary>
    public double? Accuracy { get; init; }

    /// <summary>Cohen's kappa, −1–1.</summary>
    public double? Kappa { get; init; }

    /// <summary>Labelled failures the judge passed; never above <see cref="N"/> (§3.9 <c>calibration</c>).</summary>
    public long? DangerousErrors { get; init; }

    /// <summary>When it was measured: not after the run's start (§3.9 <c>calibration</c>), at the precision given ([ENC-8]).</summary>
    public required AefTime MeasuredAt { get; init; }

    internal JsonObject ToJson()
    {
        var json = new JsonObject { ["labelSet"] = LabelSet, ["n"] = AefWire.Integer(N, "calibration.n") };
        json.Put("accuracy", Accuracy);
        json.Put("kappa", Kappa);
        json.Put("dangerousErrors", DangerousErrors);
        json["measuredAt"] = AefWire.Time(MeasuredAt);
        return json;
    }
}

/// <summary>run.json <c>config</c>: the run's configuration as the producer applied it.</summary>
public sealed record AefRunConfig
{
    /// <summary>Per metric or result path, the rule the producer's verdict used, for display.</summary>
    public IReadOnlyDictionary<string, AefThreshold>? Thresholds { get; init; }

    /// <summary>
    /// The configuration's other members (the schema leaves <c>config</c> open); never a key, token or password
    /// ([RUN-10]). A member named <c>thresholds</c> here is refused: it is <see cref="Thresholds"/>.
    /// </summary>
    public JsonObject? Settings { get; init; }

    internal JsonObject ToJson()
    {
        var json = new JsonObject();
        if (Thresholds is not null)
        {
            var thresholds = new JsonObject();
            foreach (var (key, threshold) in Thresholds)
            {
                thresholds[key] = new JsonObject { ["op"] = AefWire.Node(threshold.Op), ["value"] = AefWire.Number(threshold.Value, "config.thresholds.value") };
            }

            json["thresholds"] = thresholds;
        }

        foreach (var (name, value) in Settings ?? [])
        {
            if (name == "thresholds")
            {
                throw new ArgumentException("config.Settings holds 'thresholds': give them as Thresholds.");
            }

            json[name] = value?.DeepClone();
        }

        return json;
    }
}

/// <summary>A threshold of run.json <c>config.thresholds</c>.</summary>
/// <param name="Op">How the measured value is compared.</param>
/// <param name="Value">The threshold.</param>
public sealed record AefThreshold(AefThresholdOp Op, double Value);

/// <summary>run.json <c>otel</c> ([RUN-14]).</summary>
public sealed record AefOtel
{
    /// <summary>The semantic conventions' version, MAJOR.MINOR or MAJOR.MINOR.PATCH.</summary>
    public string? SemconvVersion { get; init; }

    /// <summary>The semantic-convention namespaces the traces use, such as <c>gen_ai</c>.</summary>
    public IReadOnlyList<string>? Dialects { get; init; }

    /// <summary>The OpenTelemetry schema URLs the traces and logs follow.</summary>
    public IReadOnlyList<string>? SchemaUrls { get; init; }

    internal JsonObject ToJson()
    {
        var json = new JsonObject();
        json.Put("semconvVersion", SemconvVersion);
        json.Put("dialects", Dialects is null ? null : AefWire.Strings(Dialects));
        json.Put("schemaUrls", SchemaUrls is null ? null : AefWire.Strings(SchemaUrls));
        return json;
    }
}

/// <summary>run.json <c>costPolicy</c>.</summary>
public sealed record AefCostPolicy
{
    /// <summary>The spending limit, in US dollars.</summary>
    public double? MaxUsd { get; init; }

    /// <summary>The price table the producer used, by name.</summary>
    public string? PriceTable { get; init; }

    internal JsonObject ToJson()
    {
        var json = new JsonObject();
        json.Put("maxUsd", MaxUsd);
        json.Put("priceTable", PriceTable);
        return json;
    }
}

/// <summary>run.json <c>provenance</c> ([RUN-12]).</summary>
public sealed record AefProvenance
{
    /// <summary>The plan's id.</summary>
    public required string PlanId { get; init; }

    /// <summary>The SHA-256 of the plan file's exact bytes, 64 lower-case hex characters.</summary>
    public required string PlanDigest { get; init; }

    /// <summary>The job that produced the run.</summary>
    public required string JobId { get; init; }

    /// <summary>The runner that produced the run.</summary>
    public required string RunnerId { get; init; }

    internal JsonObject ToJson() => new() { ["planId"] = PlanId, ["planDigest"] = PlanDigest, ["jobId"] = JobId, ["runnerId"] = RunnerId };
}

/// <summary>run.json <c>execution</c> ([RUN-7]).</summary>
public sealed record AefExecution
{
    /// <summary>How the target was driven.</summary>
    public required AefTargetMode TargetMode { get; init; }

    /// <summary>Where the inputs came from.</summary>
    public AefStimulus? Stimulus { get; init; }

    internal JsonObject ToJson()
    {
        var json = new JsonObject { ["targetMode"] = AefWire.Node(TargetMode) };
        json.Put("stimulus", Stimulus is { } s ? AefWire.Node(s) : null);
        return json;
    }
}

/// <summary>run.json <c>imported</c> ([RUN-15]).</summary>
public sealed record AefImported
{
    /// <summary>The original tool and its version.</summary>
    public required string From { get; init; }

    /// <summary>The run.json fields the converter supplied, as dotted paths (<c>subject.version</c>).</summary>
    public required IReadOnlyList<string> Asserted { get; init; }

    internal JsonObject ToJson() => new() { ["from"] = From, ["asserted"] = AefWire.Strings(Asserted) };
}

#endregion

#region results.ndjson

/// <summary>
/// One node of the run's result tree (a line of results.ndjson, [RES-1]–[RES-11]). The writer computes
/// <c>resultId</c> ([RES-4]) from the run id, <see cref="CaseId"/>, <see cref="Path"/> and <see cref="Trial"/>, and
/// <c>parentResultId</c> from the parent the line is added under.
/// </summary>
public sealed record AefResult
{
    /// <summary>The case: 1–256 characters, no control character ([RES-4]).</summary>
    public required string CaseId { get; init; }

    /// <summary>The node's place in the tree, <c>/</c> between levels: 1–1024 characters, no control character.</summary>
    public required string Path { get; init; }

    /// <summary>The 0-based trial, on a line of a case that ran several times ([RES-8]); never with <see cref="Trials"/>.</summary>
    public int? Trial { get; init; }

    /// <summary>The rollup of a case that ran several times ([RES-8]); never with <see cref="Trial"/>.</summary>
    public AefTrials? Trials { get; init; }

    /// <summary>The check, judge or composite that produced the line ([RES-11]).</summary>
    public required AefEvaluator Evaluator { get; init; }

    /// <summary>The state ([RES-1]).</summary>
    public required AefState State { get; init; }

    /// <summary>Why the line is in a typed absence: required then ([RES-2]).</summary>
    public string? Reason { get; init; }

    /// <summary>The scores, one per metric; none on a typed absence ([RES-2]).</summary>
    public IReadOnlyList<AefScore>? Scores { get; init; }

    /// <summary>The rule a leaf applied, for display ([RES-7]).</summary>
    public AefVerdictRule? VerdictRule { get; init; }

    /// <summary>Who or what decided the node ([RES-10]).</summary>
    public AefAnnotator? Annotator { get; init; }

    /// <summary>The judge's reasoning, a blob of this run (<see cref="AefRunWriter.PutBlob(ReadOnlySpan{byte})"/>); refused under <c>contentCapture: off</c> ([RUN-11]).</summary>
    public AefBlob? Reasoning { get; init; }

    /// <summary>How uncertain the score is ([RES-10]).</summary>
    public AefUncertainty? Uncertainty { get; init; }

    /// <summary>What producing the node consumed, one entry per role and model ([RES-10]).</summary>
    public IReadOnlyList<AefUsage>? Usage { get; init; }

    /// <summary>The span (or trace) the result evaluates ([RES-10]).</summary>
    public AefTraceLink? TraceLink { get; init; }

    /// <summary>On a composite: how its children became its state ([RES-5], [RES-6]).</summary>
    public AefAggregation? Aggregation { get; init; }

    /// <summary>On a child of a composite: its weight and whether it is required ([RES-5]).</summary>
    public AefComponent? Component { get; init; }

    /// <summary>The evidence ids the node cites ([EVD-1]).</summary>
    public IReadOnlyList<string>? Evidence { get; init; }

    /// <summary>Producer extensions ([ENC-19]).</summary>
    public JsonObject? Ext { get; init; }

    /// <summary>The summary lane the line belongs to ([SUM-3]).</summary>
    public string? Lane { get; init; }

    /// <summary>How bad a failure is ([RES-9]).</summary>
    public AefSeverity? Severity { get; init; }

    /// <summary>Wall-clock time, in milliseconds.</summary>
    public double? DurationMs { get; init; }

    /// <summary>Conversation turns the case took.</summary>
    public long? Turns { get; init; }

    /// <summary>For an adversarial case ([RES-10]).</summary>
    public AefAttack? Attack { get; init; }

    /// <summary>When work on the node started, at the precision given ([ENC-8]).</summary>
    public AefTime? StartedAt { get; init; }

    /// <summary>When it ended: not before <see cref="StartedAt"/> (§3.9 <c>result-times</c>), at the precision given ([ENC-8]).</summary>
    public AefTime? EndedAt { get; init; }

    /// <summary>The line, in the schema's member order.</summary>
    internal JsonObject ToJson(string resultId, string? parentResultId)
    {
        var json = new JsonObject { ["schemaVersion"] = AefRunWriter.SchemaVersion, ["resultId"] = resultId };
        json.Put("parentResultId", parentResultId);   // absent on a root (never written as null)
        json["caseId"] = CaseId;
        json["path"] = Path;
        json.Put("trial", Trial);
        json.Put("trials", Trials?.ToJson());
        json["evaluator"] = Evaluator.ToJson();
        json["state"] = AefWire.Node(State);
        json.Put("reason", Reason);
        json.Put("scores", Scores is null ? null : AefWire.Array(Scores, s => s.ToJson()));
        json.Put("verdictRule", VerdictRule?.ToJson());
        json.Put("annotator", Annotator?.ToJson());
        json.Put("reasoning", Reasoning is { } r ? new JsonObject { ["blob"] = r.Digest, ["bytes"] = AefWire.Integer(r.Size, "reasoning.bytes") } : null);
        json.Put("uncertainty", Uncertainty?.ToJson());
        json.Put("usage", Usage is null ? null : AefWire.Array(Usage, u => u.ToJson()));
        json.Put("traceLink", TraceLink?.ToJson());
        json.Put("aggregation", Aggregation?.ToJson());
        json.Put("component", Component?.ToJson());
        json.Put("evidence", Evidence is null ? null : AefWire.Strings(Evidence));
        json.Put("ext", AefWire.Ext(Ext));
        json.Put("lane", Lane);
        json.Put("severity", Severity is { } s ? AefWire.Node(s) : null);
        json.Put("durationMs", DurationMs);
        json.Put("turns", Turns);
        json.Put("attack", Attack?.ToJson());
        json.Put("startedAt", StartedAt is { } started ? AefWire.Time(started) : null);
        json.Put("endedAt", EndedAt is { } ended ? AefWire.Time(ended) : null);
        return json;
    }
}

/// <summary>A result's <c>evaluator</c> ([RES-11]).</summary>
/// <param name="Id">The evaluator's id, as the producer names it.</param>
/// <param name="Version">Its version, when known.</param>
public sealed record AefEvaluator(string Id, string? Version = null)
{
    internal JsonObject ToJson()
    {
        var json = new JsonObject { ["id"] = Id };
        json.Put("version", Version);
        return json;
    }
}

/// <summary>One of a result's <c>scores</c> ([RES-10]).</summary>
public sealed record AefScore
{
    /// <summary>The metric, by its id in metrics.json ([SUM-1]).</summary>
    public required string Metric { get; init; }

    /// <summary>The score, on the metric's scale.</summary>
    public required double Value { get; init; }

    /// <summary>The score normalised to 0–1.</summary>
    public double? Normalized { get; init; }

    /// <summary>A categorical value (an OpenTelemetry score label, a grader's class).</summary>
    public string? Label { get; init; }

    internal JsonObject ToJson()
    {
        var json = new JsonObject { ["metric"] = Metric, ["value"] = AefWire.Number(Value, "scores.value") };
        json.Put("normalized", Normalized);
        json.Put("label", Label);
        return json;
    }
}

/// <summary>A result's <c>verdictRule</c> ([RES-7]: never evaluated by a reader).</summary>
/// <param name="Expr">A human-readable description of the rule.</param>
/// <param name="Threshold">The threshold the rule compared the score with.</param>
/// <param name="Source">Where the threshold came from.</param>
public sealed record AefVerdictRule(string Expr, double? Threshold = null, string? Source = null)
{
    internal JsonObject ToJson()
    {
        var json = new JsonObject { ["expr"] = Expr };
        json.Put("threshold", Threshold);
        json.Put("source", Source);
        return json;
    }
}

/// <summary>A result's <c>annotator</c> ([RES-10]).</summary>
public sealed record AefAnnotator
{
    /// <summary>Who graded.</summary>
    public required AefAnnotatorKind Kind { get; init; }

    /// <summary>The model that graded, when one did.</summary>
    public string? Model { get; init; }

    /// <summary>The SHA-256 of the grader's prompt (<c>sha256:</c> and 64 hex); refused under <c>contentCapture: off</c> ([RUN-11]).</summary>
    public string? PromptHash { get; init; }

    /// <summary>The SHA-256 of the rubric (<c>sha256:</c> and 64 hex).</summary>
    public string? RubricDigest { get; init; }

    /// <summary>For a panel: how many agreed, of how many.</summary>
    public AefPanel? Panel { get; init; }

    internal JsonObject ToJson()
    {
        var json = new JsonObject { ["kind"] = AefWire.Node(Kind) };
        json.Put("model", Model);
        json.Put("promptHash", PromptHash);
        json.Put("rubricDigest", RubricDigest);
        json.Put("panel", Panel is { } p ? new JsonObject { ["agree"] = AefWire.Integer(p.Agree, "panel.agree"), ["of"] = AefWire.Integer(p.Of, "panel.of") } : null);
        return json;
    }
}

/// <summary>An annotator's <c>panel</c>: <paramref name="Agree"/> members agreed (never more than <paramref name="Of"/>, §3.9 <c>annotator</c>).</summary>
/// <param name="Agree">How many members agreed.</param>
/// <param name="Of">How many members the panel had.</param>
public sealed record AefPanel(long Agree, long Of);

/// <summary>A result's <c>uncertainty</c> ([RES-10]).</summary>
/// <param name="Stderr">The standard error of the score.</param>
/// <param name="Ci">An interval around it.</param>
public sealed record AefUncertainty(double? Stderr = null, AefInterval? Ci = null)
{
    internal JsonObject ToJson()
    {
        var json = new JsonObject();
        json.Put("stderr", Stderr);
        json.Put("ci", Ci?.ToJson());
        return json;
    }
}

/// <summary>An interval at a confidence level (§3.9 <c>interval</c>: <paramref name="Low"/> ≤ <paramref name="High"/>).</summary>
/// <param name="Low">The lower bound.</param>
/// <param name="High">The upper bound.</param>
/// <param name="Level">The confidence level, strictly between 0 and 1.</param>
/// <param name="Method">How it was computed.</param>
public sealed record AefInterval(double Low, double High, double Level, string? Method = null)
{
    internal JsonObject ToJson()
    {
        var json = new JsonObject
        {
            ["low"] = AefWire.Number(Low, "ci.low"),
            ["high"] = AefWire.Number(High, "ci.high"),
            ["level"] = AefWire.Number(Level, "ci.level"),
        };
        json.Put("method", Method);
        return json;
    }
}

/// <summary>One party's usage: of a result line ([RES-10]) or of the run, in summary.json ([SUM-7]).</summary>
public sealed record AefUsage
{
    /// <summary>Whose usage this is: no role and model twice in a line ([RES-10]) or in summary.json ([SUM-9]).</summary>
    public required AefUsageRole Role { get; init; }

    /// <summary><c>gen_ai.usage.input_tokens</c>.</summary>
    public long? InputTokens { get; init; }

    /// <summary><c>gen_ai.usage.output_tokens</c>.</summary>
    public long? OutputTokens { get; init; }

    /// <summary><c>gen_ai.usage.cache_read.input_tokens</c> (included in input tokens).</summary>
    public long? CacheReadInputTokens { get; init; }

    /// <summary><c>gen_ai.usage.cache_write.input_tokens</c> (included in input tokens).</summary>
    public long? CacheWriteInputTokens { get; init; }

    /// <summary><c>gen_ai.usage.reasoning.output_tokens</c> (included in output tokens).</summary>
    public long? ReasoningOutputTokens { get; init; }

    /// <summary>The cost, in US dollars.</summary>
    public double? CostUsd { get; init; }

    /// <summary>Where the cost figure came from.</summary>
    public string? CostSource { get; init; }

    /// <summary>The model: no two entries of a result line ([RES-10]) or of summary.json ([SUM-9]) with one role and model (an absent model is a value of its own).</summary>
    public string? Model { get; init; }

    internal JsonObject ToJson()
    {
        var json = new JsonObject();
        json.Put("gen_ai.usage.input_tokens", InputTokens);
        json.Put("gen_ai.usage.output_tokens", OutputTokens);
        json.Put("costUsd", CostUsd);
        json.Put("costSource", CostSource);
        json["role"] = AefWire.Node(Role);
        json.Put("gen_ai.usage.cache_read.input_tokens", CacheReadInputTokens);
        json.Put("gen_ai.usage.cache_write.input_tokens", CacheWriteInputTokens);
        json.Put("gen_ai.usage.reasoning.output_tokens", ReasoningOutputTokens);
        json.Put("model", Model);
        return json;
    }
}

/// <summary>A result's <c>traceLink</c> ([RES-10]): lower-case hex ids (§3.9 <c>trace-link</c>: a writer writes lower case).</summary>
/// <param name="TraceId">32 lower-case hex characters.</param>
/// <param name="SpanId">16 lower-case hex characters; without it the link names the trace.</param>
public sealed record AefTraceLink(string TraceId, string? SpanId = null)
{
    internal JsonObject ToJson()
    {
        var json = new JsonObject { ["traceId"] = TraceId };
        json.Put("spanId", SpanId);
        return json;
    }
}

/// <summary>A composite's <c>aggregation</c> ([RES-5], [RES-6]: descriptive).</summary>
public sealed record AefAggregation
{
    /// <summary>The strategy the producer used, for display.</summary>
    public required AefAggregationStrategy Strategy { get; init; }

    /// <summary>The threshold the composite score was compared with.</summary>
    public double? Threshold { get; init; }

    /// <summary>The composite score the strategy gave.</summary>
    public double? Score { get; init; }

    /// <summary>Which branch of the producer's verdict rules decided the state.</summary>
    public required AefRulePath RulePath { get; init; }

    /// <summary>How many children were measured; at most <see cref="Total"/>.</summary>
    public required long Measured { get; init; }

    /// <summary>How many children the node has.</summary>
    public required long Total { get; init; }

    /// <summary>The share of children the producer's rules require to be measured, 0–1.</summary>
    public double? MinimumMeasuredShare { get; init; }

    /// <summary>
    /// The children not measured, by state: they add up to <see cref="Total"/> − <see cref="Measured"/> ([RES-6]). The
    /// writer requires it when <see cref="Measured"/> is below <see cref="Total"/>.
    /// </summary>
    public AefUnmeasured? Unmeasured { get; init; }

    /// <summary>The result ids of the children that decided the verdict (<see cref="AefRunWriter.ResultIdOf"/> gives a child's id before it is added).</summary>
    public IReadOnlyList<string>? Decisive { get; init; }

    internal JsonObject ToJson()
    {
        var json = new JsonObject { ["strategy"] = AefWire.Node(Strategy) };
        json.Put("threshold", Threshold);
        json.Put("score", Score);
        json["rulePath"] = AefWire.Node(RulePath);
        json["measured"] = AefWire.Integer(Measured, "aggregation.measured");
        json["total"] = AefWire.Integer(Total, "aggregation.total");
        json.Put("minimumMeasuredShare", MinimumMeasuredShare);
        json.Put("unmeasured", Unmeasured?.ToJson());
        json.Put("decisive", Decisive is null ? null : AefWire.Strings(Decisive));
        return json;
    }
}

/// <summary>An aggregation's <c>unmeasured</c> counts, per typed absence; a count not given is not written.</summary>
public sealed record AefUnmeasured
{
    /// <summary>Children in <c>not_measured</c>.</summary>
    public long? NotMeasured { get; init; }

    /// <summary>Children in <c>not_applicable</c>.</summary>
    public long? NotApplicable { get; init; }

    /// <summary>Children in <c>skipped</c>.</summary>
    public long? Skipped { get; init; }

    /// <summary>Children in <c>error</c>.</summary>
    public long? Error { get; init; }

    /// <summary>Children in <c>pending</c> (only in an open run).</summary>
    public long? Pending { get; init; }

    /// <summary>The counts given, added up (a count not given is 0).</summary>
    public long Total => (NotMeasured ?? 0) + (NotApplicable ?? 0) + (Skipped ?? 0) + (Error ?? 0) + (Pending ?? 0);

    internal JsonObject ToJson()
    {
        var json = new JsonObject();
        json.Put("not_measured", NotMeasured);
        json.Put("not_applicable", NotApplicable);
        json.Put("skipped", Skipped);
        json.Put("error", Error);
        json.Put("pending", Pending);
        return json;
    }
}

/// <summary>A child's <c>component</c> ([RES-5]).</summary>
/// <param name="Weight">Its weight in the parent's strategy, 0 or more.</param>
/// <param name="Required">Whether the parent's rules require it.</param>
public sealed record AefComponent(double Weight, bool Required)
{
    internal JsonObject ToJson() => new() { ["weight"] = AefWire.Number(Weight, "component.weight"), ["required"] = Required };
}

/// <summary>A rollup line's <c>trials</c> ([RES-8]): the case's result at its path over <paramref name="N"/> trials.</summary>
/// <param name="N">How many trials the case had, 1–1000.</param>
/// <param name="Passed">How many passed; never more than <paramref name="N"/> (§3.9 <c>trials</c>).</param>
/// <param name="Aggregation">How the trials were combined.</param>
/// <param name="Agree">Whether they agreed (false: the case is flaky).</param>
/// <param name="K">k, for <see cref="AefTrialAggregation.PassAtK"/>.</param>
public sealed record AefTrials(int N, int Passed, AefTrialAggregation Aggregation, bool Agree, long? K = null)
{
    internal JsonObject ToJson()
    {
        var json = new JsonObject { ["n"] = N, ["passed"] = Passed, ["aggregation"] = AefWire.Node(Aggregation), ["agree"] = Agree };
        json.Put("k", K);
        return json;
    }
}

/// <summary>A result's <c>attack</c> ([RES-10]).</summary>
public sealed record AefAttack
{
    /// <summary>The technique, as the producer names it.</summary>
    public required string Technique { get; init; }

    /// <summary>Its ids in public taxonomies, none twice.</summary>
    public IReadOnlyList<AefTaxonomyId>? Taxonomy { get; init; }

    /// <summary>Whether it succeeded (not written when not decided). An attack that succeeded is not a pass (§3.9 <c>attack</c>).</summary>
    public bool? Success { get; init; }

    internal JsonObject ToJson()
    {
        var json = new JsonObject { ["technique"] = Technique };
        json.Put("taxonomy", Taxonomy is null ? null : AefWire.Array(Taxonomy, t => new JsonObject { ["scheme"] = AefWire.Node(t.Scheme), ["id"] = t.Id }));
        json.Put("success", Success);
        return json;
    }
}

/// <summary>A technique's id in a public taxonomy.</summary>
/// <param name="Scheme">The taxonomy.</param>
/// <param name="Id">The id there, such as <c>LLM01</c>.</param>
public sealed record AefTaxonomyId(AefTaxonomyScheme Scheme, string Id);

/// <summary>
/// A result line added to a run: its id ([RES-4]) and what made it. Children are added under it with
/// <see cref="AddChild"/>.
/// </summary>
public sealed class AefResultHandle
{
    internal AefResultHandle(AefRunWriter writer, string resultId, string caseId, string path, int? trial, string? parentResultId)
    {
        Writer = writer;
        ResultId = resultId;
        CaseId = caseId;
        Path = path;
        Trial = trial;
        ParentResultId = parentResultId;
    }

    /// <summary>The line's <c>resultId</c>.</summary>
    public string ResultId { get; }

    /// <summary>The line's <c>caseId</c>.</summary>
    public string CaseId { get; }

    /// <summary>The line's <c>path</c>.</summary>
    public string Path { get; }

    /// <summary>The line's <c>trial</c>, or null.</summary>
    public int? Trial { get; }

    /// <summary>The parent's <c>resultId</c>, or null on a root.</summary>
    public string? ParentResultId { get; }

    internal AefRunWriter Writer { get; }

    /// <summary>Adds a child of this node (<see cref="AefRunWriter.AddResult"/> with this handle as the parent).</summary>
    public AefResultHandle AddChild(AefResult child) => Writer.AddResult(child, this);
}

#endregion

#region Blobs and evidence.ndjson

/// <summary>
/// A blob of a run ([EVD-3]): <c>blobs/sha256/&lt;ab&gt;/&lt;hex&gt;</c>, named by the SHA-256 of its bytes. Returned by
/// <see cref="AefRunWriter.PutBlob(ReadOnlySpan{byte})"/>; the writer refuses one that is not in its run.
/// </summary>
/// <param name="Sha256">The SHA-256 of its bytes, 64 lower-case hex characters (its file name).</param>
/// <param name="Size">Its size in bytes.</param>
public sealed record AefBlob(string Sha256, long Size)
{
    /// <summary>The digest as a blob link or a reasoning names it ([EVD-2], [ENC-11]): <c>sha256:</c> and the hex.</summary>
    public string Digest => "sha256:" + Sha256;

    /// <summary>Its path in the run.</summary>
    public string Path => AefRunFolder.BlobPath(Sha256);
}

/// <summary>An evidence record's <c>link</c> ([EVD-1]): exactly one of a blob of the run, a span, or a URI outside the run.</summary>
public sealed record AefEvidenceLink
{
    private AefEvidenceLink(AefBlob? blob, string? traceId, string? spanId, string? uri, string? uriDigest)
    {
        Blob = blob;
        TraceId = traceId;
        SpanId = spanId;
        Uri = uri;
        UriDigest = uriDigest;
    }

    /// <summary>The blob, for a blob link (its digest is the record's <c>digest</c>, [EVD-2]).</summary>
    public AefBlob? Blob { get; }

    /// <summary>The trace id, for a span link (32 lower-case hex).</summary>
    public string? TraceId { get; }

    /// <summary>The span id, for a span link (16 lower-case hex).</summary>
    public string? SpanId { get; }

    /// <summary>The URI, for a link outside the run.</summary>
    public string? Uri { get; }

    /// <summary>For a URI link, the SHA-256 of the bytes it served when the record was written (<c>sha256:</c> and 64 hex), or null ([EVD-2]).</summary>
    public string? UriDigest { get; }

    /// <summary>A link to a blob of the run.</summary>
    public static AefEvidenceLink ToBlob(AefBlob blob) => new(blob ?? throw new ArgumentNullException(nameof(blob)), null, null, null, null);

    /// <summary>A link to a span.</summary>
    public static AefEvidenceLink ToSpan(string traceId, string spanId) =>
        new(null, traceId ?? throw new ArgumentNullException(nameof(traceId)), spanId ?? throw new ArgumentNullException(nameof(spanId)), null, null);

    /// <summary>A link to a URI outside the run, with the SHA-256 of what it served (<c>sha256:</c> and 64 hex) when known.</summary>
    public static AefEvidenceLink ToUri(string uri, string? digest = null) => new(null, null, null, uri ?? throw new ArgumentNullException(nameof(uri)), digest);

    internal JsonObject ToJson()
    {
        var json = new JsonObject();
        json.Put("blob", Blob?.Digest);
        json.Put("traceId", TraceId);
        json.Put("spanId", SpanId);
        json.Put("uri", Uri);
        return json;
    }

    // [EVD-2]: a blob link's digest is the blob's; a URI link's is optional; a span link has none.
    internal string? Digest => Blob?.Digest ?? UriDigest;
}

/// <summary>One line of evidence.ndjson ([EVD-1]).</summary>
public sealed record AefEvidence
{
    /// <summary>The record's id: <c>E-</c> and 1–64 letters, digits, <c>.</c>, <c>_</c> or <c>-</c>; unique in the run.</summary>
    public required string EvidenceId { get; init; }

    /// <summary>What the record is; a content kind is refused under <c>contentCapture: off</c> ([RUN-11]).</summary>
    public required AefEvidenceKind Kind { get; init; }

    /// <summary>Where the evidence is.</summary>
    public required AefEvidenceLink Link { get; init; }

    /// <summary>A description.</summary>
    public string? Description { get; init; }

    /// <summary>Producer extensions ([ENC-19]).</summary>
    public JsonObject? Ext { get; init; }

    internal JsonObject ToJson()
    {
        var json = new JsonObject
        {
            ["schemaVersion"] = AefRunWriter.SchemaVersion,
            ["evidenceId"] = EvidenceId,
            ["kind"] = AefWire.Node(Kind),
        };
        json.Put("digest", Link.Digest);
        json["link"] = Link.ToJson();
        json.Put("description", Description);
        json.Put("ext", AefWire.Ext(Ext));
        return json;
    }
}

#endregion

#region gates.ndjson

/// <summary>One line of gates.ndjson: a decision the producer took when the run closed ([GATE-1], [GATE-2]).</summary>
public sealed record AefGateDecision
{
    /// <summary>The gate, as a typed reference (<c>gate:ci</c>).</summary>
    public required string GateId { get; init; }

    /// <summary>The decision's id.</summary>
    public required string DecisionId { get; init; }

    /// <summary>The rule, for display.</summary>
    public required AefGateRule Rule { get; init; }

    /// <summary>What the decision read.</summary>
    public required AefGateInputs Inputs { get; init; }

    /// <summary>Whether a comparison it needed was shown comparable; <c>incomparable</c> never yields <c>ship</c> ([GATE-2]).</summary>
    public required AefComparability Comparability { get; init; }

    /// <summary>The outcome.</summary>
    public required AefGateOutcome Outcome { get; init; }

    /// <summary>The exit code, 0–255.</summary>
    public int? ExitCode { get; init; }

    /// <summary>The result ids that decided it: lines of this run (§3.9 <c>gate</c>).</summary>
    public IReadOnlyList<string>? Decisive { get; init; }

    /// <summary>When it was decided, at the precision given ([ENC-8]).</summary>
    public required AefTime DecidedAt { get; init; }

    /// <summary>Producer extensions ([ENC-19]).</summary>
    public JsonObject? Ext { get; init; }

    internal JsonObject ToJson()
    {
        var json = new JsonObject
        {
            ["schemaVersion"] = AefRunWriter.SchemaVersion,
            ["gateId"] = GateId,
            ["decisionId"] = DecisionId,
            ["rule"] = Rule.ToJson(),
            ["inputs"] = Inputs.ToJson(),
            ["comparability"] = AefWire.Node(Comparability),
            ["outcome"] = AefWire.Node(Outcome),
        };
        json.Put("exitCode", ExitCode);
        json.Put("decisive", Decisive is null ? null : AefWire.Strings(Decisive));
        json["decidedAt"] = AefWire.Time(DecidedAt);
        json.Put("ext", AefWire.Ext(Ext));
        return json;
    }
}

/// <summary>A gate decision's <c>rule</c>.</summary>
/// <param name="Strategy">A strategy name, for display.</param>
/// <param name="Inputs">The rule's inputs, as the producer names them.</param>
public sealed record AefGateRule(string Strategy, IReadOnlyList<string>? Inputs = null)
{
    internal JsonObject ToJson()
    {
        var json = new JsonObject { ["strategy"] = Strategy };
        json.Put("inputs", Inputs is null ? null : AefWire.Strings(Inputs));
        return json;
    }
}

/// <summary>A gate decision's <c>inputs</c>.</summary>
public sealed record AefGateInputs
{
    /// <summary>The result ids it read: lines of this run (§3.9 <c>gate</c>).</summary>
    public IReadOnlyList<string>? Results { get; init; }

    /// <summary>The requirement ids it read.</summary>
    public IReadOnlyList<string>? Requirements { get; init; }

    /// <summary>A baseline, as a run reference with its run hash.</summary>
    public AefRunPointer? BaselineRun { get; init; }

    internal JsonObject ToJson()
    {
        var json = new JsonObject();
        json.Put("results", Results is null ? null : AefWire.Strings(Results));
        json.Put("requirements", Requirements is null ? null : AefWire.Strings(Requirements));
        json.Put("baselineRun", BaselineRun is { } b ? new JsonObject { ["runId"] = b.RunId, ["runHash"] = b.RunHash } : null);
        return json;
    }
}

/// <summary>One sealed run, exactly: its id and its run hash ([SEAL-4]).</summary>
/// <param name="RunId">The run's runId.</param>
/// <param name="RunHash">Its run hash, 64 lower-case hex characters.</param>
public sealed record AefRunPointer(string RunId, string RunHash);

#endregion

#region metrics.json

/// <summary>A metric of metrics.json ([SUM-1]).</summary>
public sealed record AefMetric
{
    /// <summary>The metric's id, unique in metrics.json (§3.9 <c>metric</c>).</summary>
    public required string Id { get; init; }

    /// <summary>Its kind.</summary>
    public required AefMetricKind Kind { get; init; }

    /// <summary>Which way is better.</summary>
    public required AefMetricDirection Direction { get; init; }

    /// <summary>Its scale.</summary>
    public required AefScale Scale { get; init; }

    /// <summary>Its unit.</summary>
    public string? Unit { get; init; }

    /// <summary>A description.</summary>
    public string? Description { get; init; }

    internal JsonObject ToJson()
    {
        var json = new JsonObject
        {
            ["id"] = Id,
            ["kind"] = AefWire.Node(Kind),
            ["direction"] = AefWire.Node(Direction),
            ["scale"] = Scale.ToJson(),
        };
        json.Put("unit", Unit);
        json.Put("description", Description);
        return json;
    }
}

/// <summary>A metric's <c>scale</c>: <see cref="Unbounded"/>, or <see cref="Between"/> a minimum and a maximum (min ≤ max).</summary>
public sealed record AefScale
{
    private AefScale(double? min, double? max)
    {
        Min = min;
        Max = max;
    }

    /// <summary><c>"unbounded"</c>.</summary>
    public static AefScale Unbounded { get; } = new(null, null);

    /// <summary>The minimum, or null when unbounded.</summary>
    public double? Min { get; }

    /// <summary>The maximum, or null when unbounded.</summary>
    public double? Max { get; }

    /// <summary><c>{"min", "max"}</c>.</summary>
    public static AefScale Between(double min, double max) => new(min, max);

    internal JsonNode ToJson() => Min is { } min && Max is { } max
        ? new JsonObject { ["min"] = AefWire.Number(min, "scale.min"), ["max"] = AefWire.Number(max, "scale.max") }
        : JsonValue.Create("unbounded");
}

#endregion

#region summary.json

/// <summary>
/// What summary.json summarises ([SUM-2]–[SUM-9]): its lanes and, per lane, the entries (metric and path) the producer
/// reports, the run's cost and usage. The writer computes each entry's figures from results.ndjson at close
/// (<see cref="AefSummaryCalculator"/>).
/// </summary>
public sealed record AefSummary
{
    /// <summary>The lanes, each named once.</summary>
    public required IReadOnlyList<AefSummaryLane> Lanes { get; init; }

    /// <summary>The run's total cost ([SUM-7]).</summary>
    public AefCost? Cost { get; init; }

    /// <summary>The run's total usage, one entry per role and model ([SUM-7], [SUM-9]).</summary>
    public IReadOnlyList<AefUsage>? Usage { get; init; }

    /// <summary>Producer extensions ([ENC-19]).</summary>
    public JsonObject? Ext { get; init; }
}

/// <summary>A lane of summary.json: a named group of cases and the entries reported for it.</summary>
/// <param name="Lane">The lane's name (an id).</param>
/// <param name="Entries">Its entries: no metric and path twice ([SUM-9]).</param>
public sealed record AefSummaryLane(string Lane, IReadOnlyList<AefSummaryEntry> Entries);

/// <summary>
/// One summary entry the producer reports: a metric at a path. Its figures (<c>N</c>, <c>n</c>, <c>notMeasured</c>,
/// <c>sum</c>, <c>sumSq</c> and, unless the aggregate method is the producer's, <c>value</c>) are computed by the writer
/// ([SUM-3]–[SUM-8]); the producer gives its verdict on them through <see cref="Decide"/>.
/// </summary>
public sealed record AefSummaryEntry
{
    /// <summary>The metric, declared in metrics.json.</summary>
    public required string Metric { get; init; }

    /// <summary>The result path the entry summarises.</summary>
    public required string Path { get; init; }

    /// <summary>An aggregate instead of the mean ([SUM-8]).</summary>
    public AefAggregate? Aggregate { get; init; }

    /// <summary>The rule the producer's verdict applied, for display.</summary>
    public string? Rule { get; init; }

    /// <summary>
    /// The producer's verdict on the computed figures ([SUM-6]), with its <c>stderr</c> and <c>ci</c> over the same values
    /// ([SUM-5]) and, for an aggregate method AEF does not define, its <c>value</c> ([SUM-8]). Not called when <c>n</c>
    /// is 0 (the verdict is then <c>not_measured</c>). Null: the verdict is <c>scored</c> (no rule applied).
    /// </summary>
    public Func<AefSummaryFigures, AefSummaryDecision>? Decide { get; init; }
}

/// <summary>A summary entry's <c>aggregate</c> ([SUM-8]).</summary>
/// <param name="Method"><c>median</c>, <c>min</c>, <c>max</c> (AEF's), or the producer's own (pass@k, f1, …).</param>
/// <param name="K">k, for a method that takes one.</param>
public sealed record AefAggregate(string Method, long? K = null);

/// <summary>The producer's verdict on one summary entry's figures ([SUM-6]).</summary>
/// <param name="Verdict">The verdict; <c>not_measured</c> only when <c>n</c> is 0, which the writer writes itself.</param>
/// <param name="Value">For an aggregate method AEF does not define: its value ([SUM-8]); otherwise null (the writer computes it).</param>
/// <param name="Stderr">The standard error, over the measured values.</param>
/// <param name="Ci">An interval, over the measured values.</param>
public sealed record AefSummaryDecision(AefSummaryVerdict Verdict, double? Value = null, double? Stderr = null, AefInterval? Ci = null);

/// <summary>summary.json <c>cost</c> ([SUM-7]).</summary>
/// <param name="TotalUsd">The run's total cost, in US dollars.</param>
/// <param name="Source">Where the figure came from.</param>
public sealed record AefCost(double TotalUsd, string? Source = null);

#endregion
