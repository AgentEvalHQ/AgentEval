#!/usr/bin/env python3
"""
Generates TypedMemEval-Semantic (ADR-027 SS3.1, narrowed): RESOLUTION, not recall.

SS2.1 REFUSED plain-fact Semantic outright. Stating a fact once and asking about it later is
saturated by construction -- a lexical baseline answers it, LongMemEval's semantic questions
already cover it, and a vertical that cannot fail measures nothing. The three shapes here share
one property that plain recall lacks: RETRIEVING THE EVIDENCE IS NECESSARY AND NOT SUFFICIENT.

  current-value      An attribute is stated, then REPLACED k times. What is it now? A stack that
                     retrieves all k statements still has to order them and take the last. k is
                     the replacement depth.

  co-reference       A fact is stated under one designation and asked under another. Lexical
                     retrieval of the asked term does not reach the session that states the fact;
                     it reaches the session that LINKS the two designations, and only then the
                     fact. Designation distance is the number of alias hops.

  source-attribution Which conversation did a belief come from? Scored on the cited source, not on
                     restating the belief. Candidate-source count is how many conversations could
                     plausibly have carried it.

BOUNDARY AGAINST FORGETTING, enforced rather than promised. Forgetting asks "is it still true, or
was it cancelled?" -- abstention is in its outcome space and its gold is sometimes "nothing
recorded". Semantic current-value asks "what is it NOW?" after k REPLACEMENTS: never a
cancellation, never an abstention, always a stated value. `check_semantic` refuses any replacement
phrased as a cancellation, because a corpus that blurs the two measures Forgetting's construct
under Semantic's name.

ORDER IS STATED TWICE, AND BOTH MUST AGREE. Each replacement NAMES the value it supersedes, so the
chain is recoverable from text alone; and gold session dates are laid out to follow that chain, so
a reader who sorts by recency gets the same answer as a reader who follows the sentences.

Both halves were learned the hard way. An early revision asserted here that order "is carried by
the TEXT" while the frames encoded no order at all - V1 scored 6/20 and the model answered with the
FIRST value. Adding the chain moved it only to 7/20, because the second half was still wrong:
sessions are shuffled and dates were stamped in shuffled order, so the corpus told the reader one
thing in prose and the opposite in metadata. The probe renders "### Session N (date)", so dates are
read. Asked the same questions with no dates the model answered 4 of 5 correctly, which is what
located it. Ordering by recency IS the resolution SS3.1 asks for, so agreeing dates cost the shape
nothing: every statement must still be retrieved before the last one can be named.

NO DIFFICULTY AXIS IS STAMPED. The paired second axis -- fact-grain competition -- is HELD pending
validation on a non-lexical arm. See tools/validate_factgrain_axis.py: the only competition
measure that predicts our V9 misses is derived from the same BM25 that V9 uses, and the two
retriever-independent measures carry no signal at all. k, designation distance and
candidate-source count are recorded as CONSTRUCTION PARAMETERS, not as a validated difficulty
ladder. That distinction is the whole lesson of the ladder already retracted.
"""

from __future__ import annotations

import random  # DevSkim: ignore DS148264 - corpus generation must be replayable under a seed; a CSPRNG cannot be seeded to reproduce a draw, and this selects filler text, not secrets.
from datetime import datetime, timedelta

import typedmemeval_common as tmc

SHAPE_CURRENT = "current-value"
SHAPE_COREF = "co-reference"
SHAPE_SOURCE = "source-attribution"
TYPE_CURRENT = "semantic-current-value"
TYPE_COREF = "semantic-co-reference"
TYPE_SOURCE = "semantic-source-attribution"

CURRENT_QUESTIONS = 20
COREF_QUESTIONS = 15
SOURCE_QUESTIONS = 15

H_MIN, H_MAX = 14, 24
_BASE = datetime(2026, 3, 2, 10, 0)

