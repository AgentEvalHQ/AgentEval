# Paired LongMemEval evidence

Run the normal memory evaluation and a gold-only oracle over one selected workload:

```powershell
agenteval bench longmemeval --preset subset --subject my-agent --paired-oracle --aef ./evidence
```

The command uses the configured provider for answers and judging. Pairing adds an oracle arm,
approximately doubling calls. Normal execution finishes before oracle history is projected.
The final exit code follows the normal benchmark verdict: an improved oracle score cannot turn a
normal failure into a pass. An interrupted execution exits 1; an evidence-write failure exits 3.

Each prepared arm produces its own AEF run and definition. Normal evidence is written first and
retains the normal gate. Oracle evidence has no gate; its root score and the oracle-minus-normal gap
are diagnostics. The two results are never combined into one benchmark score or inserted into the
legacy output store as a single normal run. This mode prints both scores and evidence locations.

Both summaries carry `ext["agenteval.memoryPair"]` with schema version `1.0`:

| Field | Meaning |
|---|---|
| `pairId` | Unique association for this execution; excluded from suite content hashes |
| `role` | `normal` or `oracle` |
| `sourceInputsDigest` | Shared SHA-256 URI for the actual selected source workload |
| `plannedQuestions` | Size of the shared selected roster |
| `observedQuestions` | Questions retained by this arm |
| `scoredQuestions` | Completed arm's accuracy denominator; null on interruption |
| `totalLlmCalls` | Completed arm's answer and judge call count; null on interruption |
| `complete` | Whether this arm completed normally |
| `diagnosticOnly` | True for the oracle |
| `accuracyPercent` | Completed arm's accuracy, or null when unavailable/interrupted |
| `normalRunHash` | Oracle only: exact normal seal hash, or null for explicitly unsealed evidence |
| `oracleGapPercentagePoints` | Oracle only: completed oracle minus completed normal accuracy, otherwise null |

The oracle points backward to the normal seal; there is no circular hash dependency. Readers should
verify both runs, match the referenced normal hash, pair ID and source digest, and inspect both arm
definitions and statuses. Sharing a source workload does not make the two different evaluations
equivalent under `aef compare-suites`.

Cancellation before the first question retains an empty aborted normal run with its planned
definition. Cancellation between arms retains completed normal evidence alone. Once oracle inputs
are prepared, interruption retains an aborted oracle run, possibly empty, linked to normal. An arm
that never reached preparation has no fabricated result or projected definition. Previously written
evidence is not rewritten when a later arm fails.

The usual AEF controls apply: `--aef-unsealed` leaves both runs unsealed and the normal hash link
null; `--aef-key` signs seals; `--no-aef` writes no evidence. Pair summaries contain identifiers,
digests and counts, not questions, answers or retrieved text. Invocation-wide usage totals are not
duplicated into each arm; the per-arm call counts are reported explicitly instead.

For an offline transport and independent-verifier check, build the CLI and run:

```powershell
python scripts/verify-paired-memory.py --cli src/AgentEval.Cli/bin/Debug/net10.0/AgentEval.Cli.dll
```

The script forces a loopback-only fixture provider. It covers sealed and unsealed exports, suppressed
evidence and invalid destinations, and checks emitted runs using the Python AEF reference reader.
