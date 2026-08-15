#!/usr/bin/env python3
"""
Generates TypedMemEval-WorkingMemory v1 (ADR-026 §5.4) -- 48 questions.

What the vertical measures: recall of a stable profile fact as a function of *distance* --
how many sessions of same-domain interference sit between the statement and the query. The
grid is 12 fact families x 4 distances (d in {1, 5, 15, 40}), and the result surface reports
the outcome x distance curve rather than one number, because an aggregate over a distance
ladder is exactly the summary this family exists to refuse.

Every (family, distance) cell is an **independent question with its own haystack**. That
independence is the vertical's load-bearing guard (ADR §9 item 6): probing one stored fact
at increasing distances would let each probe rehearse the memory -- retrieval-augmented
systems typically re-write what they retrieve -- so the later rungs would measure refreshed
memory rather than aged memory. The cost is corpus size; what it buys is an independent
variable that means what its name says.

Two construct decisions are pinned here rather than left to the generator's convenience:

  * **Gold position is session 0, always** (`gold_position_shuffled=False`). Distance is
    therefore deliberately confounded with absolute position and recency. The ADR names that
    composite -- "how far back the memory sits" -- as the construct instead of pretending
    session-count distance was isolated from recency bias.
  * **Timestamp spacing is one fixed interval across every cell of the grid** (`tmc.spread`'s
    default). Distance-in-sessions and distance-in-time therefore cannot vary independently;
    two d=15 cells with different timestamp spreads would be incomparable for any memory
    system that decays with wall-clock time.

Validity rules on top of the family-wide V1-V6: the fact is stated exactly once and never
restated or paraphrased in an interference session (a paraphrased restatement silently
converts an aging measurement into a rehearsal measurement); interference is same-domain and
same-register -- other people's employers, other people's pets -- and **never contradicts**
the fact, because contradiction is the Forgetting vertical's independent variable and
blending the two would confound both; and the facts themselves are arbitrary, built from an
invented-stem pool drawn by the rng, so nothing about the answer is inferable from the
question (V2).

Gold answers are the stated value itself, taken from the same variable that produced the gold
sentence (V5). They are short by construction -- one invented stem plus at most one domain
word -- which is below the length at which `check_answer_not_verbatim` treats a literal echo
as a string-search giveaway. That exemption is deliberate and worth naming rather than
leaving for a reader to discover: for a profile-recall question the answer *is* the stated
value, and any paraphrase of it would be a different question.

Run:  python tools/gen_typedmemeval_workingmemory.py
"""

from __future__ import annotations

import random  # DevSkim: ignore DS148264 - corpus generation must be replayable under a seed; a CSPRNG cannot be seeded to reproduce a draw, and this selects filler text, not secrets.
from dataclasses import dataclass
from datetime import datetime

import typedmemeval_common as tmc

QTYPE = "workingmemory-recall"

#: The distance ladder. Each rung is a diagnostic stratum of n=12 (ADR §5 floor rule), and
#: the four rungs are the reportable shapes -- `distance-1` ... `distance-40`.
DISTANCES = (1, 5, 15, 40)

#: One fixed epoch for every cell. Combined with `tmc.spread`'s default interval this makes
#: session-distance and time-distance a single variable across the whole grid.
EPOCH = datetime(2026, 2, 2, 9, 0)

#: Invented proper nouns, split into two pools that share no token. Gold values are built
#: from the first pool and interference values only ever from the second, which is what makes
#: the "no interference session restates or contradicts the fact" check decidable rather than
#: a matter of reading the prose. Real words are avoided so every stem is a distinctive,
#: high-IDF token that a leak screen can search for exactly.
GOLD_STEMS = ("Pomfret", "Quillon", "Vessik", "Tarraby", "Brindlow", "Verrick",
              "Ferrow", "Dunmoor", "Ashlore", "Marrowby", "Halverstock", "Ondrey")
