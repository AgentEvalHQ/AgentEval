#!/usr/bin/env python3
"""
Generates TypedMemEval-Episodic v1 (ADR-026 §5.2) -- 50 questions.

What the vertical measures: memory of the conversation *as an event* -- who said what,
in what order -- rather than facts extractable from any single message. Every question
here is unanswerable from the content of one turn read in isolation. The answer is a
property of the transcript (which speaker, which position), never a property of the
world, so a system that stores well-summarised *facts* and discards the conversation
that carried them should visibly fail even when its recall is perfect.

    Assistant-stated answers   20    G=1      the value exists only in an assistant turn
    List-order                 15    G=4..7   one item per session; the order is the answer
    Participant attribution    15    G=1      the answer is a speaker, not a fact

Why the validity rules are what they are:

  * Assistant-stated measures nothing unless the user never states the value. Storing
    only user utterances is a common and reasonable memory design, and this shape exists
    to make that choice visible -- which it cannot do if the value leaks into a user turn
    anywhere in the haystack. The lexical half of the §5.2 leak screen runs in
    `check_episodic` below: the answer's distinctive tokens must appear in the gold
    assistant turn and in no user turn at all. The LLM paraphrase half runs later, in
    tools/run_typedmemeval_probes.py, because a token screen cannot see a paraphrase and
    claiming it could would be exactly the dishonesty this family exists to remove.

  * List-order is arbitrary by construction (V2). The items are invented names carrying
    no semantics to order them by, so the sequence is recoverable only from the session
    sequence -- there is no "starter before dessert" prior to fall back on. The items are
    named in the question, presented in an order drawn independently of the true one (and
    re-drawn if it happens to coincide), so the shape isolates *ordering* instead of
    silently re-testing membership recall, and a system that answers in the order it was
    given scores no better than chance.

  * Attribution statements come from matched templates emitted identically for both
    roles, with the speaker drawn from the rng. A statement only an assistant would
    plausibly make is answerable by inference about register, which is not memory (V2 in
    spirit). The user/assistant marginal is held near even so "always say the user" is
    worth no more than a coin flip.

Every gold answer is read back out of the sessions this generator emitted -- the speaker
off the emitted Turn, the order off the emitted session indices, the value off the same
variable that was interpolated into the assistant turn -- so an answer and its evidence
cannot drift apart (V5).

One honest note on `check_answer_not_verbatim`: for assistant-stated the value is
*necessarily* present in the haystack, because "what the assistant said" is the construct.
The difficulty of that shape is the user/assistant asymmetry, not obscurity of the value,
so its gold answers are kept terse rather than padded past the 25-character threshold to
dodge a check that was never aimed at this shape.

Run:  python tools/gen_typedmemeval_episodic.py
"""

from __future__ import annotations

import random  # DevSkim: ignore DS148264 - corpus generation must be replayable under a seed; a CSPRNG cannot be seeded to reproduce a draw, and this selects filler text, not secrets.
from datetime import datetime, timedelta

import typedmemeval_common as tmc

SHAPE_STATED = "assistant-stated"
SHAPE_ORDER = "list-order"
SHAPE_ATTRIB = "participant-attribution"

TYPE_STATED = "episodic-assistant-stated"
TYPE_ORDER = "episodic-list-order"
TYPE_ATTRIB = "episodic-attribution"

#: Where every haystack starts in time. Sessions are re-stamped from here after placement
#: so session order and timestamp order agree -- §5.2 promises the two channels never
#: contradict each other, and an order vertical that broke that promise would be scoring
#: systems on which channel they happened to trust.
BASE = datetime(2026, 1, 5, 9, 0)

# --------------------------------------------------------------------------------------
# Arbitrary vocabulary (V2). None of these names mean anything, which is the point: an
# invented token cannot be ordered, attributed, or guessed from world knowledge.
# --------------------------------------------------------------------------------------

