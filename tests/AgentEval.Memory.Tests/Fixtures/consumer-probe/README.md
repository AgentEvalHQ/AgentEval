# TypedMemEval within-question separability probe — v4 acceptance fixtures

Handoff from the AgentMemory .NET measurement track to the AgentEval maintainer, 2026-08-16.
This is the probe that found the `'on the'` (episodic, 0.763) marker on the v3 corpora — the
finding your deep-dive traced to the stopword-dropping tokenizer and extended to `'i have'`
(WorkingMemory, AUC 0.958, present since v1). Per your ask, the next corpus has to clear this
probe as well as your own V7 before it ships. Everything needed to re-run it is in this folder.

## Contents

| File | What it is |
|---|---|
| `within-question-probe.json` | Full probe output on the five **v3** corpora (`AgentEval 0.23.0-beta`, published package): per-vertical numeric-feature table (pooled within-question AUC + per-question distribution) and the top-25 n-gram screen rows |
| `meta-<vertical>.json` | The five corpus `meta.json` files as read from the published package via `TypedMemEvalCorpus.ReadMetadataJson` — the probe's reference for ids, SHAs, and your own V7 records |
| `probe-source-Program.cs` / `probe-source-V3Check.csproj` | The probe itself: a standalone console app referencing the published `AgentEval` package. Point the `PackageReference` at the candidate version, `dotnet run -- <outDir>`, read the console + JSON |

## Methodology (the parts that are load-bearing)

1. **Within-question pairs, never pooled statistics.** For every gold-bearing question, (gold,
   distractor) pairs are formed only against that question's own haystack. Pooled per-corpus
   statistics are exactly the mistake that let v2 certify itself (your 0.903 → 0.616 dilution
   example); our first pass made the same mistake and read v3 as clean.
2. **Pooled-pairs AUC, folded once.** Pairs are pooled across questions (weighting by pair
   count), AUC computed by rank (ties = 0.5), folded to [0.5, 1.0] at the end — matching your V7
   definition so numbers are directly comparable across the two implementations.
3. **Per-question AUC distribution, not just the mean.** For every feature we also report the
   per-question folded AUC's mean / median / p90 / max, the share of questions ≥ 0.75, and the
   share at 1.0. A bimodal split (half the questions perfectly separable, half clean) hides in a
   mean; the distribution is what showed workingmemory's per-question medians near 0.9 on
   size-style features whose pooled AUC looked innocent.
4. **Refusal threshold: 0.75 pooled within-question**, adopted from your V7 gate. Anything ≥ 0.75
   on a non-exempt feature blocks the corpus.
5. **Relevance exemption (yours, adopted):** gold is *supposed* to out-relevance a distractor;
   the BM25 calibration gate bounds that channel. Operationally we test it with question-text
   presence: a marker that also appears in ~100% of the question texts (wm `'have'`, in every
   "…do I have?" question) is relevance-channel and exempt-but-reported; a marker near-absent
   from question texts (`'on the'`: 4%) is not relevance and counts against the corpus.
   Structural design variables declared exempt in the meta (wm `position_in_haystack`) are
   honoured and re-verified, not re-litigated.

## Feature inventory

**Numeric, per session** (25): turn_count, session_chars, mean_turn_chars, user/assistant turn
counts and chars, first/last turn chars, first_user / first_assistant / last_assistant chars,
punctuation-class counts (full stop, comma, em dash incl. ` -- ` / ` - ` spellings, question mark,
exclamation, semicolon, colon, apostrophe incl. ’), punct_density, digit_density,
uppercase_density, date_mention_count (months, weekdays, today/tomorrow/yesterday/tonight,
next/last week/month, ISO dates, d/m, ordinals), position_in_haystack.

**N-gram presence screen**: unigrams + bigrams over **raw lowercase word tokens — STOPWORDS
INCLUDED**. This is the root-cause requirement: your V7 `tokenize()` dropped stopwords, which
made `'on the'`, `'have'`, `'has'`, and `'i have'` *unrepresentable* as features, and `'i have'`
was sitting at 0.958 since v1. Any v4 screen (and any re-run of this probe) must tokenize raw.
Candidate restriction: n-grams recurring in ≥ 20% of the vertical's questions, so per-question
content is not mistaken for a corpus-wide marker (your rule, kept). Presence is binary per
session; the 2×2 within-question table gives the pair AUC directly.

## v3 results in one line each (the fixture baseline v4 must beat)

- Numeric features: clean everywhere (worst non-exempt: prospective date_mention_count 0.744).
- N-gram screen: episodic `'on the'` **0.763** (gold 76% / distractor 22% / question-text 4% —
  the blocker); wm `'have'` 0.791 (relevance-exempt, reported); arithmetic `'today'` 0.737
  (replicates your 0.7365); wm `'has'`-absence 0.707. Mechanism: gold states datable
  first-person facts; filler doesn't — statement grammar differs.
- Hygiene (all clean on v3, keep asserting them): gold ids present in haystack ids, no duplicate
  session ids, zero HasAnswer flags on distractor sessions, date-count = session-count.

## Acceptance criterion proposed for v4

For every vertical: no non-exempt numeric feature and no raw-token n-gram (unigram or bigram,
≥20% question recurrence) with pooled within-question folded AUC ≥ 0.75, and no feature whose
per-question distribution shows share-at-1.0 ≥ 25% even with a passing pooled value; every
exemption named in the meta with its reason. Both probes (yours with `tokenize_raw`, this one)
run against the *published package bytes*, not the source tree.
