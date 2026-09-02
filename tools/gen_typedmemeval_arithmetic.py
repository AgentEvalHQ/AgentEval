#!/usr/bin/env python3
"""
Generates TypedMemEval-Arithmetic v1 (ADR-026 §5.3) -- 50 questions.

What the vertical measures: derived answers. Every other vertical degrades gracefully
under partial retrieval -- miss one of two corroborating sessions and the answer is still
roughly right. Here it is not. An input the memory system fails to surface does not make
the answer vaguer, it makes it *wrong*, and wrong by an amount that looks exactly like a
confidently computed number. That is why inputs are spread one per session and G runs
3..6: this is the vertical where coverage bites, on purpose.

    Counts     14   count of events matching a stated predicate
    Sums       14   sum of stated payments
    Deltas     10   difference between two stated spends
    Durations  12   elapsed days, recovered from session timestamps alone

Why the validity rules differ per operation:

  * Counts get **predicate decidability**, not a coincidence rule. A subset-coincidence
    rule is vacuous for counting -- every same-size subset of matching events "counts" to
    the same number -- so the thing that can actually be wrong is the predicate's
    extension. Every candidate event in the haystack, matching and non-matching alike, is
    labelled in `count_predicate` / `candidates`, so a disagreement between a system and
    the corpus is attributable to a specific event rather than to arithmetic.

  * Sums, deltas and durations get **no coincident combination**, computed over gold and
    distractor values *mixed*. The failure this exists to catch is not "the model added
    up wrong"; it is a system that drops one gold input, substitutes a distractor value of
    the same unit, and lands on the gold answer anyway. That run scores as correct and its
    evidence attribution is silently corrupt, which is worse than a visible miss.

  * V4 (no absolute dates, no four-digit years in message content) is enforced
    corpus-wide, not just on the 12 duration questions that need it. Twelve questions
    would have to be special-cased otherwise, and the rule costs the other 38 nothing --
    they never wanted to print a date. Every stated amount is kept under $480 and every
    stated day-count under 46 for the same reason: a bare "2015.00" in a message would
    trip the four-digit-year guard, and a corpus that has to argue with its own checker is
    a corpus nobody should trust.

Two places where the shapes are wider than "two stated values", and why. A difference over
exactly two sessions cannot reach the vertical's G ∈ 3..6, and a duration over exactly two
sessions cannot either; padding gold with a session no input depends on would leave V6's
leave-one-out probe certifying a component that is not load-bearing. So each side of a
delta is a spend spread over one to three sessions (one stated value each, `side` on every
input), and each duration is a sum over two or three open/close spells (one input per
spell, carrying both endpoints). Both stay genuine differences and genuine elapsed times;
both keep every gold session accounted for by the derivation, which the checks enforce in
both directions.

Every number in here comes out of the rng (V2). Amounts, day-counts, how many orders were
placed, which side of a delta is larger -- none of it is chosen because it reads well. A
plausible answer is a guessable answer, and a guessable answer measures nothing.

Two honest residuals, stated rather than engineered away:

  * Count golds live in {3,4,5,6}. V2's zero-context probe is weak against a four-value
    range by construction; what carries this shape is the candidate labelling, not the
    non-inferability probe.
  * The no-coincident-combination rule is enforced at exact tolerance (half a cent, half a
    day) across all subsets up to G+1, and additionally at a *near-miss* margin ($0.50,
    2 days) for the single-substitution family specifically. Signed combinations are dense
    enough that a near-miss margin across all subset sizes is not satisfiable by rejection
    sampling at this pool size; larger-subset near-misses are a residual risk, which is
    the same posture ADR §5.3 takes on larger coincidences generally.

Run:  python tools/gen_typedmemeval_arithmetic.py
"""

from __future__ import annotations

import random  # DevSkim: ignore DS148264 - corpus generation must be replayable under a seed; a CSPRNG cannot be seeded to reproduce a draw, and this selects filler text, not secrets.
import re
from copy import deepcopy
from dataclasses import dataclass
from datetime import datetime, timedelta

import typedmemeval_common as tmc

SHAPE_COUNT = "count"
SHAPE_SUM = "sum"
SHAPE_DELTA = "delta"
SHAPE_DURATION = "duration"

TYPE_COUNT = "arithmetic-count"
TYPE_SUM = "arithmetic-sum"
TYPE_DELTA = "arithmetic-delta"
TYPE_DURATION = "arithmetic-duration"

#: Stated amounts stay well inside (0, 1900) so no literal can look like a year to the V4
#: guard, and well above the near-miss margin so a dropped input is never a rounding error.
AMOUNT_MIN_CENTS = 4_000
AMOUNT_MAX_CENTS = 47_999

#: Same reasoning, in days. The floor is above the near-miss margin.
DAYS_MIN = 4
DAYS_MAX = 45

#: Same-unit distractor values per question. Six is a deliberate ceiling: the mixed-subset
#: search is exponential in the pool, and a pool wide enough to make rejection sampling
#: fail is a pool the corpus cannot honestly certify.
NUMERIC_DISTRACTORS = 6

#: Exact tolerance per unit -- "sums of exact inputs are exact" (ADR §5.3). Half a cent and
#: half a day are the representable epsilons, not a softening.
EPS_USD = 0.005
EPS_DAYS = 0.5

#: Near-miss margin for the single-substitution family only (see the module docstring).
NEAR_USD = 0.50
NEAR_DAYS = 2.0

BASE_STAMP = datetime(2026, 2, 2, 9, 0)

VENDORS = [
    "Meridian Tools", "Ashgrove Supply", "Corbel and Finch", "Naldern Hardware",
    "Pettifer Timber", "Quillon Fixings", "Sarrat Ironmongers", "Thackeray Abrasives",
    "Vindle Fasteners", "Wrenfield Castings", "Ilbury Joinery", "Ockham Coatings",
    "Brackenhoe Glazing", "Stelling Adhesives",
]

ORDER_ITEMS = [
    "a box of countersunk screws", "a litre of shellac", "a replacement chuck key",
    "two rolls of jointing tape", "a bag of resin anchors", "a spare guide rail",
    "a set of forstner bits", "a length of piano hinge", "a tub of frame sealant",
    "a pack of sanding discs", "a coil of earth bonding cable", "a case of wood filler",
]

