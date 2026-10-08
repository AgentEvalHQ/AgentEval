# Rationale and FAQ

*Informative. Why AEF 1.0 is the way it is. Each answer names the rules it explains.*

## Design choices

### Files, not an API

A run is a folder of plain files ([RUN-1]). Files can be copied, archived, attached to a release, diffed and read in
twenty years without a server. An API would tie the evidence to whoever runs it; the transport is left to the user
(a copy, an artifact store, an upload), and nothing in AEF depends on it.

### Seal the bytes, not a canonical form

Many formats sign a canonical serialisation (JCS, sorted keys). AEF seals each file's exact bytes ([SEAL-2]) instead.
Canonicalisation is a second parser in the trust path. Implementations disagree on number formatting, Unicode
normalisation and duplicate keys, and a disagreement there is a signature bypass. Hashing bytes has one
implementation everywhere. The cost is that a file must not be re-encoded after sealing, which AEF requires anyway
([RUN-4]), and which the corpus tests by sealing a blob with non-ASCII text and a CRLF inside.

### I-JSON, and refusing duplicate keys

Two JSON parsers can read `{"status":"aborted","status":"completed"}` differently: one keeps the first, one the last.
If the seal covers bytes that two readers interpret differently, the seal protects nothing. AEF requires I-JSON
([RFC 7493]) and a reader must refuse such a file rather than pick one ([ENC-2]).

### in-toto and DSSE, not a new envelope

The seal is an in-toto Statement and the signature a DSSE envelope ([SEAL-5], [SIG-1]). Supply-chain tooling
(policy engines, transparency logs, Sigstore) already understands both. AEF adds two predicate types and reuses
everything else.

### ECDSA P-256 required, Ed25519 recommended

A verifier must support at least one algorithm everyone can rely on. P-256 is in every mainstream crypto library
(.NET, Java, Go, WebCrypto, OpenSSL), in FIPS 186-5, and in hardware keys. Ed25519 is better engineered but not
built into every platform AEF expects verifiers on (.NET has no Ed25519 in its base library). So P-256 is a MUST and
Ed25519 a SHOULD ([SIG-2]). There is no low-S rule, because DSSE, in-toto and Sigstore have none, and a verifier that
added one would refuse valid signatures.

### The trust policy is an input

A run can say anything about itself, including "trust this key". So which keys to trust is always the verifier's
input, never read from the evidence ([SIG-4]). An *intact* run is internally consistent and nothing more; AEF forbids
calling it authentic ([SIG-7]).

### Typed absences

The most common way evaluation reports mislead is by turning "not measured" into a number: a timeout becomes a 0, an
empty reply becomes a pass, a filtered case disappears. AEF has five typed-absence states, each with a reason, never a
score, and never counted as a pass ([RES-1], [RES-2]). Summaries count them separately ([SUM-5]), and a summary entry
with nothing measured has no value and the verdict `not_measured` ([SUM-6]).

### Aggregation is descriptive

Evaluation tools combine child scores in many ways (weighted sums, minimums, majority votes, rules about required
children). A format that defined one formula would be wrong for most tools, and one that defined all of them would be
a scoring library. AEF records how the producer reached a composite's state, so a reader can show it, and forbids
recomputing it ([RES-6]). What AEF does define exactly is the summary, because release decisions read it ([SUM-3]–[SUM-5]).

The same line runs through summary aggregates. The mean, median, minimum and maximum are defined, so a verifier
recomputes them. pass@k, F1 and the many other figures tools report are kept as the producer wrote them, so a reader
sees them, but no lane reads them ([SUM-8], [LANE-2]). A release decision rests only on numbers anyone can recompute
from the sealed lines.

### The target mode is required

A run against a scripted stand-in looks exactly like a run against the real agent. If nothing in the format says
which it was, a demo's numbers end up in a release decision. `execution.targetMode` is required ([RUN-7]). Only `live`
runs are eligible evidence for a checkpoint ([LANE-1]), and a reader must show the mode wherever it shows results.

### A pure decision function, recorded with its input

A release decision is easy to state and hard to audit. AEF makes the decision a pure function with no clock and no
I/O ([§5.4](spec/05-checkpoints.md)), whose input and output are both recorded in the checkpoint. Anyone can recompute
it, and the corpus pins its behaviour down to the nanosecond. Lane results are themselves functions of the sealed runs
([§5.3](spec/05-checkpoints.md)), so the chain from bytes to outcome has no step that rests only on the producer's
word.

### An exact sign test for comparisons

