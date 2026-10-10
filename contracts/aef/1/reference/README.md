# Field reference

*Generated from the writer schemas by `tools/gen_reference.py`. Informative: the schemas and the [specification](../spec/01-introduction.md) are the rule.*

| Schema | What it describes |
|---|---|
| [AEF 1.0: run.json](run.md) | The header of one run folder |
| [AEF 1.0: one line of results.ndjson](result.md) | One node of a run's result tree |
| [AEF 1.0: metrics.json](metrics.md) | The metrics this run reports |
| [AEF 1.0: summary.json](summary.md) | Aggregates per lane and metric, with what was measured beside what was asked for, and the sufficient statistics to pool runs |
| [AEF 1.0: one line of evidence.ndjson](evidence.md) | One piece of evidence a result cites: what it is, its digest, and where to find it |
| [AEF 1.0: one line of gates.ndjson](gate-decision.md) | A gate decision the producer made when the run closed (a CLI --fail-on gate, a baseline comparison): its rule, inputs, outcome and exit code, sealed with the run |
| [AEF 1.0: seal.json](seal.md) | An in-toto Statement v1 over every file of the run except seal.json, attestation.dsse.json and overlays/: one subject per file, the exact bytes' SHA-256, and the run hash (SEAL-1, SEAL-5) |
| [AEF 1.0: one line of overlays/events.ndjson](overlay-event.md) | Everything after a run closed: a human decision, an adjudication, an annotation, a waiver |
| [AEF 1.0: overlays/seal-<n>.json](overlay-seal.md) | An in-toto Statement v1 over one batch of overlay events (a byte range of overlays/events.ndjson) that names the previous batch's seal: the batches form a chain |
| [AEF 1.0: checkpoint manifest](checkpoint.md) | A release decision over several evidence lanes for one exact subject version: the lanes and their rules, the exact sealed runs each lane used (with their run hashes), the decision function's input and output once decided, and the state (CKP-1 to CKP-6) |
| [AEF 1.0: the checkpoint decision function's input and output](decision.md) | Decide(input) -> output is pure: no I/O, no clock (the evaluation time is an input) |
| [AEF 1.0: run plan](run-plan.md) | What a runner is asked to evaluate: the exact subject version, the suites and lanes, the limits, what text the runs keep (contentCapture), how they drive the target (targetMode), where and how it runs, and the credentials it needs, as references only (PLAN-1 to PLAN-5) |
| [AEF 1.0: runner capability manifest](runner.md) | What a runner is and can do: its identity, kind, platform, the providers it supports, its tags and the target modes it can give |
| [AEF 1.0: one line of a runner event stream](runner-event.md) | A runner reports a job as NDJSON events, each with a sequence number |
| [AEF 1.0: a trust policy](trust-policy.md) | SIG-4: the public keys a verifier trusts, each with the identity it speaks for and what that identity may do beyond signing |
| [AEF 1.0: shared definitions](common.md) | Definitions the other AEF 1.0 schemas reference |