#: (attribute, question, value bank). Values are invented and interchangeable: nothing about one
#: makes it likelier to be the current one, so a zero-context guess has nothing to work with (V2).
ATTRIBUTES = (
    ("usual courier", "Who is my usual courier now?",
     ("Pellham Freight", "Bardsey Logistics", "Corvin Dispatch", "Marlow Carriage",
      "Nettleford Runners", "Oakhurst Transit")),
    ("supplier for cartridges", "Who do I get cartridges from now?",
     ("Ashcombe Supplies", "Brentwood Stationers", "Culverton Office", "Denhurst Paper",
      "Elmsworth Trade", "Fairbourne Depot")),
    ("standing meeting room", "Which room is my standing meeting in now?",
     ("the Ridgeway room", "the Selby room", "the Thornbury room", "the Underhill room",
      "the Vanbrugh room", "the Westcote room")),
    ("main project code", "What is my main project code now?",
     ("Harbourlight", "Ironvale", "Junewell", "Kestrelmoor", "Larkspur", "Millbrook")),
)

#: How a replacement is stated. Every frame REPLACES; none cancels. `check_semantic` enforces that
#: -- a cancellation would put the question in Forgetting's outcome space, where abstention is a
#: legitimate answer and here it never is.
REPLACEMENT_FRAMES = (
    "I have switched my {attribute} from {previous} to {value}.",
    "My {attribute} is now {value} rather than {previous}.",
    "Change of plan on the {attribute} -- {previous} is out, {value} is in.",
    "I moved the {attribute} off {previous} and over to {value}.",
    "{value} has taken over from {previous} as my {attribute}.",
)
#: The opening statement has no predecessor, so `previous` is accepted and ignored -- str.format
#: tolerates unused keys, which keeps one call site for both frame banks.
FIRST_FRAMES = (
    "My {attribute} is {value}.",
    "I use {value} as my {attribute}.",
    "Setting this down: my {attribute} is {value}.",
)

#: (stated designation, asked designation, link sentence). The link session is what a lexical
#: retriever CAN reach from the asked term; the stating session is what it cannot.
DESIGNATIONS = (
    ("the place on Ferrow Row", "the new flat", "The new flat is the place on Ferrow Row."),
    ("the unit behind the depot", "the workshop", "The workshop is the unit behind the depot."),
    ("the Calderwick office", "head office", "Head office is the Calderwick office."),
    ("the blue estate car", "the runaround", "The runaround is the blue estate car."),
    ("the cottage at Wray Head", "the weekend place",
     "The weekend place is the cottage at Wray Head."),
    ("the Peverel building", "the annexe", "The annexe is the Peverel building."),
)

#: An intermediate designation, for questions with two alias hops rather than one.
MIDDLE_DESIGNATIONS = (
    "the second address", "the other site", "the spare set", "the overflow space",
    "the back office", "the far end",
)

#: (fact, question template, gold answer). Asked under the FAR designation.
#: (fact, question template, answer, RIVAL FACTS about other places).
#:
#: The fourth element exists because V6 refuted this shape's central claim. The generator said "the
#: chain is the evidence, not just its endpoint"; leave-one-out passed only 4 of 15. Reading the
#: survivors gives the mechanism at once: the haystack contained exactly ONE lease fact, ONE boiler
#: fact, ONE roof fact. So "How long does the lease run at the workshop?" is answerable by finding
#: the only lease statement in the corpus, whatever place it is filed under, and the co-reference
#: hop the shape exists to measure is never required. Dropping the link changed nothing:
#:
#:   tme-sem-022, link ablated -> "The lease runs to the end of next year." (correct, no hop taken)
#:
#: This is `conjunction/alias-then-count`'s decoy defect in another vertical: resolving the alias
#: WRONG, or not at all, still gives the right answer. Rivals are same-KIND facts under other
#: designations with DIFFERENT answers, so a reader that does not resolve the designation is left
#: with several candidates and no way to choose.
COREF_FACTS = (
    ("the boiler was replaced in the spring", "When was the boiler replaced at {asked}?",
     "In the spring.",
     ("the boiler was replaced two winters ago", "the boiler was replaced right after the sale")),
    ("the lease runs to the end of next year", "How long does the lease run at {asked}?",
     "To the end of next year.",
     ("the lease runs to the end of the month", "the lease runs for another three years")),
    ("the alarm code was changed after the break-in", "What happened to the alarm code at {asked}?",
     "It was changed after the break-in.",
     ("the alarm code was changed when the keys went missing",
      "the alarm code was changed at the start of the year")),
    # SAME PREDICATE, DIFFERENT VALUE -- as every other entry here does, and as this one did not.
    # Its rivals were "the flooring needs doing" and "the guttering needs doing", a different
    # SUBJECT rather than a different answer. So a reader that never resolved the designation could
    # still pick the only ROOF fact and be right, and V6 caught it 3 of 3: "The roof needs doing
    # before winter -- logged for the blue estate car", naming the wrong place and matching gold
    # anyway, because the gold answer does not name a place either.
    ("the roof needs doing before winter", "What work is outstanding at {asked}?",
     "Roof work, due before winter.",
     ("the roof needs doing before the spring", "the roof needs doing once the scaffold is free")),
    ("parking there is on the north side", "Where is the parking at {asked}?",
     "On the north side.",
     ("parking there is behind the loading bay", "parking there is on the street only")),
)

