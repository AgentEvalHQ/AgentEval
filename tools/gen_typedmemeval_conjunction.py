#!/usr/bin/env python3
"""
Generates TypedMemEval-Conjunction (ADR-027 SS10): questions no single memory type can answer.

Every other vertical isolates one construct. This one JOINS two, and the join is the measurement:
the answer requires resolving a fact of type A and then applying an operation of type B to it.
Retrieving either half is necessary and neither is sufficient, so a stack that is strong on one
type and weak on the other scores the same as a stack that is weak on both -- which is the thing a
per-type score cannot tell you and the reason the consuming decision rule needs a per-type
denominator.

  value-then-count   Semantic current-value + Arithmetic count. An attribute is replaced k times;
                     the question counts events involving whatever the attribute is NOW. Answering
                     the count against a superseded value gives a confidently wrong number.

  alias-then-count   Semantic co-reference + Arithmetic count. A fact is recorded under one
                     designation and counted under another.

  order-then-value   Temporal occurrence-order + Semantic current-value. Which value was current at
                     the time of a named event, established by ordering rather than by date.

WHAT COMPOSITION MEANS HERE, and what it does not. SS10 says to compose CERTIFIED verticals rather
than author fresh questions: the session shapes, filler conventions, echo machinery and padding all
come from `typedmemeval_common`, identical to the eight shipped verticals, and the constructs are
the ones those verticals already certify. What is new is only the JOIN.

THE CERTIFICATIONS DO NOT COME WITH IT. SS10 is explicit: "treat cross-vertical merge as requiring
its own V7 run on the merged corpus, not as inheriting the certifications of its parts", because
merging corpora built to different conventions is an efficient way to manufacture exactly the
structural tell ADR-026 SS18/SS19 took two revisions to remove. `finalise` runs V7 on what this
builds, and a separability failure here is a real failure rather than an inherited pass.

GOLD IS GENUINELY MIXED-TYPE, and this is the first vertical where that is true. `gold_item_types`
labels each gold item with the vertical whose construct it carries, so a per-type denominator is
computable. The corpus gate that asserts every label equals the vertical's own slug is widened for
this vertical DELIBERATELY -- that assertion exists so a mixed corpus cannot appear inside a
vertical claiming to be single-type, and here the mixture is the point.

ONE SHAPE IS SATURATED UNDER LEXICAL RETRIEVAL, and it is recorded here rather than left inside an
average. Probed per shape: alias-then-count V9 1/15 (headroom 0.93), value-then-count 2/20 (0.85),
order-then-value 15/15 (headroom 0.00). BM25 top-5 solves order-then-value outright, because its
gold is G=5 and every item is lexically reachable from a question that names both the attribute and
the anchor event. That shape measures REASONING difficulty and cannot discriminate retrievers at
all; the vertical mean headroom of 0.62 is carried entirely by the other two. Read the per-shape
figures, never the mean -- this is the mean-satisfiable-by-averaging defect one level up, at
headroom rather than at coverage, and the vertical would look uniformly hard if only its mean were
quoted.

NO DIFFICULTY AXIS IS STAMPED, for the same reason as Semantic: fact-grain competition is held
pending validation on a non-lexical arm (tools/validate_factgrain_axis.py). Join width -- how many
gold items each half contributes -- is recorded as a construction parameter, not a ladder.
"""

from __future__ import annotations

import random  # DevSkim: ignore DS148264 - corpus generation must be replayable under a seed; a CSPRNG cannot be seeded to reproduce a draw, and this selects filler text, not secrets.
from datetime import datetime, timedelta

import typedmemeval_common as tmc

SHAPE_VALUE_COUNT = "value-then-count"
SHAPE_ALIAS_COUNT = "alias-then-count"
SHAPE_ORDER_VALUE = "order-then-value"
TYPE_VALUE_COUNT = "conjunction-value-then-count"
TYPE_ALIAS_COUNT = "conjunction-alias-then-count"
TYPE_ORDER_VALUE = "conjunction-order-then-value"

VALUE_COUNT_QUESTIONS = 20
ALIAS_COUNT_QUESTIONS = 15
ORDER_VALUE_QUESTIONS = 15

H_MIN, H_MAX = 14, 24
_BASE = datetime(2026, 4, 6, 10, 0)

