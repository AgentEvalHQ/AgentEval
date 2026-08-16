`AgentEval 0.23.0-beta` and `AgentEval.Cli 0.23.0-beta`.

> [!IMPORTANT]
> **TypedMemEval v1 corpora (shipped in 0.22.0-beta) are superseded and must not be cited.**
> So is v2, which was never released. Gold in the v1 corpora is recoverable by cheap, model-free
> features at AUC 0.890–1.000 — a classifier can find the evidence without reading it. Re-run any
> v1 numbers against v3 before quoting them.

---

**TypedMemEval corpus revision v3, and V7 — adversarial separability.** The consuming project's
verification round asked whether the clause-parity check added in 0.22.0-beta would catch the
*next* tell, which would not be a clause. It would not. V7 is the general probe. It found real
separability in all five corpora 0.22.0-beta shipped — and then, on review, in the corpora it had
itself just certified, because the check was measuring the wrong thing.

> **v1 corpora are superseded and must not be cited.** So is v2, which was never released and
> existed only on an unmerged branch. Every session is rewritten, so retrieval difficulty moved and
> no v1 or v2 score is comparable with a v3 score. Corpus ids are now
> `agenteval-typedmemeval-<vertical>-v3`; cite as "TypedMemEval-\<Vertical\> **v3** (AgentEval)".


**Shipped v3 corpora — validity probes as measured.** V1/V2/V3/V6 ran against reference
deployment `gpt-5.5` at authoring time with three ablation samples per question; V7 is model-free
and re-measured in CI and again by an independent C# implementation.

| Vertical | V1 oracle | V1 pair-flip | V2 | V3 | V6 | V7 worst refused |
|---|---|---|---|---|---|---|
| Prospective | 49/50 | 18/19 | 50/50 | 39/39 | — | 0.715 (`gold_marker_ngram`) |
| Episodic | 48/50 | — | 50/50 | 49/50 | — | 0.724 (`boilerplate_ngram`) |
| Arithmetic | 48/50 | — | 50/50 | 50/50 | 50/50 | 0.737 (`gold_marker_ngram`) |
| WorkingMemory | 48/48 | — | 48/48 | 48/48 | — | 0.631 (`boilerplate_ngram`) |
| Forgetting | 34/35 | 14/15 | 35/35 | 35/35 | 20/20 | 0.663 (`first_user_length_chars`) |

Prospective's V3 denominator is 39 because V3 abstains where a gold answer carries no value the
question did not already supply — it cannot tell "reached the evidence" from "said what any model
with no evidence says". Episodic's V1 and V3 shortfalls are all `participant-attribution`, whose
answer is one of two; an ablation probe cannot separate reaching the evidence from a coin flip
there, and V2 (ten zero-context samples) is what bounds guessability for that shape.

**Corpus identity.** Pin these; a run whose provenance names a different hash is a different
benchmark.

| Corpus id | Coverage @ K_ref = 5 | SHA-256 (newline-normalised) |
|---|---|---|
| `agenteval-typedmemeval-prospective-v3` | 0.820 | `1686919510b1bfccbf66fbb2b5e55f1cdeb1309358c1bfce5b150adc1529a76f` |
| `agenteval-typedmemeval-episodic-v3` | 0.871 | `5f1efa83c197d01335df733c4251ffcf2bf6515421403ec5c5390bb4946bedd5` |
| `agenteval-typedmemeval-arithmetic-v3` | 0.661 | `4624eb78b21178ab06ab372063d4a41a069269bc841c3289e44db13a899035e2` |
| `agenteval-typedmemeval-workingmemory-v3` | 0.792 | `c5361ebe47150e7d9e6dbaa9da87b3b1e55d50e450b1d3393b0e70ae29e8ca86` |
| `agenteval-typedmemeval-forgetting-v3` | 0.690 | `843b176a056e9f03575e01a4bb8cf830ef1999d0955d1c174bd2b50c42a6dcaf` |

### Added

- **V7, adversarial separability.** Tries cheap single-feature classifiers at telling gold sessions
  from distractors, scoring each as a direction-folded AUC over (gold, distractor) pairs formed
  **within a question**. It refuses a corpus at 0.75 on any shape feature, runs as a
  generator-refusal rule, is stamped into every corpus's metadata beside V1–V6, and is re-measured
  in CI *and* recomputed independently in C# — a stamped number nothing recomputes is a claim, not
  a check.

  Refused features: session length, turn count, position, digit density, capitalisation density,
  sentence count, punctuation density, em-dash density, mean turn characters, type-token ratio, and
  recurring phrases in **both** directions — one carried by gold marks the evidence, one carried by
  filler marks it by absence, and those are the same defect.

  Measured against the corpora 0.22.0-beta shipped, under the corrected metric:

  | Vertical | worst refused feature (v1) | v3 |
  |---|---|---|
  | Prospective | session length **0.903** | 0.715 |
  | Episodic | session length **0.936** | 0.727 |
  | Arithmetic | capitalisation density **0.890** | 0.737 |
  | WorkingMemory | session length **1.000** | 0.728 |
  | Forgetting | sentence count **1.000** | 0.703 |