#: (topic, belief). The topic is how a source gets NAMED in an answer, so it has to be distinctive
#: enough to cite and mundane enough not to be mistaken for the belief itself.
SOURCE_TOPICS = (
    ("the roof repair", "the guttering has to be done at the same time"),
    ("the insurance renewal", "the excess doubles if a claim is made twice"),
    ("the parking permit", "the permit does not cover the visitor bay"),
    ("the broadband change", "the old line stays live for a month"),
    ("the recycling collection", "glass is taken on a different week"),
    ("the boiler service", "the warranty lapses if a service is missed"),
    ("the window quote", "scaffolding is charged separately"),
    ("the fence dispute", "the boundary follows the old wall"),
)

#: Conversations that COULD have carried the belief but did not. These are the competitors for
#: source-attribution: each is a plausible citation, so naming the right one requires reading.
DECOY_TOPICS = (
    "the gutter clearing", "the chimney inspection", "the drain survey",
    "the loft insulation", "the porch repair", "the driveway resurfacing",
    "the garage door", "the shed replacement",
)

OPENERS = (
    "Quick update.", "Something to record.", "Noting this down.",
    "One more thing.", "For the file.", "Worth logging.",
)
REPLIES = ("Noted.", "Filed.", "Got it.", "Recorded.", "Understood.", "That is on the record.")

FILLER_CHAT = (
    ("The printer on the second floor is jammed again.", "It usually is."),
    ("Someone has rearranged the storage cupboard.", "Nobody will admit to it."),
    ("The coffee order arrived short by two boxes.", "Worth chasing."),
    ("There is a new visitor sign-in tablet.", "Progress, of a sort."),
    ("The lift is out until Thursday.", "Stairs it is."),
    ("A window in the back office will not shut.", "Add it to the list."),
    ("The recycling bins were not emptied.", "Again."),
    ("Someone left a bicycle in the corridor.", "It will be moved."),
)

#: Any of these in a replacement statement would make the question a Forgetting question.
CANCELLATION_WORDS = (
    "cancel", "cancelled", "no longer", "stopped using", "dropped", "ended",
    "terminated", "closed the account", "not any more", "gave up",
)


def _reply(rng: random.Random) -> str:  # DevSkim: ignore DS148264 - deterministic corpus generation
    return rng.choice(REPLIES)


#: Attributes nobody is ever asked about, so filler can state and replace them freely.
UNASKED_ATTRIBUTES = (
    ("stationery order", ("Ravensworth Print", "Sowerby Papers", "Tarnhill Supply")),
    ("archive box supplier", ("Ulverstone Storage", "Verney Cartons", "Wickham Crates")),
    ("cleaning contract", ("Yatesbury Services", "Ackworth Clean", "Bramfield Care")),
    ("shredding pickup", ("Cawdor Secure", "Dilworth Disposal", "Elvaston Shred")),
)