_NAME_STEMS = ["Ard", "Bray", "Cal", "Dun", "Fer", "Glen", "Hal", "Hest", "Kest", "Mar",
               "Nad", "Ott", "Pen", "Quin", "Rush", "Sel", "Tor", "Vel", "Wex", "Bram"]
_NAME_TAILS = ["combe", "foot", "holt", "how", "lick", "more", "rel", "ridge", "stow", "way"]

#: 200 invented names, built without the rng so the pool itself is fixed and only the
#: draws from it vary. Sampling without replacement per question keeps a question's items
#: distinct; collisions *across* questions are harmless, since haystacks are independent.
NAMES = [stem + tail for stem in _NAME_STEMS for tail in _NAME_TAILS]

#: Consonants only: a two-letter code that happened to spell a stopword would be dropped
#: by the tokenizer and would silently shrink the leak screen it is supposed to feed.
_CODE_LETTERS = "BCDFGHJKLMNPQRSTVWXZ"

# (question, user ask, assistant statement, answer) -- the answer's only word that is not
# already in the question is the value itself, so "distinctive" in the leak screen means
# the value and nothing else. Phrasings are matched deliberately to keep it that way.
DETAILS = [
    ("code",
     "Which bay did you tell me the {topic} sits in?",
     "Can you find out which bay the {topic} sits in?",
     "I looked it up, the {topic} sits in bay {v}.",
     "Bay {v}."),
    ("code",
     "What reference did you give me for the {topic}?",
     "Do we have a reference for the {topic} anywhere?",
     "Yes, the {topic} is filed under reference {v}.",
     "Reference {v}."),
    ("name",
     "Who did you say handles the {topic}?",
     "Who should I be speaking to about the {topic}?",
     "That one goes through {v}, going by the file.",
     "{v}."),
    ("name",
     "Which yard did you tell me the {topic} is being held at?",
     "Any idea where the {topic} has ended up?",
     "It is sitting at the {v} yard until someone claims it.",
     "The {v} yard."),
    ("code",
     "What slot code did you read out for the {topic}?",
     "Is there a slot code on the {topic} booking?",
     "There is, the booking for the {topic} carries slot code {v}.",
     "Slot code {v}."),
]

TOPICS = [
    "loft insulation quote", "allotment shed permit", "bike-rack delivery",
    "piano tuning booking", "kayak trailer hire", "attic ladder order",
    "compost bin swap", "window survey", "garage door part", "boiler flue check",
    "chimney sweep visit", "greenhouse glass order", "fence panel delivery",
    "cellar dehumidifier hire", "roof valley repair", "gate latch order",
    "skip hire booking", "stair carpet order", "drain survey", "solar diverter part",
    "woodstore delivery", "guttering replacement",
]

CATEGORIES = [
    "coastal walks", "board games", "podcast series", "campsites", "climbing routes",
    "supper clubs", "swimming spots", "market stalls", "bothy stops", "cycle loops",
    "pottery studios", "record shops", "birding hides", "sea-swim beaches",
    "orchard varieties", "repair cafes", "ferry crossings",
]

NUMBER_WORDS = {4: "four", 5: "five", 6: "six", 7: "seven"}

# (topic, statement). Every statement is a small practical fact about a shared place or
# routine: something the user could know from experience and something an assistant could
# equally have looked up. That symmetry is the whole shape -- a statement whose register
# gives the speaker away is answerable without remembering anything.
STATEMENTS = [
    ("the Marrow Lane depot", "it only takes card after six"),
    ("the west stairwell", "the light there is on its own breaker"),
    ("the wheelie bin round", "it skips the first week of every month"),
    ("the canal gate", "it gets locked from the far side"),
    ("the bulk-buy account", "it needs two signatures before anything changes"),
    ("the hall projector", "it wakes up faster on the side input"),
    ("the corner pharmacy", "it shuts for an hour over lunch"),
    ("the loft hatch", "it sticks unless you lift before you push"),
    ("the station car park", "the top deck is card only"),
    ("the community fridge", "it stops taking donations mid-afternoon"),
    ("the tool library", "renewals happen at the counter, never online"),
    ("the low bend on the river path", "it floods for days after heavy rain"),
    ("the print shop", "it wants files flattened before upload"),
    ("the allotment tap", "it gets turned off at the main over winter"),
    ("the bus replacement", "it leaves from the far side of the bridge"),
    ("the side entrance", "it is propped open on delivery mornings"),
    ("the laundrette", "the big machines take tokens rather than coins"),
]

