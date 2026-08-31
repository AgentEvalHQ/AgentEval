#!/usr/bin/env python3
"""
Generates TypedMemEval-Bitemporal (ADR-027 §3.3): two clocks that disagree.

VALID time is when a fact was true. TRANSACTION time is when the record learned it. They are the
same number until a RETROACTIVE CORRECTION arrives, and the divergence is the whole measurement:

    Recorded in September: "Alice moved to Berlin in March -- it was not Munich after all."
    "Where does the record say Alice lived in April?"                -> Berlin   (valid time)
    "As of June, what did the record say about April?"               -> Munich   (transaction time)

A store with one clock cannot represent the difference, so its ceiling here is STRUCTURAL rather
than a matter of retrieval quality. That is the point: every other vertical in this family can be
saturated by a good retriever, and ADR-027 §6.1 measured exactly that -- interference cost ~0 on
four of the five shipped verticals, meaning a perfect retriever and no retriever produce the same
answers. This vertical is built the other way round.

A PREDICTION THIS GENERATOR MADE AND THE PROBE REFUTED. The first version of this docstring argued
that Bitemporal would have a large interference cost by construction: the transaction arm's gold is
the ORIGINAL record, the correction is recorded after the asked instant, so a system handed the whole
haystack should see the correction and answer the corrected value -- wrong. Measured, V8 is 59/60 and
the interference cost is 0.0167. The answer model reads the timestamps and reasons about them
correctly without help.

That is a better property than the one predicted, and it is worth stating plainly rather than
quietly deleting. V1 = V8 = ~1.0 means the corpus contains no reasoning ambiguity and no retrieval
difficulty: a reader given the sessions gets it right either way. So when a real memory system fails
the transaction arm, the failure cannot be attributed to an unanswerable question, an ambiguous
frame, or a model that cannot do the arithmetic of "before" and "after" -- it is attributable to the
store having no way to represent WHEN it learned a thing. That is the structural ceiling ADR-027
§3.3 claims for single-clock stores, and a corpus where the full-context reader scores ~1.0 is what
makes the claim testable rather than confounded.

THE AS-OF PRECONDITION (ADR-027 §4). A transaction-time question is ill-posed unless retrieval can be
restricted to what was recorded at or before the asked instant: a retriever that can see the
September correction while being asked what was believed in June has no defensible answer, and a
system that answers it "correctly" has done so by ignoring the ask. The window is DERIVED from the
question, not tuned -- there is no lambda here and nothing to calibrate. Every question therefore
carries `asof_instant`, and the transaction arms additionally carry `asof_excluded_session_ids`, so
the precondition is checkable against the corpus rather than asserted in prose.

CLASS PARITY (ADR-026 §19). Filler states the same KIND of fact as gold, in the same construction,
about entities no question asks about -- including CORRECTIONS of its own. Without that, "correction"
is a gold marker and the whole vertical is separable on one phrase, which is the defect that cost
this family two corpus revisions.
"""

from __future__ import annotations

import random  # DevSkim: ignore DS148264 - corpus generation must be replayable under a seed; a CSPRNG cannot be seeded to reproduce a draw, and this selects filler text, not secrets.
from datetime import datetime, timedelta

import typedmemeval_common as tmc

SHAPE_BELIEF = "belief-at-instant"
SHAPE_DEPTH = "correction-depth"
TYPE_BELIEF = "bitemporal-belief"
TYPE_DEPTH = "bitemporal-correction-depth"

#: 18 + 12 pairs, two arms each -> 60 questions.
BELIEF_PAIRS = 18
DEPTH_PAIRS = 12

H_MIN, H_MAX = 14, 24
_BASE = datetime(2026, 1, 5, 9, 15)

#: Same-subject, other-month distractors per question. Three is enough to push the sessions naming
#: the asked subject past K_REF=5 once the record and its corrections are counted, which is the
#: point: below that a retriever can return every subject match and never choose.
_SIDE_FACTS = 3

#: Months named as instants. Bitemporal cannot avoid naming a time -- "as of June" IS the question --
#: so this vertical declares `no_absolute_dates=False` rather than pretending otherwise. Bare month
#: names carry no year and no day, so they cannot be resolved to a calendar date from the text alone;
#: the session timestamps remain the only way to place them.
MONTHS = ("February", "March", "April", "May", "June", "July", "August", "September")