#: Equative pairs for things nobody asks about, so the alias FRAME is not a gold marker.
UNASKED_ALIASES = (
    ("The spare key", "the one on the blue fob"),
    ("The back gate", "the one by the bins"),
    ("The old printer", "the grey one upstairs"),
    ("The overflow shelf", "the top one in the corner"),
    ("The visitor mug", "the chipped one"),
    ("The side door", "the one nobody uses"),
)


def _filler(rng: random.Random, echoed: list[str]) -> tmc.Session:  # DevSkim: ignore DS148264 - deterministic corpus generation
    """A non-gold session.

    Two jobs. Echo terms are woven in so the calibration knob has something to move -- a distractor
    with no question vocabulary is lexically invisible and coverage cannot be tuned down. And a
    share of filler states its OWN attribute replacements, using the same frames gold uses, for
    attributes nobody asks about. Without that the replacement frame appears only in gold and
    becomes a marker: the separability probe reads "change of plan on the" as a perfect gold
    predictor, and it would be right. A construction only gold ever receives is a frame, not
    content, however little it says about the answer."""
    draw = rng.random()  # DevSkim: ignore DS148264 - deterministic corpus generation

    # Alias-shaped filler. The co-reference link sentence is an equative -- "X is the Y" -- and if
    # only gold ever uses one, "is the" separates gold perfectly and the probe is right to say so.
    # These name irrelevant things, so they are a frame the retriever sees everywhere and evidence
    # nowhere.
    if draw < 0.25:
        left, right = rng.choice(UNASKED_ALIASES)
        return tmc.Session(
            turns=[tmc.Turn("user", tmc.weave_echo(f"{left} is {right}.", echoed)),
                   tmc.Turn("assistant", _reply(rng))],
            timestamp=_BASE, is_gold=False, tag="filler-alias")

    if draw < 0.55:
        attribute, values = rng.choice(UNASKED_ATTRIBUTES)
        frame = rng.choice(REPLACEMENT_FRAMES + FIRST_FRAMES)
        pair = rng.sample(list(values), 2)
        user = frame.format(attribute=attribute, previous=pair[0], value=pair[1])
        return tmc.Session(
            turns=[tmc.Turn("user", tmc.weave_echo(user, echoed)),
                   tmc.Turn("assistant", _reply(rng))],
            timestamp=_BASE, is_gold=False, tag="filler-replacement")

    user, assistant = rng.choice(FILLER_CHAT)
    return tmc.Session(
        turns=[tmc.Turn("user", tmc.weave_echo(user, echoed)), tmc.Turn("assistant", assistant)],
        timestamp=_BASE, is_gold=False, tag="filler")


def _gold(user: str, assistant: str, rng: random.Random, tag: str) -> tmc.Session:  # DevSkim: ignore DS148264 - deterministic corpus generation
    return tmc.Session(
        turns=[tmc.Turn("user", f"{rng.choice(OPENERS)} {user}"),
               tmc.Turn("assistant", assistant, has_answer=True)],
        timestamp=_BASE, is_gold=True, tag=tag)


def _lay_out(sessions: list[tmc.Session], ordinal: int, chain: list[str] | None = None) -> None:
    """Stamps timestamps in list order, after optionally reordering gold so the chain reads forward.

    The probe renders each session as "### Session N (date)", so dates ARE read. Shuffling sessions
    and then stamping dates in the shuffled order made the metadata contradict the prose: a value
    stated third could carry the earliest date. That is Temporal's misleading-timestamp construct,
    it does not belong in Semantic, and it cost two probe runs before a no-dates control found it.

    Gold sessions keep the SLOTS the shuffle gave them - so gold position carries no signal, and the
    list stays in chronological order, which the harness checks - but they are permuted among those
    slots so that the earliest gold slot holds the first statement in the chain. Ordering by recency
    is exactly the resolution SS3.1 asks for, so this costs the shape nothing: every statement must
    still be retrieved before the last can be named.
    """
    if chain is not None:
        position = {value: rank for rank, value in enumerate(chain)}

        def rank_of(session: tmc.Session) -> int:
            # A session's place in the chain is the LATEST value it names: an opening statement
            # names only chain[0]; a replacement names its predecessor and its successor.
            text = session.text()
            return max((rank for value, rank in position.items() if value in text), default=0)

        slots = [i for i, session in enumerate(sessions) if session.is_gold]
        ordered = sorted((sessions[i] for i in slots), key=rank_of)
        for slot, session in zip(slots, ordered):
            sessions[slot] = session

    start = _BASE + timedelta(days=ordinal * 37)
    for i, session in enumerate(sessions):
        session.timestamp = start + timedelta(days=i, hours=(i * 3) % 11)