# Same-domain, same-register filler: ordinary household chat between the same two
# speakers, carrying no value, no list item and no attributable claim about any gold
# topic. V3's "plausible, not a strawman" -- the calibration gate then layers echoed
# question vocabulary on top to make them actually compete for the retrieval budget.
FILLER = [
    ("The hallway radiator has started knocking in the evenings.", "Worth bleeding it before the cold sets in."),
    ("I finally cleared the shelf by the back door.", "That has been on the list a while."),
    ("The neighbours are having their drive resurfaced.", "Expect a noisy week, then."),
    ("My waterproof has given up at the seams.", "Reproofing might buy it another season."),
    ("The freezer drawer is icing up again.", "Sounds like the seal rather than the thermostat."),
    ("I swapped the kitchen bulbs for warmer ones.", "Much easier on the eyes at night."),
    ("The bread I tried came out dense as a brick.", "Under-proved, most likely."),
    ("Rosa lent me her tile cutter.", "Return it before she needs it back."),
    ("The back fence is leaning after the wind.", "Worth propping it before the next storm."),
    ("I have run out of the good coffee again.", "Order before the weekend, then."),
    ("The washing line pulley is seized solid.", "A little oil usually frees those."),
    ("I took the long way home along the old rail line.", "Good for the head, that walk."),
    ("The car boot smells of wet dog again.", "A rinse of the liner should sort it."),
    ("Someone left a crate of apples at the gate.", "Chutney weather, then."),
    ("The upstairs tap drips whenever the heating runs.", "That pairing usually means pressure."),
    ("I am halfway through repainting the sill.", "Do not stop before the second coat."),
    ("The hedge trimmer will not start on the first pull.", "Old fuel, nine times out of ten."),
    ("My reading glasses have gone missing again.", "They turn up in coat pockets, usually."),
    ("I cooked far too much rice for two people.", "Fried rice tomorrow, then."),
    ("The shed roof felt has lifted at one corner.", "Tack it down before it takes in water."),
]


#: Items and acknowledgements for decoy shortlists -- deliberately disjoint from the real
#: shortlist vocabulary, so a decoy can never be mistaken for a listed item.
_DECOY_ITEMS = (
    "the Kelder ridge route", "the Vane estuary hide", "the Orrin mill cafe",
    "the Brackwater crossing", "the Sallow Fields pitch", "the Tarn Head loop",
    "the Windle bothy", "the Quarry Lane studio", "the Marden ferry", "the Ostler orchard",
)
_DECOY_ACKS = (
    "Added to that one.", "Noted for later.", "That list is getting long.",
    "Filed under optimism.", "One more for the winter.",
)


def _filler_value(rng: random.Random) -> str:  # DevSkim: ignore DS148264 - deterministic corpus generation
    """A value for a decoy detail: same shape as a gold value, belonging to another topic."""
    return f"{rng.choice(_FILLER_VALUE_HEADS)}-{rng.randrange(100, 999)}"


_FILLER_VALUE_HEADS = ("QR", "TL", "MV", "HD", "PN", "RS", "BK", "WG")


