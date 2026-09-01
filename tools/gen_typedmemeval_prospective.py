#!/usr/bin/env python3
"""
Generates TypedMemEval-Prospective v1 (ADR-026 §5.1) -- 50 questions.

What the vertical measures: whether a system places remembered facts *in time*. Things
due later, validity that expires, assertions not yet true. Run under TimestampsOnly
grounding, where the harness strips its own printed dates, a system that stamps messages
with ingestion time has nothing left to read.

    Seed carry-over (from agenteval-timegrounded-v1)   12
    Due-later reminders                                 8   =  4 before/after pairs
    Expiring validity                                   6   =  3 before/after pairs
    Not-yet-true assertions                             6   =  3 before/after pairs
    Due-window sweeps                                  18   =  9 before/after pairs

The pairs are the teeth. Each pair is one haystack asked twice, differing only in
question_date: once before the pivotal instant, once after. Gold flips between the arms
by construction, so a system that fires the reminder early is visibly *premature* rather
than merely wrong -- a distinction no single question can draw.

DUE-WINDOW EXISTS BECAUSE THE PAIRS ALONE WERE NOT ENOUGH, and the consuming project
proved it. They ran this corpus with ProspectiveFiring AND ValidTime=Current both DARK --
a doubly-dark control -- and scored 49/50. Every differentiator-shaped cell was perfect.
Their prereg had fixed the reading in advance: "if it scores HIGH anyway, that is a
finding about the corpus."

The mechanism, once you look: the other three shapes NAME THE THING. "Has the reminder
about the allotment lease renewal come due yet?" hands a similarity retriever the exact
words of the session it needs, the harness hands the model "today", and the corpus hands
it the due date. Comparing two dates in context is arithmetic, not firing semantics --
so a system with no prospective feature at all answers correctly, and the pairs
discriminate date arithmetic rather than the thing the vertical claims to measure.

Due-window names NOTHING. "What did I ask to be reminded about that falls due in the next
fortnight?" gives a similarity retriever no entity to match on; the haystack holds five to
seven reminders whose only distinguishing property is WHEN each falls due. The answer is a
SET, and the set membership changes with the as-of instant, so the two arms of a pair have
different answers over identical evidence. A system that cannot query its own memory by
time has nothing to retrieve on.

The corpus difficulty this vertical carries is therefore now temporal-semantics difficulty
in this shape and retrieval difficulty in the others, and the two are reported separately.

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
SHAPE_WINDOW = "due-window"

TYPE_SEED_ASOF = "temporal-as-of"
TYPE_SEED_CURRENT = "temporal-current"
TYPE_SEED_PROSPECTIVE = "prospective-memory"
TYPE_REMINDER = "prospective-reminder"
TYPE_VALIDITY = "prospective-validity"
TYPE_NOT_YET = "prospective-not-yet"
TYPE_WINDOW = "prospective-due-window"

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


#: Filler that uses gold's OWN constructions -- setting a reminder, and picking something up that
#: stays valid for a span -- about tasks and items no question asks about.
#:
#: Gold's two shapes are "Remind me to <task> <phrase>." and "I picked up a <thing> today, it stays
#: valid for <span> from today.", and no filler session used either, so `'remind me to'` sat in 16
#: gold sessions and 0 distractors and `'weeks from today'` in 13 and 0. Filler was same-DOMAIN
#: (other people's appointments) without being same-CONSTRUCTION, which is the distinction v4's
#: shared-frame work was supposed to close and reached only the statement verb.
#:
#: The tasks and items are deliberately foreign to REMINDERS and VALIDITY: a distractor asking to be
#: reminded about something no question mentions carries the construction without carrying a
#: candidate answer, because every question names the specific commitment it is about. Asserted below
#: rather than trusted.
PARITY_REMINDERS = [
    "chase the missing recycling bin", "photograph the meter for the landlord",
    "top up the parking app", "collect the dry cleaning from Hessle Street",
    "wind the hallway clock", "descale the shower head",
    "post the birthday card to Aunt Nell", "order more printer paper",
]
PARITY_VALIDITY = [
    "swimming-pool ten-pass", "car-wash loyalty card", "cinema voucher book",
    "bowling-alley credit", "garden-centre gift card", "canal-boat day licence",
]
#: Share of filler sessions built from a gold construction rather than the generic bank. Enough that
#: the phrases recur across distractors at gold's own rate; not so much that the haystack stops
#: reading like a life and starts reading like a form.
PARITY_FILLER_SHARE = 0.22


def _filler_session(rng: random.Random, echo_terms: list[str], stamp: datetime) -> tmc.Session:  # DevSkim: ignore DS148264 - deterministic corpus generation
    if rng.random() < PARITY_FILLER_SHARE:
        draw = rng.random()
        if draw < 0.34:
            user = f"Remind me to {rng.choice(PARITY_REMINDERS)} {rng.choice(WHENS)}."
        elif draw < 0.67:
            user = (f"I picked up a {rng.choice(PARITY_VALIDITY)} today, it stays valid for "
                    f"{rng.choice(WHENS).replace('in ', '')} from today.")
        else:
            # The not-yet-true construction: "due to <thing>". Without it `'due to'` stayed in 10
            # gold sessions and no distractor after the other two were shared -- each construction
            # has to be covered on its own, because sharing two of three just relocates the tell.
            user = (f"I am due to {rng.choice(PARITY_REMINDERS)} "
                    f"{rng.choice(WHENS)}, going by the letter.")
        # A trailing sentence from the generic bank. Without it the parity sessions were built from
        # a small bank in fixed phrasing, so their vocabulary was thinner than gold's and gold's
        # type/token ratio separated perfectly in 32% of questions at 4.4 sd -- a tell created by
        # the fix for a different tell, which is this corpus family's whole history in one line.
        extra, assistant = rng.choice(FILLER)
        user = f"{user} {extra.format(when=rng.choice(WHENS))}"
        return tmc.make_session(stamp, (user, tmc.weave_echo(assistant, echo_terms)), tag="filler")
    # TWO sentences, like the parity sessions above. Not cosmetic: gold's type/token ratio is
    # corrected in equalise_echo against the MEAN of this question's distractors, so a distractor
    # pool that is bimodal -- one-sentence generic sessions and two-sentence parity ones -- leaves
    # gold matching a mean that half the pool sits far from, and its ratio separated perfectly in
    # 24-32% of questions at 2.6-4.4 sd. Lowering the parity share only moved it between those two
    # numbers, because the spread came from the pool having two modes rather than from how many
    # sessions were in each.
    user, assistant = rng.choice(FILLER)
    extra, _ = rng.choice(FILLER)
    user = f"{user.format(when=rng.choice(WHENS))} {extra.format(when=rng.choice(WHENS))}"
    return tmc.make_session(stamp, (user, tmc.weave_echo(assistant, echo_terms)), tag="filler")


# Enforced, not trusted: a parity task or item colliding with a real REMINDERS/VALIDITY entry would
# put a second candidate commitment in the haystack for that question.
_real_tasks = {task for task, _ in REMINDERS} | {noun for _, noun in REMINDERS}
_real_things = {thing for thing, _ in VALIDITY} | {noun for _, noun in VALIDITY}
for _parity in PARITY_REMINDERS:
    if _parity in _real_tasks:
        raise AssertionError(f"parity reminder {_parity!r} collides with a real reminder task")
for _parity in PARITY_VALIDITY:
    if _parity in _real_things:
        raise AssertionError(f"parity validity item {_parity!r} collides with a real one")


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


#: Reminders used by the due-window sweeps. Kept apart from REMINDERS so a window question and a
#: named-entity question can never share a task and let the named one leak an answer to the other.
WINDOW_TASKS = [
    "re-tension the bike chain", "reorder the printer toner", "book the boiler service",
    "return the borrowed telescope", "chase the deposit refund", "swap the smoke-alarm batteries",
    "file the allotment water claim", "submit the conference abstract",
    "renew the parking permit", "collect the repaired lamp",
]

#: The sweep window, in days, and the wording that expresses it without naming a date.
WINDOW_SPANS = ((14, "the next fortnight"), (21, "the next three weeks"), (10, "the next ten days"))

#: Varied phrasings for a window reminder. One uniform sentence shape made punctuation density a
#: perfect gold separator in a fifth of the questions -- V7 caught it, correctly: a construction
#: only gold receives is a frame, however little it says.
WINDOW_FRAMES = (
    "Remind me to {task} in {word} weeks.",
    "Give me a nudge in {word} weeks -- I need to {task}.",
    "In {word} weeks, remind me: {task}!",
    "Something for the list; in {word} weeks I have to {task}.",
    "Can you remind me in {word} weeks? I need to {task}.",
    "Diary note: {task}, in {word} weeks.",
)


def _window_pair(
    qid_before: str,
    qid_after: str,
    pair_id: str,
    anchor,
    rng: random.Random,  # DevSkim: ignore DS148264 - deterministic corpus generation
    echo: float,
    index: int,
) -> list[tmc.Question]:
    """One due-window sweep, asked twice.

    THE SHAPE EXISTS BECAUSE NAMING THE THING DEFEATS THE MEASUREMENT. Every other shape here asks
    "has <named reminder> come due?", which hands a similarity retriever the exact vocabulary of
    the session it needs; the harness supplies "today" and the session supplies the due date, so
    comparing them is arithmetic a model does in context with no prospective feature at all. The
    consuming project proved it by scoring 49/50 with firing and valid-time BOTH dark.

    This question names NOTHING. The haystack carries several reminders whose only distinguishing
    property is when each falls due, and the answer is the SUBSET falling inside a window measured
    from the as-of instant. Similarity has no entity to match on, and the two arms have different
    answers over identical evidence, so a system that cannot query its memory by time has nothing
    to retrieve on.

    Gold differs BETWEEN THE ARMS, which no other shape here does: a session is gold on the arm
    whose window contains its due date. Both arms still present identical evidence - the marking
    differs, not the haystack.
    """
    span_days, span_words = WINDOW_SPANS[index % len(WINDOW_SPANS)]
    question_text = (f"What did I ask you to remind me about that falls due in {span_words}? "
                     f"Name each one and its date.")
    echoed = tmc.echo_terms(question_text, echo, rng)

    count = 5 + (index % 3)
    tasks = [WINDOW_TASKS[(index * 3 + k) % len(WINDOW_TASKS)] for k in range(count)]

    # Same parity fix as _pair: the reminder's assistant turn is empty and gets only an
    # acknowledgement, so it starts far below filler's and is padded past it.
    reminders = [tmc.make_session(anchor, ("", rng.choice(FILLER)[1]), gold_turn=0, tag="reminder")
                 for _ in tasks]
    filler = [_filler_session(rng, echoed, anchor) for _ in range(rng.randint(10, 14))]

    sessions = list(filler)
    for session in reminders:
        sessions.insert(rng.randrange(len(sessions) + 1), session)

    stamps = tmc.spread(anchor, len(sessions), hours=30)
    for session, stamp in zip(sessions, stamps):
        session.timestamp = stamp

    latest = sessions[-1].timestamp

    # Due dates are read back from the FINAL timestamps, never from the anchor - the lesson that
    # cost this generator all 38 of its paired questions on its first V1 probe.
    due = {}
    for offset, (session, task) in enumerate(zip(reminders, tasks)):
        weeks = ((latest - session.timestamp).days // 7) + 2 + offset
        word = next((w for w, v in WEEK_WORDS.items() if v == weeks), None)
        if word is None:
            return []   # no word form for this offset; the pair is not emitted rather than fudged
        frame = WINDOW_FRAMES[(index + offset) % len(WINDOW_FRAMES)]
        session.turns[0].content = frame.format(task=task, word=word)
        due[id(session)] = (task, session.timestamp + timedelta(weeks=weeks))

    ordered = sorted(due.values(), key=lambda x: x[1])

    # Two as-of instants whose windows select DIFFERENT non-empty subsets. Walk the sorted due
    # dates and take a cut that leaves at least one on each side; a pair whose arms agree would
    # measure nothing, so the construction refuses rather than emitting it.
    before_at = after_at = None
    for cut in range(1, len(ordered)):
        candidate_before = ordered[0][1] - timedelta(days=1)
        candidate_after = ordered[cut][1] - timedelta(days=1)
        if candidate_before <= latest:
            continue
        in_before = [t for t, d in ordered if candidate_before < d <= candidate_before + timedelta(days=span_days)]
        in_after = [t for t, d in ordered if candidate_after < d <= candidate_after + timedelta(days=span_days)]
        if in_before and in_after and set(in_before) != set(in_after):
            before_at, after_at = candidate_before, candidate_after
            break
    if before_at is None:
        return []

    def arm(asked, qid, label):
        window = [(t, d) for t, d in ordered if asked < d <= asked + timedelta(days=span_days)]
        copy = _copy(sessions)
        chosen = {t for t, _ in window}
        for session in copy:
            session.is_gold = session.tag == "reminder" and any(
                t in session.turns[0].content for t in chosen)
            # A reminder outside THIS arm's window is not evidence for it. make_session marks the
            # turn answer-bearing at construction, and the schema forbids that on a non-gold
            # session, so the flag has to follow the per-arm marking rather than the tag.
            for turn in session.turns:
                turn.has_answer = session.is_gold and turn.role == "user"
        listed = ", ".join(f"{t} on {_fmt(d)}" for t, d in window)
        answer = (f"{len(window)}: {listed}." if len(window) != 1
                  else f"One: {listed}.")
        return tmc.Question(
            qid, TYPE_WINDOW, question_text, answer, asked, copy,
            {"pair_id": pair_id, "shape": SHAPE_WINDOW, "arm": label,
             "window_days": span_days, "reminders_in_haystack": count,
             "reminders_in_window": len(window),
             "difficulty_dial": "distance", "difficulty_validated": False,
             "difficulty": _displacement_band(asked, copy),
             "displacement_days": _displacement_days(asked, copy)})

    return [arm(before_at, qid_before, "before"), arm(after_at, qid_after, "after")]


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
    questions -- not of the corpus.

    WHAT "SHARED" MEANS HERE, MEASURED RATHER THAN ASSUMED. An earlier version of this docstring
    claimed the arms present IDENTICAL evidence. They do not, and never have in any shipped
    revision: `finalise`'s equalisation pipeline runs per question, so the padding appended to each
    session is regenerated independently after the copy and 0 of 19 pairs match byte for byte. What
    IS identical is everything load-bearing -- the same sessions in the same order at the same
    timestamps, and the same answer-bearing sentence, so both arms derive the same pivot from the
    same reminder. Only decorative padding differs. The claim is narrowed to what holds because the
    stronger one was checked and was false.
    """
    echoed = tmc.echo_terms(question_text, echo, rng)

    # FIRST-ASSISTANT PARITY, and it is a LENGTH fix rather than a vocabulary one.
    #
    # Filler's assistant turn carries a reaction from FILLER; gold's is empty for most shapes here,
    # because the evidence sits in the USER turn and equalise_reply later prepends only a short
    # acknowledgement. Measured at the point the padder receives them, gold's first assistant turn
    # is 26 characters against filler's 71 -- a 45-character gap gold must close with whole-sentence
    # padding, so it takes the most steps and the last one overshoots. That is why gold finishes
    # LONGEST in 8 of 50 questions against 4.1 expected by chance, and why the tell sits in this one
    # slot and in no other.
    #
    # Correlates exactly across the family: episodic -36 shows the same tell at 0.120, while
    # semantic (-4), bitemporal (-2) and temporal (+1) show none. Bitemporal's gap was closed this
    # same release by giving gold the reaction filler gets, and its tell went away.
    gold_reaction = gold_assistant or rng.choice(FILLER)[1]
    gold = tmc.make_session(anchor, ("", gold_reaction), gold_turn=0, tag="gold")
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

    # Distance is this vertical's dial, and it was already there: displacement from the last
    # gold session to the question runs 15 to 142 days, a 9.5x spread that stratified nothing.
    # Banded per ARM, because the two arms of a pair sit at different displacements by
    # construction -- that is what makes them a pair.
    common = {"pair_id": pair_id, "shape": shape, "difficulty_dial": "distance", "difficulty_validated": False}
    return [
        tmc.Question(qid_before, qtype, question_text, answer_before, before_date,
                     _copy(sessions),
                     {**common, "arm": "before",
                      "difficulty": _displacement_band(before_date, sessions),
                      "displacement_days": _displacement_days(before_date, sessions)}),
        tmc.Question(qid_after, qtype, question_text, answer_after, after_date,
                     _copy(sessions),
                     {**common, "arm": "after",
                      "difficulty": _displacement_band(after_date, sessions),
                      "displacement_days": _displacement_days(after_date, sessions)}),
    ]