#: (entity, attribute, question noun). The entity is a person the record is ABOUT -- never the user --
#: because a retroactive correction to one's own fact reads as forgetting rather than as bitemporality.
SUBJECTS = (
    ("Alice Renwick", "city", "which city"),
    ("Tomas Beddoe", "team", "which team"),
    ("Priya Nandra", "office", "which office"),
    ("Colm Whitaker", "department", "which department"),
    ("Ines Vargas", "site", "which site"),
    ("Ravi Ellory", "branch", "which branch"),
    ("Nell Cortazar", "unit", "which unit"),
    ("Otto Lindqvist", "campus", "which campus"),
    ("Sara Mbeki", "region", "which region"),
    ("Jonas Pfeiffer", "depot", "which depot"),
)

#: Values are invented so zero-context guessing has nothing to work with (V2). Two disjoint pools:
#: gold facts draw from PLACES, filler from FILLER_PLACES, so a filler value can never be read as a
#: candidate answer to a gold question.
PLACES = ("Ardenholm", "Bexmoor", "Calderwick", "Dunmarsh", "Elverton", "Fenwick Cross",
          "Garrowby", "Halstead Vale", "Ilminster", "Jarrow Bank", "Kelsford", "Lowick",
          "Marchmont", "Northolt Bay", "Orrindale", "Pellworth")
FILLER_PLACES = ("Quarrenden", "Rushmere", "Saltcoats", "Tarnbury", "Ulverdale", "Verity Cross",
                 "Wexcombe", "Yarrowfield", "Zennor Hill", "Ashcombe Ridge", "Brackwater",
                 "Cranmere End")

#: One bank, drawn for gold and filler alike. The original statement.
STATEMENT_FRAMES = (
    "Putting it on record: {who} is at {value}, as of {when}.",
    "For the file -- {who} has been at {value} since {when}.",
    "Noting that {who} moved to {value} back in {when}.",
    "{who} is at {value}; that started in {when}.",
    "Logging it: {who} went to {value} in {when}.",
)

#: The retroactive correction. Same bank for gold and filler, same reason.
#: The correction NEVER names the value it supersedes.
#:
#: It did in the first draft -- "...was at {value} from {when}, not {stale}" -- which reads naturally
#: and is fatal: `stale` IS the transaction-time arm's answer, so ablating that arm's gold left the
#: answer sitting in the correction. V3 failed 28 of 60, and every single failure was a transaction
#: arm. A correction that quotes what it replaces makes the superseded belief recoverable without
#: the record that held it, which is exactly the thing this vertical exists to make un-recoverable.
CORRECTION_FRAMES = (
    "Correction to the file: {who} was at {value} from {when}.",
    "That earlier note about {who} was wrong -- it was {value} from {when}.",
    "Amending the record: {who} was actually at {value} as of {when}.",
    "Putting {who} right: {value} from {when}.",
    "Revising what we logged for {who}: {value} from {when}.",
)

#: Question frames, varied per question for the same reason every other bank in this family is.
#:
#: A single template makes its own verb recur in 100% of questions, and the calibration echo weaves
#: question vocabulary into DISTRACTORS by design -- so that verb becomes a filler marker whose
#: ABSENCE identifies gold. Measured on the first draft: `'show'` at AUC 0.826. Gold cannot be given
#: the query words to compensate, because that lifts its retrieval score and busts the coverage
#: ceiling, so the fix has to be variety in the question rather than parity in the echo.
VALID_FRAMES = (
    "{which} does the record show for {who} in {when}?",
    "{which} is {who} down as, for {when}?",
    "{which} does the file give for {who} in {when}?",
    "According to the record, {which} was {who} at in {when}?",
    "{which} do we have for {who} covering {when}?",
)
TXN_FRAMES = (
    "As of {asof}, {which} did the record show for {who} in {when}?",
    "As things stood on {asof}, {which} was {who} down as for {when}?",
    "Going by what was on file at {asof}, {which} did we have for {who} in {when}?",
    "At {asof}, before anything later arrived, {which} did the file give for {who} in {when}?",
    "Reading the record as it stood at {asof}: {which} for {who} in {when}?",
)

REPLIES = ("Updated.", "Filed.", "Noted.", "Recorded.", "Got it.", "Amended.")