def _filler_session(rng: random.Random, echo_source: str, echo: float, index: int) -> tmc.Session:
    """One filler exchange, with the calibration echo attached to alternating roles.

    The echo sample is drawn per session rather than once per question. Sharing one sample
    across every distractor puts the echoed terms in every document at once, which drives
    their IDF to zero and leaves the un-echoed remainder as a clean gold-only signal: the
    knob then does nothing at all until it covers the whole query, at which point coverage
    collapses. Drawing per session degrades each term's IDF gradually, which is what a
    calibration knob has to do if the value the gate converges on is to mean anything.

    The role alternates because the echo clause is the only text in the corpus whose
    placement is under the generator's control rather than the fiction's. Parking it
    permanently on the user turn would hand every attribution question a systematic tilt
    towards "the user said it", and the tilt would grow with the echo the gate dials in --
    i.e. the calibration knob would quietly become a speaker cue.
    """
    echoed = tmc.echo_terms(echo_source, echo, rng)
    # Half the fillers are built from the SAME detail frames the gold sessions use, on a
    # different topic. Gold's user turn is question-shaped ("Is there a slot code on the X
    # booking?") while filler was domestic chatter, and that register split was the tell:
    # "on the" marked gold at AUC 0.763 against 22% of filler. Sharing the frames means a
    # reader has to match the TOPIC -- which is the memory task -- rather than notice which
    # sessions are shaped like questions.
    draw = rng.random()
    if draw < 0.35:
        _, _, ask, answer, _ = rng.choice(DETAILS)
        topic = rng.choice(TOPICS)
        user = ask.format(topic=topic)
        assistant = answer.format(topic=topic, v=_filler_value(rng))
    elif draw < 0.6:
        # The list-order frame on a DECOY category. Its gold sessions read "Put X on the Y
        # shortlist", which put "on the" in 90 gold sessions against a quarter of filler --
        # the single biggest contributor to that marker. A shortlist the question never asks
        # about is the same sentence with a different subject, which is what forces a reader
        # to match the category rather than spot the frame.
        category = rng.choice(CATEGORIES)
        user = f"Put {rng.choice(_DECOY_ITEMS)} on the {category} shortlist."
        assistant = rng.choice(_DECOY_ACKS)
    else:
        user, assistant = rng.choice(FILLER)
    if index % 2 == 0:
        user = tmc.weave_echo(user, echoed)
    else:
        assistant = tmc.weave_echo(assistant, echoed)
    # Placeholder stamp: _place re-stamps everything once the running order is known.
    return tmc.make_session(BASE, (user, assistant), tag="filler")


def _place(rng: random.Random, gold: list[tmc.Session], filler: list[tmc.Session]) -> list[tmc.Session]:
    """Scatters gold through the filler at random positions while preserving gold's own
    relative order, then re-stamps chronologically.

    tmc.interleave does the scattering but inserts gold sessions one at a time, which
    permutes them relative to each other. For list-order that permutation *is* the answer,
    so this vertical draws the slots up front instead. Position stays randomised -- a
    corpus whose gold sits first measures position, not retrieval.
    """
    total = len(gold) + len(filler)
    slots = sorted(rng.sample(range(total), len(gold)))
    ordered: list[tmc.Session] = []
    gi = fi = 0
    for i in range(total):
        if gi < len(slots) and i == slots[gi]:
            ordered.append(gold[gi])
            gi += 1
        else:
            ordered.append(filler[fi])
            fi += 1
    for session, stamp in zip(ordered, tmc.spread(BASE, total)):
        session.timestamp = stamp
    return ordered


def _asked_after(sessions: list[tmc.Session]) -> datetime:
    """Query time, always clear of the whole haystack: a question that lands inside its own
    conversation would let a system answer 'that has not happened yet' and be right."""
    return sessions[-1].timestamp + timedelta(days=3, hours=7)


def _code(rng: random.Random) -> str:
    return f"{rng.choice(_CODE_LETTERS)}{rng.choice(_CODE_LETTERS)}-{rng.randrange(100, 1000)}"


