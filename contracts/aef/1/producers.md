# Writing AEF runs: a guide for producers

*Informative.* The specification says what a valid run is; this page says how to write one that is also as useful as
it can be: easy to check, honest about what it did not measure, and safe to keep. Every point names the rule it comes
from. Start with the [primer](primer.md) if AEF is new to you.

## The four files

A closed run that checks clean needs `run.json`, `results.ndjson`, `metrics.json` and `summary.json`
([RUN-2](spec/03-run.md)). Write them against the **writer** schemas, never the reader ones.

1. **Open the run.** Write `run.json` with `status: running` ([RUN-5]). While it is running you may rewrite any file
   ([RUN-4]).
2. **Write one line per result.** Compute every `resultId` from the run id, the case id, the path and the trial
   ([RES-4]); never invent one. A parent's line names its children by `parentResultId`.
3. **Write the summary** from the lines, exactly as [SUM-3]–[SUM-5] compute it, and leave no line `pending`
   ([RES-3]).
4. **Close the run.** Set `status` to `completed` (or `aborted`, with an `abortReason`) and `endedAt`, writing every
   file's final form at once. After this, nothing outside `overlays/` changes ([RUN-4]).
5. **Seal it** ([SEAL-1]–[SEAL-5]), and sign the seal if anyone else will rely on the run ([SIG-1]).

`tools/aef_produce.py` is a reference writer for steps 4 and 5; `tools/aef_verify.py run DIR` checks the result.

## Say what you did not measure

- **A typed absence is not a failure.** A case that could not be judged (the judge timed out, the tool was down) is
  `not_measured`, `error` or `skipped`, with a `reason`; one that does not apply is `not_applicable` ([RES-1], [RES-2]).
  Writing `failed` with a score of 0 instead makes the run say something false, and a release rule will read it.
- **Say how the target was driven.** `execution.targetMode` is `live` only when the real subject answered; a stand-in
  is `mocked`, a recorded conversation `replayed` ([RUN-7]). Only `live` evidence counts toward a release ([LANE-1]).
- **Name the exact version.** `subject.version` and `suite.version` are exact versions (a commit, a digest), never
  "latest" ([RUN-6], [RUN-8], [ENC-10]). A run without one serves no checkpoint lane.
- **Give every failure its severity** ([RES-9]). A failure without one counts as `critical` wherever a rule needs it.

## Optional fields that make a run more checkable

None of these is required. Each lets a reader check more without trusting you.

| Write | Because |
|---|---|
| `sum` (and `sumSq`) on every summary entry | a reader checks the mean without the results ([SUM-5]); `sum` must be exact |
| `target.runHash` on every overlay event | the event cannot be replayed onto another run with the same `runId` ([OVL-2]) |
| `judges[]` with `rubricDigest`, and `calibration` when it was measured before the run | a reader knows which rubric graded, and how far the judge was right on labelled cases ([RUN-9]) |
| `suite.digest` | the cases themselves, not only their name and version ([RUN-8]) |
| `usage` per role and model, and `cost` with its source | spend can be checked against a plan's budget ([RES-10], [SUM-7]) |
| `startedAt` and `endedAt` on result lines | a reader can see a result that claims to predate its run ([RES-10]) |
| `trials` rollups with `agree` | a flaky case shows as one ([RES-8]) |

## Composites and trials

- A node with children carries `aggregation`, and each child `component` ([RES-5]). `total` is the number of its
  children, `measured` those in a measured state ([RES-6]). `strategy` and `rulePath` describe what you did; nobody
  recomputes your verdict from them. When the node's verdict is its own and its children are only recorded beside it,
  say so: `strategy: Own`, each child with weight 0.
- A case run several times has one line per trial, each tree carrying its `trial`, and one rollup per path with
  `trials` (`n`, `passed`, `agree`) ([RES-8]). A composite case in trials has a rollup at every path its trials have,
  and those rollups form its tree.
- `tools/aef_conformance.py --kind produce` checks that a writer derives all of this right.

## Privacy and secrets

