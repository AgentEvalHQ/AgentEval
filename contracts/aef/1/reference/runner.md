# AEF 1.0: runner capability manifest

*Generated from [`schemas/writer/runner.schema.json`](../schemas/writer/runner.schema.json) by `tools/gen_reference.py`. Do not edit: edit the schema.*

What a runner is and can do: its identity, kind, platform, the providers it supports and its tags. A plan's runnerSelector is matched against the tags.

| Field | Type | Required | Bounds | Description |
|---|---|---|---|---|
| `schemaVersion` | [schemaVersion](common.md#schemaversion) | yes |  | The AEF version this document follows, MAJOR.MINOR: 1.0 for AEF 1.0 (VER-1). |
| `runnerId` | [id](common.md#id) | yes |  | The runner's id; the events and runs it produces name it (PLAN-6). |
| `identity` | object |  |  | The runner's workload identity, and the id of the key it signs sealed evidence with, if any. |
| `identity.workloadId` | string | yes | ≥ 1 chars; ≤ 256 chars | The runner's workload identity (PLAN-6). |
| `identity.signingKeyId` | string |  | ≥ 1 chars; ≤ 256 chars | The id of the key the runner signs sealed evidence with: a hint for a reader, never a reason to trust the key (PLAN-6, SIG-4). |
| `kind` | one of `"local"`, `"remote"`, `"ci"`, `"pool"` | yes |  | What the runner is: local (a local process), remote (a remote service), ci (a CI job) or pool (a pool of runners) (PLAN-6). |
| `os` | one of `"linux"`, `"windows"`, `"macos"` | yes |  | The runner's operating system: linux, windows or macos (PLAN-6). |
| `runtime` | object | yes |  | The runtime the runner runs on (PLAN-6). |
| `runtime.name` | string | yes | ≥ 1 chars; ≤ 64 chars | The runtime's name. |
| `runtime.version` | string | yes | ≥ 1 chars; ≤ 64 chars | The runtime's version. |
| `providers` | array of [provider](common.md#provider) | yes | ≥ 1 items; ≤ 16 items | The providers the runner supports. It refuses a plan whose provider is not among them (PLAN-7). |
| `tags` | array of string | yes | ≤ 64 items | The runner's tags. It can take a plan only when it carries every tag of the plan's runnerSelector (PLAN-7). |
| `gpu` | boolean |  |  | Whether the runner has a GPU (PLAN-6). |
| `networkZone` | string |  | pattern `^[a-z0-9][a-z0-9._:-]{0,63}$` | The network zone the runner runs in. A remote-zone plan needs it to be the plan's zone (PLAN-7). |
| `version` | string | yes | ≥ 1 chars; ≤ 64 chars | The runner software's version. |
| `ext` | [ext](common.md#ext) |  |  | Producer extensions, named reverse-DNS or with the producer's prefix. A reader ignores what it does not know; never holds a secret (ENC-19). |
