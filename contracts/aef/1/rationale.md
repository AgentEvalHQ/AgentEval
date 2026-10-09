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
([RUN-4]), and which the corpus tests by sealing a blob with non-ASCII text and a CRLF inside. In return, member
order, whitespace and the form of a number are free, and no reader depends on them ([ENC-2], [ENC-4]): two writers'
bytes, and so their run hashes, may differ, but every reader reads the same values.

### I-JSON, and refusing duplicate keys

Two JSON parsers can read `{"status":"aborted","status":"completed"}` differently: one keeps the first, one the last.
If the seal covers bytes that two readers interpret differently, the seal protects nothing. AEF requires I-JSON
([RFC 7493]) and a reader must refuse such a file rather than pick one ([ENC-2]).

### in-toto and DSSE, not a new envelope

The seal is an in-toto Statement and the signature a DSSE envelope ([SEAL-5], [SIG-1]). Supply-chain tooling
(policy engines, transparency logs, Sigstore) already understands both. AEF adds two predicate types and reuses
everything else.

### ECDSA P-256 and Ed25519, both required of verifiers

A signer may use either: P-256 is in every mainstream crypto library (.NET, Java, Go, WebCrypto, OpenSSL), in FIPS
186-5, and in hardware keys; Ed25519 is better engineered and common in signing tools. A verifier that knew only one
could not check what a signer may write, so a verifier must support both ([SIG-2]). Ed25519 is not in every base
library (.NET's has none), but verifying it takes about two hundred lines over big integers, as AgentEval's own
implementation shows. The checks a verifier makes are pinned where libraries differ: k reduced mod L, the equation
without the cofactor, keys of small order refused, and a strict PEM whose lines are 64 characters but the last
([SIG-3]). There is no low-S rule, because DSSE, in-toto and Sigstore have
none, and a verifier that added one would refuse valid signatures.

### The trust policy is an input

A run can say anything about itself, including "trust this key". So which keys to trust is always the verifier's
input, never read from the evidence ([SIG-4]). An *intact* run is internally consistent and nothing more; AEF forbids
calling it authentic ([SIG-7]).

### Typed absences

The most common way evaluation reports mislead is by turning "not measured" into a number: a timeout becomes a 0, an
empty reply becomes a pass, a filtered case disappears. AEF has five typed-absence states, each with a reason, never a
score, and never counted as a pass ([RES-1], [RES-2]). Summaries count them separately ([SUM-5]), and a summary entry
with nothing measured has no value and the verdict `not_measured` ([SUM-6]). A case run several times cannot vanish
either: its rollup line must count every trial line, and a closed run has no trials without one ([RES-8]).

### Aggregation is descriptive

Evaluation tools combine child scores in many ways (weighted sums, minimums, majority votes, rules about required
children). A format that defined one formula would be wrong for most tools, and one that defined all of them would be
a scoring library. AEF records how the producer reached a composite's state, so a reader can show it, and forbids
recomputing it ([RES-6]). What AEF does define exactly is the summary, because release decisions read it ([SUM-3]–[SUM-5]).

### Recomputed aggregates, and the producer's own

A lane decides a release on a summary value, so that value must be one anyone can recompute from the sealed lines.
AEF defines the mean (the sum, for a count), the median, the minimum and the maximum, and a verifier recomputes each
([SUM-5], [SUM-8]). That set is fixed for major 1, so verifiers of every 1.x agree on which values a lane may read.
pass@k, F1 and bootstrap figures each come in variants (an estimator, a way of averaging, a resampling scheme), and AEF
does not pick one. Such an entry is kept as the producer wrote it, so a reader sees it, and its counts and `sum` are
still checked. But no lane reads its value: a lane over it is `not_measured` ([LANE-2]). A release never rests on a
number nobody can check.

### Exact sums

A summary's `sum` is computed exactly and rounded once to binary64 ([SUM-5]). Adding binary64 values one by one can
lose a value to cancellation: `1e20 + 1 − 1e20` gives 0, not 1. Two implementations that add in different orders, or
with compensated summation, would then disagree on whether an entry is wrong. No tolerance absorbs this, because what
is lost can be as large as the result. Exact arithmetic gives every implementation the same `sum` from the same lines.

### The target mode is required

A run against a scripted stand-in looks exactly like a run against the real agent. If nothing in the format says
which it was, a demo's numbers end up in a release decision. `execution.targetMode` is required ([RUN-7]). Only `live`
runs are eligible evidence for a checkpoint ([LANE-1]), and a reader must show the mode wherever it shows results.

### A pure decision function, recorded with its input

A release decision is easy to state and hard to audit. AEF makes the decision a pure function with no clock and no
I/O ([§5.4](spec/05-checkpoints.md#54-the-decision-function)), whose input and output are both recorded in the
checkpoint. Anyone can recompute it, and the corpus pins its behaviour down to the nanosecond. Lane results are
themselves functions of the sealed runs ([§5.3](spec/05-checkpoints.md#53-lane-evaluation)), so the chain from bytes
to outcome has no step that rests only on the producer's word.

### Exceptions name the evidence they accept

A release sometimes ships with a failure someone has read and accepted. AEF records that as an exception in the
checkpoint's decision input: the lane, the run hashes whose failure it accepts, why, who, and until when ([DEC-1]). It
waives a `failed` lane only when it names exactly the lane's evidence and is in force ([DEC-2]). A re-run has new run
hashes, so accepting one failure never accepts the next. An exception is not a policy ("accept failures in this lane
until March"), because a policy accepts failures nobody has read yet. For the same reason it never waives a `missing`,
`stale`, `not_measured` or `incomparable` lane: an unknown risk needs evidence, not a signature ([DEC-3]).

### An exception is not an overlay waiver

An overlay `waive` ([OVL-1]) is a note on a run's result or requirement, which anyone who can append to the events
file can write. It changes no summary, lane result or decision ([OVL-7]). An exception is part of a checkpoint's
decision input: the decision function applies it, a verifier recomputes its effect ([CKP-7]), and whoever signs the
checkpoint vouches for it ([CKP-5]). Its `by` is a claim, as an overlay's is; the checkpoint's signature is what makes
it attributable ([DEC-1]). A reader that shows the checkpoint later lets it lapse at its expiry ([CKP-10]).

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
lets a captured blob holding personal data be deleted without breaking the seal, when someone allowed to redact
signed it ([OVL-10], below).

### Redaction needs signed authority

Personal data captured in a blob must be erasable, and the run must stay verifiable after. A `redact` event lets the
blob be deleted, and the seal then reports it as `withheld`, not `missing` ([OVL-10]). But anyone who can append to
the events file can write an event, so an unsigned redaction would let anyone hide evidence. A redaction withholds a
blob only when its batch's signature verifies for an identity the verifier's trust policy allows to redact
(`"may": ["redact"]`, [SIG-4]). Otherwise the deleted blob is `missing` and the run is invalid. Only blobs can be
withheld, and a reader says "intact, *n* withheld", never plain intact.

### Strict writers, tolerant readers

Producers write against strict schemas, so mistakes surface where they are made. Readers accept what a later 1.x may
add, and read every unknown enum value as its safest known value ([§7.3](spec/07-versioning.md#73-reading-a-value-this-version-does-not-know)):
an unknown severity is `critical`, and an unknown target mode is not live. Three enums are the exception (below). The
reader schemas are derived mechanically from the writer schemas, so the two cannot drift.

### Three enums are closed

Most enums are open: a later 1.x may add a value, and a 1.0 reader reads it as its safest known value ([VER-8]).
Three cannot be read that way, because the rules across files compute with them: a result's `state` (what is measured
and what passes, [SUM-4]), a run's `status` (whether it is closed, [RUN-5]) and a metric's `kind` (a mean or a sum,
[SUM-5]). A verifier that met a new state could not recompute a summary, and reading it as a known state would be a
guess. If a minor could add one, a 1.0 verifier would find legal runs invalid. So the three are closed for major 1
([VER-9]): a value outside them is a `schema` problem, and `tools/schema_diff.py` refuses a new one.

### Portable patterns

JSON Schema says patterns are ECMA-262, but validators use their host language's regex engine, and engines disagree:
in Python and .NET, `$` also matches before a final newline. AEF's patterns avoid every construct engines read
differently (lookaround, backreferences, `\d`, `\w`, an unescaped `.`), and require `$` to mean the end of the input
([ENC-14], [ENC-15]). The corpus holds values ending in a newline that a conforming validator refuses.

### Paths a file system cannot mangle

Run folders travel between Windows, macOS and Linux. AEF restricts paths to ASCII letters, digits, `.`, `_` and `-`,
forbids names Windows reserves, and forbids two paths that differ only in case ([RUN-3]). Otherwise a run sealed on
Linux could fail to check out on Windows, or two files could merge on macOS.

### Runs checked against the plan, not only the stream

A stream verifier ([STRM-3]) checks what a runner reported: the plan's bytes, the spend and cases it counted, the runs
it announced. A runner that reports faithfully and runs the wrong thing would pass. Plan conformance opens the runs the
stream names ([STRM-4]): each must be the announced, intact run, carry the job's provenance, and be of the plan's
subject, suites, judges, deployment and content capture, `live`, and made between the job's acceptance and its end.
Cost and cases are added up across the job, so splitting the work across runs does not escape a limit. A run that
started before the job was accepted was adopted, not produced, and a plan does not authorize adopting runs.

### Two implementations, written apart

When one program is the only implementation, its behaviour becomes the specification and the gaps in the text stay
hidden. AEF has two, each written from the text and neither from the other's code: the Python reference tools
(`tools/`) and AgentEval's .NET library (`src/AgentEval.Results` in the AgentEval repository). Writing the second found
places where the text was ambiguous, contradictory or silent, and crafted inputs on which the two disagreed; each was
ruled in the text and pinned by a vector, generated by the runner when it is large ([changelog](CHANGELOG.md);
`conformance/rulings/`). One cannot be: a symbolic link under `overlays/`, which no portable corpus can hold; both
implementations report it as `unexpected-file`. The corpus's expected results come from neither: they are
written by hand, or by generators that implement only the rule they write, and cross-checked by a second
implementation ([§9.2](spec/09-conformance.md#92-the-corpus)).

## FAQ

**Can I use AEF without AgentEval?**
Yes. AgentEval is the first producer, but nothing in the specification depends on its code, names or conventions,
and the reference tools are standalone Python ([§1.4](spec/01-introduction.md#14-relation-to-agenteval)).

**Do I have to sign runs?**
No. A sealed, unsigned run is *intact*, and that is a valid outcome. Sign when someone else needs to know who sealed
it ([§4.5](spec/04-integrity.md#45-verification-outcomes)). One thing needs a signature: a redaction withholds a blob
only when its overlay batch is signed by an identity allowed to redact ([OVL-10]).

**Can a release ship with a known failure?**
Yes, when someone accepts it by name. An exception in the checkpoint waives a `failed` lane for the run hashes it
names, until it expires, and the outcome is `approved_with_exceptions`, never plain `approved` ([DEC-2], [DEC-3]).
Missing, stale, unmeasured or incomparable evidence cannot be waived. See
[Exceptions name the evidence they accept](#exceptions-name-the-evidence-they-accept).

**Is AEF a replacement for OpenTelemetry?**
No. Traces stay OpenTelemetry (OTLP/JSON, in `traces.otlp.jsonl` or an external store), and results link to spans
([RUN-14]). AEF adds what traces do not have: a result tree with typed absences, a seal, overlays and release
decisions. See [interop](interop/) for how evaluation results map to OpenTelemetry's GenAI evaluation events.

**Why not Inspect logs or OpenAI Evals output?**
Both are good records of what one tool did. Neither is sealed, neither has a release decision, and each is tied to
its tool's data model. AEF has a place for most of what they record: a score with no pass/fail rule (`scored`, on a
result line and as a summary verdict, [RES-1], [SUM-6]); the case's input, expected answer, output and transcript as
evidence ([RUN-11]); token counts per party and model, cache and reasoning tokens included, per result ([RES-10])
and per run ([SUM-7]); and each result's own start and end times ([RES-10]). [interop](interop/) gives each mapping and
what it still loses, such as Inspect's list-valued scores.

**What does AEF *not* protect against?**
A producer that lies when it writes the run, a judge that is wrong, and a suite that measures the wrong thing. A seal
proves the files did not change after sealing; it does not prove they were true. [§8](spec/08-security.md) lists each
threat and which mechanism, if any, addresses it.

**How big can a run be?**
[§2.6](spec/02-encoding.md#26-limits) sets limits a writer never exceeds and a reader always refuses beyond, so two
readers never disagree on a file ([ENC-17]): JSON nested at most 64 deep; 4 MiB per JSON file or NDJSON line; 40 MiB
for `seal.json` or a batch seal, which list every sealed file, and 56 MiB for a signature envelope, which holds one in
base64; a million lines and 1 GiB per NDJSON file; 1 GiB per blob; 100,000 files per run, not counting the seal, its
signature and `overlays/`, which holds at most 9,999 batches (19,999 files). A reader reports what it refuses as `limit`, and
refuses nothing within the limits ([ENC-18]).

**What happens when AEF 1.1 adds a field?**
A 1.0 reader keeps working: it ignores the field, and reads an unknown value as its safest known value ([VER-8]). A
minor version only adds, and a problem code it adds applies only to what it adds, so a file valid under 1.0 stays
valid ([VER-5]). It never adds a value to the three closed enums ([VER-9]), nor an aggregate a verifier recomputes
([SUM-8]). Checkpoints too: a 1.0 checkpoint verifier reports a lane as `unverifiable`, never as wrong, when
recomputing it reads something it does not know, in the rule (a later rule kind, member, maximum, operator or axis)
or in the runs (a target mode, a severity, a metric direction) ([CKP-8]); and a minor never changes what an existing
rule computes through a field it adds to a run, only through a new rule kind or member ([VER-5]).
`tools/schema_diff.py` refuses any schema change a minor may not make ([CONF-5]).

**Who decides what goes into AEF?**
See [GOVERNANCE.md](../GOVERNANCE.md): the editors, the proposal process, and how changes are versioned.

**What licence is AEF under?**
Today everything is Apache-2.0 ([LICENSE](../LICENSE), [NOTICE](../NOTICE)). For the 1.0 release, the plan (pending
counsel) is the Community Specification License 1.0 for the specification text, so that anyone who implements AEF
from the text has a royalty-free patent commitment, limited to [what the specification requires](../SCOPE.md); the
schemas, corpus and tools stay Apache-2.0. See [GOVERNANCE.md](../GOVERNANCE.md#licence-and-patents).