#: Same-domain filler that states and corrects facts about people no question asks about. Class
#: parity with instance divergence: identical construction, entities that cannot be candidate answers.
FILLER_SUBJECTS = (
    ("Dara Olusegun", "desk"), ("Marta Kovac", "wing"), ("Ewan Trelawny", "annexe"),
    ("Yui Nakamura", "floor"), ("Piotr Zielinski", "yard"), ("Aisha Rahman", "block"),
    ("Ben Hollowell", "store"), ("Lena Fischer", "workshop"),
)
FILLER_CHAT = (
    ("The lift in the east stair is out again {when}.", "Someone should chase that."),
    ("Parking permits are being reissued {when}.", "Worth doing before the deadline."),
    ("The archive boxes are going off-site {when}.", "About time, honestly."),
    ("Fire drill is pencilled in for {when}.", "Everyone will love that."),
    ("The kitchen tap has been dripping since {when}.", "Add it to the list."),
    ("New badge readers go live {when}.", "Hopefully less temperamental."),
)


def _sentence(text: str) -> str:
    """Upper-cases the first character and leaves the rest alone.

    `str.capitalize()` lower-cases everything after it, which turned "Alice Renwick" into "alice
    renwick" and "15 January" into "15 january" in every question this vertical asks.
    """
    return text[:1].upper() + text[1:]


def _copy(sessions: list[tmc.Session]) -> list[tmc.Session]:
    """Both arms must present IDENTICAL evidence, deep-copied so a mutation on one cannot reach the
    other -- the arms differ in which sessions are marked gold, and sharing objects would make the
    second assignment silently overwrite the first."""
    return [
        tmc.Session([tmc.Turn(x.role, x.content, x.has_answer) for x in s.turns],
                    s.timestamp, s.is_gold, s.tag)
        for s in sessions
    ]


def _lay_out(sessions: list[tmc.Session], ordinal: int) -> datetime:
    """Stamps the haystack chronologically and returns a query time after all of it.

    Order and timestamp must agree here: "recorded after" is the vertical's entire subject, so a
    corpus whose session order contradicts its timestamps would make the transaction-time arm
    unanswerable in one reading and trivial in the other.
    """
    start = _BASE + timedelta(days=5 * ordinal)
    for session, stamp in zip(sessions, tmc.spread(start, len(sessions))):
        session.timestamp = stamp
    return sessions[-1].timestamp + timedelta(days=3, hours=4)


#: Sessions between the original record and the first correction, per band. CORRECTION LATENCY is
#: the dial, and it is a reasoning dial: ADR-027 §6.1 measured interference cost ~0 on four of the
#: five shipped verticals, so dispersion cannot discriminate a stack that retrieves everything, but
#: how much record accumulated before the correction landed changes what has to be reasoned about.
_LATENCY_BANDS = {1: (1, 2), 2: (3, 4), 3: (5, 7), 4: (8, 10), 5: (11, 14)}


def _band_of_latency(gap: int) -> int:
    for band, (low, high) in sorted(_LATENCY_BANDS.items()):
        if low <= gap <= high:
            return band
    return 5


def _mark_gold(question: tmc.Question, chosen) -> None:
    """Marks the arm's gold sessions and keeps `has_answer` consistent with them.

    Both arms are built from one haystack in which the record AND every correction carry an
    answer-bearing turn, because each of them IS the answer on some clock. Once an arm picks its
    gold, the others are no longer answer-bearing for that question -- the original record states the
    superseded value, which on the valid-time clock is simply wrong. Leaving the flag set makes the
    corpus claim a distractor carries the answer, and the family's own structural check refuses it.
    """
    for session in question.sessions:
        session.is_gold = chosen(session)
        for turn in session.turns:
            if turn.has_answer and not session.is_gold:
                turn.has_answer = False