#: Days from the last gold session to the question -> band, cut on the spread the generator
#: already produces. Diagnostics rather than claims: cells are far under the n >= 30 floor.
_DISPLACEMENT_BANDS = ((25, 1), (45, 2), (70, 3), (105, 4))


def _displacement_days(asked: datetime, sessions: list[tmc.Session]) -> float:
    last_gold = max(s.timestamp for s in sessions if s.is_gold)
    return round((asked - last_gold).total_seconds() / 86400.0, 1)


def _displacement_band(asked: datetime, sessions: list[tmc.Session]) -> int:
    days = _displacement_days(asked, sessions)
    for ceiling, band in _DISPLACEMENT_BANDS:
        if days <= ceiling:
            return band
    return 5


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


def build(echo, rng: random.Random) -> list[tmc.Question]:
    """`echo` is a float, or a dict keyed by shape under per-shape calibration.

    Per-shape matters here more than it did before the reshape: due-window is deliberately less
    lexically reachable than the named-entity shapes - that is the whole point of it - so one knob
    tuned on the mean would loosen the named shapes to pay for it, which is the
    mean-satisfiable-by-averaging defect this family has now hit at coverage, at headroom, and here.
    """
    def knob(shape: str) -> float:
        return echo.get(shape, 0.0) if isinstance(echo, dict) else echo

    questions: list[tmc.Question] = []
    questions += _seed_questions(rng, knob(SHAPE_SEED), start_index=1)

    index = 13
    pair_no = 1
    base = datetime(2026, 3, 2, 9, 30)

    # --- Due-later reminders: 4 pairs -------------------------------------------------
    for i in range(4):
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
            base + timedelta(days=11 * i), rng, knob(SHAPE_REMINDER),
            filler_count=rng.randint(12, 17),
        )
        index += 2
        pair_no += 1

    # --- Expiring validity: 3 pairs ---------------------------------------------------
    for i in range(3):
        thing, noun = VALIDITY[i % len(VALIDITY)]
        questions += _pair(
            f"tme-pro-{index:03d}", f"tme-pro-{index + 1:03d}", f"tme-pro-p{pair_no:02d}",
            SHAPE_VALIDITY, TYPE_VALIDITY,
            f"Is {noun} still valid, and when does or did it run out?",
            f"I picked up a {thing} today, it stays valid for {{span}} from today.",
            "",  # shared bank only — see equalise_reply
            f"Yes, still valid. The {thing} runs out on {{date}}.",
            f"No, it has expired. The {thing} ran out on {{date}}.",
            base + timedelta(days=9 * i + 4), rng, knob(SHAPE_VALIDITY),
            filler_count=rng.randint(12, 17),
        )
        index += 2
        pair_no += 1

    # --- Not-yet-true assertions: 3 pairs ---------------------------------------------
    for i in range(3):
        future, past, noun = NOT_YET[i % len(NOT_YET)]
        questions += _pair(
            f"tme-pro-{index:03d}", f"tme-pro-{index + 1:03d}", f"tme-pro-p{pair_no:02d}",
            SHAPE_NOT_YET, TYPE_NOT_YET,
            # Asks what the RECORD shows, not whether the thing happened. The old phrasing
            # ("Have I {past} yet?") required the model to withhold an inference the evidence
            # licenses socially but not logically -- a plan plus a passed date does not
            # establish occurrence -- and answer models assert it anyway. Two of them scored
            # 50% and 90% on this shape while every other Prospective shape ran at 100%, which
            # made its V1 answer-model variance rather than memory signal. The temporal
            # judgement the vertical exists to test ("is the date still ahead?") is preserved;
            # the occurrence inference, which it never meant to test, is gone.
            f"What does the record say about when I am due to {future} — is that date still "
            f"ahead of me, and what is it?",
            f"I am due to {future} {{phrase}}.",
            "",  # shared bank only — see equalise_reply
            f"It is still ahead. The record has you due to {future} on {{date}}.",
            f"It is no longer ahead. The record had you due to {future} on {{date}}, which has "
            f"passed; nothing since then records whether it went ahead.",
            base + timedelta(days=13 * i + 2), rng, knob(SHAPE_NOT_YET),
            filler_count=rng.randint(12, 17),
        )
        index += 2
        pair_no += 1

    # --- Due-window sweeps: 9 pairs ---------------------------------------------------
    # The shape the consuming project's doubly-dark 49/50 asked for: no entity named, so
    # similarity has nothing to match and only a time-indexed query reaches the answer.
    made = 0
    attempt = 0
    while made < 9:
        pair = _window_pair(
            f"tme-pro-{index:03d}", f"tme-pro-{index + 1:03d}", f"tme-pro-p{pair_no:02d}",
            base + timedelta(days=7 * attempt + 3), rng, knob(SHAPE_WINDOW), attempt)
        attempt += 1
        if not pair:
            continue          # arms would have agreed; that pair measures nothing
        questions += pair
        index += 2
        pair_no += 1
        made += 1

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
        if before.extension.get("shape") == SHAPE_WINDOW:
            # A window arm names a SET of things falling due ahead of the as-of instant; there is
            # no single pivot to describe as "not yet". The equivalent guarantee is enforced by
            # _check_window, which requires every gold due date to lie strictly after the question
            # date, and by _check_window_arms_differ.
            continue
        lowered = before.answer.lower()
        # "still ahead" joins the list for the not-yet-true shape, which now asks what the
        # record shows rather than whether the thing happened. The property being checked is
        # unchanged: the before arm must state, in words, that the moment has not arrived --
        # so a system that answers it correctly cannot have done so by describing the past.
        if not any(marker in lowered for marker in ("not yet", "still valid", "still ahead")):
            failures.append(f"{pid}: before-arm gold does not say the moment is still ahead")

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
        if q.extension.get("shape") in (SHAPE_SEED, SHAPE_WINDOW):
            continue   # SHAPE_WINDOW has its own arithmetic check: _check_window
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