OTHER_STEMS = ("Calderwick", "Ninnis", "Torvald", "Estrey", "Wraymond", "Pellick",
               "Solvane", "Grennow", "Ibbetson", "Larkwood", "Mossam", "Trevanion")


@dataclass(frozen=True)
class Family:
    """One profile fact and the same-domain chatter that surrounds it.

    `suffix` is the domain word welded onto a stem to make a value ("Pomfret Instruments");
    it is shared with the interference values on purpose, so the interference reads as the
    same kind of thing. The *stem* is the part that must never leak out of the gold session.
    """
    key: str
    question: str
    statement: str
    acknowledgement: str
    suffix: str
    interference: tuple[tuple[str, str], ...]

    def value(self, stem: str) -> str:
        return f"{stem} {self.suffix}" if self.suffix else stem


# The shape of every family below is the same, and it is the shape the calibration gate needs.
# The gold sentence carries most of the question's content words; each interference sentence
# carries two or three of them natively, because same-domain chatter really does reuse the
# domain's vocabulary and a distractor that shares nothing with the question is a strawman
# (V3). That leaves gold ahead on term count but not by much -- and the echo clause closes the
# gap by handing a filler the question words it lacks *and* a second occurrence of the ones it
# already has. Distractors that shared no vocabulary would leave nothing for the gate to turn.
FAMILIES: tuple[Family, ...] = (
    Family(
        key="employer",
        question="Which new employer have I switched to, and what is the outfit called these days?",
        statement="I have switched to a new employer, an outfit called {value}, and start there in the spring.",
        acknowledgement="That is a proper change of scene.",
        suffix="Instruments",
        interference=(
            ("Priya has an offer from the outfit called {other}.", "Worth taking seriously."),
            ("Dad's old employer, {other}, has switched hands again.", "The end of an era for him."),
            ("Rosa turned down a new outfit, {other}, over the commute.", "Sensible of her."),
            ("Kit switched employer in the spring and is at {other} these days.", "And then what?"),
            ("The quiz team is half staffed by the new outfit at {other} these days.", "Small town, small pool."),
        ),
    ),
    Family(
        key="cat",
        question="What name have we settled on for the stray cat we took in last month?",
        statement="We have settled on the name {value} for the stray cat we took in last month.",
        acknowledgement="It suits a stray.",
        suffix="",
        interference=(
            ("Dan's terrier {other} chewed the doormat again last month.", "Terriers are like that."),
            ("The neighbours settled on the name {other} for their kitten.", "Bold of them."),
            ("Rosa took in a stray last month and calls it {other}.", "Brave of her."),
            ("The vet's own cat, {other}, is enormous.", "An occupational hazard."),
            ("Kit's rabbit {other} has escaped twice this month.", "Reinforce the hutch."),
        ),
    ),
    Family(
        key="climbing_wall",
        question="Which climbing wall have I taken out a membership at since the summer?",
        statement="I have taken out a membership at {value}, the climbing wall by the canal, since the summer.",
        acknowledgement="Use it while the enthusiasm lasts.",
        suffix="Wall",
        interference=(
            ("Kit has trained at {other} on Thursdays since the summer.", "Different holds, different problems."),
            ("Rosa cancelled her membership at {other} when the prices went up.", "Everyone is."),
            ("The bouldering league is hosted by the climbing wall at {other}.", "A busy weekend for them."),
            ("{other} has resurfaced its slab wall since the spring.", "About time."),
            ("Dan has taken out a membership at {other} as well.", "He collects them."),
        ),
    ),
    Family(
        key="street",
        question="Which street have I moved onto since the flat fell through in the winter?",
        statement="I have moved onto {value}, a street of terraces, since the flat fell through in the winter.",
        acknowledgement="Somewhere to unpack, at least.",
        suffix="Row",
        interference=(
            ("Priya moved her studio onto {other} in the spring.", "More light there."),
            ("The roadworks on {other} have closed the street to traffic.", "The long way round, then."),
            ("Rosa's sale fell through on {other} in the winter.", "Bruising."),
            ("The bakery on {other} has a queue through the door.", "It will settle down."),
            ("Kit has moved onto {other} since the autumn.", "A minor miracle."),
        ),
    ),
    Family(
        key="dental_practice",
        question="Which dental practice have I registered with since the old surgery shut?",
        statement="I have registered with {value}, a dental practice on the high street, since the old surgery shut.",
        acknowledgement="At least the waiting list is over.",
        suffix="Dental",
        interference=(
            ("Rosa waited nine months to be registered at {other}.", "That is the state of it."),
            ("{other} has stopped taking new patients at the practice.", "Everywhere has."),
            ("Kit's hygienist left the old surgery at {other}.", "They all do eventually."),
            ("Dad has been on the books at {other} since the merger.", "Lucky him."),
            ("The surgery next to {other} shut its doors as well.", "Another one."),
        ),
    ),
    Family(
        key="choir",
        question="Which choir have I joined this term, and where do they rehearse on a Wednesday evening?",
        statement="I have joined the {value} choir this term; they rehearse in the old chapel on a Wednesday evening.",
        acknowledgement="Good to hear you singing again.",
        suffix="Singers",
        interference=(
            ("Rosa's choir, {other}, rehearse on a Wednesday too.", "Ambitious of them."),
            ("{other} lost their accompanist this term.", "Hard to replace."),
            ("Kit auditioned for {other} last term and did not get in.", "Their loss."),
            ("The hall is double-booked with {other} on a Wednesday evening.", "Someone will have to move."),
            ("Dad joined {other} decades ago and still talks about it.", "He would."),
        ),
    ),
    Family(
        key="bike",
        question="Which bike have I ended up buying for the commute this spring?",
        statement="I have ended up buying a {value} bike for the commute this spring.",
        acknowledgement="That is the deliberating over with.",
        suffix="Cycles",
        interference=(
            ("Kit's {other} bike cracked at the dropout.", "That is a warranty job."),
            ("Rosa is selling the {other} she ended up never riding.", "They all say that."),
            ("The shop is only buying {other} stock this spring.", "A narrow range."),
            ("Dan rebuilt a {other} bike from a box of parts.", "More patience than sense."),
            ("The commute is full of {other} riders these days.", "A tribe."),
        ),
    ),
    Family(
        key="broadband",
        question="Which broadband provider have I ended up on since the switch went through?",
        statement="I have ended up on {value} as my provider since the switch went through.",
        acknowledgement="Hopefully the speeds hold.",
        suffix="Broadband",
        interference=(
            ("Rosa's provider, {other}, throttles her in the evenings.", "Typical."),
            ("{other} is digging up the pavement for fibre.", "Chaos for a month."),
            ("Kit went through a bad switch to {other} last year.", "Time to haggle."),
            ("Dad refuses to leave {other} since the merger.", "Loyalty is expensive."),
            ("The office ended up on {other} and it drops daily.", "Unworkable."),
        ),
    ),
    Family(
        key="allotment",
        question="Which allotment site have I finally been given a plot on this year?",
        statement="I have finally been given a plot on {value}, the allotment site behind the depot, this year.",
        acknowledgement="Time to buy a fork.",
        suffix="Fields",
        interference=(
            ("Rosa has been on the list at {other} for a third year.", "Glacial."),
            ("{other} floods every winter at the bottom of the site.", "Not ideal."),
            ("Kit finally gave up his plot at {other}.", "It is a lot of work."),
            ("The committee at {other} has banned bonfires this year.", "Predictable."),
            ("Dad was given a shed at {other} by the allotment secretary.", "He will fill it."),
        ),
    ),
    Family(
        key="cello_teacher",
        question="Who have I taken on as my new cello teacher for the winter term?",
        statement="I have taken on {value} as my new cello teacher for the winter term.",
        acknowledgement="Back to scales, then.",
        suffix="",
        interference=(
            ("Rosa's piano teacher, {other}, has moved north.", "She will miss him."),
            ("Kit has taken on {other} for trumpet at the school.", "A loud house."),
            ("{other} runs the Saturday strings group this term.", "Good for the little ones."),
            ("Dad had cello lessons with {other} decades ago.", "He still remembers the scales."),
            ("The conservatoire lost {other} to a touring job this winter.", "Understandable."),
        ),
    ),
    Family(
        key="bakery",
        question="Which bakery have I started getting the weekly sourdough from on a Saturday?",
        statement="I have started getting the weekly sourdough from {value}, the bakery on the corner, on a Saturday.",
        acknowledgement="Worth the detour, then.",
        suffix="Bakehouse",
        interference=(
            ("{other} has put its sourdough prices up again.", "Flour costs."),
            ("Rosa queues at {other} every Saturday.", "Devotion."),
            ("Kit says the weekly rye at {other} is heavy.", "Rye usually is."),
            ("{other} has started closing on Mondays.", "Noted."),
            ("The market stall is getting its bread from {other}.", "The same loaves, cheaper."),
        ),
    ),
    Family(
        key="reading_circle",
        question="Which reading circle have I signed up to for the winter, and where do they meet?",
        statement="I have signed up to the {value} reading circle for the winter; they meet in the back room.",
        acknowledgement="Long books, dark evenings.",
        suffix="Circle",
        interference=(
            ("Rosa's group, {other}, only reads translated fiction.", "Narrow, but interesting."),
            ("{other} meet in the pub these days.", "Better acoustics than the library."),
            ("Kit signed up to {other} and dropped out after the Proust.", "Fair enough."),
            ("The library is hosting {other} through the winter.", "A busy room."),
            ("Dad joined {other} and left within a month.", "He does that."),
        ),
    ),
)