def _filler_session(rng: random.Random, echoed: list[str],  # DevSkim: ignore DS148264 - deterministic corpus generation
                    stamp: datetime, want_correction: bool) -> tmc.Session:
    """Filler in gold's own construction, about people no question asks about.

    `want_correction` makes a share of the filler CORRECT itself. Without it, the correction frames
    appear only in gold sessions and the vertical is separable on the single word "Correction" --
    the `REPLIES`/"noted" defect that cost this family three revisions, in the one construction this
    vertical cannot do without.
    """
    who, noun = rng.choice(FILLER_SUBJECTS)
    if want_correction:
        user = rng.choice(CORRECTION_FRAMES).format(
            who=who, value=rng.choice(FILLER_PLACES), when=rng.choice(MONTHS))
    elif rng.random() < 0.55:
        user = rng.choice(STATEMENT_FRAMES).format(
            who=who, value=rng.choice(FILLER_PLACES), when=rng.choice(MONTHS))
    else:
        template, reply = rng.choice(FILLER_CHAT)
        return tmc.make_session(stamp, (template.format(when=rng.choice(MONTHS)),
                                        tmc.weave_echo(reply, echoed)), tag="filler")
    return tmc.make_session(stamp, (user, tmc.weave_echo(rng.choice(REPLIES), echoed)), tag="filler")


