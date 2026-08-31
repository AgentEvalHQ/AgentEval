#!/usr/bin/env python3
"""
Generates TypedMemEval-Temporal (ADR-027 §3.2): the order events OCCURRED, not the order they were
mentioned, and never a number.

THE CONSTRUCTION IS THE WHOLE DESIGN. If events are narrated in the order they happened, every
ordering question is answerable from the session timestamp index alone -- a metadata sort with no
reading and no reasoning, which measures a database rather than a memory. So narration order
deliberately DISAGREES with occurrence order: sessions mention events retrospectively, each anchored
to another by a stated relation ("the rewiring came after the survey"), and the true order is
recoverable only by following that chain.

The session timestamps are therefore actively misleading, on purpose. A system that sorts by
`haystack_dates` gets a defensible-looking answer that is wrong, and that is the discrimination this
vertical exists for.

BOUNDARY AGAINST ARITHMETIC (ADR-027 §3.2). Arithmetic computes a value from numbers -- sums, counts,
durations. Temporal orders events, and NO ANSWER HERE MAY BE A NUMBER REQUIRING ADDITION. "How long
between" belongs to Arithmetic and is excluded. The rule is mechanical rather than editorial, so
`check_temporal` enforces it: an answer carrying a digit fails the corpus.

BOUNDARY AGAINST EPISODIC list-order. Episodic orders MENTIONS -- the sequence in which items were
added to a shortlist. Temporal orders OCCURRENCES, which here contradict mention order by
construction. The two are distinguishable precisely because this vertical requires the contradiction
to exist, and `check_temporal` refuses a question whose narration order happens to match its
occurrence order.

DIFFICULTY IS A REASONING DIAL. ADR-027 §6.1 measured interference cost ~0 on four of the five
shipped verticals: dispersion cannot discriminate a stack that retrieves everything. The dial here is
NARRATION DISORDER -- how many events sit out of place, and how long the relation chain between the
two asked events runs. Both change what must be reasoned after retrieval has already succeeded.
"""

from __future__ import annotations

import random  # DevSkim: ignore DS148264 - corpus generation must be replayable under a seed; a CSPRNG cannot be seeded to reproduce a draw, and this selects filler text, not secrets.
from datetime import datetime, timedelta

import typedmemeval_common as tmc

SHAPE_ORDER = "occurrence-order"
SHAPE_BETWEEN = "interval-position"
SHAPE_RECENCY = "recency"
TYPE_ORDER = "temporal-occurrence-order"
TYPE_BETWEEN = "temporal-interval-position"
TYPE_RECENCY = "temporal-recency"

ORDER_QUESTIONS = 20
BETWEEN_QUESTIONS = 15
RECENCY_QUESTIONS = 15

H_MIN, H_MAX = 14, 24
_BASE = datetime(2026, 2, 2, 10, 0)

#: Milestone names, VERIFIED non-referential rather than assumed so.
#:
#: This comment used to read "Invented milestone names. Arbitrary by construction (V2): nothing
#: about a name makes it likelier to be first, so zero-context guessing has nothing to work with."
#: V2 disproved it. Asked "Which came first, the Fenn commissioning or the Yarrow move?" with no
#: context at all, the reference model answered "Yarrow moved its shipbuilding operations to
#: Scotstoun, Glasgow in the early 1900s, while the Fenn commissioning was during World War II" --
#: real knowledge about Yarrow Shipbuilders and a USS Fenn. NINE of the twelve names were real
#: entities: Harrow and Kessel are places, Bellamy and Vance and Ruskin are people, Calder is a
#: river, Esker is a company.
#:
#: AND V2 SEES ONLY PART OF IT, because it scores a leak that agrees with gold. Five questions show
#: the model reasoning from world knowledge; V2 flagged two. `tme-tem-013` hit 10/10 purely because
#: the corpus happened to order those two events the way history did -- ordered the other way, the
#: model would have been confidently wrong ten times out of ten and the question would have PASSED.
#: So the arm that caught this cannot be the arm that certifies the fix.
#:
#: Every name below was put through `tools/audit_name_collisions.py`, which asks the reference model
#: whether it can state a concrete fact about the name, and kept only those it could not. The
#: instrument that condemned the old bank is the one that cleared the new one.
MILESTONES = (
    "the Vreskade survey", "the Quorlory rewiring", "the Zethisk handover", "the Ondrey audit",
    "the Traymoor fit-out", "the Draimune inspection", "the Wrenfield sign-off",
    "the Plennell move", "the Skarvarn relining", "the Trevuade changeover",
    "the Ovridory retrofit", "the Janduisk commissioning",
)
FILLER_MILESTONES = (
    "the Wexilune clear-out", "the Ghelmell repaint", "the Prazzarn resurfacing",
    "the Sallow re-roofing", "the Yolviade rewire", "the Kreshory screed",
)