FAMILY_BY_KEY = {f.key: f for f in FAMILIES}


def _interference_session(family: Family, question_text: str, echo: float,
                          rng: random.Random, stamp: datetime) -> tmc.Session:
    """One same-domain, same-register, non-contradicting interference session.

    The echo sample is drawn *per session* rather than once per question. With G=1 every
    question's coverage is binary, so a single echo sample shared by all of a question's
    fillers would make those fillers score alike and every question in the corpus flip at the
    same echo value -- a step function the gate's binary search cannot land inside. Sampling
    per session makes the *number* of fillers that outrank gold grow smoothly with echo, which
    is also what makes coverage fall with distance rather than staying flat across the ladder.
    """
    template, assistant = rng.choice(family.interference)
    other = family.value(rng.choice(OTHER_STEMS))
    terms = tmc.echo_terms(question_text, echo, rng)
    return tmc.make_session(stamp, (tmc.weave_echo(template.format(other=other), terms), assistant),
                            tag="interference")


def build(echo: float, rng: random.Random) -> list[tmc.Question]:
    questions: list[tmc.Question] = []
    index = 1

    for family in FAMILIES:
        # One distinct stem per rung, so the four cells of a family have four different gold
        # answers. The haystacks are independent anyway; distinct answers mean that even a
        # system that somehow saw two cells at once could not carry one answer to the other.
        stems = rng.sample(GOLD_STEMS, len(DISTANCES))

        for distance, stem in zip(DISTANCES, stems):
            value = family.value(stem)

            # d + 1 sessions and then the query, all on one fixed interval: the cells of the
            # grid differ in how many sessions intervene and in nothing else.
            stamps = tmc.spread(EPOCH, distance + 2)
            gold = tmc.make_session(
                stamps[0],
                (family.statement.format(value=value), family.acknowledgement),
                gold_turn=0, tag="gold")
            sessions = [gold] + [
                _interference_session(family, family.question, echo, rng, stamp)
                for stamp in stamps[1:-1]
            ]

            questions.append(tmc.Question(
                question_id=f"tme-wm-{index:03d}",
                question_type=QTYPE,
                question=family.question,
                # Taken from the same variable that produced the gold sentence (V5); a typed
                # constant here could drift from the corpus without anything noticing.
                answer=value,
                question_date=stamps[-1],
                sessions=sessions,
                extension={
                    "shape": f"distance-{distance}",
                    "distance_sessions": distance,
                    "fact_family": family.key,
                },
            ))
            index += 1

    return questions