#: Every job name is two distinctive words, and no two share a word. The echo knob works by
#: flooding the filler with question vocabulary, which drives those terms' IDF to nearly
#: zero; what is left to rank on is whatever query term the echo sample happened to miss.
#: A one-word subject therefore gives a coin-flip corpus -- questions land at full coverage
#: or at zero, and the mean stops describing any individual question.
#:
#: Three words, matching the three the duration shape gets from its thing-plus-site naming.
#: Not decoration: subject length is what sets a shape's realised coverage under this knob,
#: so shapes named with different numbers of words land at different coverage for reasons
#: that have nothing to do with what the shapes measure, and the per-shape report surface
#: would read that artefact as a finding. No word is reused between entries, or across
#: LINE_ITEMS, so a distractor never picks up a subject term by accident.
JOBS = [
    "the Marrow Lane rewire", "the Bellweather Court attic", "the Dunstan Yard roofline",
    "the Ferrier Row cellar", "the Crowmarsh Mill dormer", "the Halloway Wharf porch",
    "the Ingle Bridge veranda", "the Kestrel Quay annexe", "the Larkspur Terrace workshop",
    "the Nettlebed Barn studio", "the Ockendon Rise glazing", "the Padstow Green plumbing",
    "the Quarrier Walk screed", "the Rushmere Fields brickwork",
    "the Threlkeld Vale staircase", "the Wexcombe Gate garage",
]

LINE_ITEMS = [
    "the plasterboard", "the underfloor insulation", "the replacement sash cords",
    "the tile adhesive", "the skirting", "the socket faceplates", "the loft hatch",
    "the extractor ducting", "the primer", "the door furniture", "the guttering",
    "the bathroom sealant", "the stair spindles", "the render mesh", "the cavity trays",
    "the roof battens",
]

#: (noun, site, spell-opens line, spell-closes line). Both lines name the site as well as
#: the thing, for the reason given above the job list: four distinctive words between them,
#: so the echo sample can rarely swamp all of the question's signal at once.
#:
#: Neither line states a duration or a date. The whole point of the shape is that the
#: interval exists only in the session timestamps, so anything a speaker could read off the
#: message text would defeat it.
EQUIPMENT = [
    ("the loaner van", "Ockendon Rise",
     "The loaner van turned up at Ockendon Rise this morning.",
     "I handed the loaner van back from Ockendon Rise today."),
    ("the tower scaffold", "Marrow Lane",
     "The tower scaffold went up at Marrow Lane today.",
     "The tower scaffold came down at Marrow Lane today."),
    ("the hired floor sander", "Ferrier Row",
     "Collected the hired floor sander for Ferrier Row today.",
     "Took the hired floor sander back from Ferrier Row today."),
    ("the site dehumidifier", "Kestrel Quay",
     "The site dehumidifier went in at Kestrel Quay today.",
     "The site dehumidifier came out of Kestrel Quay today."),
    ("the drive skip", "Larkspur Terrace",
     "The drive skip landed at Larkspur Terrace this morning.",
     "They took the drive skip away from Larkspur Terrace today."),
    ("the borrowed tile cutter", "Nettlebed Barn",
     "Borrowed the tile cutter for Nettlebed Barn today.",
     "Gave the borrowed tile cutter back from Nettlebed Barn today."),
    ("the temporary fencing", "Padstow Green",
     "The temporary fencing went up at Padstow Green today.",
     "The temporary fencing came down at Padstow Green today."),
    ("the hired mini digger", "Quarrier Walk",
     "The hired mini digger arrived at Quarrier Walk today.",
     "The hired mini digger left Quarrier Walk today."),
    ("the loaned laser level", "Rushmere Fields",
     "Picked up the loaned laser level for Rushmere Fields today.",
     "Returned the loaned laser level from Rushmere Fields today."),
    ("the rented drying fans", "Threlkeld Vale",
     "The rented drying fans went on at Threlkeld Vale today.",
     "The rented drying fans came off hire at Threlkeld Vale today."),
    ("the borrowed threading machine", "Wexcombe Gate",
     "The borrowed threading machine came over to Wexcombe Gate today.",
     "Sent the borrowed threading machine back from Wexcombe Gate today."),
    ("the propped acrow frame", "Crowmarsh Mill",
     "The propped acrow frame went in at Crowmarsh Mill today.",
     "The propped acrow frame came out of Crowmarsh Mill today."),
]

#: Same-unit day distractors. Same register, same site, never the answer -- V3's
#: "plausible, not a strawman" rule applied to the unit that matters for durations.
DAY_DISTRACTORS = [
    "The plasterer's compressor was on site for {n} days.",
    "The surveyor kept the damp meter for {n} days.",
    "The window samples sat in the hall for {n} days.",
    "The electrician's cable drums were in the way for {n} days.",
    "The chimney tarpaulin stayed up for {n} days.",
    "The neighbour's trailer blocked the lane for {n} days.",
    "The boiler crate sat in the garage for {n} days.",
    "The bricklayer's mixer was parked out front for {n} days.",
    "The kitchen units waited in the van for {n} days.",
    "The party-wall notice stayed pinned to the door for {n} days.",
]

#: Same-domain, same-register filler with no digits at all. The absence of digits is load
#: bearing: the audit in `check_arithmetic` recovers the haystack's same-unit values by
#: reading the emitted text, so any stray number here would be a number the corpus claims
#: not to have.
FILLER = [
    # Filler that says "today", because gold's statement always does and none of this bank did.
    # `'today'` sat in gold at 20% perfect separation with a chance rate so low the excess is 76 sd,
    # and it was invisible for two revisions because the pooled AUC reads 0.7365 -- under the bar.
    # Still no digits: the audit in `check_arithmetic` recovers the haystack's same-unit values by
    # reading this text, so a number here would be a number the corpus claims not to have.
    ("Swept the cut-off station out today before knocking off.",
     "It gets everywhere otherwise."),
    ("The yard gate was propped open all day today.",
     "Someone will say something eventually."),
    ("Signed for a delivery on the neighbour's behalf today.",
     "They will owe you a favour."),
    ("Put the good trestles away today so they stop walking.",
     "Sensible, given last time."),
    ("Nobody turned up for the skip exchange today.",
     "Another morning gone, then."),
    ("Marked up the plasterboard for cutting today.",
     "Better than measuring twice on the day."),
    ("The trade counter has moved to the far end of the yard again.",
     "That adds a walk to every collection."),
    ("Spent the morning sorting offcuts back into the rack.",
     "Worth it next time you need a length of something."),
    ("The compressor is making that ticking noise again.",
     "Better to look at it before it stops altogether."),
    ("Rain all afternoon, so nothing outside got touched.",
     "Some days the weather decides for you."),
    ("Lent my long level to the plasterer next door.",
     "Write it on the board or it will vanish."),
    ("The skip company changed their collection window.",
     "Worth checking before you fill it."),
    ("Swapped the blade on the track saw.",
     "A sharp blade makes a different tool of it."),
    ("The van needs a new wiper on the driver's side.",
     "Cheap to fix, annoying to ignore."),
    ("Client wants to walk through the kitchen layout again.",
     "Better now than after the units arrive."),
    ("Ran out of masking tape halfway through cutting in.",
     "That always happens at the worst moment."),
    ("The dust extractor bag was full and nobody said.",
     "Check it at the start of the day, then."),
    ("Building control want another look at the joist hangers.",
     "Straightforward if the spec was followed."),
    ("Greased the loft ladder mechanism; it was catching.",
     "A little grease goes a long way."),
    ("The timber merchant has changed their delivery slots.",
     "Plan the week around it."),
    ("Nearly through the snagging list on the upstairs bathroom.",
     "The last stretch always drags."),
    ("Somebody moved my chalk line and did not put it back.",
     "Label everything, apparently."),
    ("The apprentice starts on second-fix work this week.",
     "Good time to learn it properly."),
    ("Had to re-cut the architrave; the mitre was out.",
     "Measure twice, as they say."),
    ("Left the site radio on overnight and flattened it.",
     "It will charge back up."),
    ("The scaffolder wants his boards back before the weekend.",
     "Stack them where he can reach them."),
]