#: One bank for gold and filler alike. `{a}` is anchored relative to `{b}`.
AFTER_FRAMES = (
    "Worth noting {a} came after {b}.",
    "For the order of things: {a} followed {b}.",
    "{a} happened once {b} was out of the way.",
    "We only got to {a} after {b}.",
    "{a} came later than {b}, for the record.",
)
BEFORE_FRAMES = (
    "Worth noting {a} came before {b}.",
    "For the order of things: {a} preceded {b}.",
    "{a} was done well ahead of {b}.",
    "{a} happened first, then {b}.",
    "{a} came earlier than {b}, for the record.",
)
OPENERS = (
    "Thinking back over the programme.",
    "Going through the file again.",
    "Sorting out what happened when.",
    "Reconstructing the sequence.",
    "Tidying up the notes.",
)
REPLIES = ("Noted.", "Filed.", "Got it.", "Recorded.", "Understood.", "That is on the record.")

FILLER_CHAT = (
    ("The site office is being moved again.", "Somebody will lose a chair."),
    ("Two of the access badges stopped working.", "Worth reporting before Monday."),
    ("The skip was collected earlier than booked.", "Convenient for once."),
    ("There is a new sign-in sheet at the gate.", "Progress, of a sort."),
    ("The kettle in the portacabin has died.", "A genuine emergency."),
    ("Parking is being reorganised for the works.", "Expect complaints."),
)


def _inversions(narration: list[int]) -> int:
    """How many pairs the narration order gets the wrong way round against occurrence order.

    This IS the difficulty dial. `narration[i]` is the occurrence index of the event narrated i-th,
    so a fully chronological narration scores 0 -- and a question that scores 0 is refused, because
    it would be answerable by sorting the session dates without reading anything.
    """
    return sum(1 for i in range(len(narration))
               for j in range(i + 1, len(narration)) if narration[i] > narration[j])


#: Inversions per band. Bands are the REASONING dial: more inversions means more of the chain has to
#: be followed before the order comes out, with the evidence equally easy to retrieve throughout.
_DISORDER_BANDS = {1: (1, 2), 2: (3, 4), 3: (5, 7), 4: (8, 11), 5: (12, 99)}


def _band_of(inversions: int) -> int:
    for band, (low, high) in sorted(_DISORDER_BANDS.items()):
        if low <= inversions <= high:
            return band
    return 5


def _shuffled_narration(count: int, band: int,
                        rng: random.Random) -> list[int]:  # DevSkim: ignore DS148264 - deterministic corpus generation
    """A narration order whose inversion count lands in `band`, or the closest reachable.

    Drawn by rejection rather than constructed, because the reachable inversion counts depend on
    `count` and an analytic construction would silently clamp -- which is how Bitemporal's first
    latency dial ended up with one shape owning a band.
    """
    low, high = _DISORDER_BANDS[band]
    best, best_gap = None, None
    for _ in range(400):
        candidate = list(range(count))
        rng.shuffle(candidate)  # DevSkim: ignore DS148264 - deterministic corpus generation
        found = _inversions(candidate)
        if low <= found <= high:
            return candidate
        gap = low - found if found < low else found - high
        if best_gap is None or gap < best_gap:
            best, best_gap = candidate, gap
    return best or list(reversed(range(count)))


def _filler_session(rng: random.Random, echoed: list[str],  # DevSkim: ignore DS148264 - deterministic corpus generation
                    stamp: datetime) -> tmc.Session:
    """Filler in gold's own construction: other programmes, ordered relative to each other.

    Without this the relation frames appear only in gold and the vertical is separable on the word
    "after" -- the shared-bank rule that took this family three revisions to learn.
    """
    if rng.random() < 0.55:
        a, b = rng.sample(FILLER_MILESTONES, 2)
        frame = rng.choice(AFTER_FRAMES if rng.random() < 0.5 else BEFORE_FRAMES)
        user = f"{rng.choice(OPENERS)} {frame.format(a=a, b=b)}"
        return tmc.make_session(stamp, (user, tmc.weave_echo(rng.choice(REPLIES), echoed)),
                                tag="filler")
    # The chat filler gets the same OPENER and the same REPLIES bank as everything else. Written
    # with its own bespoke replies and no opener, it was the odd construction out, and the gate found
    # it from two directions at once -- assistant uppercase density at 3.2 sd and user punctuation
    # density at 2.8. One bank for both sides means ALL of both sides, not most of it.
    user, _ = rng.choice(FILLER_CHAT)
    return tmc.make_session(stamp, (f"{rng.choice(OPENERS)} {user}",
                                    tmc.weave_echo(rng.choice(REPLIES), echoed)), tag="filler")