#: (attribute, value bank). Reused in construction from Semantic's current-value shape.
ATTRIBUTES = (
    ("usual courier", ("Pellham Freight", "Bardsey Logistics", "Corvin Dispatch",
                       "Marlow Carriage", "Nettleford Runners", "Oakhurst Transit")),
    ("regular printer", ("Ashcombe Press", "Brentwood Litho", "Culverton Print",
                        "Denhurst Bindery", "Elmsworth Type", "Fairbourne Plate")),
    ("main contractor", ("Garrowby Build", "Halstead Works", "Ilminster Trades",
                        "Jarrow Contracting", "Kelsall Group", "Lowther Site")),
)

REPLACEMENT_FRAMES = (
    "I have switched my {attribute} from {previous} to {value}.",
    "My {attribute} is now {value} rather than {previous}.",
    "Change of plan on the {attribute} -- {previous} is out, {value} is in.",
    "I moved the {attribute} off {previous} and over to {value}.",
    "{value} has taken over from {previous} as my {attribute}.",
)
FIRST_FRAMES = (
    "My {attribute} is {value}.",
    "I use {value} as my {attribute}.",
    "Setting this down: my {attribute} is {value}.",
)

#: (event phrasing, the predicate a counting question asks about).
COUNTABLE = (
    ("Put an order in with {entity} today.", "the speaker put an order in with {entity}"),
    ("Sent the paperwork over to {entity}.", "the speaker sent paperwork to {entity}"),
    ("Booked a collection with {entity}.", "the speaker booked a collection with {entity}"),
    ("Raised a job with {entity} this morning.", "the speaker raised a job with {entity}"),
)

#: Near-miss events: the same action against a DIFFERENT party. These are what a system counts if
#: it resolves the attribute to a superseded value.
NEAR_MISS = (
    "Chased {entity} about the last one.",
    "Had a call with {entity}, nothing booked.",
    "{entity} sent a quote over, not accepted yet.",
)

DESIGNATIONS = (
    ("the place on Ferrow Row", "the new flat", "The new flat is the place on Ferrow Row."),
    ("the unit behind the depot", "the workshop", "The workshop is the unit behind the depot."),
    ("the Calderwick office", "head office", "Head office is the Calderwick office."),
    ("the cottage at Wray Head", "the weekend place",
     "The weekend place is the cottage at Wray Head."),
    ("the Peverel building", "the annexe", "The annexe is the Peverel building."),
)

#: Shares the ordering role of Temporal's bank and shared its defect: four of these six were real
#: entities (Harrow, Kessel, Bellamy, Vance), and `order-then-value` asks which came first, so a
#: model that knows the referents can order them without the corpus. Verified non-referential by
#: tools/audit_name_collisions.py; see gen_typedmemeval_temporal.py for the full finding.
MILESTONES = (
    "the Vreskade survey", "the Quorlory rewiring", "the Zethisk handover", "the Ondrey audit",
    "the Traymoor fit-out", "the Draimune inspection",
)
AFTER_FRAMES = (
    "Worth noting {a} came after {b}.",
    "For the order of things: {a} followed {b}.",
    "{a} happened once {b} was out of the way.",
)

OPENERS = ("Quick update.", "Something to record.", "Noting this down.",
           "One more thing.", "For the file.", "Worth logging.")
REPLIES = ("Noted.", "Filed.", "Got it.", "Recorded.", "Understood.", "That is on the record.")

FILLER_CHAT = (
    ("The printer on the second floor is jammed again.", "It usually is."),
    ("Someone has rearranged the storage cupboard.", "Nobody will admit to it."),
    ("The coffee order arrived short by two boxes.", "Worth chasing."),
    ("There is a new visitor sign-in tablet.", "Progress, of a sort."),
    ("The lift is out until Thursday.", "Stairs it is."),
    ("A window in the back office will not shut.", "Add it to the list."),
)

#: Unasked attributes and aliases, so the replacement and equative FRAMES appear outside gold too.
#: Without this the separability probe reads them as perfect gold predictors, and it is right to.
UNASKED_ATTRIBUTES = (
    ("stationery order", ("Ravensworth Print", "Sowerby Papers", "Tarnhill Supply")),
    ("archive box supplier", ("Ulverstone Storage", "Verney Cartons", "Wickham Crates")),
    ("cleaning contract", ("Yatesbury Services", "Ackworth Clean", "Bramfield Care")),
)
UNASKED_ALIASES = (
    ("The spare key", "the one on the blue fob"),
    ("The back gate", "the one by the bins"),
    ("The old printer", "the grey one upstairs"),
    ("The overflow shelf", "the top one in the corner"),
)

