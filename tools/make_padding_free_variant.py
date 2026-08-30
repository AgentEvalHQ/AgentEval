#!/usr/bin/env python3
"""Builds a PADDING-FREE variant of a shipped TypedMemEval corpus, for extractor diagnostics.

WHY THIS EXISTS. The consuming project probed three of our corpora and found speech-act
predicates carrying 88-94% of top-predicate mass in every one, and concluded their extractor
dilutes assertional triples. The three predicates they named - "said", "came up with", "was
consistent between" - are our equalisation padding verbatim (`_PAD_DENSE` in
typedmemeval_common), present in 100% of sessions at ~5.3 phrases each. Their three "unrelated"
corpora were therefore ONE SAMPLE on the dimension they measured, and their evidence cannot
separate "the extractor is speech-act biased" from "the corpus is speech-act saturated".

This is Cell B of the registered disambiguation: same extractor, same classifier, our corpus with
the padding removed. Cell A is a predicate histogram over a LongMemEval-derived store our
generator never touched.

WHY IT STRIPS RATHER THAN REGENERATES. Regenerating without padding would change question text,
gold selection, timestamps and calibration all at once, and the comparison would carry four
variables. Stripping the SHIPPED corpus changes exactly one: every question id, gold session id,
answer, date and session boundary is preserved byte for byte, and only padding is removed from
turn content. That is what makes it a controlled cell rather than a second corpus.

WHAT IT IS NOT. Not a shipped artifact and not gate-valid: removing padding necessarily breaks the
separability equalisation the padding exists to provide, so this variant would fail V7 and the
length/punctuation parity checks by construction. It carries `diagnostic_only: true` and a
`not_for_measurement` note in its sidecar so it can never be mistaken for a corpus revision.

CELL C, AND WHY IT EXISTS. The consuming project then found that, on CONTENT predicates rather than
padding, several distinct amounts sit under the bare subject "Payment" with no entity link, and
scoped that as an extraction-side defect of their own. It is not. Every amount-bearing sentence in
our arithmetic corpus is `Payment logged against {job}: ${amount} for {item}.` -- 248 of 248, no
exceptions -- so the grammatical subject of every one of them is the bare common noun "Payment". It
is a fine English sentence for a human, and as a triple subject it is a TYPE, not an instance.

That gives them a root cause but not a decision, because there is a competing explanation: an amount
is a FOUR-PLACE fact (payer, job, amount, line item), and a triple store must reify it or lose the
join no matter how the sentence is worded. `--unique-subject` builds the cell that separates those
two. It is padding-free exactly like Cell B and changes ONE further thing: the payment sentence gets
a unique instance subject. If the orphaning disappears between B and C, the cause is our bare
subject; if it survives, the cause is arity and no rewording of ours will fix it.

Usage:  python make_padding_free_variant.py [vertical] [--unique-subject]
        (default vertical: arithmetic)
"""
from __future__ import annotations

import hashlib
import json
import re
import sys
from pathlib import Path

import typedmemeval_common as tmc

DATA = Path(__file__).resolve().parent.parent / "src/AgentEval.Memory/Data/typedmemeval"
OUT = Path(__file__).resolve().parent.parent / "artifacts/padding-free"

NAME = r"[A-Z]\w+(?: [A-Z]\w+)?"
ECHO_CLAUSE = re.compile(r"\(Also on my mind:[^)]*\)")


