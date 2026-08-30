#!/usr/bin/env python3
"""How much of each corpus is SIGNAL, and how much is equalisation scaffolding?

WHY THIS EXISTS. Nothing in the family asked this question. The gates check separability (V7),
coverage, answerability (V1), interference (V8) and retrieval (V9) -- every one of them a property
of whether a question can be ANSWERED. None of them asks what fraction of the text an answering
system has to wade through is content at all.

That hole cost the consuming project two false findings in a week. Their extractor produced
speech-act predicates at 88-94% of top-predicate mass and they scoped it as an extraction bias; the
predicates were our padding bank verbatim. Then, on content predicates, they found amounts sitting
under a bare subject with no entity link and scoped it as their own orphaning defect; the bare
subject is our sentence template, 248 of 248. Both times the corpus was the confound, and both times
nothing on our side would have flagged it, because nothing on our side was looking.

WHAT IT MEASURES. Padding share by character, using the same template bank the equaliser emits from
and the same stripper the diagnostic cells are built with, so this number and the cells cannot
disagree. Plus two things the share alone hides:

  - PURE-PADDING SESSIONS: sessions with no content left once padding is removed. To an extractor
    these are sessions that say nothing, and they are indistinguishable from real ones until it has
    already spent the tokens.
  - LEDGER VOICE: sentences whose grammatical subject is a bare common noun that DIRECTLY CARRIES A
    VALUE ("Payment logged against X: $414.30 for Z"). A fine English sentence for a human reader,
    and a TYPE rather than an instance to anything building triples - which is why five distinct
    amounts arrive at a consumer sharing one subject and no entity link.

    The detector is deliberately NARROW, because a loose one lied. A first version matched any
    record-line prefix and reported ledger voice in five verticals; inspection showed it was firing
    on bitemporal's "Correction to the file: Alice Renwick was at Ardenholm" - where the FACT's
    subject is a named entity and the prefix is only framing - on prospective imperatives, and on
    the echo clause's own colon. It was measuring "has a colon near the start", under a column name
    claiming something much stronger. That is the claim-without-instrument shape, so the rule here
    is the same one that catches it elsewhere: name the command that would falsify the column, and
    run it. Narrowed, the count is arithmetic 248 and every other vertical exactly 0.

WHAT IT IS NOT. Not a gate and deliberately not a threshold. Padding is load-bearing -- it is what
equalises length, punctuation and role sequence so V7 cannot separate gold from filler on shape
alone -- so a ceiling picked out of the air would trade a measured property for an invented one.
The point is that the number is PUBLISHED and moves under review, not that it clears a bar.

Usage:  python measure_signal_density.py [--json]
"""
from __future__ import annotations

import glob
import json
import os
import re
import sys
from pathlib import Path

from make_padding_free_variant import _templates, strip

BASE = Path(__file__).resolve().parent.parent / "src/AgentEval.Memory/Data/typedmemeval"

# A capitalised common-noun subject that directly carries a VALUE. See the module docstring for why
# this is narrow: the loose version reported ledger voice in five verticals and was wrong in four.
_LEDGER = re.compile(r"^[A-Z][a-z]+\b[^:.]*:\s*\$?\d")
_SENTENCE = re.compile(r"(?<=\.)\s+")
# The echo clause carries its own colon and often a bare number after it, which the detector would
# otherwise read as a ledger line. It is scaffolding, not a stated fact.
_ECHO = re.compile(r"\(Also on my mind:[^)]*\)")


def main() -> int:
    as_json = "--json" in sys.argv
    patterns = _templates()
    report = {}
    rows = []

    for vertical in sorted(os.listdir(BASE)):
        paths = glob.glob(str(BASE / vertical / "*v5.json"))
        if not paths:
            continue
        corpus = json.loads(Path(paths[0]).read_text(encoding="utf-8"))

        chars = kept = 0
        sessions = empty_sessions = 0
        value_sentences = ledger_sentences = 0
        for entry in corpus:
            for session in entry["haystack_sessions"]:
                sessions += 1
                remaining = 0
                for turn in session:
                    content = turn["content"]
                    chars += len(content)
                    stripped = strip(content, patterns)
                    kept += len(stripped)
                    remaining += len(stripped)
                    for sentence in _SENTENCE.split(_ECHO.sub(" ", content)):
                        sentence = sentence.strip()
                        if re.search(r"\$?\d", sentence):
                            value_sentences += 1
                            if _LEDGER.match(sentence):
                                ledger_sentences += 1
                if remaining == 0:
                    empty_sessions += 1

        padding_share = 1 - kept / chars if chars else 0.0
        ledger_share = ledger_sentences / value_sentences if value_sentences else 0.0
        report[vertical] = {
            "characters": chars,
            "characters_of_content": kept,
            "padding_share": round(padding_share, 4),
            "sessions": sessions,
            "pure_padding_sessions": empty_sessions,
            "value_bearing_sentences": value_sentences,
            "ledger_voice_sentences": ledger_sentences,
            "ledger_voice_share": round(ledger_share, 4),
        }
        rows.append((vertical, chars, padding_share, sessions, empty_sessions, ledger_share))

    if as_json:
        print(json.dumps(report, indent=2))
        return 0

    print("SIGNAL DENSITY - how much of each corpus is content rather than scaffolding")
    print("=" * 96)
    print(f"{'vertical':14s} {'chars':>9s} {'padding':>8s} {'sessions':>9s} "
          f"{'pure-padding':>13s} {'ledger voice':>13s}")
    print("-" * 96)
    for v, chars, pad, sess, empty, ledger in rows:
        print(f"{v:14s} {chars:9,d} {pad:7.1%} {sess:9d} {empty:13d} {ledger:12.1%}")
    print("-" * 96)
    total = sum(r[1] for r in rows)
    content = sum(report[r[0]]["characters_of_content"] for r in rows)
    print(f"{'FAMILY':14s} {total:9,d} {1 - content / total:7.1%} "
          f"{sum(r[3] for r in rows):9d} {sum(r[4] for r in rows):13d}")
    print()
    print("padding      : share of characters emitted by the equalisation banks.")
    print("pure-padding : sessions with NO content left once padding is removed - to a consumer")
    print("               these say nothing, and look like real sessions until the tokens are spent.")
    print("ledger voice : share of value-bearing sentences whose subject is a bare common noun")
    print("               carrying the value directly - a TYPE, not an instance, to a triple store.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
