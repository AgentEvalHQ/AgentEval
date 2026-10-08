# AEF 1.0: run.json

*Generated from [`schemas/writer/run.schema.json`](../schemas/writer/run.schema.json) by `tools/gen_reference.py`. Do not edit: edit the schema.*

The header of one run folder. Identity lives here, not in the folder path.

| Field | Type | Required | Bounds | Description |
|---|---|---|---|---|
| `schemaVersion` | [schemaVersion](common.md#schemaversion) | yes |  | The AEF version this document follows, MAJOR.MINOR: 1.0 for AEF 1.0 (VER-1). |
| `runId` | [id](common.md#id) | yes |  | The run's id. A producer never reuses it for a different run; identity lives here, never in the folder path (RUN-1, RUN-13). |
| `status` | one of `"running"`, `"completed"`, `"aborted"` | yes |  | running while the producer writes; completed or aborted when it closes. There is no 'sealed' status: sealed is a fact about seal.json, never a field. |
| `abortReason` | string |  | ≥ 1 chars; ≤ 2048 chars | Why the run was aborted. Required when status is aborted (RUN-5). |
| `producer` | object | yes |  | The tool that wrote the run: its name, its version and, optionally, the runtime it ran on. |
| `producer.name` | string | yes | ≥ 1 chars; ≤ 128 chars | The producer's name. Part of the producer comparability axis (LANE-6). |
| `producer.version` | [exactVersion](common.md#exactversion) | yes |  | The producer's exact version, compared byte for byte. Part of the producer comparability axis (ENC-10, LANE-6). |
| `producer.runtime` | object |  |  | The runtime the producer ran on. |
| `producer.runtime.name` | string | yes | ≥ 1 chars; ≤ 64 chars | The runtime's name. |
| `producer.runtime.version` | string |  | ≤ 64 chars | The runtime's version. |
| `subject` | object | yes |  | What was evaluated. |
| `subject.ref` | [ref](common.md#ref) | yes |  | The evaluated subject, as a typed reference. Compared by the subject comparability axis (RUN-6, LANE-6). |
| `subject.kind` | one of `"agent"`, `"workflow"`, `"model"`, `"endpoint"`, `"mcp-server"`, `"other"` | yes |  | What the subject is: agent, workflow, model, endpoint, mcp-server (an MCP server) or other. A reader shows a kind it does not know as written and treats it as other (RUN-6, VER-3). |
| `subject.version` | [exactVersion](common.md#exactversion) |  |  | The subject's exact version. Required when the run serves a checkpoint: a run without it is not eligible for a lane (RUN-6, LANE-1). |
| `subject.environment` | string |  | ≤ 64 chars | The environment the subject ran in, as the producer names it. |
| `subject.externalIds` | object |  |  | The subject's ids in other systems, by system name; each value is a string or null. |
| `subject.telemetry` | object |  |  | The keys that join this subject to OpenTelemetry (gen_ai.agent.id, service.name). |
| `subject.telemetry.agentId` | string |  | ≤ 256 chars | The subject's gen_ai.agent.id in its OpenTelemetry traces. |
| `subject.telemetry.serviceName` | string |  | ≤ 256 chars | The subject's service.name in its OpenTelemetry traces. |
| `deployment` | object |  |  | Where the subject ran (RUN-6). |
| `deployment.ref` | [ref](common.md#ref) | yes |  | The deployment, as a typed reference. Compared by the deployment comparability axis (LANE-6). |
| `deployment.environment` | string |  | ≤ 64 chars | The deployment's environment, as the producer names it. |
| `deployment.endpoint` | string |  | ≤ 2048 chars; pattern `^[a-z][a-z0-9+.-]*://[!"$-.0->A-~]+(/[!"$->@-~]*)?$` | Where the subject was reached: scheme, host and path only. No user information, no query string and no fragment, where credentials hide (RUN-10). |
| `deployment.externalIds` | object |  |  | The deployment's ids in other systems, by system name; each value is a string or null. |
| `suite` | object |  |  | The test cases run. A frozen suite's digest identifies its exact content. |
| `suite.ref` | [ref](common.md#ref) | yes |  | The suite, as a typed reference. With version, compared by the suite comparability axis (RUN-8, LANE-6). |
| `suite.version` | [exactVersion](common.md#exactversion) | yes |  | The suite's exact version (RUN-8). |
| `suite.digest` | [sha256Uri](common.md#sha256uri) |  |  | The SHA-256 of the suite's content, when the content is frozen. Compared by the suite-content comparability axis (RUN-8, LANE-6). |
| `suite.frozen` | boolean |  |  | True when the suite's content is frozen, so that digest identifies it (RUN-8). |
| `suite.executionPolicy` | object |  |  | How many trials each case had, and how they were combined (RUN-8). |
| `suite.executionPolicy.trialsPerCase` | integer | yes | ≥ 1; ≤ 1000 | Trials per case, from 1 to 1000. With more than one, each trial has its own lines and the case has a rollup line (RES-8). |
| `suite.executionPolicy.requirePasses` | integer |  | ≥ 1; ≤ 1000 | How many of a case's trials must pass for the case to pass, from 1 to 1000. |
| `suite.executionPolicy.aggregation` | one of `"MajorityVote"`, `"AllPass"`, `"AnyPass"`, `"Mean"`, `"Median"`, `"Max"`, `"PassAtK"` |  |  | How a case's trials are combined (RES-8): the same vocabulary as a rollup line's trials.aggregation. |
| `suite.executionPolicy.k` | integer |  | ≥ 1; ≤ 9007199254740991 | k, for PassAtK. |
| `judges` | array of object |  | ≤ 64 items | The models that graded results, in order. The judges and rubrics comparability axes compare this list in order (RUN-9, LANE-6). |
| `judges[].model` | string | yes | ≥ 1 chars; ≤ 256 chars | The judge model, as its provider names it. Part of the judges comparability axis (RUN-9, LANE-6). |
| `judges[].provider` | string |  | ≤ 128 chars | Who serves the judge model (RUN-9). |
| `judges[].mode` | one of `"single"`, `"panel"`, `"primary"`, `"shadow"`, `"other"` |  |  | How the judge was used: single (alone), panel (one judge of a panel), primary (its grade counts) or shadow (it grades alongside, without counting). A reader shows a mode it does not know as written (RUN-9, VER-3). |
| `judges[].panelSize` | integer |  | ≥ 1; ≤ 64 | How many judges the panel had, when mode is panel. |
| `judges[].rubricDigest` | [sha256Uri](common.md#sha256uri) |  |  | The SHA-256 of the rubric the judge graded with. Part of the rubrics comparability axis (RUN-9, LANE-6). |
| `judges[].calibration` | object |  |  | The producer's claim about how far this judge agreed with labelled cases before the run (spec 03, RUN-9). |
| `judges[].calibration.labelSet` | [ref](common.md#ref) | yes |  | The labelled cases, as a typed reference. |
| `judges[].calibration.n` | integer | yes | ≥ 0; ≤ 9007199254740991 | Labelled cases the judge decided. |
| `judges[].calibration.accuracy` | number |  | ≥ 0; ≤ 1 | Share of decided cases where the judge agreed with the label. |
| `judges[].calibration.kappa` | number |  | ≥ -1; ≤ 1 | Cohen's kappa between the judge and the labels. |
| `judges[].calibration.dangerousErrors` | integer |  | ≥ 0; ≤ 9007199254740991 | Labelled failures the judge passed. |
| `judges[].calibration.measuredAt` | [timestamp](common.md#timestamp) | yes |  | When the calibration was measured, before this run (RUN-9). |
| `config` | object |  |  | The run's configuration as the producer applied it. thresholds maps a metric (or result path) to the rule its verdict used. |
| `config.thresholds` | object |  |  | Per metric or result path, the rule the producer's verdict used, recorded for display. |
| `startedAt` | [timestamp](common.md#timestamp) | yes |  | When the run started (RUN-5). |
| `endedAt` | [timestamp](common.md#timestamp) |  |  | When the run closed: required for a completed or aborted run, absent while it is running, and never earlier than startedAt (RUN-5). |
| `otel` | object |  |  | The OpenTelemetry conventions the run's traces follow (RUN-14). |
| `otel.semconvVersion` | string |  | pattern `^[0-9]+\.[0-9]+(\.[0-9]+)?$` | The version of the OpenTelemetry semantic conventions the traces use, as MAJOR.MINOR or MAJOR.MINOR.PATCH (RUN-14). |
| `otel.dialects` | array of string |  | ≤ 16 items | The semantic-convention namespaces the traces use, such as gen_ai. |
| `otel.schemaUrls` | array of [uri](common.md#uri) |  | ≤ 16 items; unique | The OpenTelemetry schema URLs the traces and logs follow, such as the GenAI registry's (RUN-14). |
| `contentCapture` | one of `"off"`, `"on"` |  |  | What text the run keeps: on (prompts, responses, tool arguments and judge reasoning may be kept, in blobs) or off (none of it, and no digest of it). A producer SHOULD write it; a reader treats a run without it as on (RUN-11). |
| `costPolicy` | object |  |  | The spending limit and the price table the producer used to estimate cost. |
| `costPolicy.maxUsd` | number |  | ≥ 0 | The spending limit, in US dollars. |
| `costPolicy.priceTable` | string |  | ≤ 64 chars | The price table the producer used to estimate cost, by name. |
| `ext` | [ext](common.md#ext) |  |  | Producer extensions, named reverse-DNS or with the producer's prefix. A reader ignores what it does not know; never holds a credential (ENC-19, RUN-10). |
| `provenance` | object |  |  | Written by a runner into every run it produces: the plan (by id and digest), the job and the runner. Sealed with run.json, it ties the run to the job that made it (RUN-12). |
| `provenance.planId` | [id](common.md#id) | yes |  | The id of the plan the run was produced for (RUN-12). |
| `provenance.planDigest` | [sha256Hex](common.md#sha256hex) | yes |  | The SHA-256 of the plan file's exact bytes (RUN-12, PLAN-5). |
| `provenance.jobId` | [id](common.md#id) | yes |  | The job that produced the run (RUN-12). |
| `provenance.runnerId` | [id](common.md#id) | yes |  | The runner that produced the run (RUN-12). |
| `execution` | object | yes |  | How the evaluated target was driven (spec 03, RUN-7). Only live evidence describes how the subject behaves. |
| `execution.targetMode` | one of `"live"`, `"replayed"`, `"scripted"`, `"mocked"` | yes |  | live: the real subject at run time; replayed: its recorded answers played back; scripted: a scripted stand-in; mocked: a stand-in that is not the subject. |
| `execution.stimulus` | one of `"suite"`, `"generated"`, `"external"`, `"other"` |  |  | Where the cases' inputs came from: suite (a fixed suite), generated (at run time, for example by an attacker model), external (another tool's cases), or other (RUN-7). A run converted from another tool's output says so in imported (RUN-15), not here. |
| `imported` | object |  |  | For a run converted from another tool's output: the tool, and every run.json field the converter supplied because the original did not record it (RUN-15). A reader shows those as the converter's claims. |
| `imported.from` | string | yes | ≥ 1 chars; ≤ 256 chars | The original tool and its version, such as inspect_ai 0.3.277. |
| `imported.asserted` | array of string | yes | ≤ 64 items; unique | The run.json fields the converter supplied, as dotted paths (subject.version, execution.targetMode). |

- **Rule:** An open run has not ended.
- **Rule:** Only an aborted run has an abortReason (RUN-5).
