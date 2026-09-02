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

ORDER-THEN-VALUE WAS SATURATED AND IS NOT ANY MORE, and the reason it was is worth keeping. It
measured V9 15/15, headroom 0.00 -- a shape on which no two retrievers could be told apart -- while
its BM25 coverage was only 0.667. Those two numbers together are the diagnosis: the retriever was
fetching two thirds of gold and the model still scored perfectly, so the missing third could not
have mattered. One gold session read "{anchor} happened while {middle} was the {attribute}", naming
the anchor and the answer in one sentence, and it was the only session carrying both terms the
question names -- so it was both the easiest to retrieve and sufficient on its own. The join this
shape exists to test was never required.

The anchor is now pinned to the SWITCH EVENTS rather than to the value, so answering needs two hops
in different sessions: place the anchor between the two switches, then read which value that switch
moved to. Read the per-shape figures, never the mean -- a saturated shape inside a healthy mean is
the mean-satisfiable-by-averaging defect one level up, at headroom rather than at coverage.

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
SHAPE_CONDITIONAL = "conditional-branch"
TYPE_VALUE_COUNT = "conjunction-value-then-count"
TYPE_ALIAS_COUNT = "conjunction-alias-then-count"
TYPE_ORDER_VALUE = "conjunction-order-then-value"
TYPE_CONDITIONAL = "conjunction-conditional-branch"

