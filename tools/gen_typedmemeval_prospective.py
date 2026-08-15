#!/usr/bin/env python3
"""
Generates TypedMemEval-Prospective v1 (ADR-026 §5.1) -- 50 questions.

What the vertical measures: whether a system places remembered facts *in time*. Things
due later, validity that expires, assertions not yet true. Run under TimestampsOnly
grounding, where the harness strips its own printed dates, a system that stamps messages
with ingestion time has nothing left to read.

    Seed carry-over (from agenteval-timegrounded-v1)   12
    Due-later reminders                                16   =  8 before/after pairs
    Expiring validity                                  12   =  6 before/after pairs
    Not-yet-true assertions                            10   =  5 before/after pairs

The pairs are the teeth. Each pair is one haystack asked twice, differing only in
question_date: once before the pivotal instant, once after. Gold flips between the arms
by construction, so a system that fires the reminder early is visibly *premature* rather
than merely wrong -- a distinction no single question can draw.

V4 governs every message: no absolute date, no four-digit year anywhere in the
conversations. Every temporal expression a speaker uses is relative ("in eight weeks",
"a fortnight from tomorrow"), so resolving it requires the session's own timestamp.
Gold answers do carry absolute dates -- they are the *answer* -- and every one of them is
computed here from the session timestamps rather than typed, so the arithmetic in the
answers cannot drift from the arithmetic in the conversations (V5).

Run:  python tools/gen_typedmemeval_prospective.py
"""

from __future__ import annotations

import json
import random  # DevSkim: ignore DS148264 - corpus generation must be replayable under a seed; a CSPRNG cannot be seeded to reproduce a draw, and this selects filler text, not secrets.
from datetime import datetime, timedelta
from pathlib import Path

import typedmemeval_common as tmc

TG_CORPUS = (Path(__file__).resolve().parent.parent / "src" / "AgentEval.Memory" / "Data" /
             "longmemeval" / "timegrounded" / "agenteval-timegrounded-v1.json")

SHAPE_SEED = "seed-carry-over"
SHAPE_REMINDER = "due-later-reminder"
SHAPE_VALIDITY = "expiring-validity"
SHAPE_NOT_YET = "not-yet-true"

TYPE_SEED_ASOF = "temporal-as-of"
TYPE_SEED_CURRENT = "temporal-current"
TYPE_SEED_PROSPECTIVE = "prospective-memory"
TYPE_REMINDER = "prospective-reminder"
TYPE_VALIDITY = "prospective-validity"
TYPE_NOT_YET = "prospective-not-yet"

# Relative phrasings and the week offsets they mean. The generator computes every gold
# date from (session timestamp + offset), so the phrase and the answer cannot disagree.
OFFSETS = [
    ("in three weeks", 3), ("in five weeks", 5), ("in six weeks", 6),
    ("in eight weeks", 8), ("in nine weeks", 9), ("in eleven weeks", 11),
    ("in four weeks", 4), ("in seven weeks", 7), ("in ten weeks", 10),
    ("in twelve weeks", 12), ("in thirteen weeks", 13), ("in fifteen weeks", 15),
    ("in sixteen weeks", 16), ("in eighteen weeks", 18),
]

REMINDERS = [
    ("renew the allotment lease", "the allotment lease renewal"),
    ("book the boiler service", "the boiler service booking"),
    ("send the quarterly figures to Priya", "the figures for Priya"),
    ("re-tension the bike chain", "the bike chain"),
    ("return the borrowed telescope", "the borrowed telescope"),
    ("submit the conference abstract", "the conference abstract"),
    ("chase the deposit refund", "the deposit refund"),
    ("swap the smoke-alarm batteries", "the smoke-alarm batteries"),
    ("reorder the printer toner", "the printer toner"),
    ("file the allotment water claim", "the water claim"),
]

VALIDITY = [
    ("climbing-wall pass", "the climbing-wall pass"),
    ("ferry multi-trip ticket", "the ferry ticket"),
    ("museum membership", "the museum membership"),
    ("parking permit", "the parking permit"),
    ("language-app subscription", "the language-app subscription"),
    ("physiotherapy referral", "the physiotherapy referral"),
    ("tool-library card", "the tool-library card"),
    ("darkroom booking credit", "the darkroom credit"),
]