def _stated_questions(rng: random.Random, echo: float, start: int) -> list[tmc.Question]:
    """20 questions whose answer was spoken by the assistant and by nobody else."""
    out: list[tmc.Question] = []
    topics = rng.sample(TOPICS, 20)
    for offset, topic in enumerate(topics):
        kind, q_tmpl, ask_tmpl, say_tmpl, ans_tmpl = rng.choice(DETAILS)
        value = _code(rng) if kind == "code" else rng.choice(NAMES)

        question_text = q_tmpl.format(topic=topic)

        # Built by hand rather than through tmc.make_session: that helper marks the *user*
        # turn, and the entire shape is that the user turn does not carry the answer.
        gold = tmc.Session(
            [tmc.Turn("user", ask_tmpl.format(topic=topic)),
             tmc.Turn("assistant", say_tmpl.format(topic=topic, v=value), has_answer=True)],
            BASE, is_gold=True, tag="stated")

        filler = [_filler_session(rng, question_text, echo, i)
                  for i in range(rng.randint(15, 25))]
        sessions = _place(rng, [gold], filler)
        out.append(tmc.Question(
            f"tme-epi-{start + offset:03d}", TYPE_STATED, question_text,
            # Derived from the same `value` that reached the transcript, never retyped.
            ans_tmpl.format(v=value),
            _asked_after(sessions), sessions,
            {"shape": SHAPE_STATED, "stated_by": "assistant"},
        ))
    return out


def _order_questions(rng: random.Random, echo: float, start: int) -> list[tmc.Question]:
    """15 questions whose answer is the sequence the sessions were spoken in."""
    out: list[tmc.Question] = []
    categories = rng.sample(CATEGORIES, 15)
    for offset, category in enumerate(categories):
        size = rng.randint(4, 7)
        items = rng.sample(NAMES, size)

        # Presentation order is drawn independently of the true order and re-drawn if it
        # lands on it: a question that happens to list the items in the answer's order is
        # free marks for a system that just echoes the question back.
        presented = items[:]
        while True:
            rng.shuffle(presented)  # DevSkim: ignore DS148264 - corpus generation must be replayable under a seed; a CSPRNG cannot be seeded to reproduce a draw, and this shuffles filler text, not secrets.
            if presented != items:
                break

        # The filler echo is drawn from the stem rather than the full question. Item names
        # are the question's most discriminative tokens, and echoing them would plant gold
        # items in filler sessions -- trading §5.2's "each item is mentioned in exactly one
        # session" for retrieval difficulty. The category vocabulary carries the competition
        # instead, and the gate still has a knob (list-order coverage moves with echo).
        stem = (f"I put {NUMBER_WORDS[size]} {category} on a shortlist, one at a time. "
                f"In what order did I add them, earliest first?")
        question_text = (f"I put {NUMBER_WORDS[size]} {category} on a shortlist, one at a time: "
                         f"{', '.join(presented)}. In what order did I add them, earliest first?")

        gold = [tmc.make_session(BASE, (f"Put {item} on the {category} shortlist.",
                                        ""),
                                 gold_turn=0, tag="item")
                for item in items]
        filler = [_filler_session(rng, stem, echo, i) for i in range(rng.randint(15, 25))]
        sessions = _place(rng, gold, filler)

        # Read the answer back off the emitted haystack rather than off `items`: if _place
        # ever stopped preserving gold order, this reads the corpus that shipped (V5).
        ordered = [s.turns[0].content.split()[1] for s in sessions if s.is_gold]
        answer = "In order: " + ", then ".join(ordered) + "."

        out.append(tmc.Question(
            f"tme-epi-{start + offset:03d}", TYPE_ORDER, question_text, answer,
            _asked_after(sessions), sessions,
            {
                "shape": SHAPE_ORDER,
                # The item-to-session map is the answer key §6's conditional scoring needs:
                # pairwise-order accuracy is computed over the items whose sessions were
                # actually surfaced, which is unknowable from the answer string alone.
                "list_order": {
                    "items": ordered,
                    "presented": presented,
                    "item_session_indices": [i for i, s in enumerate(sessions) if s.is_gold],
                },
            },
        ))
    return out