- **A credential-free `--self-test` for the probe evidence screen**, wired into CI. The screen
  decides whether a response reached the gold answer, so it decides which questions are valid; it
  had been wrong twice, and both defects manufactured leaks in corpora that had none.

- **Structural re-derivation in CI.** The declared `H`, `G` and ceiling table are now recomputed
  from the shipped corpus bytes rather than read back from the sidecar the generator stamped, and
  session order is asserted against timestamp order.

### Changed

- **V3 and V6 sample three ablations instead of one.** A single sample can miss a leak that is
  there — the gutter/inspection leak fixed in 0.22.0-beta was caught by one sample and could as
  easily have been missed. Unlike V2 there is no hit threshold: one sample that reconstructs the
  answer from distractors alone condemns the question.

- **Shape parity is now a search, not a formula.** Padding is chosen greedily over all six raw
  counts the refused features are built from, scoring overshoot as harshly as shortfall. Equalising
  one axis at a time had simply relocated the tell: matching characters left capitals-per-character
  at 0.89, and matching capitals left sentences-per-character at 1.000.

- **Gold acknowledgements come from one bank shared with filler**, and the calibration clause sits
  on the same turn role for both. Gold's share of that clause is counted rather than drawn per
  session — a question with one gold session and a 0.92 rate left it bare 8% of the time.

### Fixed

- **V7 pooled (gold, distractor) pairs across questions**, which is a different and much easier
  question than the one the threat model asks. It diluted a real within-question tell in Forgetting
  from 0.903 to 0.616, and it got *better* the more abstention questions a vertical had, because
  questions with no gold contributed distractor-only values that paired against every other
  question's gold.

- **Balancing an aggregate does not balance its parts.** Padding lands on one turn, so equalising
  the pooled session left every other slice untouched: gold was recoverable from user-turn length
  at AUC **1.000** in WorkingMemory and from user-turn sentence count in Forgetting, while every
  pooled figure sat under the bar. Equalising each role then left the *first* user turn — the one
  carrying the evidence — separable at 1.000 again. Padding is now applied per turn slot, and the
  refused set carries the per-role and first-turn variants of every numeric feature. Worst over
  every slice tried is now 0.701.

- **The refused-feature list covered only the tells we had already thought of.** Measured properly,
  v2 gold was recoverable from Forgetting at AUC **1.000** by the literal substring `"Noted"`, at
  0.95 on two verticals by the presence of an em dash, and at 0.990 in WorkingMemory by counting
  full stops. `ECHO_LEAD not in session` was the v1 defect; `"Noted" in session` is the same defect
  wearing a different string.

- **A literal backspace byte in the probes' negative-gold guard**, where a word boundary was
  intended, made its leading-negative alternative unmatchable — half the guard was dead code from
  the day it was written.

- **A greedy number pattern captured `2026,` with its trailing comma**, which then failed to match
  the bare `2026` the prompt itself supplied, so a year the model had been *handed* counted as
  evidence it had reached the corpus. Month and weekday names no longer count as distinctive
  evidence either; in a corpus family made of dates they are world knowledge. Together these
  reported a Prospective leak on an answer that said, in as many words, that the conversations did
  not contain the information.

- **The C# separability test took its threshold and its list of features to check from the record
  it was testing**, so a trimmed `refused_features` array or a `threshold_auc` of 0.99 would have
  passed. Both are C# constants now, and the AUCs are recomputed from the corpus.

- **The citation rule is built from the revision constant** instead of retyped beside it. The
  projected result — the copy that reaches a consumer's report — named a revision the corpora had
  already left behind.

- Documentation claims corrected where they overstated what is verified: three of four "structural,
  in CI" assertions did not exist (they do now), the validity-rule table said all seven rules are
  re-checked in CI when only V4, V5 and V7 can be, the Episodic attribution limitation was labelled
  v1 and promised a fix in v2 that v2 did not ship, and the CLI told users to run `agenteval init`
  when the workspace bootstrap is `agenteval init-workspace`.