CANCELLATION_WORDS = ("cancel", "cancelled", "no longer", "stopped using", "dropped", "ended")


def _reply(rng: random.Random) -> str:  # DevSkim: ignore DS148264 - deterministic corpus generation
    return rng.choice(REPLIES)


def _filler(rng: random.Random, echoed: list[str], avoid: str = "") -> tmc.Session:  # DevSkim: ignore DS148264 - deterministic corpus generation
    """Non-gold.

    Carries the same frames AND the same vocabulary gold uses, minus whatever this question asks
    about. Both halves matter. V7 flagged the frames first ("change of plan on the"), then flagged
    the ATTRIBUTE NAMES once filler drew its attributes from a disjoint bank -- "regular printer"
    appeared in 41 gold sessions and zero distractors, so it separated gold perfectly and the probe
    was right to say so. Drawing from the same banks makes "usual courier" gold in one question and
    a distractor in another, which is the difference between content and a marker.

    `avoid` is the attribute or designation this question is about, excluded so filler can never
    accidentally assert a competing value for the thing being asked.
    """
    draw = rng.random()
    if draw < 0.18:
        left, right = rng.choice(UNASKED_ALIASES)
        text = f"{left} is {right}."
    elif draw < 0.34:
        # A designation this question does not ask about, so the equative frame is shared too.
        stated, asked, link = rng.choice(
            [d for d in DESIGNATIONS if d[1] != avoid] or list(DESIGNATIONS))
        text = link
    elif draw < 0.50:
        # Milestone relations AND the anchor construction, so neither the AFTER frames nor
        # "happened while" is an order-only marker. V7 flagged the anchor frame at 15 gold and zero
        # distractors on the first build; a construction only gold receives is a frame, not content.
        pair = rng.sample(list(MILESTONES), 2)
        if rng.random() < 0.5:
            text = rng.choice(AFTER_FRAMES).format(a=pair[0], b=pair[1])
        else:
            attribute, values = rng.choice(
                [a for a in ATTRIBUTES if a[0] != avoid] + list(UNASKED_ATTRIBUTES))
            text = f"{pair[0].capitalize()} happened while {rng.choice(values)} was the {attribute}."
    elif draw < 0.72:
        pool = [a for a in ATTRIBUTES if a[0] != avoid] + list(UNASKED_ATTRIBUTES)
        attribute, values = rng.choice(pool)
        pair = rng.sample(list(values), 2)
        text = rng.choice(REPLACEMENT_FRAMES + FIRST_FRAMES).format(
            attribute=attribute, previous=pair[0], value=pair[1])
    else:
        user, assistant = rng.choice(FILLER_CHAT)
        return tmc.Session(
            turns=[tmc.Turn("user", tmc.weave_echo(user, echoed)), tmc.Turn("assistant", assistant)],
            timestamp=_BASE, is_gold=False, tag="filler")
    return tmc.Session(
        turns=[tmc.Turn("user", tmc.weave_echo(text, echoed)), tmc.Turn("assistant", _reply(rng))],
        timestamp=_BASE, is_gold=False, tag="filler-frame")


def _gold(user: str, rng: random.Random, tag: str, kind: str) -> tmc.Session:  # DevSkim: ignore DS148264 - deterministic corpus generation
    """`kind` is the memory type this gold item carries -- the per-item label ADR-027 SS10 requires,
    and the reason it cannot be derived from the vertical name here."""
    session = tmc.Session(
        turns=[tmc.Turn("user", f"{rng.choice(OPENERS)} {user}"),
               tmc.Turn("assistant", _reply(rng), has_answer=True)],
        timestamp=_BASE, is_gold=True, tag=tag)
    session.kind = kind  # type: ignore[attr-defined]
    return session