def _check_window(questions: list[tmc.Question]) -> list[str]:
    """Re-does the arithmetic for the due-window sweeps, over the whole gold SET.

    _check_arithmetic verifies one pivot against one gold, which is what the named-entity pairs
    carry. A window answer names one date per gold, and every one of them has to be the date that
    gold session's own timestamp produces - the same guarantee, applied set-wise. Written because
    the single-gold version would silently pass a window question after checking one of its four
    dates.
    """
    failures = []
    for q in questions:
        if q.extension.get("shape") != SHAPE_WINDOW:
            continue

        gold = [q.sessions[i] for i in q.gold_indices]
        if not gold:
            failures.append(f"{q.question_id}: due-window arm has no gold at all")
            continue

        span = timedelta(days=q.extension["window_days"])
        for session in gold:
            text = session.turns[0].content
            weeks = next((v for w, v in WEEK_WORDS.items() if f" {w} weeks" in text), None)
            if weeks is None:
                failures.append(f"{q.question_id}: a gold turn states no week offset")
                continue
            due = session.timestamp + timedelta(weeks=weeks)
            if _fmt(due) not in q.answer:
                failures.append(
                    f"{q.question_id}: answer does not name {_fmt(due)}, the date a gold session's "
                    f"own timestamp plus {weeks} weeks produces")
            # THE DEFINING PROPERTY: gold is exactly what falls inside the window measured from the
            # as-of instant. A gold outside it would mean the arm is scored on evidence its own
            # question does not ask for.
            if not (q.question_date < due <= q.question_date + span):
                failures.append(
                    f"{q.question_id}: a gold session falls due {_fmt(due)}, outside the "
                    f"{q.extension['window_days']}-day window from {_fmt(q.question_date)}")

        # And nothing outside gold may fall inside the window, or the answer is incomplete.
        for session in q.sessions:
            if session.is_gold or session.tag != "reminder":
                continue
            text = session.turns[0].content
            weeks = next((v for w, v in WEEK_WORDS.items() if f" {w} weeks" in text), None)
            if weeks is None:
                continue
            due = session.timestamp + timedelta(weeks=weeks)
            if q.question_date < due <= q.question_date + span:
                failures.append(
                    f"{q.question_id}: a NON-gold reminder falls due {_fmt(due)}, inside the "
                    f"window - the answer is incomplete and the question unanswerable as gold")
    return failures