def _attribution_questions(rng: random.Random, echo: float, start: int) -> list[tmc.Question]:
    """15 questions whose answer is a speaker."""
    out: list[tmc.Question] = []
    chosen = rng.sample(STATEMENTS, 15)
    # Near-even marginal, then shuffled: which question gets which speaker stays arbitrary
    # (V2), but the majority class is worth 8/15 rather than whatever an unconstrained
    # coin flip happened to produce on this seed.
    speakers = ["user"] * 8 + ["assistant"] * 7
    rng.shuffle(speakers)  # DevSkim: ignore DS148264 - corpus generation must be replayable under a seed; a CSPRNG cannot be seeded to reproduce a draw, and this shuffles filler text, not secrets.

    for offset, ((topic, statement), speaker) in enumerate(zip(chosen, speakers)):
        question_text = (f"Earlier one of us said, about {topic}, that {statement}. "
                         f"Was that me or you?")

        # The statement clause is byte-identical across the two variants; only the role
        # carrying it moves. Anything else that differed would be a cue.
        said = f"One thing about {topic}, {statement}."
        if speaker == "user":
            turns = [tmc.Turn("user", said, has_answer=True),
                     tmc.Turn("assistant", "")]
        else:
            turns = [tmc.Turn("user", f"Anything I should keep in mind about {topic}?"),
                     tmc.Turn("assistant", said, has_answer=True)]
        gold = tmc.Session(turns, BASE, is_gold=True, tag="attribution")

        filler = [_filler_session(rng, question_text, echo, i)
                  for i in range(rng.randint(15, 25))]
        sessions = _place(rng, [gold], filler)

        # Derived from the emitted turn, so the answer cannot disagree with the transcript.
        role = next(t.role for t in gold.turns if t.has_answer)
        answer = ("You did — it was in one of your own messages, not one of mine."
                  if role == "user" else
                  "I did — it was in one of my replies, not one of your messages.")

        out.append(tmc.Question(
            f"tme-epi-{start + offset:03d}", TYPE_ATTRIB, question_text, answer,
            _asked_after(sessions), sessions,
            {"shape": SHAPE_ATTRIB, "attributed_speaker": role},
        ))
    return out


def build(echo: float, rng: random.Random) -> list[tmc.Question]:
    questions = _stated_questions(rng, echo, start=1)
    questions += _order_questions(rng, echo, start=21)
    questions += _attribution_questions(rng, echo, start=36)
    return questions


# --------------------------------------------------------------------------------------
# Vertical validity (ADR §5.2)
# --------------------------------------------------------------------------------------

def _distinctive(question: tmc.Question) -> set[str]:
    """The answer's tokens minus the question's.

    "Distinctive" has to exclude the question's own vocabulary, because every question
    word is shared with the corpus by construction -- the calibration echo is *sampled
    from the question*, so a screen that counted question words would fail the moment the
    gate turned the knob up, and it would be failing on scaffolding rather than on a leak.
    What is left is the value the assistant supplied, which is the thing that must not
    have been spoken by the user.
    """
    return set(tmc.tokenize(question.answer)) - set(tmc.tokenize(question.question))


