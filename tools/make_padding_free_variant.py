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

Usage:  python make_padding_free_variant.py [vertical]      (default: arithmetic)
"""
from __future__ import annotations

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

    PADDING IS COMPOSED, NOT EMITTED WHOLE. The equaliser appends tails to a base sentence to hit
    its character and punctuation targets, so a verbatim template match leaves most of it behind:
    a first version of this matched templates exactly, stripped only 62% of the speech-act phrases
    and left orphaned ", , ." in the text. Each pattern therefore matches the template's CORE and
    consumes to the end of the sentence it sits in.
    """
    banks = (tmc._PAD_PLAIN + tmc._PAD_SHORT + tmc._PAD_NAMED + tmc._PAD_DENSE
             + tmc._PAD_TAILS + tmc._PAD_NAME_TAILS + tmc._PAD_PUNCT_TAILS)
    patterns = []
    for entry in banks:
        core = re.escape(entry.rstrip(" .")).replace(r"\{n\}", NAME).replace(r"\{m\}", NAME)
        patterns.append(re.compile(core + r"[^.]*\.?"))
    # Longest first: a short tail that is a prefix of a longer one must not win and leave a stub.
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


def main() -> int:
    vertical = sys.argv[1] if len(sys.argv) > 1 else "arithmetic"
    corpus_path = next((DATA / vertical).glob("*v5.json"))
    meta_path = next((DATA / vertical).glob("*v5.meta.json"))
    corpus = json.loads(corpus_path.read_text(encoding="utf-8"))
    meta = json.loads(meta_path.read_text(encoding="utf-8"))

    patterns = _templates()
    before = after = 0
    for entry in corpus:
        for session in entry["haystack_sessions"]:
            for turn in session:
                before += len(turn["content"])
                turn["content"] = strip(turn["content"], patterns)
                after += len(turn["content"])

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
    stem = corpus_path.stem
    (OUT / f"{stem}-padding-free.json").write_text(
        json.dumps(corpus, indent=2, ensure_ascii=False) + "\n", encoding="utf-8", newline="\n")

    sidecar = {
        "diagnostic_only": True,
        "not_for_measurement":
            "Padding-free variant built for extractor diagnostics (Cell B). NOT a corpus revision "
            "and NOT gate-valid: removing padding breaks the separability equalisation the padding "
            "exists to provide, so this would fail V7 and the length/punctuation parity checks by "
            "construction. Never probe it, never cite a score from it, never ship it.",
        "derived_from": {
            "vertical": vertical,
            "corpus_id": meta["corpus_id"],
            "corpus_sha256": meta["corpus_sha256"],
        },
        "what_changed": "Padding sentences and the echo clause removed from turn content. Question "
                        "ids, questions, answers, gold session ids, session counts and timestamps "
                        "are byte-identical to the source, asserted at build time. Zero residual "
                        "speech-act phrases, asserted at build time.",
        "characters_before": before,
        "characters_after": after,
        "characters_removed_share": round(1 - after / before, 4),
    }
    (OUT / f"{stem}-padding-free.meta.json").write_text(
        json.dumps(sidecar, indent=2) + "\n", encoding="utf-8", newline="\n")

    print(f"{vertical}: {before:,} -> {after:,} chars "
          f"({sidecar['characters_removed_share']:.1%} removed), 0 speech-act phrases remaining")
    print(f"  wrote {OUT / (stem + '-padding-free.json')}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
