# AEF 1.0: run plan

*Generated from [`schemas/writer/run-plan.schema.json`](../schemas/writer/run-plan.schema.json) by `tools/gen_reference.py`. Do not edit: edit the schema.*

What a runner is asked to evaluate: the exact subject version, the suites and lanes, the limits, what text the runs keep (contentCapture), how they drive the target (targetMode), where and how it runs, and the credentials it needs, as references only (PLAN-1 to PLAN-5).

| Field | Type | Required | Bounds | Description |
|---|---|---|---|---|
| `schemaVersion` | [schemaVersion](common.md#schemaversion) | yes |  | The AEF version this document follows, MAJOR.MINOR: 1.0 for AEF 1.0 (VER-1). |
| `planId` | [id](common.md#id) | yes |  | The plan's id. With the digest of the plan file's bytes, it identifies the plan (PLAN-5). |
| `checkpointId` | [id](common.md#id) |  |  | The checkpoint the plan gathers evidence for, when there is one. |
| `subject` | object | yes |  | Resolved and immutable: 'latest' is resolved before the plan exists. |
| `subject.ref` | [ref](common.md#ref) | yes |  | The subject to evaluate, as a typed reference (PLAN-1). |
| `subject.version` | [exactVersion](common.md#exactversion) | yes |  | The subject's exact version; never latest (PLAN-1). |
| `subject.image` | [sha256Uri](common.md#sha256uri) |  |  | The OCI image manifest digest of an already-built image. |
| `subject.deployment` | [ref](common.md#ref) |  |  | Where the subject is deployed, as a typed reference. Every run the runner produces records it as its deployment.ref (STRM-4). |
| `subject.endpoint` | string |  | ≤ 2048 chars; pattern `^[a-z][a-z0-9+.-]*://[!"$-.0->A-~]+(/[!"$->@-~]*)?$` | Where the subject answers: scheme, host and path only, as run.json's deployment.endpoint. No user information, no query string and no fragment, where credentials hide (PLAN-1, PLAN-4, RUN-10). Every run the runner produces records it as its deployment.endpoint (STRM-4). |
| `subject.repository` | string |  | pattern `^[!-~]{1,256}$` | Where the image is pulled from (registry/name), when it is. |
| `suites` | array of object | yes | ≥ 1 items; ≤ 64 items | The suites to run, each with an exact version and the lane it serves; a runner runs each as one run (PLAN-8). |
| `suites[].ref` | [ref](common.md#ref) | yes |  | The suite, as a typed reference. |
| `suites[].version` | [exactVersion](common.md#exactversion) | yes |  | The suite's exact version. |
| `suites[].digest` | [sha256Uri](common.md#sha256uri) |  |  | The SHA-256 of the suite's content, when it is frozen. A runner checks the content it resolves against it and refuses the job on a mismatch (PLAN-8). |
| `suites[].lane` | [laneName](common.md#lanename) |  |  | The lane the suite serves. |
| `limits` | object | yes |  | Hard limits the runner enforces: it stops before exceeding them, and says so in the event stream. |
| `limits.maxUsd` | number | yes | ≥ 0 | The most the runner may spend, in US dollars. Always set; spend equal to it is within it (PLAN-2, STRM-3). |
| `limits.cases` | integer |  | ≥ 1; ≤ 9007199254740991 | The most cases the runner may complete, when set (PLAN-2). |
| `limits.timeout` | [duration](common.md#duration) |  |  | The longest the job may take, from its job.accepted, as a duration (ENC-9): PT2H, PT45M, P1D, P1DT2H30M (PLAN-2, STRM-3). |
| `contentCapture` | one of `"off"`, `"on"` | yes |  | What text the runs keep: off or on, as run.json's contentCapture (RUN-11). Every run the runner produces carries this value (STRM-4). |
| `targetMode` | one of `"live"`, `"replayed"`, `"scripted"`, `"mocked"` |  |  | How the runs drive the target, as run.json's execution.targetMode (RUN-7): live (the real subject; also when the plan has no targetMode), replayed, scripted or mocked. Every run the runner produces carries this value (STRM-4); a runner that cannot drive the target so refuses the plan (PLAN-7). |
| `isolation` | one of `"process"`, `"container"`, `"remote-zone"` | yes |  | How the run is isolated: process, container (an already-built image, named by digest) or remote-zone (in the network zone given by zone) (PLAN-1). |
| `provider` | [provider](common.md#provider) | yes |  | Where the run executes: local, docker, k8s or ci:<name>. A runner that does not support it, or does not know it, refuses the plan (PLAN-7). |
| `credentialRefs` | array of object |  | ≤ 64 items | What the runner resolves where it runs, and gives to the process the purpose names as an environment variable called name. A plan holds references, never secret values. |
| `credentialRefs[].name` | string | yes | pattern `^[A-Z_][A-Z0-9_]{0,127}$` | The environment variable the value is given as. |
| `credentialRefs[].scheme` | one of `"env"`, `"keychain"`, `"vault"` | yes |  | Where the value lives: env (an environment variable), keychain or vault. A runner refuses a plan with a scheme it does not know (PLAN-3, PLAN-7). |
| `credentialRefs[].path` | string | yes | pattern `^[!-~]{1,512}$` | The variable, keychain entry or vault path: a name, never the value. |
| `credentialRefs[].purpose` | one of `"subject"`, `"judge"`, `"attacker"`, `"evaluator"`, `"other"` | yes |  | What the credential is for: subject, judge, attacker, evaluator or other. The runner gives the value to the process this names, and refuses a purpose it does not know (PLAN-3, PLAN-7). |
| `runnerSelector` | array of string |  | ≤ 32 items | Tags a runner's capability manifest must all carry. |
| `ext` | [ext](common.md#ext) |  |  | Producer extensions, named reverse-DNS or with the producer's prefix. A reader ignores what it does not know; never holds a secret (ENC-19, PLAN-4). |
| `zone` | string |  | pattern `^[a-z0-9][a-z0-9._:-]{0,63}$` | The network zone a remote-zone run must execute in. |
| `judges` | array of object |  | ≤ 16 items | The judge models and rubrics the evaluators may use, fixed by the plan: a run names those that graded it, some of these in this order (STRM-4). |
| `judges[].model` | string | yes | ≥ 1 chars; ≤ 256 chars | A judge model the evaluators may use. |
| `judges[].provider` | string |  | ≤ 128 chars | Who serves the judge model. |
| `judges[].rubricDigest` | [sha256Uri](common.md#sha256uri) |  |  | The SHA-256 of the rubric the judge must grade with. |
| `baseline` | object or object |  |  | What a comparison lane compares against: a policy, or one sealed run. |
| `comparability` | object |  |  | The comparability axes a comparison lane uses, fixed by the plan so the runner cannot pick them. |
| `comparability.required` | array of [axis](common.md#axis) | yes | ≤ 16 items; unique | The axes on which a comparison lane's runs must match their baseline (spec 05, LANE-6). |

- **Rule:** A container run evaluates an already-built image, identified by digest.
- **Rule:** A remote-zone run names its zone.