def _question(qid: str, shape: str, qtype: str, band: int, ordinal: int,
              rng: random.Random, echo: float) -> tmc.Question:  # DevSkim: ignore DS148264 - deterministic corpus generation
    """One haystack whose narration order contradicts its occurrence order.

    `events` is occurrence order. `narration` says which event each session mentions, in the order
    the sessions appear, and it is shuffled to land in the requested disorder band. Every session
    anchors its event to its OCCURRENCE-order neighbour, so the chain is a single line: drop one link
    and the pair it separates can no longer be ordered, which is what makes gold necessary (V3).
    """
    count = 4 + (ordinal % 3)                                   # 4-6 events
    events = list(rng.sample(MILESTONES, count))                # index 0 happened first
    narration = _shuffled_narration(count, band, rng)
    disorder = _inversions(narration)

    question_text = _ask(shape, events, rng)
    echoed = tmc.echo_terms(question_text, echo, rng)

    # Each event is anchored to the one that occurred before it, so the relations form one chain in
    # OCCURRENCE order while the sessions appear in NARRATION order.
    relation_of: dict[int, tmc.Session] = {}
    for position in narration:
        if position == 0:
            continue
        later, earlier = events[position], events[position - 1]
        frame = rng.choice(AFTER_FRAMES if rng.random() < 0.5 else BEFORE_FRAMES)
        text = (frame.format(a=later, b=earlier) if frame in AFTER_FRAMES
                else frame.format(a=earlier, b=later))
        # ECHO PARITY, IN THE SAME TURN AND AT THE SAME RATE AS FILLER. The calibration clause goes
        # into filler's ASSISTANT turn to push coverage down; a relation session that omits it is
        # separable by the clause's ABSENCE, which is the first shipped build's tell inverted.
        #
        # It surfaced only when recency's gold grew from two links to the whole chain: more gold
        # needed a higher echo to hold coverage, which pushed the distractor rate to 1.00 and the
        # gap past the 0.5 parity bar. The imbalance was always there, just under the threshold.
        #
        # A first attempt wove it into the USER turn at 60%, and the gate caught that from two
        # directions at once - parity still failing at 0.40 vs 1.00, and assistant_punctuation_
        # density separating at AUC 0.849, because filler assistants carried the clause and gold
        # assistants did not. Same turn, same rate, no exceptions: this file's own filler comment
        # already said one bank for both sides means ALL of both sides.
        relation_of[position] = tmc.make_session(
            _BASE, (f"{rng.choice(OPENERS)} {text}",
                    tmc.weave_echo(rng.choice(REPLIES), echoed)),
            gold_turn=0, tag=f"link{position}")

    gold_links = _links_needed(shape, count)
    distractors = rng.randint(H_MIN, H_MAX)
    sessions = [_filler_session(rng, echoed, _BASE)
                for _ in range(distractors - (len(relation_of) - len(gold_links)))]

    # Links go in narration order, which is not occurrence order -- the point of the vertical.
    for position in narration:
        if position in relation_of:
            sessions.insert(rng.randint(0, len(sessions)), relation_of[position])

    asked_at = _lay_out(sessions, ordinal)
    answer = _answer(shape, events)
    question = tmc.Question(
        qid, qtype, question_text, answer, asked_at, sessions,
        {"shape": shape, "events": count, "narration_inversions": disorder,
         "difficulty": _band_of(disorder), "difficulty_dial": "narration-disorder",
         "difficulty_validated": False})
    for session in question.sessions:
        session.is_gold = any(session is relation_of.get(p) for p in gold_links)
        for turn in session.turns:
            if turn.has_answer and not session.is_gold:
                turn.has_answer = False
    return question