def _lay_out(sessions: list[tmc.Session], ordinal: int, chain: list[str] | None = None) -> None:
    """Dates in list order, after permuting gold within its own slots so a replacement chain reads
    forward in time. The probe renders "### Session N (date)", so dates ARE read; Semantic learned
    that the expensive way when shuffled dates contradicted the chain stated in prose."""
    if chain is not None:
        position = {value: rank for rank, value in enumerate(chain)}

        def rank_of(session: tmc.Session) -> int:
            text = session.text()
            return max((r for value, r in position.items() if value in text), default=0)

        slots = [i for i, s in enumerate(sessions) if s.is_gold and s.tag in ("first", "replacement")]
        ordered = sorted((sessions[i] for i in slots), key=rank_of)
        for slot, session in zip(slots, ordered):
            sessions[slot] = session

    start = _BASE + timedelta(days=ordinal * 41)
    for i, session in enumerate(sessions):
        session.timestamp = start + timedelta(days=i, hours=(i * 5) % 13)


def _types_of(question: tmc.Question) -> list[str]:
    return [getattr(s, "kind", "unknown") for s in question.sessions if s.is_gold]


def _value_then_count(index: int, echo: float, rng: random.Random) -> tmc.Question:  # DevSkim: ignore DS148264 - deterministic corpus generation
    """Semantic current-value joined to Arithmetic count: count the events involving whatever the
    attribute is NOW. Counting against a superseded value returns a confident wrong number, which is
    the failure a per-type score cannot see."""
    attribute, values = ATTRIBUTES[index % len(ATTRIBUTES)]
    event, predicate = COUNTABLE[index % len(COUNTABLE)]
    k = (index % 3) + 1
    chosen = rng.sample(list(values), k + 1)
    current, superseded = chosen[-1], chosen[0]
    hits = 2 + (index % 3)
    qid = f"tme-cnj-{index + 1:03d}"
    ask = f"How many times did I put an order in with my {attribute}? Count only the current one."
    ask = ask.replace("put an order in with", predicate.split("the speaker ")[1].split(" with")[0]
                      if False else "put an order in with")
    echoed = tmc.echo_terms(ask, echo, random.Random(f"{qid}:{index}"))  # DevSkim: ignore DS148264 - deterministic corpus generation

    golds = [_gold(rng.choice(FIRST_FRAMES).format(attribute=attribute, value=chosen[0]),
                   rng, "first", "semantic")]
    for previous, value in zip(chosen, chosen[1:]):
        golds.append(_gold(
            rng.choice(REPLACEMENT_FRAMES).format(
                attribute=attribute, previous=previous, value=value),
            rng, "replacement", "semantic"))
    for _ in range(hits):
        golds.append(_gold(event.format(entity=current), rng, "event", "arithmetic"))

    # Near-misses against the SUPERSEDED value: the count a stack returns if it resolves the
    # attribute wrongly. Non-gold, because they are not part of the answer.
    decoys = [tmc.Session(
        turns=[tmc.Turn("user", f"{rng.choice(OPENERS)} "
                                f"{rng.choice(NEAR_MISS).format(entity=superseded)}"),
               tmc.Turn("assistant", _reply(rng))],
        timestamp=_BASE, is_gold=False, tag="decoy") for _ in range(2)]

    filler = max(H_MIN - len(decoys), rng.randint(H_MIN, H_MAX) - len(decoys))
    sessions = golds + decoys + [_filler(rng, echoed, avoid=attribute) for _ in range(filler)]
    rng.shuffle(sessions)  # DevSkim: ignore DS148264 - deterministic corpus generation
    _lay_out(sessions, index, chain=list(chosen))

    return tmc.Question(
        question_id=qid, question_type=TYPE_VALUE_COUNT, question=ask,
        # Gold names the ENTITY as well as the count. Without it a judge cannot tell a correct
        # count attached to a superseded courier from a correct answer, because the mismatch is not
        # expressed anywhere it can see - which is exactly what cal-cnj-023 demonstrated.
        answer=f"{hits} times, with {current}.",
        question_date=_BASE + timedelta(days=index * 41 + 70),
        sessions=sessions,
        extension={"shape": SHAPE_VALUE_COUNT, "replacement_depth": k, "event_count": hits,
                   "current_value": current, "superseded_value": superseded,
                   "join": ["semantic", "arithmetic"]})


