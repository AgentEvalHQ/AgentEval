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
# Five rungs, every one of which the reference retriever can fail. The old ladder
# (1, 5, 15, 40) had two that could not: d=1 gives H=2 and d=5 gives H=6, and BM25@K_ref=5
# realised 1.00 on both, so half the vertical sat in a structurally unfailable band and the
# ladder graded at three levels rather than four. H>K_ref turns out to be necessary and not
# sufficient -- H=6 still saturates -- so the bottom rung starts where the measurement showed
# grading actually begins.
DISTANCES = (8, 15, 25, 40, 60)

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
            ("I have turned down an offer from {other} back in the spring.", "Their loss."),
            ("I have done a fortnight of contract work at {other} last year.", "Useful to have seen inside."),
            ("I have interviewed at {other} and never heard back.", "That happens more than it should."),
            ("I have nearly applied to {other} before the agency called.", "Timing decides most of it."),
            ("I have known people at {other} from the old days.", "Worth keeping warm."),
        ),
    ),
    Family(
        key="cat",
        question="What name have we settled on for the stray cat we took in last month?",
        statement="We have settled on the name {value} for the stray cat we took in last month.",
        acknowledgement="It suits a stray.",
        suffix="",
        interference=(
            ("We have nearly called the stray {other} before we changed our minds.", "It suited her less."),
            ("We have had a cat called {other} before.", "Names come back around."),
            ("We have used {other} for the foster kitten last spring.", "Only for a fortnight."),
            ("We have caught ourselves calling her {other}.", "Old habits."),
            ("We have written {other} on the vet form by mistake.", "Easily done."),
        ),
    ),
    Family(
        key="climbing_wall",
        question="Which climbing wall have I taken out a membership at since the summer?",
        statement="I have taken out a membership at {value}, the climbing wall by the canal, since the summer.",
        acknowledgement="Use it while the enthusiasm lasts.",
        suffix="Wall",
        interference=(
            ("I have trained at {other} for a season before the prices went up.", "They all creep up."),
            ("I have let a membership lapse at {other} somewhere.", "Worth cancelling properly."),
            ("I have climbed at {other} twice and never went back.", "A bad first impression, then."),
            ("I have got a day pass for {other} and forgot to use it.", "That is the usual fate."),
            ("I have been meaning to try the bouldering at {other}.", "A different discipline entirely."),
        ),
    ),
    Family(
        key="street",
        question="Which street have I moved onto since the flat fell through in the winter?",
        statement="I have moved onto {value}, a street of terraces, since the flat fell through in the winter.",
        acknowledgement="Somewhere to unpack, at least.",
        suffix="Row",
        interference=(
            ("I have viewed a flat on {other} before this one came up.", "The market moves fast."),
            ("I have lived on {other} for two years after university.", "Formative, probably."),
            ("I have nearly taken a place on {other} last winter.", "A close call."),
            ("I have walked down {other} on the way to the station.", "Habit forms quickly."),
            ("I have had post redirected from {other} for months.", "Royal Mail's patience."),
        ),
    ),
    Family(
        key="dental_practice",
        question="Which dental practice have I registered with since the old surgery shut?",
        statement="I have registered with {value}, a dental practice on the high street, since the old surgery shut.",
        acknowledgement="At least the waiting list is over.",
        suffix="Dental",
        interference=(
            ("I have been with {other} until the waiting lists got silly.", "Everywhere is the same."),
            ("I have rung {other} and they were not taking anyone new.", "Predictable."),
            ("I have had a filling done at {other} years ago.", "It held, at least."),
            ("I have been referred to {other} once and never went.", "Easy to let slide."),
            ("I have had reminders from {other} for an appointment I never made.", "Worth telling them."),
        ),
    ),
    Family(
        key="choir",
        question="Which choir have I joined this term, and where do they rehearse on a Wednesday evening?",
        statement="I have joined the {value} choir this term; they rehearse in the old chapel on a Wednesday evening.",
        acknowledgement="Good to hear you singing again.",
        suffix="Singers",
        interference=(
            ("I have sung with {other} for a term and left.", "Not every fit works."),
            ("I have auditioned for {other} and did not get in.", "Their standard is high."),
            ("I have been to hear {other} at the cathedral in December.", "Good acoustics there."),
            ("I have nearly joined {other} instead.", "It came down to the night."),
            ("I have borrowed a folder of music from {other} and never returned it.", "They always want it back."),
        ),
    ),
    Family(
        key="bike",
        question="Which bike have I ended up buying for the commute this spring?",
        statement="I have ended up buying a {value} bike for the commute this spring.",
        acknowledgement="That is the deliberating over with.",
        suffix="Cycles",
        interference=(
            ("I have test ridden a {other} and did not get on with it.", "Geometry is personal."),
            ("I have had a {other} stolen from outside the library.", "Grim."),
            ("I have borrowed a {other} for a weekend last summer.", "Enough to know."),
            ("I have looked at a second-hand {other} and walked away.", "The frame told the story."),
            ("I have kept the panniers off my old {other}.", "They outlast the bike."),
        ),
    ),
    Family(
        key="broadband",
        question="Which broadband provider have I ended up on since the switch went through?",
        statement="I have ended up on {value} as my provider since the switch went through.",
        acknowledgement="Hopefully the speeds hold.",
        suffix="Broadband",
        interference=(
            ("I have been with {other} before the contract ran out.", "They all do the same trick."),
            ("I have been quoted by {other} and it was worse.", "Introductory pricing."),
            ("I have had an engineer from {other} out twice.", "Twice is a pattern."),
            ("I have nearly signed with {other} in the spring.", "Glad you did not."),
            ("I have had post from {other} about an old account.", "Close it properly."),
        ),
    ),
    Family(
        key="allotment",
        question="Which allotment site have I finally been given a plot on this year?",
        statement="I have finally been given a plot on {value}, the allotment site behind the depot, this year.",
        acknowledgement="Time to buy a fork.",
        suffix="Fields",
        interference=(
            ("I have been on the waiting list at {other} for three years.", "That is the going rate."),
            ("I have given up a half plot at {other} when work got busy.", "Sensible."),
            ("I have helped a friend clear a plot at {other} in the spring.", "Hard work, that."),
            ("I have been shown round {other} and it was too exposed.", "Wind ruins a season."),
            ("I have owed {other} a key from years ago.", "Post it back."),
        ),
    ),
    Family(
        key="cello_teacher",
        question="Who have I taken on as my new cello teacher for the winter term?",
        statement="I have taken on {value} as my new cello teacher for the winter term.",
        acknowledgement="Back to scales, then.",
        suffix="",
        interference=(
            ("I have had lessons with {other} when I was at school.", "Foundations stick."),
            ("I have asked {other} and they were full for the term.", "Good teachers usually are."),
            ("I have been recommended {other} by someone at the choir.", "Word of mouth works."),
            ("I have sat in on a lesson with {other} once.", "Instructive, watching."),
            ("I have nearly gone with {other} instead.", "It came down to timing."),
        ),
    ),
    Family(
        key="bakery",
        question="Which bakery have I started getting the weekly sourdough from on a Saturday?",
        statement="I have started getting the weekly sourdough from {value}, the bakery on the corner, on a Saturday.",
        acknowledgement="Worth the detour, then.",
        suffix="Bakehouse",
        interference=(
            ("I have been getting the seeded rye from {other} on a Thursday.", "A different loaf entirely."),
            ("I have walked to {other} before they moved.", "A shame about the move."),
            ("I have stopped buying from {other} when the prices went up.", "Flour costs."),
            ("I have tried the focaccia at {other} once.", "Once was enough?"),
            ("I have ordered the Christmas bread from {other} every year.", "A fixed point."),
        ),
    ),
    Family(
        key="reading_circle",
        question="Which reading circle have I signed up to for the winter, and where do they meet?",
        statement="I have signed up to the {value} reading circle for the winter; they meet in the back room.",
        acknowledgement="Long books, dark evenings.",
        suffix="Circle",
        interference=(
            ("I have been to {other} twice and stopped.", "Not every group fits."),
            ("I have been invited to {other} and never went.", "Easy to put off."),
            ("I have read alongside {other} for a while without joining.", "The best of both."),
            ("I have nearly signed up to {other} instead.", "It came down to the night."),
            ("I have kept a book that belongs to {other}.", "Return it before winter."),
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
    return tmc.make_session(stamp, (template.format(other=other), tmc.weave_echo(assistant, terms)),
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
                    # The band variable is the rung, which IS the memory dial for this
                    # vertical (ADR-026 §16 C): distance between the fact and the question,
                    # with nothing else varying. Diagnostics, never a claim -- n = 12 per band
                    # is well under the n >= 30 floor a citable figure needs.
                    "difficulty": DISTANCES.index(distance) + 1,
                    "difficulty_dial": "distance", "difficulty_validated": True,
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