def _links_needed(shape: str, count: int) -> list[int]:
    """The chain links that are individually NECESSARY and jointly SUFFICIENT for the answer.

    Both halves matter and they pull against each other. Too few links and the question is not
    answerable from its own gold, so V1 fails and the corpus is measuring the model's willingness to
    guess. Too many and ablating one leaves the rest, so V3 passes on redundant evidence and the
    corpus claims a necessity it does not have.

    Each shape is therefore scoped to a window it can close on its own:

      occurrence-order   the two ENDS of the chain, needing every link between them
      interval-position  a three-event window, needing both links inside it
      recency            spans the chain, most recent decided by every link

    THIS PARAGRAPH USED TO SAY THE OPPOSITE, and it was wrong in a way worth keeping visible:
    "asking about non-adjacent events would need the whole chain, and then dropping a link at the
    far end would leave the answer derivable -- which is exactly the redundancy this avoids."
    Dropping ANY link splits the chain into two segments; events[0] and events[-1] land on opposite
    sides and cannot be related at all, so every link is NECESSARY rather than redundant. The
    argument was never checked against the construction it described.

    What it cost: with the asked pair ADJACENT, the single link between them is one session stating
    the answer outright -- "we only got to the Vreskade survey after the Quorlory rewiring" -- while
    the question hands BM25 both rare names. required_sessions_median was 1 and V9 was 19/20 against
    V1 20/20, headroom 0.05: a lexical lookup wearing an ordering costume, in the vertical whose
    entire premise is that narration order must be followed rather than read off. Spanning the chain
    is the same repair recency already carries, for the same reason (ADR-028 s7.4).
    """
    if shape == SHAPE_ORDER:
        return list(range(1, count))
    # recency now spans the chain, so every link between the earliest asked event and the latest is
    # required. See _ask: the window is events[0], a middle event, events[-1].
    if shape == SHAPE_RECENCY:
        return list(range(1, count))
    return [1, 2]