REPLIES = ["Noted.", "Logged.", "Got it.", "Recorded.", "Filed.", "Understood."]

#: The one phrasing that constitutes an order. Fixed rather than varied so the counting
#: predicate is decidable by inspection: a paraphrase a reader has to interpret is exactly
#: the borderline event ADR §5.3 forbids.
ORDER_PHRASE = "put an order in with "

_USD_RE = re.compile(r"\$(\d+\.\d{2})")
_DAYS_RE = re.compile(r"\b(\d+) days\b")


# --------------------------------------------------------------------------------------
# Combination search -- the machinery behind the no-coincident-combination rule
# --------------------------------------------------------------------------------------

def _coincident(values, target, max_size, gold_signature, signed, eps):
    """First non-gold combination of `values` landing within `eps` of `target`, else None.

    `values` is [(key, magnitude)] drawn from gold AND distractor sessions mixed, because
    the case that corrupts evidence attribution is a gold value swapped for a distractor
    one, not a distractor-only accident. `signed` is set for deltas, where a combination is
    a subset *plus a side assignment* -- the search space is 3^n rather than 2^n, hence the
    branch-and-bound: the remaining magnitudes give an exact bound on how far the running
    total can still travel, and most branches die on it immediately.
    """
    order = sorted(values, key=lambda kv: -abs(kv[1]))
    n = len(order)
    reachable = [0.0] * (n + 1)
    for i in range(n - 1, -1, -1):
        reachable[i] = reachable[i + 1] + abs(order[i][1])

    signs = (1, -1) if signed else (1,)
    found: list[frozenset] = []

    def walk(i, used, current, chosen):
        if found:
            return
        # Checked on entry, so every prefix-subset is tested exactly where it is complete.
        if used and abs(current - target) <= eps and frozenset(chosen) != gold_signature:
            found.append(frozenset(chosen))
            return
        if i == n or used == max_size:
            return
        # The most the remaining budget can move the total: the `max_size - used` largest
        # magnitudes left. Sorted descending, those are exactly the next few entries.
        headroom = reachable[i] - reachable[min(i + max_size - used, n)]
        if abs(target - current) > headroom + eps:
            return
        key, value = order[i]
        for sign in signs:
            chosen.append((key, sign))
            walk(i + 1, used + 1, current + sign * value, chosen)
            chosen.pop()
            if found:
                return
        walk(i + 1, used, current, chosen)

    walk(0, 0, 0.0, [])
    return found[0] if found else None


def _substitution_near_miss(gold, distractors, target, margin):
    """True if swapping exactly one gold value for one distractor value lands within
    `margin` of the gold answer.

    Held to a wider margin than the exact rule because this is the specific failure mode
    the vertical exists to expose: a system that misses one input, picks up a same-unit
    distractor instead, and reports a number a judge's numeric normalization would wave
    through. Drop-one and add-one variants need no extra margin -- every stated value is
    larger than the margin, so those land visibly off.
    """
    for _, gold_value, sign in gold:
        without = target - sign * gold_value
        for _, distractor_value in distractors:
            if abs(without + sign * distractor_value - target) < margin:
                return True
    return False


# --------------------------------------------------------------------------------------
# Planning -- everything that must not move when the calibration gate turns the echo knob
# --------------------------------------------------------------------------------------

@dataclass
class _Plan:
    """One fully-decided question, minus the echo clause.

    Splitting planning from rendering is what keeps `build` a pure function of
    (echo, rng) *and* affordable. The rejection sampling that certifies the
    no-coincident-combination rule is the expensive part of generation, and the calibration
    gate calls `build` up to two dozen times; planning consumes rng before any
    echo-dependent draw, so the plan is identical at every echo and can be memoized.
    """
    qid: str
    shape: str
    qtype: str
    question: str
    answer: str
    hours: int
    script: list[tuple[str, str, bool]]
    derivation: dict
    extra: dict


_PLAN_CACHE: dict = {}


def _plans(rng: random.Random) -> list[_Plan]:
    """Memoized planning, keyed on the rng's entry state.

    The key is the exact generator state, so a plan can never be served to a different
    seed, and the post-planning state is replayed rather than recomputed, so the rng the
    renderer sees is byte-identical to the one it would have seen without the cache. The
    cache is an optimization that cannot change the output -- if it could, it would be a
    bug dressed as a speed-up.
    """
    key = rng.getstate()
    cached = _PLAN_CACHE.get(key)
    if cached is not None:
        plans, after = cached
        rng.setstate(after)
        return plans
    plans = _draw_plans(rng)
    _PLAN_CACHE[key] = (plans, rng.getstate())
    return plans


def _layout(rng: random.Random, g: int) -> tuple[int, list[int]]:
    """Haystack size and the ascending slots the gold sessions occupy.

    Gold order is preserved (ascending slots) rather than shuffled per session, because the
    duration shape reads intervals off the slot order; position is randomized by *which*
    slots are drawn, which is what the position-artefact check actually cares about.
    """
    h = rng.randint(15, 25)
    total = g + h
    return total, sorted(rng.sample(range(total), g))