- **Choose `contentCapture` on purpose** ([RUN-11]). With `off`, no prompt, response, transcript or judge reasoning is
  kept, no digest of one either, and `reason` and `ext` hold none of it ([SEC-3]).
- **Never seal a secret** ([SEC-1], [SEC-2]): no key, token or password in any file, `ext` included. A sealed run
  cannot be edited; it can only be withdrawn and produced again.
- Identities in overlays should be stable opaque ids, not e-mail addresses: an overlay cannot be erased ([SEC-4]).

## Encoding traps

These are the mistakes implementations made while AEF was being built.

- One JSON value per file, UTF-8, no byte-order mark; NDJSON lines end in LF, never CRLF, with no blank line
  ([ENC-1], [ENC-5]). On Windows, write with an explicit LF; do not let a text mode or `core.autocrlf` convert it.
- No member twice in one object, no unpaired surrogate ([ENC-2]); numbers finite ([ENC-3]).
- Times in UTC with `Z`, years 0001–9999, up to nine fraction digits; never a time that does not exist
  ([ENC-8]).
- Paths of ASCII letters, digits, `.`, `_` and `-` in segments, no two differing only in case ([RUN-3]).
- Stay within the limits: 4 MiB per JSON file or NDJSON line, 64 levels deep, 1,000,000 lines, 100,000 files
  ([ENC-17]). Split a large OpenTelemetry export into several `TracesData` lines.
- A ref built from a free-text name is percent-encoded as [ENC-13] says, so two writers agree on it.

## Overlays

- Append only; never rewrite `overlays/events.ndjson` ([OVL-1]). Seal each batch, chained to the previous seal
  ([OVL-4]).
- If the file ends in half a line (a writer crashed), end that line with an LF before you append: it then costs one
  `event-invalid` line, never the chain ([OVL-5]).
- Sign a batch that redacts a blob, with a key the readers' trust policy lets redact; an unsigned redaction withholds
  nothing ([OVL-10]).

## Testing your writer

Run the write-side vectors of the conformance corpus through your implementation (§9.3): `summarize` and `produce`
for a Producer, `seal-write` and `sign` for a Sealer:

```bash
python contracts/aef/tools/aef_conformance.py --command "your-tool" --class Producer --class Sealer
```

Then check a real run of yours with `aef_verify.py run`, and keep it if it passes: a run you can show is worth more than
one you describe.

[ENC-1]: spec/02-encoding.md
[ENC-2]: spec/02-encoding.md
[ENC-3]: spec/02-encoding.md
[ENC-5]: spec/02-encoding.md
[ENC-8]: spec/02-encoding.md
[ENC-10]: spec/02-encoding.md
[ENC-13]: spec/02-encoding.md
[ENC-17]: spec/02-encoding.md
[RUN-3]: spec/03-run.md
[RUN-4]: spec/03-run.md
[RUN-5]: spec/03-run.md
[RUN-6]: spec/03-run.md
[RUN-7]: spec/03-run.md
[RUN-8]: spec/03-run.md
[RUN-9]: spec/03-run.md
[RUN-11]: spec/03-run.md
[RES-1]: spec/03-run.md
[RES-2]: spec/03-run.md
[RES-3]: spec/03-run.md
[RES-4]: spec/03-run.md
[RES-5]: spec/03-run.md
[RES-6]: spec/03-run.md
[RES-8]: spec/03-run.md
[RES-9]: spec/03-run.md
[RES-10]: spec/03-run.md
[SUM-3]: spec/03-run.md
[SUM-5]: spec/03-run.md
[SUM-7]: spec/03-run.md
[SEAL-1]: spec/04-integrity.md
[SEAL-5]: spec/04-integrity.md
[SIG-1]: spec/04-integrity.md
[OVL-1]: spec/04-integrity.md
[OVL-2]: spec/04-integrity.md
[OVL-4]: spec/04-integrity.md
[OVL-5]: spec/04-integrity.md
[OVL-10]: spec/04-integrity.md
[LANE-1]: spec/05-checkpoints.md
[SEC-1]: spec/08-security.md
[SEC-2]: spec/08-security.md
[SEC-3]: spec/08-security.md
[SEC-4]: spec/08-security.md