def check_episodic(questions: list[tmc.Question]) -> list[str]:
    failures: list[str] = []
    shapes: dict[str, int] = {}
    for q in questions:
        shapes[q.extension["shape"]] = shapes.get(q.extension["shape"], 0) + 1

    for q in questions:
        shape = q.extension["shape"]

        if shape == SHAPE_STATED:
            # (a) The lexical half of the §5.2 stated leak screen. Two-sided on purpose:
            # a value absent from every user turn but also absent from the assistant turn
            # would pass a one-sided screen while being unanswerable.
            distinctive = _distinctive(q)
            if not distinctive:
                failures.append(f"{q.question_id}: answer adds no token the question lacks -- "
                                f"the leak screen would be vacuous")
                continue
            gold_assistant = {tok for i in q.gold_indices for t in q.sessions[i].turns
                              if t.role == "assistant" for tok in tmc.tokenize(t.content)}
            missing = distinctive - gold_assistant
            if missing:
                failures.append(f"{q.question_id}: answer tokens {sorted(missing)} are not in the "
                                f"gold assistant turn -- answer is not derived from it")
            for si, session in enumerate(q.sessions):
                for ti, turn in enumerate(session.turns):
                    if turn.role != "user":
                        continue
                    leaked = distinctive & set(tmc.tokenize(turn.content))
                    if leaked:
                        failures.append(f"{q.question_id} s{si}t{ti}: user turn states "
                                        f"{sorted(leaked)} -- assistant-stated answer leaked")

        elif shape == SHAPE_ORDER:
            # (b) One item per gold session, no items anywhere else, and the emitted
            # sequence is the answer. Ordering is only measurable if membership is not
            # ambiguous: an item appearing twice would make "earlier" undefined.
            items = [i.lower() for i in q.extension["list_order"]["items"]]
            if len(q.gold_indices) != len(items):
                failures.append(f"{q.question_id}: {len(q.gold_indices)} gold sessions for a "
                                f"list of {len(items)}")
            seen: list[str] = []
            for si, session in enumerate(q.sessions):
                tokens = set(tmc.tokenize(session.text()))
                present = [item for item in items if item in tokens]
                if session.is_gold:
                    if len(present) != 1:
                        failures.append(f"{q.question_id} s{si}: gold session mentions "
                                        f"{len(present)} list items, expected exactly 1")
                    seen.extend(present)
                elif present:
                    failures.append(f"{q.question_id} s{si}: non-gold session mentions "
                                    f"{present} -- item is not confined to one session")
            if seen != items:
                failures.append(f"{q.question_id}: session order {seen} does not match the "
                                f"answer's order {items}")

        elif shape == SHAPE_ATTRIB:
            role = next((t.role for i in q.gold_indices for t in q.sessions[i].turns
                         if t.has_answer), None)
            if role != q.extension["attributed_speaker"]:
                failures.append(f"{q.question_id}: answer key says {q.extension['attributed_speaker']} "
                                f"but the has_answer turn is a {role} turn")

    # (c) The shape mix is the vertical's design, and the report surface reads per shape:
    # a corpus that silently drifted to 25/10/15 would still pass every other check here
    # and would quietly re-weight what the headline number means.
    for shape, expected in ((SHAPE_STATED, 20), (SHAPE_ORDER, 15), (SHAPE_ATTRIB, 15)):
        if shapes.get(shape, 0) != expected:
            failures.append(f"shape '{shape}': {shapes.get(shape, 0)} questions, "
                            f"ADR §5.2 declares {expected}")

    # A degenerate speaker marginal would make attribution answerable by always guessing
    # the majority class -- a shortcut with nothing to do with episodic memory.
    attributed = [q.extension["attributed_speaker"] for q in questions
                  if q.extension["shape"] == SHAPE_ATTRIB]
    if attributed:
        majority = max(attributed.count("user"), attributed.count("assistant"))
        if majority / len(attributed) > 0.6:
            failures.append(f"attribution speaker marginal is {majority}/{len(attributed)} -- "
                            f"majority-class guessing beats the shape")
    return failures


if __name__ == "__main__":
    tmc.finalise(
        vertical="episodic",
        build=build,
        structure=tmc.StructureSpec(
            h_min=15, h_max=25, g_values={1, 4, 5, 6, 7}, gold_position_shuffled=True,
            no_absolute_dates=False,
        ),
        generator_tool="tools/gen_typedmemeval_episodic.py",
        extra_checks=check_episodic,
    )