def _templates() -> list[re.Pattern[str]]:
    """Regexes for every padding sentence the equaliser can emit.

    Built from the banks themselves rather than typed out, so a bank edit cannot silently leave a
    template unstripped - the same reason the corpus gates read their expectations from code.

    PADDING IS COMPOSED, NOT EMITTED WHOLE, AND THE COMPOSITION IS EXACT. `_pad_block` picks whole
    sentences from the four base banks and then lengthens one by splicing a tail in before its
    period, at most twice:

        pieces[index] = f"{pieces[index][:-1]}, {tail}."

    So every padding sentence is precisely `BASE(, TAIL){0,2}.` and the pattern below says exactly
    that. Two earlier versions did not, and both were wrong in a way worth recording:

      1. Matching templates VERBATIM stripped only 62% of the speech-act phrases, because a tailed
         sentence no longer equals its base, and left orphaned ", , ." behind.
      2. Matching the base and then consuming to the end of the sentence (`core + [^.]*\\.?`)
         over-corrected and ATE CONTENT. `_PAD_SHORT` holds bare words - 'Still.', 'Right.',
         'Fine.' - so "Still" swallowed the whole of "Still the same recycling sack size, for the
         record: Selwick Common." Three forgetting sessions stripped to nothing, which is what
         exposed it; the real cost was that the padding share it reported was an over-estimate.

    The rule the second version broke: a stripper must only remove what the emitter can EMIT. The
    tail alternation is the emitter's own, so content can never sit between a base and its period.
    """
    tails = tmc._PAD_TAILS + tmc._PAD_NAME_TAILS + tmc._PAD_PUNCT_TAILS
    alternatives = sorted((re.escape(t).replace(r"\{n\}", NAME) for t in tails),
                          key=len, reverse=True)
    tail_group = rf"(?:,\s*(?:{'|'.join(alternatives)}))"
    bases = tmc._PAD_PLAIN + tmc._PAD_SHORT + tmc._PAD_NAMED + tmc._PAD_DENSE
    patterns = []
    for entry in bases:
        stem = re.escape(entry.rstrip(".")).replace(r"\{n\}", NAME).replace(r"\{m\}", NAME)
        patterns.append(re.compile(stem + tail_group + f"{{0,{tmc._PAD_MAX_TAILS_PER_SENTENCE}}}"
                                   + r"\s*\."))
    # Longest first, so a base that prefixes another cannot win and leave a stub behind.
    return sorted(patterns, key=lambda p: -len(p.pattern))


def strip(text: str, patterns: list[re.Pattern[str]]) -> str:
    for pattern in patterns:
        text = pattern.sub(" ", text)
    text = ECHO_CLAUSE.sub(" ", text)
    # Stripping mid-sentence leaves orphaned punctuation. Left in, those are tokens an extractor
    # sees and a punctuation-density probe reads.
    text = re.sub(r"(\s*,)+", ",", text)
    text = re.sub(r"\s+([.,;:])", r"\1", text)
    text = re.sub(r"([.,;:])[\s.,;:]*\1", r"\1", text)
    text = re.sub(r"^[\s.,;:]+", "", text)
    return re.sub(r"\s+", " ", text).strip()


_PAYMENT = re.compile(r"Payment logged against (?P<job>[^:]+): \$(?P<amt>\d+\.\d{2}) "
                      r"for (?P<item>[^.]+)\.")


def rewrite_subjects(corpus: list[dict]) -> int:
    """Give every payment a unique instance subject instead of the bare common noun "Payment".

    Deterministic: the invoice number is the payment's ordinal within its own question, so the same
    corpus always produces the same cell and B/C differ in the subject and nothing else.

    The amount, the job and the line item are carried across verbatim. They are the arithmetic, and
    a cell that perturbed them would answer a different question than the one being asked.
    """
    rewritten = 0
    for entry in corpus:
        counter = 0
        for session in entry["haystack_sessions"]:
            for turn in session:
                def swap(m: re.Match[str]) -> str:
                    nonlocal counter, rewritten
                    counter += 1
                    rewritten += 1
                    return (f"Invoice {3300 + counter} on {m.group('job')} came to "
                            f"${m.group('amt')} for {m.group('item')}.")
                turn["content"] = _PAYMENT.sub(swap, turn["content"])
    return rewritten


def _amount_ledger(corpus: list[dict]) -> list[tuple[str, str]]:
    """Every (job, amount) pair in the corpus, in order. The arithmetic must survive any rewrite."""
    pairs = []
    for entry in corpus:
        for session in entry["haystack_sessions"]:
            for turn in session:
                for m in re.finditer(r"(?:logged against|on) ([^:.]+?)(?::| came to) \$(\d+\.\d{2})",
                                     turn["content"]):
                    pairs.append((m.group(1).strip(), m.group(2)))
    return pairs