def _check_window_arms_differ(questions: list[tmc.Question]) -> list[str]:
    """A pair whose arms give the same answer measures nothing.

    This is the whole point of the shape: identical evidence, different as-of instant, different
    answer. If the two arms ever agree, the question has stopped discriminating a system that
    tracks time from one that does not, and it should fail the build rather than pad the count.
    """
    failures = []
    by_pair = {}
    for q in questions:
        if q.extension.get("shape") != SHAPE_WINDOW:
            continue
        by_pair.setdefault(q.extension["pair_id"], []).append(q)
    for pair_id, arms in by_pair.items():
        if len(arms) != 2:
            failures.append(f"{pair_id}: due-window pair has {len(arms)} arms, expected 2")
            continue
        if arms[0].answer == arms[1].answer:
            failures.append(
                f"{pair_id}: both arms answer identically, so the as-of instant changes nothing "
                f"and the pair measures neither firing nor valid time")
    return failures


if __name__ == "__main__":
    tmc.finalise(
        vertical="prospective",
        build=build,
        structure=tmc.StructureSpec(
            # 1-2 for the named-entity shapes; due-window carries one gold per reminder whose
            # due date falls inside the arm's window, which runs to four.
            h_min=10, h_max=24, g_values={1, 2, 3, 4}, gold_position_shuffled=True,
            no_absolute_dates=True,
        ),
        generator_tool="tools/gen_typedmemeval_prospective.py",
        extra_checks=lambda qs: (check_pairs(qs) + _check_window(qs)
                                 + _check_window_arms_differ(qs)),
        shape_of=lambda q: (q.extension or {}).get("shape"),
    )