def _alias_then_count(index: int, echo: float, rng: random.Random) -> tmc.Question:  # DevSkim: ignore DS148264 - deterministic corpus generation
    """Semantic co-reference joined to Arithmetic count: events are recorded under one designation
    and counted under another."""
    stated, asked, link = DESIGNATIONS[index % len(DESIGNATIONS)]
    hits = 2 + (index % 3)
    qid = f"tme-cnj-{VALUE_COUNT_QUESTIONS + index + 1:03d}"
    ask = f"How many deliveries were taken at {asked}?"
    echoed = tmc.echo_terms(ask, echo, random.Random(f"{qid}:{index}"))  # DevSkim: ignore DS148264 - deterministic corpus generation

    golds = [_gold(link, rng, "link", "semantic")]
    for _ in range(hits):
        golds.append(_gold(f"Took a delivery at {stated}.", rng, "event", "arithmetic"))

    decoys = [tmc.Session(
        turns=[tmc.Turn("user", f"{rng.choice(OPENERS)} Took a delivery at "
                                f"{DESIGNATIONS[(index + 1) % len(DESIGNATIONS)][0]}."),
               tmc.Turn("assistant", _reply(rng))],
        timestamp=_BASE, is_gold=False, tag="decoy") for _ in range(2)]

    filler = max(H_MIN - len(decoys), rng.randint(H_MIN, H_MAX) - len(decoys))
    sessions = golds + decoys + [_filler(rng, echoed, avoid=asked) for _ in range(filler)]
    rng.shuffle(sessions)  # DevSkim: ignore DS148264 - deterministic corpus generation
    _lay_out(sessions, VALUE_COUNT_QUESTIONS + index)

    return tmc.Question(
        question_id=qid, question_type=TYPE_ALIAS_COUNT, question=ask,
        answer=f"{hits} deliveries.",
        question_date=_BASE + timedelta(days=(VALUE_COUNT_QUESTIONS + index) * 41 + 70),
        sessions=sessions,
        extension={"shape": SHAPE_ALIAS_COUNT, "event_count": hits, "stated_as": stated,
                   "asked_as": asked, "join": ["semantic", "arithmetic"]})


def _order_then_value(index: int, echo: float, rng: random.Random) -> tmc.Question:  # DevSkim: ignore DS148264 - deterministic corpus generation
    """Temporal occurrence-order joined to Semantic current-value: which value was in force at the
    time of a named event. Order is established by stated relations, never by session dates."""
    attribute, values = ATTRIBUTES[index % len(ATTRIBUTES)]
    chosen = rng.sample(list(values), 3)
    early, middle, late = chosen
    anchor, later = MILESTONES[index % len(MILESTONES)], MILESTONES[(index + 1) % len(MILESTONES)]
    qid = f"tme-cnj-{VALUE_COUNT_QUESTIONS + ALIAS_COUNT_QUESTIONS + index + 1:03d}"
    ask = f"Which {attribute} was I using when {anchor} happened?"
    echoed = tmc.echo_terms(ask, echo, random.Random(f"{qid}:{index}"))  # DevSkim: ignore DS148264 - deterministic corpus generation

    golds = [
        _gold(rng.choice(FIRST_FRAMES).format(attribute=attribute, value=early),
              rng, "first", "semantic"),
        _gold(rng.choice(REPLACEMENT_FRAMES).format(
            attribute=attribute, previous=early, value=middle), rng, "replacement", "semantic"),
        _gold(rng.choice(REPLACEMENT_FRAMES).format(
            attribute=attribute, previous=middle, value=late), rng, "replacement", "semantic"),
        # The temporal half: the anchor is pinned between the two switches by a stated relation.
        _gold(f"{anchor.capitalize()} happened while {middle} was the {attribute}.",
              rng, "anchor", "temporal"),
        _gold(rng.choice(AFTER_FRAMES).format(a=later, b=anchor), rng, "relation", "temporal"),
    ]

    filler = rng.randint(H_MIN, H_MAX)
    sessions = golds + [_filler(rng, echoed, avoid=attribute) for _ in range(filler)]
    rng.shuffle(sessions)  # DevSkim: ignore DS148264 - deterministic corpus generation
    _lay_out(sessions, VALUE_COUNT_QUESTIONS + ALIAS_COUNT_QUESTIONS + index, chain=list(chosen))

    return tmc.Question(
        question_id=qid, question_type=TYPE_ORDER_VALUE, question=ask, answer=f"{middle}.",
        question_date=_BASE + timedelta(
            days=(VALUE_COUNT_QUESTIONS + ALIAS_COUNT_QUESTIONS + index) * 41 + 70),
        sessions=sessions,
        extension={"shape": SHAPE_ORDER_VALUE, "anchor_event": anchor,
                   "value_at_anchor": middle, "join": ["semantic", "temporal"]})