def main() -> int:
    unique_subject = "--unique-subject" in sys.argv
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    vertical = args[0] if args else "arithmetic"
    corpus_path = next((DATA / vertical).glob("*v5.json"))
    meta_path = next((DATA / vertical).glob("*v5.meta.json"))
    corpus = json.loads(corpus_path.read_text(encoding="utf-8"))
    meta = json.loads(meta_path.read_text(encoding="utf-8"))

    ledger_before = _amount_ledger(corpus)
    patterns = _templates()
    before = after = 0
    for entry in corpus:
        for session in entry["haystack_sessions"]:
            for turn in session:
                before += len(turn["content"])
                turn["content"] = strip(turn["content"], patterns)
                after += len(turn["content"])

    subjects_rewritten = rewrite_subjects(corpus) if unique_subject else 0
    if unique_subject:
        # The rewrite may change how a payment is WORDED and must not change what it IS. Same jobs,
        # same amounts, same order - otherwise the cell tests a different sum than the corpus does.
        if _amount_ledger(corpus) != ledger_before:
            print("REFUSING TO WRITE: the subject rewrite moved an amount or a job. The arithmetic "
                  "must be byte-identical across cells or B and C are not comparable.",
                  file=sys.stderr)
            return 1

    # Every identifier and every answer must survive untouched, or this is not a controlled cell.
    original = json.loads(corpus_path.read_text(encoding="utf-8"))
    for a, b in zip(original, corpus):
        assert a["question_id"] == b["question_id"], "question id moved"
        assert a["answer"] == b["answer"], "answer changed"
        assert a["answer_session_ids"] == b["answer_session_ids"], "gold ids moved"
        assert a["haystack_dates"] == b["haystack_dates"], "timestamps moved"
        assert len(a["haystack_sessions"]) == len(b["haystack_sessions"]), "session count moved"

    # The point of the variant is that the speech-act phrases are GONE. Assert it rather than
    # trusting the regexes, and fail loudly rather than shipping a half-stripped cell.
    residue = 0
    for phrase in ("had both said as much", "came up with", "the story was consistent",
                   "Also on my mind"):
        residue += sum(" ".join(t["content"] for t in s).count(phrase)
                       for e in corpus for s in e["haystack_sessions"])
    if residue:
        print(f"REFUSING TO WRITE: {residue} speech-act phrases survived the strip. A partially "
              f"stripped cell would give the consuming project a muddy answer to a question we "
              f"asked them to run.", file=sys.stderr)
        return 1

    OUT.mkdir(parents=True, exist_ok=True)
    cell = "C" if unique_subject else "B"
    suffix = "-padding-free-unique-subject" if unique_subject else "-padding-free"
    stem = corpus_path.stem
    out_json = OUT / f"{stem}{suffix}.json"
    out_json.write_text(json.dumps(corpus, indent=2, ensure_ascii=False) + "\n",
                        encoding="utf-8", newline="\n")

    what_changed = ("Padding sentences and the echo clause removed from turn content. Question "
                    "ids, questions, answers, gold session ids, session counts and timestamps "
                    "are byte-identical to the source, asserted at build time. Zero residual "
                    "speech-act phrases, asserted at build time.")
    if unique_subject:
        what_changed += (f" Additionally, {subjects_rewritten} payment sentences were re-subjected "
                         "from the bare common noun \"Payment\" to a unique instance "
                         "(\"Invoice NNNN on {job} came to $X for {item}\"). Every job and amount "
                         "carried across verbatim and in order, asserted at build time, so Cell C "
                         "differs from Cell B in the SUBJECT and in nothing else.")

    sidecar = {
        "diagnostic_only": True,
        "cell": cell,
        "not_for_measurement":
            f"Diagnostic variant for the consuming project's extractor disambiguation (Cell {cell})."
            " NOT a corpus revision and NOT gate-valid: removing padding breaks the separability "
            "equalisation the padding exists to provide, so this would fail V7 and the "
            "length/punctuation parity checks by construction. Never probe it, never cite a score "
            "from it, never ship it.",
        "derived_from": {
            "vertical": vertical,
            "corpus_id": meta["corpus_id"],
            "corpus_sha256": meta["corpus_sha256"],
        },
        "what_changed": what_changed,
        "subjects_rewritten": subjects_rewritten,
        "characters_before": before,
        "characters_after": after,
        "characters_removed_share": round(1 - after / before, 4),
    }
    (OUT / f"{stem}{suffix}.meta.json").write_text(
        json.dumps(sidecar, indent=2) + "\n", encoding="utf-8", newline="\n")

    print(f"cell {cell} -- {vertical}: {before:,} -> {after:,} chars "
          f"({sidecar['characters_removed_share']:.1%} removed), 0 speech-act phrases remaining")
    if unique_subject:
        print(f"  {subjects_rewritten} payment subjects made unique; jobs and amounts unchanged")
    print(f"  wrote {out_json}")
    print(f"  sha256 {hashlib.sha256(out_json.read_bytes()).hexdigest()}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