def _current_value(index: int, echo: float, rng: random.Random) -> tmc.Question:  # DevSkim: ignore DS148264 - deterministic corpus generation
    """An attribute stated once and then replaced k times. Gold is EVERY statement, because the
    answer is the last one and you cannot know which is last without seeing them all."""
    attribute, ask, values = ATTRIBUTES[index % len(ATTRIBUTES)]
    # DEVIATION FROM ADR-027 SS3.1, deliberate and narrow: k runs 1..4, not 1..5.
    #
    # Gold is every statement, so G = k + 1. At k=5 that is G=6, above K_REF=5, and the question
    # becomes structurally uncoverable by a K-budget retriever -- min(1, K/G) = 0.833 before any
    # resolution happens. That converts Semantic into a dispersion vertical, which is Arithmetic's
    # construct, and confounds the thing SS3.1 actually claims to measure: that retrieving the
    # statements is necessary AND NOT SUFFICIENT. A question that cannot be retrieved is not
    # measuring sufficiency at all.
    #
    # Capping at 4 keeps every question structurally reachable (G <= K_REF), so a miss is a
    # resolution failure rather than a budget failure. The dial still spans four depths.
    k = (index % 4) + 1
    chosen = rng.sample(list(values), k + 1)  # first statement plus k replacements
    qid = f"tme-sem-{index + 1:03d}"
    echoed = tmc.echo_terms(ask, echo, random.Random(f"{qid}:{index}"))  # DevSkim: ignore DS148264 - deterministic corpus generation

    golds = [_gold(
        rng.choice(FIRST_FRAMES).format(attribute=attribute, value=chosen[0]),
        _reply(rng), rng, "first")]
    # Each replacement names what it supersedes, so the statements chain. Sessions are shuffled and
    # timestamps carry no signal, so this chain is the ONLY thing that says which value is current:
    # the one named as a destination and never as a source.
    for previous, value in zip(chosen, chosen[1:]):
        golds.append(_gold(
            rng.choice(REPLACEMENT_FRAMES).format(
                attribute=attribute, previous=previous, value=value),
            _reply(rng), rng, "replacement"))

    sessions = golds + [_filler(rng, echoed) for _ in range(rng.randint(H_MIN, H_MAX))]
    # Shuffled so gold position carries no signal, and so the replacement chain cannot be read off
    # the session order -- only off the text.
    rng.shuffle(sessions)  # DevSkim: ignore DS148264 - deterministic corpus generation under a fixed seed; a CSPRNG cannot be replayed, and this orders sessions, not secrets.
    _lay_out(sessions, index, chain=list(chosen))

    return tmc.Question(
        question_id=qid, question_type=TYPE_CURRENT, question=ask,
        answer=f"{chosen[-1]}.", question_date=_BASE + timedelta(days=index * 37 + 60),
        sessions=sessions,
        extension={"shape": SHAPE_CURRENT, "replacement_depth": k,
                   "stated_values": list(chosen)})