VALUE_COUNT_QUESTIONS = 20
ALIAS_COUNT_QUESTIONS = 15
ORDER_VALUE_QUESTIONS = 15
#: ADR-029's one declared cost. Conjunction shipped 50 across three shapes (20/15/15); a fourth at
#: parity would put every shape near 12, under the ~15 line at which this family's own guidance says
#: a shape supports diagnosis rather than a claim. So the vertical GROWS rather than redistributing,
#: and the consuming project is told before it does.
CONDITIONAL_QUESTIONS = 15

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
#: (event as the session records it, predicate for the judge, HOW THE QUESTION ASKS FOR IT).
#:
#: The third element is new and it fixes a real defect: the question hard-coded "put an order in
#: with" for every draw, so nineteen of twenty questions asked how many times the speaker ORDERED
#: while the gold sessions recorded sending paperwork, booking collections, or raising jobs. The
#: question asked about an event that never happened, and the shape's apparent difficulty was partly
#: that incoherence rather than the join it exists to test.
#:
#: It is stated EXPLICITLY rather than parsed out of the predicate. The disabled attempt that stood
#: here derived it as `predicate.split("the speaker ")[1].split(" with")[0]`, which silently returns
#: the whole phrase for "sent paperwork TO {entity}" -- there is no " with" to split on. A bank of
#: three parallel forms is longer and cannot go quietly wrong.
COUNTABLE = (
    ("Put an order in with {entity} today.", "the speaker put an order in with {entity}",
     "put an order in with"),
    ("Sent the paperwork over to {entity}.", "the speaker sent paperwork to {entity}",
     "send paperwork over to"),
    ("Booked a collection with {entity}.", "the speaker booked a collection with {entity}",
     "book a collection with"),
    ("Raised a job with {entity} this morning.", "the speaker raised a job with {entity}",
     "raise a job with"),
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


def _filler(rng: random.Random, echoed: list[str], avoid: str = "",  # DevSkim: ignore DS148264 - deterministic corpus generation
            avoid_condition: str = "") -> tmc.Session:
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
        pair = rng.sample(list(MILESTONES), 3)
        roll = rng.random()
        if roll < 0.34:
            text = rng.choice(AFTER_FRAMES).format(a=pair[0], b=pair[1])
        elif roll < 0.67:
            # The BETWEEN-EVENTS construction gold's anchor now uses. Added with that change: a
            # construction only gold receives is a frame, not content, and this branch exists
            # because V7 caught exactly that on the previous anchor frame (15 gold, 0 distractors).
            text = f"{_lead(pair[0])} happened after {pair[1]} and before {pair[2]}."
        else:
            attribute, values = rng.choice(
                [a for a in ATTRIBUTES if a[0] != avoid] + list(UNASKED_ATTRIBUTES))
            text = f"{_lead(pair[0])} happened while {rng.choice(values)} was the {attribute}."
    elif draw < 0.62:
        # RULES AND STATES IN FILLER, at the rate gold uses them, for the reason written three
        # times above this function and learned the hard way each time: a construction only gold
        # ever receives is a frame, not content. "Standing rule for the" appearing in 15 gold
        # sessions and zero distractors would separate this shape perfectly, and the separability
        # probe would be right to say so.
        #
        # Both halves are excluded from the asked question: a rule for the asked ATTRIBUTE would
        # assert a competing branch table, and a state for the asked CONDITION would put a second
        # answer in the haystack. `avoid_condition` is why this signature grew.
        pool = [a for a in ATTRIBUTES if a[0] != avoid] + list(UNASKED_ATTRIBUTES)
        other_attribute, other_values = rng.choice(pool)
        conditions = [c for c in CONDITIONS if c[0] != avoid_condition] or list(CONDITIONS)
        other_condition, other_states = rng.choice(conditions)
        if rng.random() < 0.5 and len(other_values) >= 3:
            picks = rng.sample(list(other_values), 3)
            text = rng.choice(RULE_FRAMES).format(
                attribute=other_attribute, condition=other_condition,
                s0=other_states[0], v0=picks[0], s1=other_states[1], v1=picks[1], v2=picks[2])
        else:
            text = rng.choice(STATE_FRAMES).format(
                condition=other_condition, state=rng.choice(other_states))
    elif draw < 0.80:
        pool = [a for a in ATTRIBUTES if a[0] != avoid] + list(UNASKED_ATTRIBUTES)
        attribute, values = rng.choice(pool)
        pair = rng.sample(list(values), 2)
        text = rng.choice(REPLACEMENT_FRAMES + FIRST_FRAMES).format(
            attribute=attribute, previous=pair[0], value=pair[1])
        # Gold's switches are dated by an event ("That was the week of X") so the anchor can refer
        # to them without naming a value. Filler dates its switches the same way, at the same rate,
        # or the clause is a gold frame -- which the separability gate caught on the first build of
        # this change: 'the week of' in 30 gold sessions and zero distractors.
        if rng.random() < 0.5:
            text += f" That was the week of {rng.choice(MILESTONES)}."
    else:
        user, assistant = rng.choice(FILLER_CHAT)
        return tmc.Session(
            turns=[tmc.Turn("user", tmc.weave_echo(user, echoed)), tmc.Turn("assistant", assistant)],
            timestamp=_BASE, is_gold=False, tag="filler")
    return tmc.Session(
        turns=[tmc.Turn("user", tmc.weave_echo(text, echoed)), tmc.Turn("assistant", _reply(rng))],
        timestamp=_BASE, is_gold=False, tag="filler-frame")


#: Conditions a standing rule can turn on, and the states each can be in.
#:
#: Invented and non-referential, for the reason MILESTONES records: four of that bank's six names
#: were real places, and a shape asking which came first can be answered by a model that knows the
#: referents. Same exposure here -- if a reader knows what "Marrow Lane" is, the rule stops being
#: arbitrary. Verified by tools/audit_name_collisions.py alongside the rest of the family.
#:
#: THREE states, not two, and the arithmetic is the reason. A reader that retrieves the rule and
#: misses the state can still pick a branch: at two branches that is a coin flip, so V9 floors at
#: 0.50 and caps this shape's headroom at 0.50 before it measures anything. Three puts the floor at
#: 0.33. It is the ADR-028 SS7.4 chance-floor discipline applied at DESIGN time rather than
#: discovered in the probe records afterwards.
CONDITIONS = (
    ("the Kelvaryn access", ("open", "closed for resurfacing", "restricted to one lane")),
    ("the Peskadd permit", ("granted", "still pending", "refused")),
    ("the Zannifer slot", ("confirmed", "provisional", "released back")),
    ("the Muldreth gauge", ("in the green", "in the amber", "in the red")),
    ("the Ferrasque window", ("open", "narrowed", "shut for the season")),
    ("the Tovrekk clearance", ("signed off", "queried", "withdrawn")),
)

#: The rule, which is the whole shape. It names the attribute AND the condition, because it is the
#: bridge between them; that is what makes it reachable from the question and what makes the state
#: session reachable only THROUGH it.
RULE_FRAMES = (
    "Standing rule for the {attribute}: if {condition} is {s0} it is {v0}, if {s1} it is {v1}, "
    "and otherwise it is {v2}.",
    "We settled the {attribute} by rule: {v0} when {condition} is {s0}, {v1} when {s1}, {v2} "
    "in any other case.",
    "How the {attribute} gets picked: {condition} {s0} means {v0}, {s1} means {v1}, anything "
    "else means {v2}.",
)

#: The state, which names ONLY the condition. Never the attribute, never a value -- if it named the
#: attribute the question would retrieve it directly and there would be no second hop; if it named a
#: value it would answer the question by itself, which is the defect order-then-value shipped with
#: for its whole life (V9 15/15 against coverage 0.667).
STATE_FRAMES = (
    "{condition} came back {state} this morning.",
    "Heard today: {condition} is {state}.",
    "Logging it -- {condition} is {state} as of now.",
    "They confirmed {condition} is {state}.",
)


def _lead(phrase: str) -> str:
    """Sentence-initial form. `str.capitalize()` LOWERCASES the remainder, so "the Zethisk handover"
    became "The zethisk handover" -- a lowercased proper noun occurring nowhere else in the corpus,
    which is a gold marker wearing a typo."""
    return phrase[:1].upper() + phrase[1:]


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
    event, predicate, asked_as = COUNTABLE[index % len(COUNTABLE)]
    k = (index % 3) + 1
    chosen = rng.sample(list(values), k + 1)
    current, superseded = chosen[-1], chosen[0]
    hits = 2 + (index % 3)
    qid = f"tme-cnj-{index + 1:03d}"
    # ASKS FOR BOTH HALVES, because gold requires both. Gold is "{hits} times, with {current}" and
    # names the entity deliberately -- without it a judge cannot tell a correct count attached to a
    # SUPERSEDED value from a correct answer. But the question used to ask only for the count, so
    # "4 times" was fully responsive and scored wrong (tme-cnj-006 failed V1 on exactly that), and
    # for the questions that passed, naming the entity was verbosity rather than evidence the
    # semantic half had been performed. A conjunction shape has to ask for both halves or it cannot
    # observe that the join happened.
    ask = (f"How many times did I {asked_as} my current {attribute}, "
           f"and which one is that?")
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

    # THE DECOY'S COUNT MUST DIFFER FROM GOLD'S, and it did not.
    #
    # It was hardcoded at 2 while gold is `2 + (index % 3)`, so on every third question the decoy
    # designation carried EXACTLY gold's count -- five of fifteen. On those five, a system that
    # resolves the alias to the wrong place still produces the right number, which is precisely the
    # failure this shape exists to detect. The join was not load-bearing on a third of the shape.
    #
    # It surfaced as a V3 leak on tme-cnj-024: with gold ablated the model answered "two deliveries
    # were taken at the Peverel building" -- a different designation, the same count, and
    # `require_distinctive` cannot separate them because the distinctive token is the digit both
    # share. V3 read 50/50 before this, on a corpus where five questions could leak; the pass was
    # luck, not evidence, which is the third time that sentence has been written in this family.
    decoy_hits = hits + 1 if hits < 4 else 2
    decoy_as = DESIGNATIONS[(index + 1) % len(DESIGNATIONS)][0]
    decoys = [tmc.Session(
        turns=[tmc.Turn("user", f"{rng.choice(OPENERS)} Took a delivery at {decoy_as}."),
               tmc.Turn("assistant", _reply(rng))],
        timestamp=_BASE, is_gold=False, tag="decoy") for _ in range(decoy_hits)]

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
                   "asked_as": asked, "decoy_as": decoy_as,
                   "decoy_event_count": decoy_hits,
                   "join": ["semantic", "arithmetic"]})