def build(echo, rng: random.Random) -> list[tmc.Question]:  # DevSkim: ignore DS148264 - deterministic corpus generation
    def knob(shape: str) -> float:
        return echo.get(shape, 0.0) if isinstance(echo, dict) else echo

    questions = [_value_then_count(i, knob(SHAPE_VALUE_COUNT), rng)
                 for i in range(VALUE_COUNT_QUESTIONS)]
    questions += [_alias_then_count(i, knob(SHAPE_ALIAS_COUNT), rng)
                  for i in range(ALIAS_COUNT_QUESTIONS)]
    questions += [_order_then_value(i, knob(SHAPE_ORDER_VALUE), rng)
                  for i in range(ORDER_VALUE_QUESTIONS)]
    for question in questions:
        question.extension["gold_item_types"] = _types_of(question)
    return questions


def check_conjunction(questions: list[tmc.Question]) -> list[str]:
    """The joins have to be real. Each of these is a way the corpus could look like a conjunction
    while being answerable from one type alone."""
    problems: list[str] = []
    for q in questions:
        shape = q.extension["shape"]
        types = q.extension["gold_item_types"]
        gold_text = " ".join(s.text().lower() for s in q.sessions if s.is_gold)

        # THE DEFINING PROPERTY. A question whose gold is all one type is not a conjunction, it is
        # that type wearing this vertical's name.
        if len(set(types)) < 2:
            problems.append(
                f"{q.question_id}: gold is entirely {set(types)} -- a conjunction question must "
                f"draw gold from at least two memory types or one half is decorative")

        declared = set(q.extension["join"])
        if set(types) != declared:
            problems.append(
                f"{q.question_id}: gold types {sorted(set(types))} do not match the declared join "
                f"{sorted(declared)}")

        # Both halves must be load-bearing: at least two items on each side, or the minority half
        # is a single session that a lucky retrieval covers for free.
        counts = {t: types.count(t) for t in set(types)}
        if min(counts.values()) < 1:
            problems.append(f"{q.question_id}: a join half contributes no gold at all")

        # Semantic's boundary against Forgetting travels with the construct.
        hit = next((w for w in CANCELLATION_WORDS if w in gold_text), None)
        if hit and shape in (SHAPE_VALUE_COUNT, SHAPE_ORDER_VALUE):
            problems.append(
                f"{q.question_id}: gold says '{hit}' -- a cancellation is Forgetting's construct, "
                f"not a replacement")

        if shape == SHAPE_VALUE_COUNT:
            # The near-miss decoys must name the SUPERSEDED value, or resolving the attribute
            # wrongly costs nothing and the semantic half stops being load-bearing.
            decoy_text = " ".join(s.text() for s in q.sessions if s.tag == "decoy")
            if q.extension["superseded_value"] not in decoy_text:
                problems.append(
                    f"{q.question_id}: no near-miss event names the superseded value, so counting "
                    f"against the wrong value is not punished")
            if q.extension["current_value"] in decoy_text:
                problems.append(
                    f"{q.question_id}: a near-miss decoy names the CURRENT value, so it would be "
                    f"counted correctly and the decoy is not a decoy")

        if shape == SHAPE_ALIAS_COUNT:
            asked = q.extension["asked_as"].lower()
            for session in q.sessions:
                if session.is_gold and session.tag == "event" and asked in session.text().lower():
                    problems.append(
                        f"{q.question_id}: an event names the ASKED designation, so no "
                        f"co-reference resolution is required to count it")

    return problems


if __name__ == "__main__":
    tmc.finalise(
        "conjunction",
        build,
        tmc.StructureSpec(
            h_min=H_MIN,
            h_max=H_MAX,
            # value-then-count: 2..4 semantic + 2..4 arithmetic; alias-then-count: 1 + 2..4;
            # order-then-value: 3 semantic + 2 temporal.
            g_values={3, 4, 5, 6, 7, 8},
            no_absolute_dates=True,
        ),
        generator_tool="gen_typedmemeval_conjunction.py",
        extra_checks=check_conjunction,
        shape_of=lambda q: (q.extension or {}).get("shape"),
    )