def _co_reference(index: int, echo: float, rng: random.Random) -> tmc.Question:  # DevSkim: ignore DS148264 - deterministic corpus generation
    """A fact stated under one designation, asked under another. Gold is the stating session PLUS
    every link needed to get there -- the chain is the evidence, not just its endpoint."""
    stated, asked, link = DESIGNATIONS[index % len(DESIGNATIONS)]
    fact, template, answer, rivals = COREF_FACTS[index % len(COREF_FACTS)]
    hops = 1 if index % 3 else 2
    qid = f"tme-sem-{CURRENT_QUESTIONS + index + 1:03d}"
    ask = template.format(asked=asked)
    echoed = tmc.echo_terms(ask, echo, random.Random(f"{qid}:{index}"))  # DevSkim: ignore DS148264 - deterministic corpus generation

    golds = [_gold(f"At {stated}, {fact}.", _reply(rng), rng, "fact")]
    if hops == 1:
        golds.append(_gold(link, _reply(rng), rng, "link"))
    else:
        middle = MIDDLE_DESIGNATIONS[index % len(MIDDLE_DESIGNATIONS)]
        golds.append(_gold(f"{middle.capitalize()} is {stated}.", _reply(rng), rng, "link"))
        golds.append(_gold(f"{asked.capitalize()} is {middle}.", _reply(rng), rng, "link"))

    # RIVAL FACTS OF THE SAME KIND, filed under places the question does not ask about. Without
    # them the shape's only fact of its kind is gold, and the designation never has to be resolved
    # -- see COREF_FACTS. Their designations exclude everything this question's chain uses, so a
    # rival can never be read as the asked place.
    used = {stated, asked}
    if hops != 1:
        used.add(MIDDLE_DESIGNATIONS[index % len(MIDDLE_DESIGNATIONS)])
    rival_places = [d for d, _, _ in DESIGNATIONS if d not in used][:len(rivals)]
    rival_sessions = [
        tmc.Session(
            turns=[tmc.Turn("user", tmc.weave_echo(f"At {place}, {rival}.", echoed)),
                   tmc.Turn("assistant", _reply(rng))],
            timestamp=_BASE, is_gold=False, tag="rival-fact")
        for place, rival in zip(rival_places, rivals)]

    # INSIDE the haystack budget, so H does not drift with the fix.
    filler_count = max(0, rng.randint(H_MIN, H_MAX) - len(rival_sessions))
    sessions = golds + rival_sessions + [_filler(rng, echoed) for _ in range(filler_count)]
    rng.shuffle(sessions)  # DevSkim: ignore DS148264 - deterministic corpus generation under a fixed seed; a CSPRNG cannot be replayed, and this orders sessions, not secrets.
    _lay_out(sessions, CURRENT_QUESTIONS + index)

    return tmc.Question(
        question_id=qid, question_type=TYPE_COREF, question=ask, answer=answer,
        question_date=_BASE + timedelta(days=(CURRENT_QUESTIONS + index) * 37 + 60),
        sessions=sessions,
        extension={"shape": SHAPE_COREF, "designation_distance": hops,
                   # THE ANSWER MUST BE ABOUT THE ASKED DESIGNATION, on the ablation arms.
                   #
                   # The component V6 removes here IS the link from `asked` to the session stating
                   # the fact. A response that reports the fact under the OTHER designation, or
                   # says it cannot identify the asked one, has not made the link -- and 16 of the
                   # 18 scored V6 hits on this shape were exactly that: "The conversations do not
                   # mention a workshop by that name. They do say that at the unit behind the
                   # depot, the alarm code was changed after the break-in."
                   "answer_must_name": asked,
                   # THE RIVALS CREATE A CLOSED CHOICE, so the shape declares its own floor.
                   # After the co-reference link is removed the haystack still holds the gold fact
                   # and its rivals -- same kind, other places, different answers -- so a reader
                   # that cannot resolve the designation is choosing among 1 + len(rivals). V6 and
                   # the published v9_above_chance both read this; without it V6 condemns a
                   # component on a single lucky guess in three samples.
                   "chance_floor": round(1.0 / (1 + len(rivals)), 4),
                   "chance_floor_reason": (
                       f"after the link is ablated the haystack holds {1 + len(rivals)} facts of "
                       f"the asked kind, filed under different designations"),
                   # "the chain is the evidence, not just its endpoint" -- drop a link and the
                   # asked designation can no longer be resolved to the stating session.
                   #
                   # NOT declared on SHAPE_CURRENT, deliberately: dropping a MIDDLE replacement
                   # still leaves the latest one, so the answer survives and that component really
                   # is redundant. A per-vertical scope could not have expressed the difference.
                   "gold_components_load_bearing": True,
                   "stated_as": stated, "asked_as": asked})