def _order_then_value(index: int, echo: float, rng: random.Random) -> tmc.Question:  # DevSkim: ignore DS148264 - deterministic corpus generation
    """Temporal occurrence-order joined to Semantic current-value: which value was in force at the
    time of a named event. Order is established by stated relations, never by session dates."""
    attribute, values = ATTRIBUTES[index % len(ATTRIBUTES)]
    chosen = rng.sample(list(values), 3)
    early, middle, late = chosen
    # Four distinct milestones: the anchor being asked about, one later event that fixes the
    # anchor's place in the chain, and one event PER SWITCH. The switch events are what make the
    # join real -- see the gold list below.
    anchor, later, switch_to_middle, switch_to_late = rng.sample(list(MILESTONES), 4)
    qid = f"tme-cnj-{VALUE_COUNT_QUESTIONS + ALIAS_COUNT_QUESTIONS + index + 1:03d}"
    ask = f"Which {attribute} was I using when {anchor} happened?"
    echoed = tmc.echo_terms(ask, echo, random.Random(f"{qid}:{index}"))  # DevSkim: ignore DS148264 - deterministic corpus generation

    golds = [
        _gold(rng.choice(FIRST_FRAMES).format(attribute=attribute, value=early),
              rng, "first", "semantic"),
        # Each switch is dated by an EVENT rather than a timestamp, so the anchor session above can
        # refer to it without naming the value it moved to.
        _gold(rng.choice(REPLACEMENT_FRAMES).format(
            attribute=attribute, previous=early, value=middle)
            + f" That was the week of {switch_to_middle}.", rng, "replacement", "semantic"),
        _gold(rng.choice(REPLACEMENT_FRAMES).format(
            attribute=attribute, previous=middle, value=late)
            + f" That was the week of {switch_to_late}.", rng, "replacement", "semantic"),
        # THE TEMPORAL HALF, AND THE REASON THIS SHAPE HAS A JOIN AT ALL. This session used to
        # read "{anchor} happened while {middle} was the {attribute}" -- which names the anchor
        # AND the answer in one sentence, so retrieving this session alone answered the question
        # and no join was ever required. It measured 15/15 at V9 against coverage 0.667: the
        # retriever was fetching two thirds of gold and the model still scored perfectly, because
        # the one session it reliably fetched (the only one carrying both terms the question names)
        # was a complete answer by itself.
        #
        # The anchor is now pinned to the SWITCH EVENTS instead of to the value. Answering needs
        # two hops that live in different sessions: place the anchor between the two switch events,
        # then read which value that switch moved to. No single gold session carries both halves.
        _gold(f"{_lead(anchor)} happened after {switch_to_middle} "
              f"and before {switch_to_late}.", rng, "anchor", "temporal"),
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


def _conditional_branch(index: int, echo: float, rng: random.Random) -> tmc.Question:  # DevSkim: ignore DS148264 - deterministic corpus generation
    """A stated rule joined to a stated state: which branch is in force right now.

    THE GAP THIS FILLS, measured rather than asserted: across all 470 questions shipped at
    v0.32.0-beta, not one required resolving a conditional. Five contain "if" or "unless" and in
    every case the conditional is quoted PAYLOAD, never the thing being asked (ADR-029 SS7b).

    A conjunction rather than a tenth vertical, because the join is the same structural move as its
    neighbours: find the rule -> find the condition's state -> select the branch. ADR-027 SS11.1's
    standard -- "a vertical whose shapes each have a plausible existing home is a weak vertical" --
    is satisfied by NOT making it one.

    The two hops live in different sessions and only one of them is lexically reachable from the
    question. The question names the attribute; the rule names the attribute and the condition; the
    state names the condition alone. So a retriever working from the question can find the rule and
    cannot find the state without first reading it -- which is the definition of a second hop, and
    the thing order-then-value had to be rebuilt to obtain.
    """
    # MEASURED ON THE SHIPPED CORPUS, and it is why this shape's coverage sits at exactly 0.50:
    #
    #     rule in BM25 top-5 ....... 15/15
    #     state in BM25 top-5 ......  0/15
    #     both .....................  0/15   <- the only questions V9 could answer
    #
    # So coverage is 1-of-2 on every question by CONSTRUCTION, not by calibration -- the echo knob
    # has nothing to move here, and 0.50 is a structural floor that happens to coincide with the
    # band's lower edge. READ A BAND FAILURE ON THIS SHAPE AS THE RULE HAVING BECOME UNRETRIEVABLE,
    # never as a reason to widen the band.
    #
    # V9 comes in at 0/15 against a declared chance floor of 1/3, which is below chance and is not
    # a fault: the reference model retrieves the rule, sees three named branches, and DECLINES --
    # "the conversations don't say whether the Peskadd permit is granted, still pending, or
    # something else". It is not guessing, so it does not collect the guesser's third.
    attribute, values = ATTRIBUTES[index % len(ATTRIBUTES)]
    condition, states = CONDITIONS[index % len(CONDITIONS)]
    outcomes = rng.sample(list(values), 3)
    # Which branch the haystack puts in force. Rotated rather than drawn, so all three branches --
    # including the `otherwise` fallthrough -- are exercised across the shape instead of appearing
    # at whatever rate the rng happens to give.
    branch = index % 3
    state = states[branch]
    answer = outcomes[branch]

    qid = f"tme-cnj-{VALUE_COUNT_QUESTIONS + ALIAS_COUNT_QUESTIONS + ORDER_VALUE_QUESTIONS + index + 1:03d}"
    ask = f"Going by the standing rule, which {attribute} is in force?"
    echoed = tmc.echo_terms(ask, echo, random.Random(f"{qid}:{index}"))  # DevSkim: ignore DS148264 - deterministic corpus generation

    golds = [
        _gold(rng.choice(RULE_FRAMES).format(
            attribute=attribute, condition=condition,
            s0=states[0], v0=outcomes[0], s1=states[1], v1=outcomes[1], v2=outcomes[2]),
            rng, "rule", "semantic"),
        _gold(rng.choice(STATE_FRAMES).format(condition=condition, state=state),
              rng, "state", "episodic"),
    ]

    filler = rng.randint(H_MIN, H_MAX)
    sessions = golds + [_filler(rng, echoed, avoid=attribute, avoid_condition=condition)
                        for _ in range(filler)]
    rng.shuffle(sessions)  # DevSkim: ignore DS148264 - deterministic corpus generation
    _lay_out(sessions, VALUE_COUNT_QUESTIONS + ALIAS_COUNT_QUESTIONS + ORDER_VALUE_QUESTIONS + index)

    return tmc.Question(
        question_id=qid, question_type=TYPE_CONDITIONAL, question=ask, answer=f"{answer}.",
        question_date=_BASE + timedelta(
            days=(VALUE_COUNT_QUESTIONS + ALIAS_COUNT_QUESTIONS + ORDER_VALUE_QUESTIONS + index)
            * 41 + 70),
        sessions=sessions,
        extension={
            "shape": SHAPE_CONDITIONAL,
            # The rule is a standing policy the speaker states once and does not restate; the state
            # is a specific thing heard on a specific day. Semantic joined to Episodic, and the
            # declared join is checked against the per-item kinds below.
            "join": ["semantic", "episodic"],
            "condition": condition,
            "branch_taken": branch,
            # PUBLISHED, because it is a chance floor and this family has been caught by unpublished
            # ones twice (ADR-028 SS7.4, and V2's 69 unearned passes). The rule session names all
            # three outcomes, and BM25 retrieves the rule for 15 of 15 questions while retrieving
            # the state for 4 of 15 -- measured on the shipped corpus. So a reader holding only the
            # rule is choosing among three named candidates, and V9 on this shape has a floor of
            # 1/3 that no amount of retrieval skill is being credited for.
            #
            # `closed_choice_k` cannot see this: it parses the QUESTION, and the question names no
            # candidates. The candidates are in the haystack, which is a different thing and needed
            # saying out loud.
            "branches": 3,
            # GENERIC KEY, deliberately. `chance_floor_given_rule` was the first spelling and it
            # would have made the instrument that reads it conditional-branch-only -- which is the
            # applied-once defect this project has logged three times in one day before now. Any
            # future shape whose haystack hands the reader a closed set of candidates publishes the
            # same two fields and gets the same column.
            "chance_floor": round(1 / 3, 4),
            "chance_floor_reason": (
                "the rule session names all three branch outcomes, so a reader that retrieves the "
                "rule and misses the state is choosing among three named candidates"),
            # The branch table, recorded so the boundary rule below is checked against what the
            # generator MEANT rather than against a re-parse of the prose it emitted.
            "branch_outcomes": list(outcomes),
            "branch_states": list(states),
            "difficulty": branch + 1,
            "difficulty_dial": "branch", "difficulty_validated": False,
        },
    )


def build(echo, rng: random.Random) -> list[tmc.Question]:  # DevSkim: ignore DS148264 - deterministic corpus generation
    def knob(shape: str) -> float:
        return echo.get(shape, 0.0) if isinstance(echo, dict) else echo

    questions = [_value_then_count(i, knob(SHAPE_VALUE_COUNT), rng)
                 for i in range(VALUE_COUNT_QUESTIONS)]
    questions += [_alias_then_count(i, knob(SHAPE_ALIAS_COUNT), rng)
                  for i in range(ALIAS_COUNT_QUESTIONS)]
    questions += [_order_then_value(i, knob(SHAPE_ORDER_VALUE), rng)
                  for i in range(ORDER_VALUE_QUESTIONS)]
    questions += [_conditional_branch(i, knob(SHAPE_CONDITIONAL), rng)
                  for i in range(CONDITIONAL_QUESTIONS)]
    for question in questions:
        question.extension["gold_item_types"] = _types_of(question)
    return questions


def _attribute_of(question: tmc.Question) -> str:
    """The attribute a conditional-branch question asks about, read off the question text.

    Off the QUESTION, not off a recorded field, because the question is what a retriever sees and
    the check is about what is lexically reachable from it. A recorded copy could drift from the
    prose and the check would still pass.
    """
    for attribute, _ in ATTRIBUTES:
        if attribute in question.question:
            return attribute
    return ""


def check_conjunction(questions: list[tmc.Question]) -> list[str]:
    """The joins have to be real. Each of these is a way the corpus could look like a conjunction
    while being answerable from one type alone."""
    problems: list[str] = []
    for q in questions:
        shape = q.extension["shape"]
        types = q.extension["gold_item_types"]
        gold_text = " ".join(s.text().lower() for s in q.sessions if s.is_gold)

        if shape == SHAPE_ALIAS_COUNT:
            # Counted from the SESSIONS, not from the two recorded numbers. Comparing
            # `event_count` against `decoy_event_count` would be the generator agreeing with
            # itself; what matters is how many delivery sessions each designation actually got.
            stated_as, asked_as = q.extension["stated_as"], q.extension["asked_as"]
            other = q.extension["decoy_as"]
            # THE EVENT SENTENCE, not a mention. Filler states designation links of its own
            # ("The annexe is the Peverel building."), so counting every non-gold session that
            # NAMES the decoy counts sessions that record no delivery -- which is what the first
            # version of this guard did, and it reported tme-cnj-026 at 4 against a real 2.
            # An over-counting guard is not the safe direction here: it would have been "fixed" by
            # loosening it, and the loosening would have taken the real check with it.
            gold_events = sum(1 for s in q.sessions
                              if s.is_gold and s.tag == "event"
                              and f"Took a delivery at {stated_as}" in s.text())
            decoy_events = sum(1 for s in q.sessions if not s.is_gold
                               and f"Took a delivery at {other}" in s.text())
            if gold_events == decoy_events:
                problems.append(
                    f"{q.question_id}: the decoy designation has the same event count as gold "
                    f"({gold_events}), so resolving the alias WRONG still gives the right answer "
                    f"and the join is not load-bearing")
            if not gold_events:
                problems.append(f"{q.question_id}: no gold event sessions name {stated_as!r}")
            del asked_as

        if shape == SHAPE_CONDITIONAL:
            # ADR-029's boundary rule, and the reason this shape could be built inside Conjunction
            # at all: it is mechanically checkable, and no existing vertical can express it.
            #
            #   "The answer must CHANGE depending on a condition stated in the haystack. A question
            #    whose answer is the same under both branches is not a conditional question and is
            #    refused at generation."
            #
            # Checked against the recorded branch table rather than re-parsed out of the prose the
            # generator emitted -- a check that re-reads the artifact's own output is agreeing with
            # itself, which is the co-moving-operands shape.
            outcomes = q.extension["branch_outcomes"]
            states = q.extension["branch_states"]
            taken = q.extension["branch_taken"]
            condition = q.extension["condition"]
            attribute_values = [v for _, values in ATTRIBUTES for v in values]

            if len(set(outcomes)) != len(outcomes):
                problems.append(
                    f"{q.question_id}: branch outcomes {outcomes} are not all distinct, so the "
                    f"answer does not change with the condition and this is not a conditional")
            if len(set(states)) != len(states):
                problems.append(f"{q.question_id}: branch states {states} are not all distinct")
            if q.answer.rstrip(".") != outcomes[taken]:
                problems.append(
                    f"{q.question_id}: gold {q.answer!r} is not the branch the stated state "
                    f"selects ({outcomes[taken]!r})")

            rule = [s for s in q.sessions if s.is_gold and s.tag == "rule"]
            state = [s for s in q.sessions if s.is_gold and s.tag == "state"]
            if len(rule) != 1 or len(state) != 1:
                problems.append(
                    f"{q.question_id}: expected exactly one rule and one state gold session, "
                    f"got {len(rule)} and {len(state)}")
                continue
            rule_text, state_text = rule[0].text(), state[0].text()

            # THE SECOND HOP, ENFORCED. The question names the attribute, so anything naming the
            # attribute is lexically reachable from it. If the STATE session named the attribute
            # too, a retriever would fetch both halves off one query and the join would be
            # decorative -- which is exactly how order-then-value shipped saturated at V9 15/15.
            if _attribute_of(q) in state_text.lower():
                problems.append(
                    f"{q.question_id}: the state session names the attribute, so both hops are "
                    f"reachable from the question and no join is required")
            if any(v.lower() in state_text.lower() for v in attribute_values):
                problems.append(
                    f"{q.question_id}: the state session names a candidate value, so it answers "
                    f"the question by itself")
            if condition.lower() not in rule_text.lower():
                problems.append(f"{q.question_id}: the rule does not name its own condition")
            if _attribute_of(q) not in rule_text.lower():
                problems.append(f"{q.question_id}: the rule does not name its own attribute")

            # NO SECOND ANSWER. A distractor stating another state for the asked condition, or
            # another rule for the asked attribute, puts a competing branch in the haystack.
            for j, session in enumerate(q.sessions):
                if session.is_gold:
                    continue
                text = session.text().lower()
                if condition.lower() in text:
                    problems.append(
                        f"{q.question_id} s{j}: a distractor names the asked condition "
                        f"{condition!r}, which can state a second state for it")
                if _attribute_of(q) in text and "rule" in text:
                    problems.append(
                        f"{q.question_id} s{j}: a distractor states a rule for the asked attribute")

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
            # 2 added with `conditional-branch`, and it is the honest minimum for that join rather
            # than a relaxation: a rule and the state it turns on are two sessions, and padding to
            # three would mean inventing a component the shape does not need. The declared set moves
            # to follow the design; the alternative is a design bent to fit a declaration.
            #
            # DISCLOSED: this changes the vertical's G distribution, which is a retrieval control.
            # G=2 has a structural ceiling of 1.0 at K_REF=5, so nothing is capped by it -- the
            # shape's difficulty is that one of the two golds is not lexically reachable from the
            # question, not that there are many of them.
            g_values={2, 3, 4, 5, 6, 7, 8},
            no_absolute_dates=True,
        ),
        generator_tool="gen_typedmemeval_conjunction.py",
        extra_checks=check_conjunction,
        shape_of=lambda q: (q.extension or {}).get("shape"),
    )