def _pair(qid_valid: str, qid_txn: str, pair_id: str, shape: str, qtype: str,
          subject: tuple[str, str, str], corrections: int, band: int, ordinal: int,
          rng: random.Random, echo: float) -> list[tmc.Question]:  # DevSkim: ignore DS148264 - deterministic corpus generation
    """One shared haystack asked on two clocks.

    Both arms see identical evidence; only the clock differs. The valid-time arm asks what is true,
    the transaction-time arm asks what was BELIEVED at a named earlier instant, and the gold flips on
    the belief axis while the truth axis holds steady. A system that silently answers the valid-time
    question when asked the transaction-time one is WRONG here, not imprecise, which is what makes
    the result a capability statement rather than a score.
    """
    who, attribute, which = subject
    values = rng.sample(PLACES, corrections + 1)
    original, final = values[0], values[-1]
    event_month = MONTHS[ordinal % 3]                      # when the fact became true (valid time)

    # H counts NON-GOLD sessions only (ADR-026 §4), so it is not the haystack length: with G=1 the
    # record and every correction except the arm's own gold are distractors. Both arms therefore
    # carry the same H -- filler + corrections -- which is what lets the pair present identical
    # evidence on two clocks. Getting this wrong is quiet: adding the gold on top read H=27 against
    # a declared [14,24], and reserving it out of the total read H=13.
    distractors = rng.randint(H_MIN, H_MAX)
    # The same-subject distractors below are non-gold sessions, so they count toward H exactly as
    # the corrections do and come out of the filler budget rather than being added on top. Getting
    # this wrong is quiet -- an earlier version of this arithmetic read H=27 against a declared
    # [14,24] by adding gold on top, and H=13 by reserving it out of the total.
    filler_count = distractors - corrections - _SIDE_FACTS
    frame_at = ordinal % len(VALID_FRAMES)
    question_text = _sentence(VALID_FRAMES[frame_at].format(
        which=which, who=who, when=event_month))
    echoed = tmc.echo_terms(question_text, echo, rng)

    # A quarter of the filler corrects itself, so the correction construction recurs across
    # distractors at roughly the rate gold uses it.
    sessions = [_filler_session(rng, echoed, _BASE, rng.random() < 0.25)
                for _ in range(filler_count)]

    # The record is placed with ROOM for its banded latency, not at a free position that is then
    # clamped. Clamping silently demoted the long-latency bands: band 5 wanted a gap of 11-14 and got
    # whatever fitted, so belief-at-instant never realised it and band 5 ended up owned entirely by
    # the other shape -- the collinearity this design is supposed to avoid, reintroduced by an
    # implementation detail rather than by the design.
    low, high = _LATENCY_BANDS[band]
    gap = rng.randint(low, high)
    # Gold gets an acknowledgement from the same bank filler draws from. Building gold with an empty
    # assistant turn while filler always has one is a construction asymmetry, and the gate found it:
    # `assistant_slot1_length_chars` separated gold perfectly in 22% of questions at 2.9 sd. The
    # shared-bank rule applies to the acknowledgement here exactly as it does everywhere else.
    # ECHO PARITY, IN THE SAME TURN AND AT THE SAME RATE AS FILLER. Filler's assistant turn carries
    # the woven calibration clause and gold's did not, so gold was identifiable by that clause's
    # ABSENCE. Under one shared knob at 0.5 the asymmetry sat just under the bar; per-shape
    # calibration raised one shape's echo and the gate failed at once --
    # assistant_punctuation_density separating gold perfectly in 22% of questions, 2.8 sd. The
    # imbalance was always there, and only the knob moving exposed it. Temporal carries the same
    # note for the same reason: one bank for both sides means ALL of both sides.
    record = tmc.make_session(
        _BASE,
        (rng.choice(STATEMENT_FRAMES).format(who=who, value=original, when=event_month),
         tmc.weave_echo(rng.choice(REPLIES), echoed)),
        gold_turn=0, tag=f"record:{original}")
    sessions.insert(rng.randint(0, max(0, len(sessions) - gap)), record)

    # SAME-SUBJECT DISTRACTORS, ON OTHER MONTHS. Naming the subject used to hand BM25 everything it
    # needed: only two sessions in the haystack mentioned this person at all, so "which city for
    # Alice Renwick in February" retrieved the record and its correction and nothing had to be
    # discriminated. V9 ran 31/36 against V1 35/36 -- headroom 0.11, a shape that cannot rank two
    # systems. Filler could not fix it because filler is about OTHER people by construction (class
    # parity with instance divergence), so it never competes on the subject term.
    #
    # These do compete: same person, same construction, values drawn from the same PLACES pool a
    # question could answer with, so a retriever that matches on the subject alone now returns
    # sessions that do not answer the question and must select on the MONTH.
    #
    # THE MONTH MUST BE LATER THAN THE ASKED ONE, and this is the part that is easy to get wrong. A
    # distractor reading "Alice Renwick is at Garrowby, as of January" against a question about
    # February is not a distractor, it is a second true answer -- "as of January" carries forward
    # until something supersedes it. Later months cannot reach back over the asked one, so they are
    # unambiguous by construction rather than by careful reading.
    later_months = [m for m in MONTHS if MONTHS.index(m) > MONTHS.index(event_month)]
    side_values = [p for p in PLACES if p not in values]
    # Enforced, not assumed. `event_month` is drawn from the first three of eight months so there
    # are always at least five later ones, and PLACES holds sixteen values against at most five in
    # use -- but both are consequences of constants declared elsewhere in this file, and a later
    # edit to either would otherwise produce a silent modulo-by-zero or a distractor that repeats a
    # gold value as if it were a distinct fact.
    if len(later_months) < _SIDE_FACTS or len(side_values) < _SIDE_FACTS:
        raise SystemExit(
            f"bitemporal: {_SIDE_FACTS} same-subject distractors requested but only "
            f"{len(later_months)} later months and {len(side_values)} unused values are available "
            f"for {who} in {event_month}")
    for step in range(_SIDE_FACTS):
        side_month = later_months[(ordinal + step) % len(later_months)]
        side_value = side_values[(ordinal * 3 + step) % len(side_values)]
        frames = CORRECTION_FRAMES if step % 2 else STATEMENT_FRAMES
        sessions.insert(
            rng.randint(0, len(sessions)),
            tmc.make_session(
                _BASE,
                (rng.choice(frames).format(who=who, value=side_value, when=side_month),
                 tmc.weave_echo(rng.choice(REPLIES), echoed)),
                tag=f"othermonth{step}:{side_month}"))

    # Successive corrections, each recorded later than the last.
    correction_sessions = []
    stale = original
    for step in range(corrections):
        value = values[step + 1]
        node = tmc.make_session(
            _BASE,
            (rng.choice(CORRECTION_FRAMES).format(
                who=who, value=value, when=event_month),
             tmc.weave_echo(rng.choice(REPLIES), echoed)),
            gold_turn=0, tag=f"correction{step}:{value}")
        # The FIRST correction lands at the banded latency after the record; the rest follow it.
        # Always after the record they correct -- "recorded later" is this vertical's whole subject.
        anchor = sessions.index(record if step == 0 else correction_sessions[-1])
        node_at = min(len(sessions), anchor + (gap if step == 0 else rng.randint(1, 3)))
        sessions.insert(node_at, node)
        correction_sessions.append(node)
        stale = value

    # Realised, not requested: the insert is clamped at the end of the haystack, so the band must be
    # read back from where the correction actually landed. Stamping the requested gap would put a
    # difficulty label on the corpus that the corpus does not have.
    realised_gap = sessions.index(correction_sessions[0]) - sessions.index(record)
    asked_at = _lay_out(sessions, ordinal)

    # The asked instant sits BETWEEN the record and the first correction, so as of that moment the
    # file still said `original`. Read back from the laid-out timestamps rather than computed from
    # an anchor -- deriving a date from an anchor the haystack later moved is the defect that failed
    # all 38 of Prospective's paired questions in its first generation.
    first_correction_at = min(s.timestamp for s in correction_sessions)
    belief_at = record.timestamp + (first_correction_at - record.timestamp) / 2

    valid_arm = tmc.Question(
        qid_valid, qtype,
        question_text,
        f"{final}. That is what the record now shows for {who} in {event_month}.",
        asked_at, sessions,
        {"shape": shape, "pair_id": pair_id, "arm": "valid-time", "clock": "valid",
         "corrections": corrections, "asof_instant": None,
         "latency_sessions": realised_gap,
         "difficulty": _band_of_latency(realised_gap), "difficulty_dial": "correction-latency",
         "difficulty_validated": False})

    txn_sessions = _copy(sessions)
    record_at = sessions.index(record)
    txn_arm = tmc.Question(
        qid_txn, qtype,
        _sentence(TXN_FRAMES[frame_at].format(
            asof=belief_at.strftime("%d %B"), which=which, who=who, when=event_month)),
        f"{original}. As of then the correction had not been recorded yet.",
        asked_at, txn_sessions,
        {"shape": shape, "pair_id": pair_id, "arm": "transaction-time", "clock": "transaction",
         "corrections": corrections,
         "latency_sessions": realised_gap,
         "asof_instant": belief_at.strftime(tmc.DATE_FORMAT),
         "difficulty": _band_of_latency(realised_gap), "difficulty_dial": "correction-latency",
         "difficulty_validated": False})

    # Valid-time gold is the LAST correction -- it is what makes the current truth readable.
    # Transaction-time gold is the ORIGINAL record, which is what the file said at the asked instant.
    _mark_gold(valid_arm, lambda s: s is correction_sessions[-1])
    _mark_gold(txn_arm, lambda s, i=record_at: s is txn_arm.sessions[i])
    return [valid_arm, txn_arm]