def _source_attribution(index: int, echo: float, rng: random.Random) -> tmc.Question:  # DevSkim: ignore DS148264 - deterministic corpus generation
    """Which conversation did a belief come from? The belief is stated once, inside a conversation
    about a named topic; decoy conversations discuss neighbouring topics without carrying it."""
    topic, belief = SOURCE_TOPICS[index % len(SOURCE_TOPICS)]
    candidates = 2 + (index % 3)   # how many conversations plausibly could have carried it
    qid = f"tme-sem-{CURRENT_QUESTIONS + COREF_QUESTIONS + index + 1:03d}"
    ask = f"What were we discussing when I told you that {belief}?"
    echoed = tmc.echo_terms(ask, echo, random.Random(f"{qid}:{index}"))  # DevSkim: ignore DS148264 - deterministic corpus generation

    golds = [_gold(f"About {topic} -- {belief}.", _reply(rng), rng, "source")]

    decoys = []
    for offset in range(candidates - 1):
        decoy = DECOY_TOPICS[(index + offset) % len(DECOY_TOPICS)]
        # A decoy discusses a neighbouring topic and explicitly does NOT carry the belief, so it is
        # a plausible citation for a system that matches on topic rather than on content.
        decoys.append(tmc.Session(
            turns=[tmc.Turn("user", f"{rng.choice(OPENERS)} About {decoy} -- nothing decided yet."),
                   tmc.Turn("assistant", _reply(rng))],
            timestamp=_BASE, is_gold=False, tag="decoy"))

    # Decoys are non-gold, so they count toward H. Draw the filler budget against the declared
    # range MINUS the decoys rather than on top of them, or H silently exceeds what the corpus
    # claims about its own shape.
    filler = max(H_MIN - len(decoys), rng.randint(H_MIN, H_MAX) - len(decoys))
    sessions = golds + decoys + [_filler(rng, echoed) for _ in range(filler)]
    rng.shuffle(sessions)  # DevSkim: ignore DS148264 - deterministic corpus generation under a fixed seed; a CSPRNG cannot be replayed, and this orders sessions, not secrets.
    _lay_out(sessions, CURRENT_QUESTIONS + COREF_QUESTIONS + index)

    return tmc.Question(
        question_id=qid, question_type=TYPE_SOURCE, question=ask,
        answer=f"We were discussing {topic}.",
        question_date=_BASE + timedelta(
            days=(CURRENT_QUESTIONS + COREF_QUESTIONS + index) * 37 + 60),
        sessions=sessions,
        extension={"shape": SHAPE_SOURCE, "candidate_sources": candidates, "source_topic": topic})


def build(echo, rng: random.Random) -> list[tmc.Question]:  # DevSkim: ignore DS148264 - deterministic corpus generation
    """`echo` is a float or, under per-shape calibration, a dict keyed by shape."""
    def knob(shape: str) -> float:
        return echo.get(shape, 0.0) if isinstance(echo, dict) else echo

    questions = [_current_value(i, knob(SHAPE_CURRENT), rng) for i in range(CURRENT_QUESTIONS)]
    questions += [_co_reference(i, knob(SHAPE_COREF), rng) for i in range(COREF_QUESTIONS)]
    questions += [_source_attribution(i, knob(SHAPE_SOURCE), rng) for i in range(SOURCE_QUESTIONS)]
    return questions