NOT_YET = [
    ("start at Halloway Instruments", "started at Halloway Instruments", "Halloway Instruments"),
    ("move into the flat on Ferrier Row", "moved into the Ferrier Row flat", "the Ferrier Row flat"),
    ("take over the Tuesday pottery class", "taken over the Tuesday pottery class", "the pottery class"),
    ("switch to the four-day week", "switched to the four-day week", "the four-day week"),
    ("hand the archive over to Nadia", "handed the archive over to Nadia", "the archive handover"),
    ("begin the coastal survey", "begun the coastal survey", "the coastal survey"),
    ("open the studio on Wexford Lane", "opened the studio on Wexford Lane", "the Wexford Lane studio"),
]

# Same-domain filler: other people's plans, other deadlines, other passes. Same register as
# the gold sessions and containing no answer -- V3's "plausible, not a strawman" rule. The
# calibration gate turns the echo knob on top of these to make them actually compete.
FILLER = [
    ("The upstairs neighbour is repainting the stairwell {when}.", "That will brighten the whole landing."),
    ("Dad's hospital review got pushed back {when}.", "Frustrating, but at least it is booked."),
    ("The book group picked something enormous for {when}.", "Better start early, then."),
    ("Our team stand-up is moving to mornings {when}.", "Earlier starts, but shorter meetings."),
    ("Sam's car is in for its test {when}.", "Hopefully nothing expensive turns up."),
    ("The allotment committee meets again {when}.", "Any chance you will go this time?"),
    ("I promised to help Rosa move a piano {when}.", "That is a favour you will feel the next day."),
    ("The library wants its overdue atlas back {when}.", "Worth a trip before the fine grows."),
    ("Kit is running a half marathon {when}.", "That takes some training."),
    ("The choir has an extra rehearsal {when}.", "Sounds like a busy stretch."),
    # Was "inspecting the gutters", which collided with the carried question about a flat
    # INSPECTION: given only distractors, the reference model found this one, reasoned it was
    # past, and produced the gold answer without ever seeing the evidence. V3 caught it.
    ("Our landlord is repointing the brickwork {when}.", "Tidy the yard beforehand, maybe."),
    ("Jo's visa interview is {when}.", "Fingers crossed it goes smoothly."),
    ("The cycling club is doing a night ride {when}.", "Lights charged?"),
    ("I owe Marta a proper reply {when}.", "A short note beats a perfect one that never comes."),
    ("The wholesaler is closed for stocktake {when}.", "Worth buying ahead."),
    ("Our smoke detector chirped again {when}.", "That usually means the battery is going."),
    ("The pottery kiln is booked solid {when}.", "Plan around it."),
    ("Ravi asked about borrowing the roof box {when}.", "Only if it comes back clean."),
]

#: Every gold reminder is phrased "in N weeks" (see OFFSETS), so filler has to speak the same way.
#: When only one of these eight said "weeks", the bare token found gold at AUC 0.852 — a classifier
#: needed no more than a substring search to pick the evidence out of the haystack. The spans stay
#: relative, never absolute, because V4 forbids a printed date anywhere in a session.
WHENS = ["in two weeks", "in three weeks or so", "in four weeks", "in six weeks",
         "in about ten weeks", "in five weeks", "in a couple of weeks", "in seven weeks"]


def _filler_session(rng: random.Random, echo_terms: list[str], stamp: datetime) -> tmc.Session:
    user, assistant = rng.choice(FILLER)
    user = user.format(when=rng.choice(WHENS))
    return tmc.make_session(stamp, (user, tmc.weave_echo(assistant, echo_terms)), tag="filler")


def _fmt(dt: datetime) -> str:
    """Gold-answer date rendering. Absolute by necessity -- it is the answer -- and never
    reachable from the conversations, which carry no absolute dates at all (V4)."""
    return dt.strftime("%-d %B %Y") if hasattr(dt, "strftime") and _supports_dash() else dt.strftime("%d %B %Y").lstrip("0")


def _supports_dash() -> bool:
    try:
        datetime(2026, 1, 5).strftime("%-d")
        return True
    except ValueError:
        return False