def check_grid(questions: list[tmc.Question]) -> list[str]:
    """WorkingMemory-specific validity: the ladder *is* the measurement, so anything that
    lets a rung mean something other than its label is fatal here."""
    failures: list[str] = []
    seen: dict[tuple[str, int], str] = {}

    for q in questions:
        distance = q.extension["distance_sessions"]
        family_key = q.extension["fact_family"]

        # (a) H is the independent variable; if it drifts from the label, every number
        # reported against that rung describes a different experiment from the one named.
        if q.h != distance:
            failures.append(f"{q.question_id}: H={q.h} but distance_sessions={distance}")
        if q.extension["shape"] != f"distance-{distance}":
            failures.append(f"{q.question_id}: shape {q.extension['shape']!r} contradicts its distance")

        # Gold position is pinned by design (§5.4); a cell whose gold drifted off session 0
        # would have a different distance from the one it claims to have.
        if q.gold_indices != [0]:
            failures.append(f"{q.question_id}: gold at {q.gold_indices}, must be pinned to session 0")

        # (b) Stated exactly once. A restatement anywhere else in the haystack turns an aging
        # measurement into a rehearsal measurement without changing anything visible.
        holders = [i for i, s in enumerate(q.sessions) if q.answer.lower() in s.text().lower()]
        if holders != [0]:
            failures.append(
                f"{q.question_id}: value {q.answer!r} appears in sessions {holders}, expected exactly [0]")

        # (d) No interference session may carry any value this family could have stated.
        # Checked against the whole gold-stem pool rather than the one drawn value, because
        # another Pomfret in an interference session would contradict the fact whether or not
        # this particular cell happened to draw that stem.
        family = FAMILY_BY_KEY.get(family_key)
        if family is None:
            failures.append(f"{q.question_id}: unknown fact_family {family_key!r}")
            continue
        for i, session in enumerate(q.sessions):
            if session.is_gold:
                continue
            tokens = set(tmc.tokenize(session.text()))
            leaked = sorted(s for s in GOLD_STEMS if s.lower() in tokens)
            if leaked:
                failures.append(f"{q.question_id} s{i}: interference carries gold vocabulary {leaked}")

        # (c) Grid completeness, accumulated here and asserted below.
        cell = (family_key, distance)
        if cell in seen:
            failures.append(f"{q.question_id}: duplicate grid cell {cell}, already {seen[cell]}")
        seen[cell] = q.question_id

    expected = {(f.key, d) for f in FAMILIES for d in DISTANCES}
    for missing in sorted(expected - set(seen)):
        failures.append(f"grid cell {missing} is missing")
    for extra in sorted(set(seen) - expected):
        failures.append(f"grid cell {extra} is not in the declared 12x4 grid")

    return failures


if __name__ == "__main__":
    tmc.finalise(
        vertical="workingmemory",
        build=build,
        structure=tmc.StructureSpec(
            # H is the independent variable here, so this pair records the ends of the ladder
            # rather than asserting a haystack budget; the flag is what scopes the floor
            # assertion out instead of silently waiving it.
            h_min=1, h_max=40, g_values={1},
            gold_position_shuffled=False,
            no_absolute_dates=False,
            h_is_independent_variable=True,
            # ADR §5.4 pins the fact to session 0, so position separates gold perfectly and is
            # meant to: the construct is how far back the memory sits.
            separability_exempt=frozenset({"position_in_haystack"}),
        ),
        generator_tool="tools/gen_typedmemeval_workingmemory.py",
        extra_checks=check_grid,
    )
