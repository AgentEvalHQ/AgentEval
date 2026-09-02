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
# Two of these carried "one" -- "Added to that one." and "One more for the winter." -- and this bank
# is drawn for DECOY shortlists only, so the token piled up in distractors: 400 distractor sessions
# against 13 gold, and its ABSENCE marked gold at AUC 0.752, over the bar. The same defect as a
# gold-only marker with the sign reversed, which is why the phrase screen scores both directions.
# Rephrased without it rather than adding "one" to gold, because the leak is the concentration and
# the acknowledgement carries no information either way.
_DECOY_ACKS = (
    "Added to that list.", "Noted for later.", "That list is getting long.",
    "Filed under optimism.", "Something for the winter.",
)


#: Frames for the attribution shape. One per question, held byte-identical across a pair, so
#: the wording cannot stand in for the speaker label. `prompt` is what the OTHER party says in
#: the assistant-speaker arm -- it must not hint at who is about to speak.
ATTRIBUTION_FRAMES = (
    ("One thing about {topic}, {statement}.", "Anything I should keep in mind about {topic}?"),
    ("Worth remembering about {topic}: {statement}.", "Is there anything on {topic} worth noting?"),
    ("About {topic} — {statement}.", "What is the situation with {topic}?"),
    ("On {topic}, {statement}.", "Where do things stand on {topic}?"),
    ("The thing with {topic} is that {statement}.", "Remind me about {topic}?"),
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
                # Dispersion is the dial here: how many places the answer has to be assembled
                # from. It is the only graded driver Episodic has, and it lives in 30% of the
                # vertical -- the other 70% sits in a single band (G=1, full coverage), which
                # is what makes this the flattest vertical in the family.
                "difficulty": _LIST_BANDS.get(size, 3),
                # UNVALIDATED, and reclassified rather than dropped. It was stamped validated on a
                # measured drop of 0.31 (0.45 -> 0.14); after the v4 role-order regeneration the
                # same bands read 0.35 / 0.20 / 0.21 / 0.21 -- a drop of 0.14, under the 0.15 bar
                # and flat after the first band. The consuming project's rule is that a band which
                # does not slope gets reclassified, not kept, and a gradient that survives only on
                # one revision's session draw was never evidence those questions are harder.
                # n per band is 2-5 here, which is why it moved at all.
                "difficulty_dial": "dispersion", "difficulty_validated": False,
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


#: List length -> band. Four to seven is the range the generator emits today; extending it
#: (3/5/8/12) is on the v4 lever list and needs new generation rather than bookkeeping.
_LIST_BANDS = {4: 2, 5: 3, 6: 4, 7: 5}


def _attribution_questions(rng: random.Random, echo: float, start: int) -> list[tmc.Question]:
    """15 questions whose answer is a speaker."""
    out: list[tmc.Question] = []
    chosen = rng.sample(STATEMENTS, 15)
    # THREE ARMS, NOT TWO, AND THE THIRD IS WHY.
    #
    # "Was that me or you?" is a two-candidate question, so a reader with no evidence still lands
    # gold half the time. Measured on the shipped corpus that was not a theoretical worry: gold sat
    # in BM25's top-5 on 10 of 15 questions and V9 scored 12 of 15 -- retrieve ten, guess half the
    # remaining five, which is 12.5. The published headroom of 0.20 was the difference between a
    # perfect selector and a lexical one PLUS A COIN, and no amount of retrieval work closes the
    # coin's half.
    #
    # A third arm is the only lever, because the floor is 1/k and k was fixed by the question. BOTH
    # is the right third: it keeps G>0 (so V1, V3 and the accuracy arms all stay defined, unlike a
    # "neither" arm, which would recreate the no-gold hole this release just closed in Forgetting),
    # and it tests a real attribution failure -- a system that finds one mention and stops.
    #
    # Marginal held at 5/5/5, so majority-class guessing is worth exactly the 1/3 chance floor and
    # nothing more.
    speakers = ["user"] * 5 + ["assistant"] * 5 + ["both"] * 5
    rng.shuffle(speakers)  # DevSkim: ignore DS148264 - corpus generation must be replayable under a seed; a CSPRNG cannot be seeded to reproduce a draw, and this shuffles filler text, not secrets.

    for offset, ((topic, statement), speaker) in enumerate(zip(chosen, speakers)):
        # PHRASED SO THE EXISTING DETECTOR READS IT CORRECTLY, which is deliberate rather than
        # incidental. `closed_choice_k` counts " or " occurrences in a trailing clause and does NOT
        # parse comma-separated alternatives, so "me, you, or both of us" would be read as k=2 and
        # publish a floor of 0.50 for a three-way question. That detector feeds V2's and V3's
        # chance-aware thresholds, so changing it needs its own measured arc -- and no shipped
        # question currently trips the comma case, making the bug latent rather than live. Writing
        # the alternatives with repeated "or" gets k=3 out of the detector as it stands.
        question_text = (f"Earlier, about {topic}, someone said that {statement}. "
                         f"Was that me or you or both of us?")

        # The statement clause is byte-identical across the two variants; only the role
        # carrying it moves. Anything else that differed would be a cue.
        # The frame is drawn PER QUESTION from a bank and stays byte-identical across the two
        # arms. With one frame for all fifteen, a system storing no speaker label could recover
        # the answer from the wording rather than from memory, so the shape measured less than
        # its name promised and shipped as a floor (ADR-026 §13, promised for v3 and slipped).
        # Varying it across questions removes the constant; holding it fixed within a pair
        # keeps the only thing that differs between the arms the ROLE that carries it.
        frame, prompt = ATTRIBUTION_FRAMES[offset % len(ATTRIBUTION_FRAMES)]
        said = frame.format(topic=topic, statement=statement)

        def user_said() -> tmc.Session:
            return tmc.Session([tmc.Turn("user", said, has_answer=True),
                                tmc.Turn("assistant", "")], BASE, is_gold=True, tag="attribution")

        def assistant_said() -> tmc.Session:
            return tmc.Session([tmc.Turn("user", prompt.format(topic=topic)),
                                tmc.Turn("assistant", said, has_answer=True)],
                               BASE, is_gold=True, tag="attribution")

        if speaker == "user":
            golds = [user_said()]
        elif speaker == "assistant":
            golds = [assistant_said()]
        else:
            # Both arms carry the SAME statement text, exactly as the single-speaker arms do --
            # the only thing that differs anywhere in this shape is which role utters it. Two
            # sessions rather than one turn-pair, so finding either is not finding both.
            golds = [user_said(), assistant_said()]

        # THE ECHO DRAWS FROM THE TOPIC, NOT THE WHOLE QUESTION, and this is a real leak rather
        # than a tidy-up.
        #
        # `weave_echo` splices the echo source's content words into filler turns, alternating the
        # ROLE it attaches to -- deliberately, because parking it on the user turn would tilt every
        # attribution answer toward "you said it". But the question quotes the statement verbatim,
        # so echoing the whole question scattered the statement's own vocabulary across both roles'
        # filler. For a shape whose answer IS which role said it, that is the answer.
        #
        # The reference model spelled it out when the `both` arm made it visible. With both gold
        # sessions ablated it still answered "both of us", 3 draws out of 3, and explained: "the
        # 'corner / pharmacy / shuts / hour / lunch' pieces show up in both your messages and mine
        # ... it appears only as fragments in the 'Also on my mind' lists from both you and me."
        # V3 was right to call it a leak. It had been there for every arm of this shape; the
        # single-speaker arms hid it because naming the RIGHT one of two speakers from scattered
        # fragments is a coin flip, and a coin flip does not reach V3's threshold.
        #
        # The topic is the retrieval axis anyway: it is what makes gold findable and it asserts
        # nothing about who spoke.
        # NEAR-MISS SESSIONS ON THE ASKED TOPIC, roles balanced.
        #
        # Dropping the statement from the echo closed the leak and immediately traded it for
        # saturation: the question still quotes the statement verbatim, nothing else in the
        # haystack carried that vocabulary, and BM25 coverage went to 1.000 with the knob at its
        # ceiling. A shape whose retriever never fails cannot rank two retrievers.
        #
        # So the competition comes back as CONTENT rather than as echo: other claims about the
        # SAME topic, in the same frames, uttered by roles in a balanced pair. They are lexically
        # close enough to compete for a top-K slot, and they cannot answer the question, because
        # the question names the claim it is asking about and these are different claims.
        #
        # Balanced across roles ON PURPOSE. One near-miss on a single role would tilt the answer
        # toward that role exactly as the old echo did, which is the defect one level down.
        # THE SAME CLAIM ABOUT OTHER TOPICS, roles balanced.
        #
        # First cut put OTHER claims about the SAME topic here, which shares only the topic words
        # with the question and left coverage at 1.000: the question quotes the statement verbatim
        # and nothing else in the haystack carried that vocabulary. Competition has to come from
        # the words the question actually leans on, so the near-miss carries the STATEMENT and
        # changes the TOPIC instead.
        #
        # It cannot answer the question, and the reason is worth stating precisely: the question
        # asks who said this about THIS topic, and these sessions say it about a different one.
        # That is a content boundary a careful reader can hold, unlike the previous echo leak,
        # where the statement's words arrived as topic-free fragments in "Also on my mind" lists
        # and reading them as "both of us touched on it" was the correct inference.
        #
        # Roles balanced, always as a pair. One near-miss on a single role would tilt the answer
        # toward that role -- the same defect as the echo, one level down.
        # TWO KINDS OF NEAR-MISS, and it takes both. The question names a topic AND quotes a
        # statement, so gold is the only session carrying the whole query -- which is why either
        # kind alone left coverage at 1.000:
        #
        #   same TOPIC, other claim      competes on the topic words
        #   same CLAIM, other topic      competes on the statement words
        #
        # Together several sessions carry most of the query and only gold carries all of it, which
        # is what a top-K budget has to choose between. Neither kind can answer: one is a different
        # claim, the other is about a different thing.
        other_topics = [tp for tp, _ in STATEMENTS if tp != topic]
        other_claims = [st for tp, st in STATEMENTS if tp != topic]
        near_topics = rng.sample(other_topics, 2)
        near_claims = rng.sample(other_claims, 2)
        near_frame, near_prompt = ATTRIBUTION_FRAMES[(offset + 1) % len(ATTRIBUTION_FRAMES)]

        def as_user(tp: str, st: str) -> tmc.Session:
            return tmc.Session([tmc.Turn("user", near_frame.format(topic=tp, statement=st)),
                                tmc.Turn("assistant", "")], BASE, is_gold=False, tag="near-miss")

        def as_assistant(tp: str, st: str) -> tmc.Session:
            return tmc.Session([tmc.Turn("user", near_prompt.format(topic=tp)),
                                tmc.Turn("assistant", near_frame.format(topic=tp, statement=st))],
                               BASE, is_gold=False, tag="near-miss")

        # Roles balanced WITHIN each kind, so neither kind tilts the answer toward a speaker --
        # the same defect as the echo leak, one level down.
        near_miss = [
            as_user(topic, near_claims[0]), as_assistant(topic, near_claims[1]),
            as_user(near_topics[0], statement), as_assistant(near_topics[1], statement),
        ]

        # INSIDE the haystack budget, not on top of it. Adding them pushed H to 26-27 against a
        # declared [15, 25], and a haystack that quietly grows is a retrieval control drifting.
        filler = near_miss + [_filler_session(rng, topic, echo, i)
                              for i in range(rng.randint(15, 25) - len(near_miss))]
        sessions = _place(rng, golds, filler)

        # Derived from the emitted turns, so the answer cannot disagree with the transcript.
        roles = sorted({t.role for g in golds for t in g.turns if t.has_answer})
        role = "both" if len(roles) > 1 else roles[0]
        answer = {
            "user": "You did — it was in one of your own messages, not one of mine.",
            "assistant": "I did — it was in one of my replies, not one of your messages.",
            "both": "Both of us did — it is in one of your messages and in one of my replies.",
        }[role]

        out.append(tmc.Question(
            f"tme-epi-{start + offset:03d}", TYPE_ATTRIB, question_text, answer,
            _asked_after(sessions), sessions,
            {"shape": SHAPE_ATTRIB, "attributed_speaker": role},
        ))
    return out


def build(echo, rng: random.Random) -> list[tmc.Question]:
    """`echo` is a float or, under per-shape calibration, a dict keyed by shape."""
    def knob(shape: str) -> float:
        return echo.get(shape, 0.0) if isinstance(echo, dict) else echo

    questions = _stated_questions(rng, knob(SHAPE_STATED), start=1)
    questions += _order_questions(rng, knob(SHAPE_ORDER), start=21)
    questions += _attribution_questions(rng, knob(SHAPE_ATTRIB), start=36)
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
            # THE SET, not the first one. With a `both` arm there are two answer-bearing turns
            # and `next(...)` silently reported whichever came first -- so every `both` question
            # read as "user" and the check condemned a correct corpus. Still an independent read:
            # the roles come from the emitted transcript, the label from the declaration, and the
            # two must agree.
            roles = sorted({t.role for i in q.gold_indices for t in q.sessions[i].turns
                            if t.has_answer})
            role = "both" if len(roles) > 1 else (roles[0] if roles else None)
            if role != q.extension["attributed_speaker"]:
                failures.append(f"{q.question_id}: answer key says {q.extension['attributed_speaker']} "
                                f"but the answer-bearing turns are {roles}")
            if q.extension["attributed_speaker"] == "both" and len(q.gold_indices) != 2:
                failures.append(f"{q.question_id}: a `both` question has {len(q.gold_indices)} gold "
                                f"sessions; finding one must not be finding the other")

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
        # Three classes now, so the bar tightens with them: at 5/5/5 the majority class is 1/3,
        # and anything above 0.45 would put majority-guessing meaningfully above the chance floor
        # the shape publishes.
        majority = max(attributed.count(c) for c in ("user", "assistant", "both"))
        if majority / len(attributed) > 0.45:
            failures.append(f"attribution speaker marginal is {majority}/{len(attributed)} -- "
                            f"majority-class guessing beats the shape")
    return failures


if __name__ == "__main__":
    tmc.finalise(
        vertical="episodic",
        build=build,
        structure=tmc.StructureSpec(
            # 2 added with the `both` arm of participant-attribution: that arm has one gold
            # session per speaker, so finding either is not finding both.
            h_min=15, h_max=25, g_values={1, 2, 4, 5, 6, 7}, gold_position_shuffled=True,
            no_absolute_dates=False,
        ),
        generator_tool="tools/gen_typedmemeval_episodic.py",
        extra_checks=check_episodic,
        # PER-SHAPE, because one knob cannot serve three shapes this far apart. On the single-knob
        # build the vertical mean sat at a healthy 0.682 while its shapes ran
        # participant-attribution 0.933 / assistant-stated 0.800 / list-order 0.275 -- a spread of
        # 0.66. attribution was effectively saturated (V9 14/15, headroom 0.07: no two retrievers
        # could be told apart on it) and list-order was far below band, and the mean reported
        # neither. That is the mean-satisfiable-by-averaging defect the family already fixed for
        # Arithmetic; Episodic simply never received it.
        shape_of=lambda q: (q.extension or {}).get("shape"),
    )