def _balanced(rng: random.Random, values: list[int], count: int) -> list[int]:
    """A shuffled, evenly-spread draw of `count` values from `values`.

    Balanced so every G in 3..6 is actually exercised, shuffled so the sequence carries no
    pattern -- for counts the answer *is* G, and a corpus whose count answers walk 3,4,5,6
    in order is a corpus with a guessable answer key (V2).
    """
    out = [values[i % len(values)] for i in range(count)]
    rng.shuffle(out)  # DevSkim: ignore DS148264 - corpus generation must be replayable under a seed; a CSPRNG cannot be seeded to reproduce a draw, and this shuffles filler text, not secrets.
    return out


def _amount(rng: random.Random) -> float:
    return rng.randint(AMOUNT_MIN_CENTS, AMOUNT_MAX_CENTS) / 100.0


def _filler_deck(rng: random.Random, wanted: int) -> list[tuple[str, str]]:
    deck = FILLER * (wanted // len(FILLER) + 1)
    rng.shuffle(deck)  # DevSkim: ignore DS148264 - corpus generation must be replayable under a seed; a CSPRNG cannot be seeded to reproduce a draw, and this shuffles filler text, not secrets.
    return deck[:wanted]


def _draw_count_plans(rng: random.Random, start: int) -> list[_Plan]:
    plans = []
    for offset, g in enumerate(_balanced(rng, [3, 4, 5, 6], 14)):
        vendor, rival_a, rival_b = rng.sample(VENDORS, 3)
        total, gold_slots = _layout(rng, g)
        free = [i for i in range(total) if i not in set(gold_slots)]

        # Non-matching candidates come in two kinds, both clearly outside the predicate on
        # their face (ADR §5.3 forbids borderline events): an order placed with somebody
        # else, and a mention of the target vendor that is plainly not an order.
        near_miss_count = rng.randint(3, 5)
        near_miss_slots = sorted(rng.sample(free, near_miss_count))
        kinds = [("other-vendor" if i % 2 == 0 else "mention-only")
                 for i in range(near_miss_count)]
        rng.shuffle(kinds)  # DevSkim: ignore DS148264 - corpus generation must be replayable under a seed; a CSPRNG cannot be seeded to reproduce a draw, and this shuffles filler text, not secrets.

        question = f"How many times did I put an order in with {vendor}?"
        items = [rng.choice(ORDER_ITEMS) for _ in range(g + near_miss_count)]

        script: list[tuple[str, str, bool]] = [("", "", False)] * total
        for slot, item in zip(gold_slots, items[:g]):
            script[slot] = (f"I put an order in with {vendor} today for {item}.",
                            rng.choice(REPLIES), True)
        for slot, kind, item in zip(near_miss_slots, kinds, items[g:]):
            rival = rival_a if kind == "other-vendor" else rival_b
            if kind == "other-vendor":
                user = f"I put an order in with {rival} today for {item}."
            else:
                user = f"{vendor} sent their new catalogue over; nothing I need from it."
            script[slot] = (user, rng.choice(REPLIES), False)
        deck = _filler_deck(rng, total)
        for i in range(total):
            if script[i][0] == "":
                script[i] = (deck[i][0], deck[i][1], False)

        candidates = ([{"session_index": s, "matches": True} for s in gold_slots] +
                      [{"session_index": s, "matches": False} for s in near_miss_slots])
        candidates.sort(key=lambda c: c["session_index"])

        derivation = {
            "operation": "count",
            "inputs": [{"session_index": s, "value": 1} for s in gold_slots],
            "value": g,
            "unit": "count",
        }
        plans.append(_Plan(
            qid=f"tme-ari-{start + offset:03d}",
            shape=SHAPE_COUNT,
            qtype=TYPE_COUNT,
            question=question,
            # Derived from the derivation block, never typed: V5 holds only if there is
            # exactly one place the number comes from.
            answer=f"{derivation['value']} separate orders with {vendor}.",
            hours=30,
            script=script,
            derivation=derivation,
            extra={
                "count_predicate": f"the speaker put an order in with {vendor}",
                "candidates": candidates,
            },
        ))
    return plans


def _draw_sum_plans(rng: random.Random, start: int) -> list[_Plan]:
    plans = []
    for offset, g in enumerate(_balanced(rng, [3, 4, 5, 6], 14)):
        jobs = rng.sample(JOBS, 1 + NUMERIC_DISTRACTORS)
        job, other_jobs = jobs[0], jobs[1:]
        total, gold_slots = _layout(rng, g)
        free = [i for i in range(total) if i not in set(gold_slots)]
        distractor_slots = sorted(rng.sample(free, NUMERIC_DISTRACTORS))

        gold_values, distractor_values = _draw_additive(
            rng, g, NUMERIC_DISTRACTORS, gold_slots, distractor_slots,
            lambda: _amount(rng), EPS_USD, NEAR_USD, signed=False, max_size=g + 1)
        target = round(sum(gold_values), 2)

        question = (f"Add up every payment I logged against {job} -- "
                    f"what do they come to in total?")
        script: list[tuple[str, str, bool]] = [("", "", False)] * total
        items = rng.sample(LINE_ITEMS, g + NUMERIC_DISTRACTORS)
        for slot, value, item in zip(gold_slots, gold_values, items[:g]):
            script[slot] = (f"Payment logged against {job}: ${value:.2f} for {item}.",
                            rng.choice(REPLIES), True)
        for slot, value, item, other in zip(distractor_slots, distractor_values,
                                            items[g:], other_jobs):
            script[slot] = (f"Payment logged against {other}: ${value:.2f} for {item}.",
                            rng.choice(REPLIES), False)
        deck = _filler_deck(rng, total)
        for i in range(total):
            if script[i][0] == "":
                script[i] = (deck[i][0], deck[i][1], False)

        derivation = {
            "operation": "sum",
            "inputs": [{"session_index": s, "value": v}
                       for s, v in zip(gold_slots, gold_values)],
            "value": target,
            "unit": "USD",
        }
        plans.append(_Plan(
            qid=f"tme-ari-{start + offset:03d}",
            shape=SHAPE_SUM,
            qtype=TYPE_SUM,
            question=question,
            answer=f"${derivation['value']:,.2f} in total on {job}.",
            hours=30,
            script=script,
            derivation=derivation,
            extra={},
        ))
    return plans


def _draw_delta_plans(rng: random.Random, start: int) -> list[_Plan]:
    plans = []
    for offset, g in enumerate(_balanced(rng, [3, 4, 5, 6], 10)):
        # A delta over exactly two sessions cannot reach the vertical's G ∈ 3..6, and
        # padding gold with a session nobody needs would break V6. So each side of the
        # difference is a spend spread across sessions, one stated value per session --
        # every gold session load-bearing, the operation still a difference.
        left = rng.randint(1, min(3, g - 1))
        right = g - left
        if right > 3:
            left, right = g - 3, 3
        jobs = rng.sample(JOBS, 2 + NUMERIC_DISTRACTORS)
        job_a, job_b, other_jobs = jobs[0], jobs[1], jobs[2:]
        total, gold_slots = _layout(rng, g)
        free = [i for i in range(total) if i not in set(gold_slots)]
        distractor_slots = sorted(rng.sample(free, NUMERIC_DISTRACTORS))

        # Sides are assigned to slots at random rather than in slot order, so a system
        # cannot recover the split from position.
        sides = ["a"] * left + ["b"] * right
        rng.shuffle(sides)  # DevSkim: ignore DS148264 - corpus generation must be replayable under a seed; a CSPRNG cannot be seeded to reproduce a draw, and this shuffles filler text, not secrets.

        gold_values, distractor_values = _draw_additive(
            rng, g, NUMERIC_DISTRACTORS, gold_slots, distractor_slots,
            lambda: _amount(rng), EPS_USD, NEAR_USD, signed=True, max_size=g + 1,
            sides=sides)

        sum_a = round(sum(v for v, s in zip(gold_values, sides) if s == "a"), 2)
        sum_b = round(sum(v for v, s in zip(gold_values, sides) if s == "b"), 2)
        if sum_a >= sum_b:
            higher, lower, high_side = job_a, job_b, "a"
        else:
            higher, lower, high_side = job_b, job_a, "b"
        target = round(abs(sum_a - sum_b), 2)

        question = (f"What is the difference between what I logged against {job_a} "
                    f"and what I logged against {job_b}?")
        script: list[tuple[str, str, bool]] = [("", "", False)] * total
        items = rng.sample(LINE_ITEMS, g + NUMERIC_DISTRACTORS)
        for slot, value, side, item in zip(gold_slots, gold_values, sides, items[:g]):
            job = job_a if side == "a" else job_b
            script[slot] = (f"Payment logged against {job}: ${value:.2f} for {item}.",
                            rng.choice(REPLIES), True)
        for slot, value, item, other in zip(distractor_slots, distractor_values,
                                            items[g:], other_jobs):
            script[slot] = (f"Payment logged against {other}: ${value:.2f} for {item}.",
                            rng.choice(REPLIES), False)
        deck = _filler_deck(rng, total)
        for i in range(total):
            if script[i][0] == "":
                script[i] = (deck[i][0], deck[i][1], False)

        derivation = {
            "operation": "delta",
            # `side` extends the ADR's {session_index, value} input shape. Without it the
            # block records which sessions matter but not how they combine, and the
            # recomputation check would have nothing to recompute.
            "inputs": [{"session_index": s, "value": v,
                        "side": "minuend" if side == high_side else "subtrahend"}
                       for s, v, side in zip(gold_slots, gold_values, sides)],
            "value": target,
            "unit": "USD",
        }
        plans.append(_Plan(
            qid=f"tme-ari-{start + offset:03d}",
            shape=SHAPE_DELTA,
            qtype=TYPE_DELTA,
            question=question,
            answer=f"${derivation['value']:,.2f} -- {higher} cost that much more than {lower}.",
            hours=30,
            script=script,
            derivation=derivation,
            extra={},
        ))
    return plans


def _draw_duration_plans(rng: random.Random, start: int) -> list[_Plan]:
    plans = []
    for offset, spells in enumerate(_balanced(rng, [2, 3], 12)):
        g = spells * 2
        noun, site, opens, closes = EQUIPMENT[(start + offset) % len(EQUIPMENT)]

        # Session spacing is exactly one day here (not the 30 hours the other shapes use)
        # so that a spell length is a whole number of days. A shape whose gold answer is
        # "17.5 days" would be measuring the generator's arithmetic, not the system's.
        total, gold_slots, spell_days = _draw_duration_layout(rng, g)
        free = [i for i in range(total) if i not in set(gold_slots)]
        distractor_slots = sorted(rng.sample(free, NUMERIC_DISTRACTORS))
        distractor_values = _draw_day_distractors(rng, spell_days, sum(spell_days), g + 1)

        # The day-counting convention is STATED, not left to be inferred. Gold is
        # `close_slot - open_slot` over one-day spacing, i.e. the half-open interval
        # [arrival, departure) -- and the inclusive reading is exactly `gold + spells`, which is
        # what two independent oracles produced on 4 of 4 duration misses. A question whose answer
        # depends on an unstated convention measures the convention, not the memory.
        #
        # Worded to add no `day`/`days` token beyond the one the question already carries. The
        # gold sessions never state a duration, while the DAY_DISTRACTORS all say "for {n} days",
        # so every extra day-token in the query pulls BM25 toward the distractors and away from
        # gold. "arrival"/"departure" appear in neither, so they are inert for retrieval.
        question = (f"Counting every spell, how many days in total did I have {noun} "
                    f"at {site}? Exclude the departure day.")
        script: list[tuple[str, str, bool]] = [("", "", False)] * total
        for spell in range(spells):
            script[gold_slots[2 * spell]] = (opens, rng.choice(REPLIES), True)
            script[gold_slots[2 * spell + 1]] = (closes, rng.choice(REPLIES), True)
        templates = rng.sample(DAY_DISTRACTORS, NUMERIC_DISTRACTORS)
        for slot, value, template in zip(distractor_slots, distractor_values, templates):
            script[slot] = (template.format(n=value), rng.choice(REPLIES), False)
        deck = _filler_deck(rng, total)
        for i in range(total):
            if script[i][0] == "":
                script[i] = (deck[i][0], deck[i][1], False)

        derivation = {
            "operation": "duration",
            # One input per spell, carrying both endpoints. The ADR's minimal input shape
            # names a single session; a duration is not a property of one session, and
            # naming only the closing session would leave the opening session gold but
            # unaccounted for, which V6 would then have nothing to certify.
            "inputs": [{"session_index": gold_slots[2 * i + 1],
                        "from_session_index": gold_slots[2 * i],
                        "value": spell_days[i]} for i in range(spells)],
            "value": sum(spell_days),
            "unit": "days",
        }
        plans.append(_Plan(
            qid=f"tme-ari-{start + offset:03d}",
            shape=SHAPE_DURATION,
            qtype=TYPE_DURATION,
            question=question,
            answer=f"{derivation['value']} days in total, across {spells} spells.",
            hours=24,
            script=script,
            derivation=derivation,
            extra={},
        ))
    return plans


def _draw_duration_layout(rng: random.Random, g: int) -> tuple[int, list[int], list[int]]:
    """Slots whose consecutive pairs give distinct spell lengths of at least two days.

    Distinct because two equal spells would make a swap between them invisible, and at
    least two days because a one-day spell sits inside the near-miss margin, where a
    distractor value cannot be kept clear of it.
    """
    for _ in range(200):
        total, slots = _layout(rng, g)
        days = [slots[2 * i + 1] - slots[2 * i] for i in range(g // 2)]
        if min(days) >= 2 and len(set(days)) == len(days):
            return total, slots, days
    raise SystemExit("arithmetic: could not lay out a duration haystack with distinct spells")


def _draw_day_distractors(rng: random.Random, spell_days: list[int], target: int,
                          max_size: int) -> list[int]:
    """Stated day-counts that cannot be recombined into the gold total.

    Day-counts are small integers, so coincidences are far likelier here than with
    two-decimal currency; this is the shape where the rejection loop actually earns its
    keep rather than passing on the first draw.
    """
    gold = [(("spell", i), float(d)) for i, d in enumerate(spell_days)]
    gold_signature = frozenset((key, 1) for key, _ in gold)
    for _ in range(600):
        values = [rng.randint(DAYS_MIN, DAYS_MAX) for _ in range(NUMERIC_DISTRACTORS)]
        pool = gold + [(("distractor", i), float(v)) for i, v in enumerate(values)]
        if _coincident(pool, float(target), max_size, gold_signature, False, EPS_DAYS):
            continue
        if _substitution_near_miss([(k, v, 1) for k, v in gold],
                                   [(("distractor", i), float(v)) for i, v in enumerate(values)],
                                   float(target), NEAR_DAYS):
            continue
        return values
    raise SystemExit("arithmetic: could not draw coincidence-free day distractors")


def _draw_additive(rng, g, distractor_count, gold_slots, distractor_slots, draw,
                   eps, near, signed, max_size, sides=None):
    """Gold and distractor values for a sum or a delta, subject to the combination rule.

    Redraws both sets together rather than only the distractors: with the gold set fixed
    the reachable target is fixed too, and a target that happens to sit in a dense region
    of the subset-sum spectrum would spin the loop forever instead of moving off it.
    """
    for _ in range(400):
        gold_values = [round(draw(), 2) for _ in range(g)]
        distractor_values = [round(draw(), 2) for _ in range(distractor_count)]
        if signed:
            signs = [1 if s == sides[0] else -1 for s in sides]
            high = sum(v for v, s in zip(gold_values, signs) if s == 1)
            low = sum(v for v, s in zip(gold_values, signs) if s == -1)
            if high < low:
                signs = [-s for s in signs]
            target = round(abs(high - low), 2)
            if target < near * 4:
                continue                      # a near-zero delta is indistinguishable noise
        else:
            signs = [1] * g
            target = round(sum(gold_values), 2)

        gold = [((("g", s)), v) for s, v in zip(gold_slots, gold_values)]
        distractors = [((("d", s)), v) for s, v in zip(distractor_slots, distractor_values)]
        gold_signature = frozenset((("g", s), sign) for s, sign in zip(gold_slots, signs))
        if _coincident(gold + distractors, target, max_size, gold_signature, signed, eps):
            continue
        if _substitution_near_miss(
                [(("g", s), v, sign) for s, v, sign in zip(gold_slots, gold_values, signs)],
                distractors, target, near):
            continue
        return gold_values, distractor_values
    raise SystemExit("arithmetic: could not draw coincidence-free values")


def _draw_plans(rng: random.Random) -> list[_Plan]:
    plans = _draw_count_plans(rng, 1)
    plans += _draw_sum_plans(rng, 15)
    plans += _draw_delta_plans(rng, 29)
    plans += _draw_duration_plans(rng, 39)
    return plans


# --------------------------------------------------------------------------------------
# Rendering
# --------------------------------------------------------------------------------------

#: inputs -> band. Two inputs is the floor the duration shape allows; six is the ceiling the
#: generator emits. Bands are diagnostics rather than claims: cells run 6-17 questions, under
#: the n >= 30 an individually citable figure needs.
_INPUT_BANDS = {2: 1, 3: 2, 4: 3, 5: 4, 6: 5}


def _difficulty_band(derivation: dict) -> int:
    """Bands on DISPERSION -- the number of distinct gold sessions the answer is assembled from.

    It used to band on `len(inputs)`, which is a different unit for different shapes and is why the
    ladder pointed backwards. A `count`/`sum`/`delta` input is ONE session; a `duration` input is a
    spell, carrying an opening session and a closing one. So a duration assembled from six gold
    sessions was banded as three, and every duration question landed in bands 1-2 while no other
    shape could reach them. Band 1 was 100% duration.

    That produced a difficulty label anti-correlated with difficulty. Measured on v5, V8 accuracy by
    band ran 0.33 / 0.76 / 1.00 / 1.00 / 1.00 -- the band called EASIEST was where the answer model
    failed two questions in three -- and the cause was not that duration is hard for its dispersion.
    It is that `duration` is a harder OPERATION, and the mis-scaled dial had quietly sorted every
    instance of it into the low bands, so the ladder measured "is this a duration question" while
    claiming to measure dispersion.

    Counting sessions puts every shape on one unit. It does not make duration easier, and it does
    not manufacture a gradient where there is none -- V8 says count/delta/sum score 1.00 at every
    input count from three to six, so dispersion buys no answering difficulty under full context at
    all. What it fixes is the label: the bands now name the quantity they claim to name.
    """
    sessions = set()
    for item in derivation.get("inputs") or []:
        sessions.add(item["session_index"])
        if "from_session_index" in item:
            sessions.add(item["from_session_index"])
    return _INPUT_BANDS.get(len(sessions), 3)


def build(echo, rng: random.Random) -> list[tmc.Question]:
    questions: list[tmc.Question] = []
    for plan in _plans(rng):
        # The echo clause is the only thing that moves with the calibration knob, and it
        # only ever lands on non-gold sessions: gold text is what the question measures and
        # must read the same at every echo setting.
        #
        # Sampled per filler session rather than once per question. Sharing one sample makes
        # the knob a switch instead of a dial: every distractor echoes the same terms, so a
        # term is either in the sample -- present in all ~20 fillers, IDF crushed to nothing,
        # gold's advantage gone entirely -- or absent and untouched. Resampling per session
        # puts each term in a fraction of the fillers proportional to `echo`, which is what
        # makes realised coverage fall smoothly and keeps the four shapes from landing at
        # wildly different coverage for reasons that have nothing to do with the shapes.
        sessions = []
        for index, (user, assistant, is_gold) in enumerate(plan.script):
            if is_gold:
                sessions.append(tmc.make_session(BASE_STAMP, (user, assistant),
                                                 gold_turn=0, tag="gold"))
            else:
                # Per-SHAPE knob, and a per-QUESTION rng.
                #
                # The knob is per shape because one dial for the whole vertical is satisfiable by
                # averaging: `duration` collapsed to 0.083 realised coverage and the dial loosened
                # the other three shapes until the mean returned to 0.700.
                #
                # The rng is per question because `echo_terms` draws `rng.sample` at a size that
                # scales with the knob and returns early at echo<=0. On a shared stream, changing
                # one shape's knob shifts every later question's filler, so the shapes are coupled
                # and no per-shape search can converge. Seeding on the question id makes each
                # sample a function of (question, session, knob) alone.
                shape_echo = echo.get(plan.shape, 0.0) if isinstance(echo, dict) else echo
                echoed = tmc.echo_terms(
                    plan.question, shape_echo,
                    random.Random(f"{plan.qid}:{index}"))  # DevSkim: ignore DS148264 - deterministic corpus generation
                sessions.append(tmc.make_session(BASE_STAMP,
                                                 (user, tmc.weave_echo(assistant, echoed)),
                                                 tag="filler"))
        for session, stamp in zip(sessions, tmc.spread(BASE_STAMP, len(sessions), plan.hours)):
            session.timestamp = stamp

        # Deep-copied out of the plan: plans are memoized across the gate's repeated build
        # calls, so handing the same mutable derivation dict to every build would let anyone
        # who edits one corpus edit all the others it was compared against.
        questions.append(tmc.Question(
            plan.qid, plan.qtype, plan.question, plan.answer,
            sessions[-1].timestamp + timedelta(days=2), sessions,
            deepcopy({"shape": plan.shape, "derivation": plan.derivation,
                  # Every input is an operand: drop one and the arithmetic changes. This was V6's
                  # original scope and is now stated on the question instead of in the runner.
                  "gold_components_load_bearing": True,
                      # Dispersion is this vertical's dial: how many distinct gold sessions the
                      # answer must be assembled from. See _difficulty_band for why that is counted
                      # in SESSIONS rather than in `inputs` -- the two are the same number for
                      # count/delta/sum and differ by a factor of two for duration, and getting it
                      # wrong put every duration question in the bottom two bands and made band 1
                      # entirely duration.
                      "difficulty": _difficulty_band(plan.derivation),
                      # STILL UNVALIDATED, and now for an honest reason rather than a confounded
                      # one. The mis-scaled dial has been fixed and no band is owned by one shape,
                      # but the underlying finding survives the fix and is worth stating: V8 says
                      # count, delta and sum score 1.00 at THREE, FOUR, FIVE and SIX gold sessions
                      # alike, so dispersion buys no answering difficulty at all once the evidence
                      # is in context. It moves BM25 coverage (0.92 -> 0.42) and it moves nothing
                      # else, which makes it a retrieval dial rather than a memory-difficulty one.
                      #
                      # `duration` remains harder than the other three as an OPERATION -- its V8 is
                      # 0.33 against 1.00 -- and no band arrangement changes that. Spreading it
                      # across bands stops the ladder pointing backwards; it does not manufacture a
                      # gradient, and claiming one would be the same error in a new place.
                      "difficulty_dial": "dispersion", "difficulty_validated": False,
                      **plan.extra}),
        ))
    return questions


# --------------------------------------------------------------------------------------
# Vertical validity checks
# --------------------------------------------------------------------------------------

def _same_unit_pool(q: tmc.Question, unit: str) -> list[tuple[tuple, float]]:
    """Every same-unit value in the haystack, recovered by reading the emitted text.

    Deliberately re-read from the corpus rather than carried over from the plan: a check
    that trusts the generator's own record of what it emitted cannot catch the generator
    emitting something else.
    """
    pool: list[tuple[tuple, float]] = []
    if unit == "USD":
        for i, session in enumerate(q.sessions):
            for match in _USD_RE.finditer(session.text()):
                pool.append((("s", i, match.start()), float(match.group(1))))
    else:
        for i, session in enumerate(q.sessions):
            for match in _DAYS_RE.finditer(session.text()):
                pool.append((("s", i, match.start()), float(match.group(1))))
    return pool


def check_arithmetic(questions: list[tmc.Question]) -> list[str]:
    """The vertical's own rules: derivations recompute, inputs are gold, counts decide
    their predicate, and no rival combination reaches the gold value."""
    failures: list[str] = []
    shapes = {SHAPE_COUNT: 0, SHAPE_SUM: 0, SHAPE_DELTA: 0, SHAPE_DURATION: 0}

    for q in questions:
        shape = q.extension.get("shape")
        if shape not in shapes:
            failures.append(f"{q.question_id}: unknown shape {shape!r}")
            continue
        shapes[shape] += 1

        derivation = q.extension.get("derivation")
        if not derivation:
            failures.append(f"{q.question_id}: no derivation block")
            continue
        inputs = derivation["inputs"]
        value = derivation["value"]
        gold = set(q.gold_indices)

        # (c) every input session is gold, and every gold session is an input. The second
        # half is the one V6 leans on: a gold session nobody derives anything from is a
        # session the leave-one-out probe would report as load-bearing when it is not.
        referenced = set()
        for item in inputs:
            referenced.add(item["session_index"])
            if "from_session_index" in item:
                referenced.add(item["from_session_index"])
        stray = sorted(referenced - gold)
        if stray:
            failures.append(f"{q.question_id}: derivation cites non-gold sessions {stray}")
        unused = sorted(gold - referenced)
        if unused:
            failures.append(f"{q.question_id}: gold sessions {unused} feed no derivation input")

        # (b) the recorded value recomputes from the recorded inputs, per operation.
        if shape == SHAPE_COUNT:
            if any(item["value"] != 1 for item in inputs):
                failures.append(f"{q.question_id}: count inputs are not unit events")
            if len(inputs) != value or sum(i["value"] for i in inputs) != value:
                failures.append(f"{q.question_id}: count value {value} != {len(inputs)} inputs")
        elif shape == SHAPE_SUM:
            if abs(sum(i["value"] for i in inputs) - value) > EPS_USD:
                failures.append(f"{q.question_id}: sum does not recompute to {value}")
        elif shape == SHAPE_DELTA:
            minuend = sum(i["value"] for i in inputs if i["side"] == "minuend")
            subtrahend = sum(i["value"] for i in inputs if i["side"] == "subtrahend")
            if abs((minuend - subtrahend) - value) > EPS_USD:
                failures.append(f"{q.question_id}: delta does not recompute to {value}")
            if value <= 0:
                failures.append(f"{q.question_id}: delta is not a positive difference")
        elif shape == SHAPE_DURATION:
            if sum(i["value"] for i in inputs) != value:
                failures.append(f"{q.question_id}: duration does not recompute to {value}")
            for item in inputs:
                span = (q.sessions[item["session_index"]].timestamp
                        - q.sessions[item["from_session_index"]].timestamp)
                if span != timedelta(days=item["value"]):
                    failures.append(
                        f"{q.question_id}: spell {item['from_session_index']}->"
                        f"{item['session_index']} is {span}, derivation says {item['value']} days")

        # (d) counts label every candidate, and the matching ones are exactly the gold set.
        if shape == SHAPE_COUNT:
            candidates = q.extension.get("candidates")
            if not q.extension.get("count_predicate"):
                failures.append(f"{q.question_id}: count without a stated predicate")
            if not candidates:
                failures.append(f"{q.question_id}: count without labelled candidates")
            else:
                matching = {c["session_index"] for c in candidates if c["matches"]}
                if len(matching) != value:
                    failures.append(
                        f"{q.question_id}: {len(matching)} candidates match, value is {value}")
                if matching != gold:
                    failures.append(f"{q.question_id}: matching candidates are not the gold set")
                if len({c["session_index"] for c in candidates}) != len(candidates):
                    failures.append(f"{q.question_id}: duplicate candidate session indices")

                # Re-derive the predicate's extension from the message text and compare it
                # to the labels. Counting the labels only proves the labels agree with each
                # other; the rule ADR §5.3 actually asks for is that *every* candidate event
                # in the haystack is labelled, and an unlabelled near-miss is exactly the
                # event a system and the corpus would silently disagree about.
                vendor = q.extension["count_predicate"].split(" with ", 1)[1]
                ordered, mentioned = set(), set()
                for si, session in enumerate(q.sessions):
                    text = session.text()
                    if ORDER_PHRASE in text:
                        ordered.add(si)
                    # The echo clause lowercases and comma-splits its terms, so it can never
                    # reproduce the vendor's own capitalized name -- scaffolding is not an
                    # event, and this is what keeps it from being counted as one.
                    if vendor in text:
                        mentioned.add(si)
                if {c["session_index"] for c in candidates} != ordered | mentioned:
                    failures.append(
                        f"{q.question_id}: candidate labels do not cover every order or "
                        f"{vendor} mention in the haystack")
                if matching != ordered & mentioned:
                    failures.append(
                        f"{q.question_id}: labelled matches disagree with the predicate as "
                        f"the sessions state it")
        else:
            # (a) no coincident combination, over gold and distractor values mixed.
            unit = derivation["unit"]
            signed = shape == SHAPE_DELTA
            eps = EPS_DAYS if unit == "days" else EPS_USD
            near = NEAR_DAYS if unit == "days" else NEAR_USD
            if shape == SHAPE_DURATION:
                # Gold durations are never stated -- they only exist in the timestamps --
                # so they are recomputed here and joined to the day-counts the distractors
                # do state. That mixed pool is the whole point of the rule.
                pool = [(("spell", i), float(item["value"])) for i, item in enumerate(inputs)]
                gold_entries = [(k, v, 1) for k, v in pool]
                distractors = _same_unit_pool(q, unit)
                pool = pool + distractors
                gold_signature = frozenset((("spell", i), 1) for i in range(len(inputs)))
            else:
                stated = _same_unit_pool(q, unit)
                gold_amounts: list[float] = []
                signs = []
                for item in inputs:
                    gold_amounts.append(item["value"])
                    signs.append(-1 if item.get("side") == "subtrahend" else 1)
                # Bind the derivation to the text: each gold input must actually be stated
                # in the session the derivation names.
                gold_entries = []
                remaining = list(stated)
                for item, sign in zip(inputs, signs):
                    hit = next((e for e in remaining
                                if e[0][1] == item["session_index"]
                                and abs(e[1] - item["value"]) <= EPS_USD), None)
                    if hit is None:
                        failures.append(
                            f"{q.question_id}: input {item['value']} is not stated in session "
                            f"{item['session_index']}")
                        continue
                    remaining.remove(hit)
                    gold_entries.append((hit[0], hit[1], sign))
                pool = stated
                distractors = remaining
                gold_signature = frozenset((k, s) for k, _, s in gold_entries)

            if len(gold_entries) == len(inputs):
                hit = _coincident(pool, float(value), q.g + 1, gold_signature, signed, eps)
                if hit is not None:
                    failures.append(
                        f"{q.question_id}: a rival combination of same-unit values reaches "
                        f"{value} ({sorted(str(k) for k, _ in hit)})")
                if _substitution_near_miss(gold_entries, distractors, float(value), near):
                    failures.append(
                        f"{q.question_id}: a single distractor substitution lands within "
                        f"{near} of {value}")

            # The derivation value is derivable but never stated (ADR §5 leak guard).
            if any(abs(v - float(value)) <= eps for _, v in _same_unit_pool(q, unit)):
                failures.append(f"{q.question_id}: derivation value is stated verbatim")

    # (e) the shape budget the ADR declares.
    expected = {SHAPE_COUNT: 14, SHAPE_SUM: 14, SHAPE_DELTA: 10, SHAPE_DURATION: 12}
    if shapes != expected:
        failures.append(f"shape counts {shapes} != ADR §5.3 {expected}")
    return failures


if __name__ == "__main__":
    tmc.finalise(
        vertical="arithmetic",
        build=build,
        structure=tmc.StructureSpec(
            h_min=15, h_max=25, g_values={3, 4, 5, 6}, gold_position_shuffled=True,
            # Corpus-wide rather than duration-only: 12 of the 50 need it, the other 38 are
            # unaffected by it, and a rule with an exception is a rule with a hole.
            no_absolute_dates=True,
        ),
        generator_tool="tools/gen_typedmemeval_arithmetic.py",
        extra_checks=check_arithmetic,
        # Opts this vertical into per-shape calibration. Its four shapes respond to the echo knob
        # very differently -- `duration` carries a constraint clause that costs it lexical
        # retrievability -- so one dial for the vertical buys a healthy mean over an unhealthy set.
        shape_of=lambda q: (q.extension or {}).get("shape"),
    )