def _ask(shape: str, events: list[str], rng: random.Random) -> str:  # DevSkim: ignore DS148264 - deterministic corpus generation
    """Every question names its events in a SHUFFLED order, so position in the prompt carries nothing."""
    if shape == SHAPE_ORDER:
        # THE TWO ENDS OF THE CHAIN, not the first two events. An adjacent pair is decided by the
        # one link that names both of them, which is a single-session lookup; the ends are related
        # only by following every link in between. See _links_needed.
        pair = [events[0], events[-1]]
        rng.shuffle(pair)
        return f"Which came first, {pair[0]} or {pair[1]}?"
    if shape == SHAPE_RECENCY:
        # SPREAD ACROSS THE CHAIN, not the last three. Asking about three adjacent events made the
        # answer one transitive step over two sessions that named the asked events outright, and
        # the shape scored 15/15 at V1, V8 AND V9 - the only one in the family no system could be
        # ranked by. Spanning the chain means every link has to be followed.
        window = [events[0], events[len(events) // 2], events[-1]]
        rng.shuffle(window)
        return f"Of {window[0]}, {window[1]} and {window[2]}, which happened most recently?"
    window = [events[0], events[2]]
    rng.shuffle(window)
    return f"Between {window[0]} and {window[1]}, what happened in between?"


def _sentence(text: str) -> str:
    """Upper-cases the first character only; `str.capitalize()` would lower-case the rest."""
    return text[:1].upper() + text[1:]


def _answer(shape: str, events: list[str]) -> str:
    """Answers name an EVENT, never a quantity -- the Arithmetic boundary, enforced in check_temporal."""
    if shape == SHAPE_ORDER:
        return _sentence(f"{events[0]} came first.")
    if shape == SHAPE_RECENCY:
        return _sentence(f"{events[-1]} happened most recently of the three.")
    return _sentence(f"{events[1]} happened between them.")


def _lay_out(sessions: list[tmc.Session], ordinal: int) -> datetime:
    """Stamps sessions in NARRATION order and returns a query time after all of them.

    The timestamps therefore encode when a thing was MENTIONED, never when it happened. That is not a
    limitation of the corpus, it is the corpus: a system that sorts by date is answering a different
    question and should be marked wrong for it.
    """
    start = _BASE + timedelta(days=4 * ordinal)
    for session, stamp in zip(sessions, tmc.spread(start, len(sessions))):
        session.timestamp = stamp
    return sessions[-1].timestamp + timedelta(days=2, hours=6)


def build(echo: float, rng: random.Random) -> list[tmc.Question]:  # DevSkim: ignore DS148264 - deterministic corpus generation
    questions: list[tmc.Question] = []
    index = 1
    # Bands cycle WITHIN each shape, so band and shape are not collinear. Bitemporal's first draft
    # banded on a shape property and rebuilt the Arithmetic confound (ADR-026 §19) from scratch;
    # ADR-027 §6 says to refuse that at design time, which means arranging it here.
    for shape, qtype, count in ((SHAPE_ORDER, TYPE_ORDER, ORDER_QUESTIONS),
                                (SHAPE_BETWEEN, TYPE_BETWEEN, BETWEEN_QUESTIONS),
                                (SHAPE_RECENCY, TYPE_RECENCY, RECENCY_QUESTIONS)):
        for i in range(count):
            questions.append(_question(f"tme-tem-{index:03d}", shape, qtype,
                                       band=(i % 5) + 1, ordinal=index, rng=rng, echo=echo))
            index += 1
    return questions


def check_temporal(questions: list[tmc.Question]) -> list[str]:
    """The two boundary rules, enforced rather than described.

    Both are the kind of thing a reviewer nods at and a generator quietly violates.
    """
    failures: list[str] = []
    for q in questions:
        # ARITHMETIC BOUNDARY: an answer that carries a digit is a computed quantity, and computed
        # quantities belong to Arithmetic. "How long between" is deliberately not asked here.
        if any(character.isdigit() for character in q.answer):
            failures.append(f"{q.question_id}: answer contains a digit, which is Arithmetic's job")

        # THE VERTICAL'S PREMISE: narration must contradict occurrence. A question with zero
        # inversions is answerable by sorting session dates, with no reading and no reasoning -- it
        # would measure an index rather than a memory, and it would inflate every number here.
        if q.extension.get("narration_inversions", 0) < 1:
            failures.append(
                f"{q.question_id}: narration order matches occurrence order, so the answer is a "
                f"metadata sort rather than a memory question")

        # Gold must be minimal. If every link were gold, ablating one would leave the rest and V3
        # would pass on redundant evidence.
        if q.g < 1:
            failures.append(f"{q.question_id}: no gold link")
        # recency AND occurrence-order both span the chain now, so their gold is every link between
        # the earliest asked event and the latest -- count - 1 of them. Minimality still holds and
        # is the reason the number is derived rather than pinned: with a single chain A<B<C<D<E and
        # a question over {A, C, E}, dropping ANY intermediate link removes a transitive step the
        # answer needs, so V6 leave-one-out still bites on every one of them.
        if q.extension["shape"] == SHAPE_ORDER:
            # Spans the chain: every link between the two asked ends. Derived, not pinned, for the
            # same reason recency's is -- a literal would silently stop matching the construction.
            expected_g = q.extension["events"] - 1
        elif q.extension["shape"] == SHAPE_RECENCY:
            expected_g = q.extension["events"] - 1
        else:
            expected_g = 2
        if q.g != expected_g:
            failures.append(
                f"{q.question_id}: G={q.g} for {q.extension['shape']}, expected {expected_g} -- the "
                f"gold links must be exactly those that are necessary and sufficient")

    # WORD ORDER, CHECKED AT THE CORPUS LEVEL RATHER THAN PER QUESTION.
    #
    # "Which came first, A or B?" names the answer first half the time, and that is correct: the pair
    # is shuffled, so position carries no information. The first version of this check flagged every
    # individual question where the answer happened to be named first and refused a third of the
    # corpus for behaving exactly as designed -- a check measuring at the wrong grain, which is the
    # defect this whole family keeps relearning. What would actually leak is a corpus-wide BIAS, so
    # that is what is measured.
    ordered = [q for q in questions if q.extension["shape"] == SHAPE_ORDER]
    if ordered:
        # Case-insensitive: the answer is sentence-cased and the question is not, so a literal
        # comparison read 0% -- which is impossible for a shuffled pair and is therefore the tell
        # that the CHECK is broken rather than the corpus. An extreme value from a process that
        # cannot produce one is worth more than a plausible value from a process that can.
        named_first = sum(
            1 for q in ordered
            if q.question.split("Which came first, ")[-1].split(" or ")[0].strip("?").casefold()
            == q.answer.split(" came first")[0].casefold())
        share = named_first / len(ordered)
        if not 0.25 <= share <= 0.75:
            failures.append(
                f"occurrence-order names the answer first in {share:.0%} of questions; position in "
                f"the prompt is carrying information about the answer")
    return failures


if __name__ == "__main__":
    tmc.finalise(
        vertical="temporal",
        build=build,
        structure=tmc.StructureSpec(
            h_min=H_MIN, h_max=H_MAX,
            # 2 for interval-position, and 3-5 for occurrence-order and recency, which both span
            # the chain: gold is every link between the earliest asked event and the latest.
            g_values={1, 2, 3, 4, 5}, gold_position_shuffled=True,
            no_absolute_dates=True,
        ),
        generator_tool="tools/gen_typedmemeval_temporal.py",
        extra_checks=check_temporal,
    )