def build(echo, rng: random.Random) -> list[tmc.Question]:  # DevSkim: ignore DS148264 - deterministic corpus generation
    """`echo` is a float or, under per-shape calibration, a dict keyed by shape."""
    def knob(shape: str) -> float:
        return echo.get(shape, 0.0) if isinstance(echo, dict) else echo

    questions: list[tmc.Question] = []
    index = 1
    pair_no = 1

    # belief-at-instant: exactly one correction, so the two clocks differ by one step.
    # Bands cycle WITHIN the shape, so band and shape are not collinear. The first draft banded on
    # correction depth, which is a shape property: belief-at-instant landed entirely in band 2 and
    # correction-depth owned 3-5, making the cross-tab a diagonal. That is the Arithmetic confound
    # (ADR-026 §19) reproduced from scratch, and ADR-027 §6 says to refuse it at design time rather
    # than discover it in the probe records.
    for i in range(BELIEF_PAIRS):
        questions += _pair(
            f"tme-bit-{index:03d}", f"tme-bit-{index + 1:03d}", f"tme-bit-p{pair_no:02d}",
            SHAPE_BELIEF, TYPE_BELIEF, SUBJECTS[i % len(SUBJECTS)],
            corrections=1, band=(i % 5) + 1, ordinal=i, rng=rng, echo=knob(SHAPE_BELIEF))
        index += 2
        pair_no += 1

    # correction-depth: 2-4 successive corrections on one fact. The asked instant still sits before
    # the first, so the transaction arm's answer is the original however deep the chain runs -- what
    # deepens is the reasoning needed to see that none of the corrections applies yet.
    for i in range(DEPTH_PAIRS):
        questions += _pair(
            f"tme-bit-{index:03d}", f"tme-bit-{index + 1:03d}", f"tme-bit-p{pair_no:02d}",
            SHAPE_DEPTH, TYPE_DEPTH, SUBJECTS[(i + 3) % len(SUBJECTS)],
            corrections=2 + (i % 3), band=(i % 5) + 1, ordinal=BELIEF_PAIRS + i,
            rng=rng, echo=knob(SHAPE_DEPTH))
        index += 2
        pair_no += 1

    return questions