def _pair(
    qid_before: str,
    qid_after: str,
    pair_id: str,
    shape: str,
    qtype: str,
    question_text: str,
    gold_user_template: str,
    gold_assistant: str,
    answer_before_template: str,
    answer_after_template: str,
    anchor: datetime,
    rng: random.Random,
    echo: float,
    filler_count: int,
) -> list[tmc.Question]:
    """Builds one before/after pair over a single shared haystack.

    The gold session's timestamp is decided by where it lands in the shuffled haystack, so the due
    date has to be derived from that FINAL timestamp -- not from the anchor the caller passed in.
    Deriving it from the anchor was this generator's first version, and every one of its 38 paired
    questions failed the V1 oracle probe: the answers named dates the conversations could not
    produce, which is precisely the drift V5 exists to prevent. The probe found it; the fix is to
    build the haystack first and read the timestamp back.

    Both arms share one haystack, deep-copied so a mutation on one arm cannot reach the other.
    Independence between the arms is a property of the *run* -- the runner resets the agent between
    questions -- not of the corpus, which must present both arms with identical evidence.
    """
    echoed = tmc.echo_terms(question_text, echo, rng)

    gold = tmc.make_session(anchor, ("", gold_assistant), gold_turn=0, tag="gold")
    filler = [_filler_session(rng, echoed, anchor) for _ in range(filler_count)]
    sessions = list(filler)
    gold_index = rng.randint(0, len(sessions))
    sessions.insert(gold_index, gold)

    stamps = tmc.spread(anchor, len(sessions), hours=36)
    for session, stamp in zip(sessions, stamps):
        session.timestamp = stamp

    gold_stamp = sessions[gold_index].timestamp
    latest = sessions[-1].timestamp

    # The offset has to clear the rest of the haystack: a reminder that falls due before the last
    # conversation would put the pivot inside the evidence, and the before-arm would be querying a
    # moment it had already lived through.
    required_weeks = ((latest - gold_stamp).days // 7) + 2
    candidates = [(phrase, weeks) for phrase, weeks in OFFSETS if weeks >= required_weeks]
    phrase, weeks = candidates[rng.randrange(len(candidates))]
    pivot = gold_stamp + timedelta(weeks=weeks)

    # Written only now that the phrase is known, so the words in the conversation and the date in
    # the answer are two views of the same arithmetic.
    sessions[gold_index].turns[0].content = gold_user_template.format(
        phrase=phrase, span=phrase.removeprefix("in "))
    answer_before = answer_before_template.format(date=_fmt(pivot))
    answer_after = answer_after_template.format(date=_fmt(pivot))

    before_date = pivot - timedelta(days=6)
    after_date = pivot + timedelta(days=6)
    assert before_date > latest, f"{pair_id}: before-arm query precedes its own haystack"

    common = {"pair_id": pair_id, "shape": shape}
    return [
        tmc.Question(qid_before, qtype, question_text, answer_before, before_date,
                     _copy(sessions), {**common, "arm": "before"}),
        tmc.Question(qid_after, qtype, question_text, answer_after, after_date,
                     _copy(sessions), {**common, "arm": "after"}),
    ]


def _copy(sessions: list[tmc.Session]) -> list[tmc.Session]:
    return [
        tmc.Session([tmc.Turn(t.role, t.content, t.has_answer) for t in s.turns],
                    s.timestamp, s.is_gold, s.tag)
        for s in sessions
    ]


def _seed_questions(rng: random.Random, echo: float, start_index: int) -> list[tmc.Question]:
    """Carries the twelve time-grounded probe questions in as the vertical's seed.

    Their gold answers are stated in terms of their own session timestamps, so those
    timestamps are preserved exactly and the added distractors are stamped into the gaps
    around them. Re-stamping the gold sessions would silently invalidate every carried
    answer -- the questions would still look fine and would all be wrong.
    """
    raw = json.loads(TG_CORPUS.read_text(encoding="utf-8"))
    out: list[tmc.Question] = []
    for offset, entry in enumerate(raw):
        qid = f"tme-pro-{start_index + offset:03d}"
        question_text = entry["question"]
        echoed = tmc.echo_terms(question_text, echo, rng)

        sessions: list[tmc.Session] = []
        for turns, date in zip(entry["haystack_sessions"], entry["haystack_dates"]):
            stamp = datetime.strptime(date, tmc.DATE_FORMAT)
            built = [tmc.Turn(t["role"], t["content"], bool(t.get("has_answer"))) for t in turns]
            sessions.append(tmc.Session(built, stamp,
                                        is_gold=any(t.has_answer for t in built), tag="tg"))

        # Pad up to the declared H range with same-domain filler, timestamped strictly
        # inside the original span so the carried gold keeps its position in time.
        # Padding is spread on BOTH sides of the carried block, not stacked before it. Stacking it
        # earlier pinned every carried question's gold to the tail of its haystack, which the
        # metadata then described as position-shuffled -- a position artefact and a false claim in
        # one. The carried gold keeps its own timestamps either way, so the answers stay correct.
        question_date = datetime.strptime(entry["question_date"], tmc.DATE_FORMAT)
        earliest = min(s.timestamp for s in sessions)
        latest = max(s.timestamp for s in sessions)
        wanted = rng.randint(12, 18) - sum(1 for s in sessions if not s.is_gold)
        for i in range(max(0, wanted)):
            # Later padding has to stay strictly before the question is asked; a carried question's
            # query time is fixed by the corpus it came from and cannot be pushed out to make room.
            later = latest + timedelta(hours=30 * (i + 1))
            stamp = (later if rng.random() < 0.5 and later < question_date - timedelta(days=1)
                     else earliest - timedelta(hours=30 * (i + 1)))
            sessions.append(_filler_session(rng, echoed, stamp))
        sessions.sort(key=lambda s: s.timestamp)

        out.append(tmc.Question(
            qid,
            entry["question_type"],
            question_text,
            entry["answer"],
            question_date,
            sessions,
            {"shape": SHAPE_SEED, "seeded_from": entry["question_id"]},
        ))
    return out


def build(echo: float, rng: random.Random) -> list[tmc.Question]:
    questions: list[tmc.Question] = []
    questions += _seed_questions(rng, echo, start_index=1)

    index = 13
    pair_no = 1
    base = datetime(2026, 3, 2, 9, 30)

    # --- Due-later reminders: 8 pairs -------------------------------------------------
    for i in range(8):
        task, noun = REMINDERS[i % len(REMINDERS)]
        questions += _pair(
            f"tme-pro-{index:03d}", f"tme-pro-{index + 1:03d}", f"tme-pro-p{pair_no:02d}",
            SHAPE_REMINDER, TYPE_REMINDER,
            f"Has the reminder about {noun} come due yet, and on what date is or was it due?",
            f"Remind me to {task} {{phrase}}.",
            "",
            f"Not yet. You asked to be reminded to {task}, and it falls due on {{date}}, "
            f"which is still ahead of you.",
            f"Yes. The reminder to {task} came due on {{date}}, which has now passed.",
            base + timedelta(days=11 * i), rng, echo, filler_count=rng.randint(12, 17),
        )
        index += 2
        pair_no += 1

    # --- Expiring validity: 6 pairs ---------------------------------------------------
    for i in range(6):
        thing, noun = VALIDITY[i % len(VALIDITY)]
        questions += _pair(
            f"tme-pro-{index:03d}", f"tme-pro-{index + 1:03d}", f"tme-pro-p{pair_no:02d}",
            SHAPE_VALIDITY, TYPE_VALIDITY,
            f"Is {noun} still valid, and when does or did it run out?",
            f"I picked up a {thing} today, it stays valid for {{span}} from today.",
            "Good to know. Enjoy it while it lasts.",
            f"Yes, still valid. The {thing} runs out on {{date}}.",
            f"No, it has expired. The {thing} ran out on {{date}}.",
            base + timedelta(days=9 * i + 4), rng, echo, filler_count=rng.randint(12, 17),
        )
        index += 2
        pair_no += 1

    # --- Not-yet-true assertions: 5 pairs ---------------------------------------------
    for i in range(5):
        future, past, noun = NOT_YET[i % len(NOT_YET)]
        questions += _pair(
            f"tme-pro-{index:03d}", f"tme-pro-{index + 1:03d}", f"tme-pro-p{pair_no:02d}",
            SHAPE_NOT_YET, TYPE_NOT_YET,
            f"Have I {past} yet, and on what date does or did that happen?",
            f"I am due to {future} {{phrase}}.",
            "That is a real change, good luck with it.",
            f"Not yet. You are due to {future} on {{date}}, which has not arrived.",
            f"That date has passed. You were due to {future} on {{date}}, so it is no longer ahead of "
            f"you — though nothing since then records whether it went ahead.",
            base + timedelta(days=13 * i + 2), rng, echo, filler_count=rng.randint(12, 17),
        )
        index += 2
        pair_no += 1

    return questions


def check_pairs(questions: list[tmc.Question]) -> list[str]:
    """Pair-specific validity: both arms present, gold genuinely flips, pivot strictly
    between the query times. A pair whose arms agree has no signal, and would report a
    system as consistent when the corpus never asked it anything different."""
    failures: list[str] = []
    pairs: dict[str, list[tmc.Question]] = {}
    for q in questions:
        pid = q.extension.get("pair_id")
        if pid:
            pairs.setdefault(pid, []).append(q)

    for pid, arms in sorted(pairs.items()):
        if len(arms) != 2:
            failures.append(f"{pid}: {len(arms)} arms, expected 2")
            continue
        before = next((a for a in arms if a.extension["arm"] == "before"), None)
        after = next((a for a in arms if a.extension["arm"] == "after"), None)
        if not before or not after:
            failures.append(f"{pid}: missing a before/after arm")
            continue
        if before.answer == after.answer:
            failures.append(f"{pid}: gold does not flip between arms")
        if before.question != after.question:
            failures.append(f"{pid}: arms ask different questions")
        if not before.question_date < after.question_date:
            failures.append(f"{pid}: before-arm query time does not precede the after-arm")
        lowered = before.answer.lower()
        if not any(marker in lowered for marker in ("not yet", "still valid")):
            failures.append(f"{pid}: before-arm gold is not an explicit not-yet")

    expected = 19
    if len(pairs) != expected:
        failures.append(f"{len(pairs)} pairs, ADR §5.1 declares {expected}")

    failures += _check_arithmetic(questions)
    return failures


WEEK_WORDS = {
    "three": 3, "four": 4, "five": 5, "six": 6, "seven": 7, "eight": 8, "nine": 9, "ten": 10,
    "eleven": 11, "twelve": 12, "thirteen": 13, "fifteen": 15, "sixteen": 16, "eighteen": 18,
}


def _check_arithmetic(questions: list[tmc.Question]) -> list[str]:
    """Re-derives every gold date from the session timestamp the corpus actually ships.

    The first version of this generator computed due dates from an anchor that was then overwritten
    when the haystack was shuffled and re-stamped, so every generated answer named a date its own
    conversation could not produce. All 38 questions failed the V1 oracle probe and none of the
    structural checks noticed, because none of them re-did the arithmetic. This one does.
    """
    failures = []
    for q in questions:
        if q.extension.get("shape") == SHAPE_SEED:
            continue
        gold_index = q.gold_indices[0]
        session = q.sessions[gold_index]
        text = session.turns[0].content
        weeks = next((v for w, v in WEEK_WORDS.items() if f" {w} weeks" in text), None)
        if weeks is None:
            failures.append(f"{q.question_id}: gold turn states no week offset")
            continue

        pivot = session.timestamp + timedelta(weeks=weeks)
        if _fmt(pivot) not in q.answer:
            failures.append(
                f"{q.question_id}: gold answer does not name {_fmt(pivot)}, the date its own "
                f"session timestamp plus {weeks} weeks produces")
        arm = q.extension.get("arm")
        if arm == "before" and not q.question_date < pivot:
            failures.append(f"{q.question_id}: before-arm is queried at or after the pivot")
        if arm == "after" and not q.question_date > pivot:
            failures.append(f"{q.question_id}: after-arm is queried at or before the pivot")
    return failures


if __name__ == "__main__":
    tmc.finalise(
        vertical="prospective",
        build=build,
        structure=tmc.StructureSpec(
            h_min=10, h_max=24, g_values={1, 2}, gold_position_shuffled=True,
            no_absolute_dates=True,
        ),
        generator_tool="tools/gen_typedmemeval_prospective.py",
        extra_checks=check_pairs,
    )