A comparison lane asks whether the candidate regressed against a baseline on the same cases. The sign test is paired
(it compares the same case twice), distribution-free (no assumption about score shapes), exact at small sample sizes
(evaluation suites are often a few dozen cases), and one-sided because only a regression should block. AEF requires
the exact binomial computation, not a normal approximation or a two-sided test, so that two implementations agree on
every boundary ([LANE-8]). The corpus includes cases where those shortcuts give a different answer.

### Overlays instead of edits

People need to approve, override, waive and annotate after a run closes, and a sealed run cannot change. Overlays are
append-only events in batches, each batch sealed, bound to the run hash and chained to the one before
([OVL-1]–[OVL-5]). The sealed run stays what it was, and the effective view shows both ([OVL-7]). A `redact` event
lets a captured blob holding personal data be deleted without breaking the seal ([OVL-10]).

### Strict writers, tolerant readers

Producers write against strict schemas, so mistakes surface where they are made. Readers accept what a later 1.x may
add, and read every unknown enum value as its safest known value ([§7.3](spec/07-versioning.md)): an unknown state is
not a pass, and an unknown target mode is not live. The reader schemas are derived mechanically from the writer
schemas, so the two cannot drift.

### Portable patterns

JSON Schema says patterns are ECMA-262, but validators use their host language's regex engine, and engines disagree:
in Python and .NET, `$` also matches before a final newline. AEF's patterns avoid every construct engines read
differently (no lookaround or backreferences), and require `$` to mean the end of the input ([ENC-14], [ENC-15]). The
corpus holds values ending in a newline that a conforming validator refuses.

### Paths a file system cannot mangle

Run folders travel between Windows, macOS and Linux. AEF restricts paths to ASCII letters, digits, `.`, `_` and `-`,
forbids names Windows reserves, and forbids two paths that differ only in case ([RUN-3]). Otherwise a run sealed on
Linux could fail to check out on Windows, or two files could merge on macOS.

## FAQ

**Can I use AEF without AgentEval?**
Yes. AgentEval is the first producer, but nothing in the specification depends on its code, names or conventions,
and the reference tools are standalone Python ([§1.4](spec/01-introduction.md)).

**Do I have to sign runs?**
No. A sealed, unsigned run is *intact*, and that is a valid outcome. Sign when someone else needs to know who sealed
it ([§4.5](spec/04-integrity.md)).

**Is AEF a replacement for OpenTelemetry?**
No. Traces stay OpenTelemetry (OTLP/JSON, in `traces.otlp.jsonl` or an external store), and results link to spans
([RUN-14]). AEF adds what traces do not have: a result tree with typed absences, a seal, overlays and release
decisions. See [interop](interop/) for how evaluation results map to OpenTelemetry's GenAI evaluation events.

**Why not Inspect logs or OpenAI Evals output?**
Both are good records of what one tool did. Neither is sealed, neither has a release decision, and each is tied to
its tool's data model. AEF carries most of what they record, but not all of it yet: scores without a verdict, the
case's input and output as first-class fields, some token counts and per-result times have no AEF home in 1.0.
[interop](interop/) gives each mapping and lists those gaps.

**What does AEF *not* protect against?**
A producer that lies when it writes the run, a judge that is wrong, and a suite that measures the wrong thing. A seal
proves the files did not change after sealing; it does not prove they were true. [§8](spec/08-security.md) lists each
threat and which mechanism, if any, addresses it.

**How big can a run be?**
[§2.6](spec/02-encoding.md) sets limits a reader may enforce: 4 MiB per JSON file or NDJSON line, a million lines per
file, 100,000 files per run, 1 GiB per blob.

**What happens when AEF 1.1 adds a field?**
A 1.0 reader keeps working: it ignores the field, or reads an unknown value as its safest known value. A minor
version only adds ([VER-5]). `tools/schema_diff.py` refuses any schema change a minor may not make.

**Who decides what goes into AEF?**
See [GOVERNANCE.md](../GOVERNANCE.md): the editors, the proposal process, and how changes are versioned.

**What licence is AEF under?**
Today everything is Apache-2.0 ([LICENSE](../LICENSE), [NOTICE](../NOTICE)). For the 1.0 release, the plan (pending
counsel) is the Community Specification License 1.0 for the specification text, so that anyone who implements AEF
from the text has a royalty-free patent commitment, limited to [what the specification requires](../SCOPE.md); the
schemas, corpus and tools stay Apache-2.0. See [GOVERNANCE.md](../GOVERNANCE.md#licence-and-patents).