def check_bitemporal(questions: list[tmc.Question]) -> list[str]:
    """Vertical-specific invariants. Every one of these has a way of being silently wrong.

    The as-of precondition is the load-bearing check: a transaction-time question whose correction
    was recorded at or before the asked instant is not merely easier, it is a DIFFERENT question with
    a different right answer, and nothing downstream would notice.
    """
    failures: list[str] = []
    pairs: dict[str, list[tmc.Question]] = {}
    for q in questions:
        pairs.setdefault(q.extension["pair_id"], []).append(q)

    if len(pairs) != BELIEF_PAIRS + DEPTH_PAIRS:
        failures.append(f"{len(pairs)} pair ids, ADR-027 §3.3 declares {BELIEF_PAIRS + DEPTH_PAIRS}")

    for pid, arms in sorted(pairs.items()):
        clocks = sorted(a.extension.get("clock") for a in arms)
        if clocks != ["transaction", "valid"]:
            failures.append(f"{pid}: clocks {clocks}, expected one valid and one transaction")
            continue
        valid = next(a for a in arms if a.extension["clock"] == "valid")
        txn = next(a for a in arms if a.extension["clock"] == "transaction")

        # The pair is worthless if both clocks give the same answer -- that is the case a
        # single-clock store answers correctly by accident.
        if valid.answer == txn.answer:
            failures.append(f"{pid}: both clocks give the same answer, so the pair tests nothing")
        if len(valid.sessions) != len(txn.sessions):
            failures.append(f"{pid}: arms present different haystacks")
        if valid.question == txn.question:
            failures.append(f"{pid}: arms ask the same question")

        # THE AS-OF PRECONDITION (ADR-027 §4).
        instant = txn.extension.get("asof_instant")
        if not instant:
            failures.append(f"{pid}: transaction arm names no asked instant, so it is ill-posed")
            continue
        asked = datetime.strptime(instant, tmc.DATE_FORMAT)
        for session in txn.sessions:
            if session.tag.startswith("correction") and session.timestamp <= asked:
                failures.append(
                    f"{pid}: a correction is recorded at or before the asked instant "
                    f"({session.timestamp} <= {asked}), so the transaction arm's gold is wrong")
        for session in txn.sessions:
            if session.is_gold and not session.tag.startswith("record"):
                failures.append(f"{pid}: transaction gold is {session.tag!r}, expected the original record")
        gold_txn = [s for s in txn.sessions if s.is_gold]
        if len(gold_txn) != 1:
            failures.append(f"{pid}: transaction arm has {len(gold_txn)} gold sessions, expected 1")
        if gold_txn and gold_txn[0].timestamp > asked:
            failures.append(f"{pid}: the transaction arm's own gold post-dates the asked instant")

        # The valid arm must be answerable from the LAST correction.
        gold_valid = [s for s in valid.sessions if s.is_gold]
        if len(gold_valid) != 1 or not gold_valid[0].tag.startswith("correction"):
            failures.append(
                f"{pid}: valid-time gold is {[s.tag for s in gold_valid]}, expected one correction")

    return failures


if __name__ == "__main__":
    tmc.finalise(
        vertical="bitemporal",
        build=build,
        structure=tmc.StructureSpec(
            h_min=H_MIN, h_max=H_MAX, g_values={1}, gold_position_shuffled=True,
            # Bitemporal cannot avoid naming a time: "as of June" IS the question. Declared rather
            # than worked around, per ADR-027 §3.3.
            no_absolute_dates=False,
        ),
        generator_tool="tools/gen_typedmemeval_bitemporal.py",
        extra_checks=check_bitemporal,
        # PER-SHAPE, not one knob for both. belief-at-instant carries exactly one correction and
        # correction-depth carries two to four, which is a difficulty dial by construction -- so a
        # single knob tuned on the vertical MEAN is satisfiable by letting one shape drift while the
        # other compensates, which is the defect calibrate_per_shape exists to refuse (ADR-026 s19,
        # and Episodic's #205). Measured, the two shapes' headroom differed 0.11 against 0.29 under
        # one shared echo.
        shape_of=lambda q: (q.extension or {}).get("shape"),
    )
