# AEF: the AgentEval Evidence Format

AEF is an open file format for the evidence an AI-agent evaluation produces: what was run against what, every
result with its typed absences, the gate and release decisions taken on it, and the human decisions added later.
Seals and signatures cover the exact bytes, so anyone can check a result **without trusting the tool that produced
it**, AgentEval included.

> **Status: release candidate.** AEF 1.0 is AgentEval's evidence format
> ([ADR-035](adr/035-aef-is-the-evidence-format.md)). It becomes final at the `aef-1.0` tag, released together with
> AgentEval 1.0. Until then it may still change where implementation or independent review finds a gap. The
> specification lives in the repository at
> [`contracts/aef/`](https://github.com/AgentEvalHQ/AgentEval/tree/main/contracts/aef), under the Apache License 2.0.

## Why a separate format

AgentEval's own output (`.agenteval/`, see [The .agenteval Workspace](agenteval-workspace.md)) is made for
AgentEval. AEF is made for whoever has to rely on a result:

| You want to know | AEF gives you |
|---|---|
| Were the results changed after the run? | A **seal**: SHA-256 digests of every file, in an in-toto statement. Any change is detected. |
| Who produced them? | An optional **signature** (DSSE, ECDSA P-256 or Ed25519) checked against *your* list of trusted keys. |
| Did the agent really pass, or was it never measured? | **Typed absences**: `not_measured`, `not_applicable`, `skipped`, `error` are never counted as a pass or a fail. |
| Was the real agent tested, or a stand-in? | `execution.targetMode` (`live`, `replayed`, `scripted`, `mocked`). Only `live` evidence counts toward a release. |
| May this version ship? | A **checkpoint**: lanes of evidence with rules. A pure decision function recomputes its outcome from the sealed runs. |
| Did a reviewer override a result? | **Overlays**: append-only, sealed batches of human decisions on a closed run. The sealed files never change. |

## What a run looks like

```text
my-run/
  run.json          what was run: subject, suite, judges, how the target was driven, times
  results.ndjson    one line per result: a case's verdict, its checks, trials and rollups
  metrics.json      the metrics the results score
  summary.json      per lane and metric: counts, means, rates, verdicts
  evidence.ndjson   transcripts, tool calls, attachments (in blobs/, by SHA-256)
  gates.ndjson      gate decisions taken during the run (optional)
  seal.json         the seal, once the run is closed
  attestation.dsse.json   its signature (optional)
  overlays/         reviews, overrides and waivers added later, in sealed batches
```

Every file is strict I-JSON, within limits every reader enforces, so two conforming readers never read a file
differently.

## The `agenteval aef` command

| Command | What it does | Exit code 1 when |
|---|---|---|
| `agenteval aef verify <run-dir>` | Checks a run: `intact`, `unsealed` or `invalid`, with every problem. With `--policy`, also who signed it. | the run is invalid |
| `agenteval aef seal <run-dir>` | Seals a closed run. `--key` also signs it (PKCS#8 PEM, ECDSA P-256). `--sealed-by producer\|ingest`. | the run cannot be sealed |
| `agenteval aef view <run-dir>` | Shows the run as its overlays leave it: overrides, reviews, waivers in force at `--at`, withheld blobs. | (never) |
| `agenteval aef checkpoint <file> --runs <dir>` | Checks a checkpoint: its manifest, its runs, each lane's recomputed result, its decision and its signature. | any problem |
| `agenteval aef export <store-dir> <out-dir>` | Converts a run of AgentEval's `.agenteval/` store into an AEF run (an *imported* run, sealed by the exporter). | the converted run does not verify |
| `agenteval aef import assert-ai <dir> <out-dir>` | Converts an [ASSERT](assert-interop.md) run into an AEF run, with ASSERT's harm and over-refusal rates. | the converted run does not verify |

Every command takes `--json` and then prints one JSON value instead of a report. Exit code 2 is a usage or input
error (a missing folder, a trust policy that cannot be read).

### Export a run, check it, sign it

```bash
# Convert one run of the .agenteval store (store v1 records no target mode: say so if it ran live)
agenteval aef export .agenteval --run 2026-10-08T120000Z-a1b2 ./aef-run --target-mode live --no-seal

# Seal and sign it (the key is yours; keep it out of the run)
agenteval aef seal ./aef-run --key ./signing-key.pem --sealed-by ingest

# Anyone can now check it, with the public key in their own trust policy
agenteval aef verify ./aef-run --policy ./trusted-keys.json
```

A **trust policy** lists the public keys you trust, each with the identity it speaks for:

```json
{"keys": [{"identity": "git:release-bot@example.com", "publicKey": "-----BEGIN PUBLIC KEY-----\n...\n-----END PUBLIC KEY-----\n"}]}
```

A verifier never trusts a key because a run names it: which keys to trust is always your input.

### What an exported run says, and does not

An exported store run is marked as imported, and lists what the exporter *supplied* rather than read
(`imported.asserted`). Store v1 does not record how the target was driven, so `targetMode` defaults to `mocked`. An
exported run is never passed off as live evidence unless you say it was. The full mapping is in the specification,
§7.5.

## Checking AEF without AgentEval

AEF does not depend on AgentEval. The specification ships a conformance corpus (over 600 vectors, each expected result
written independently of any implementation) and Python reference tools that use the standard library only:

```bash
python contracts/aef/tools/aef_verify.py run ./aef-run --policy ./trusted-keys.json
python contracts/aef/tools/aef_conformance.py --command "<your implementation>"   # run the corpus against your own
```

AgentEval's .NET implementation (`AgentEval.Results`) is written from the specification text alone and passes the
corpus for every conformance class but Runner. A third implementation, in any language, is welcome: §9 of the
specification says how to claim conformance.

## What AEF does not do

- **A seal is not a signature.** Anyone can edit a file and compute a new seal. Only a signature from a key you trust
  says who stands behind a run.
- **Nothing makes a result true.** AEF makes results attributable and tamper-evident, nothing more.
- **Overlays never change the sealed run.** An override is shown beside the sealed result, never instead of it.

## Today and next

- **Today:** the `agenteval aef` command checks, seals and views AEF runs, checks checkpoints, and converts store v1
  and ASSERT runs.
- **Next:** AgentEval's evaluation pipelines will write AEF directly (ADR-035), and `AgentEval.Results` will be
  published as a package.

## Read more

- The specification: [`contracts/aef/1/`](https://github.com/AgentEvalHQ/AgentEval/tree/main/contracts/aef/1). Start
  with its primer.
- [ADR-035: AEF 1.0 is AgentEval's evidence format](adr/035-aef-is-the-evidence-format.md)
- [ASSERT Interoperability](assert-interop.md)