def check_semantic(questions: list[tmc.Question]) -> list[str]:
    """The boundary checks. Every one of these is a way the corpus could look right and measure
    something else, so they fail the build rather than warn."""
    problems: list[str] = []
    for q in questions:
        shape = q.extension["shape"]
        gold_text = " ".join(s.text().lower() for s in q.sessions if s.is_gold)

        # BOUNDARY AGAINST FORGETTING. A replacement that reads as a cancellation puts the question
        # in Forgetting's outcome space, where abstention is legitimate and here it never is.
        if shape == SHAPE_CURRENT:
            hit = next((w for w in CANCELLATION_WORDS if w in gold_text), None)
            if hit:
                problems.append(
                    f"{q.question_id}: current-value gold says '{hit}' -- that is a cancellation, "
                    f"which is Forgetting's construct, not a replacement")

            # The answer must be the LAST stated value. If it is the first, retrieving one session
            # answers the question and the shape has saturated.
            stated = q.extension["stated_values"]
            if not q.answer.startswith(stated[-1]):
                problems.append(f"{q.question_id}: answer is not the last stated value")
            if len(stated) != len(set(stated)):
                problems.append(f"{q.question_id}: a value is stated twice, so 'last' is ambiguous")
            if q.g != len(stated):
                problems.append(
                    f"{q.question_id}: {q.g} gold sessions for {len(stated)} statements -- every "
                    f"statement must be gold or 'which is last' is not decidable from gold alone")

            # THE ORDERING CLAIM, ENFORCED RATHER THAN ASSERTED.
            #
            # Sessions are shuffled and timestamps carry no signal, so the ONLY thing that can say
            # which value is current is the replacement chain in the text. An earlier revision
            # claimed exactly that in the module docstring while the frames encoded no order at
            # all; V1 scored 6/20 and the reference model answered with the FIRST value, which
            # under perfect retrieval and no ordering signal is as defensible as any other choice.
            #
            # The property that has to hold: every value except the first appears as a DESTINATION
            # in some statement, every value except the last appears as a SOURCE in some statement,
            # and exactly one value is a destination that is never a source. That one is the
            # answer, and it is recoverable by reading alone.
            sources, destinations = set(), set()
            for session in (x for x in q.sessions if x.is_gold and x.tag == "replacement"):
                text = session.text()
                for value in stated:
                    if value not in text:
                        continue
                    # The destination is whichever of the two the answer would become: in every
                    # frame the superseding value is the one the sentence resolves to, so compare
                    # against the chain we authored rather than re-parsing prose.
                    index = stated.index(value)
                    if index > 0 and stated[index - 1] in text:
                        destinations.add(value)
                        sources.add(stated[index - 1])

            current = destinations - sources
            if len(current) != 1 or q.answer.rstrip(".") not in current:
                problems.append(
                    f"{q.question_id}: the replacement chain does not identify a unique current "
                    f"value from the text alone (destinations-never-sources = {sorted(current)}, "
                    f"answer = {q.answer!r}). Without that, 'which is last' is not decidable by "
                    f"reading and the shape measures nothing.")

        # THE CO-REFERENCE PREMISE. The asked designation must NOT appear in the session that
        # states the fact; if it does, lexical retrieval reaches the fact directly and the shape
        # measures nothing.
        if shape == SHAPE_COREF:
            asked = q.extension["asked_as"].lower()
            for session in q.sessions:
                if session.is_gold and session.tag == "fact" and asked in session.text().lower():
                    problems.append(
                        f"{q.question_id}: the asked designation '{asked}' appears in the session "
                        f"that states the fact, so no co-reference resolution is required")

        # SOURCE ATTRIBUTION is scored on the cited source, so the answer must name a topic and the
        # belief must not be restatable as the answer.
        if shape == SHAPE_SOURCE:
            if "we were discussing" not in q.answer.lower():
                problems.append(f"{q.question_id}: source-attribution answer does not name a source")
            decoys = sum(1 for s in q.sessions if s.tag == "decoy")
            if decoys != q.extension["candidate_sources"] - 1:
                problems.append(
                    f"{q.question_id}: {decoys} decoy sources for a declared candidate count of "
                    f"{q.extension['candidate_sources']}")

    return problems


if __name__ == "__main__":
    tmc.finalise(
        "semantic",
        build,
        tmc.StructureSpec(
            h_min=H_MIN,
            h_max=H_MAX,
            # current-value carries 2..5 gold (first + k replacements, k <= 4); co-reference 2 or
            # 3; source-attribution exactly 1. Nothing reaches 6: see the cap in _current_value,
            # which keeps G <= K_REF so a miss is a resolution failure and not a budget one.
            g_values={1, 2, 3, 4, 5},
            no_absolute_dates=True,
        ),
        generator_tool="gen_typedmemeval_semantic.py",
        extra_checks=check_semantic,
        shape_of=lambda q: (q.extension or {}).get("shape"),
    )
