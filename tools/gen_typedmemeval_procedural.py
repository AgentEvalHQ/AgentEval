"""TypedMemEval — Procedural (declarative half).

An ordered, causally-constrained procedure learned across sessions: its steps, its preconditions,
and which steps were amended or retired. RECALL ONLY. ADR-029 §10.

WHY THIS EXISTS, measured rather than argued. Across the 485 questions shipped before it, their
gold and their full haystacks, ZERO carry precondition language and ZERO carry causal-necessity
language. `episodic/list-order` recalls an ordered list, but an ARBITRARY one; nothing in the family
tested an order that must HOLD, or a constraint that is neither a step nor a value.

THE TWO RISKS ADR-029 §5 NAMED, AND THE TECHNIQUE FOR EACH:

  (1) V2 is blind in exactly this question class -- it scores only a leak that AGREES with gold, and
      "harm concentrates in ORDERING questions". Inventing entity names does not reach it, because
      in a real procedure the prior is carried by the VERBS (back up before migrating, test before
      deploying), not by the nouns.
      -> INVENTED CAUSALITY. The dependency is stated and arbitrary: "the Quorlory sync consumes the
         Vreskade check's output". No model holds a prior about Quorlory, and the verb pair carries
         none either, because the relation is between two invented artefacts. ADR-026 §5.2 bans
         orderings a model can INFER, not causality as such (ADR-029 §7b).

  (2) The §7.4 structural cap: a question that names the entity it asks about is lexically
      self-retrieving, which drives V9 up and headroom toward zero. "What are the steps of the
      Verrin changeover?" names its own procedure.
      -> THE conditional-branch TECHNIQUE. Put a required hop in a session that names ONLY the
         subordinate entity, never the procedure. Measured there: the reachable half was retrieved
         15/15 and the unreachable half 0/15, and V9 came back 0/15 exactly as predicted.
"""
from __future__ import annotations

import random  # DevSkim: ignore DS148264 - corpus generation must be replayable under a seed; a CSPRNG cannot be seeded to reproduce a draw, and this selects filler text, not secrets.
from datetime import datetime

import typedmemeval_common as tmc

SHAPE_ORDER = "step-order"
SHAPE_PRECOND = "precondition"
SHAPE_AMENDED = "amended-step"
SHAPE_RETIRED = "retired-step"

TYPE_ORDER = "procedural-step-order"
TYPE_PRECOND = "procedural-precondition"
TYPE_AMENDED = "procedural-amended-step"
TYPE_RETIRED = "procedural-retired-step"

ORDER_QUESTIONS = PRECOND_QUESTIONS = AMENDED_QUESTIONS = RETIRED_QUESTIONS = 20

H_MIN, H_MAX = 15, 25
_BASE = datetime(2026, 5, 11, 9, 30)

#: Procedures. Invented, and audited by tools/audit_name_collisions.py like every other entity bank
#: in this family -- MILESTONES shipped with four of six names real, and the shape that asks which
#: came first could be answered by a model that knew the referents.
PROCEDURES = (
    "the Verrin changeover", "the Ostley rollover", "the Marrick handback",
    "the Pellinore cutover", "the Thessaly swap", "the Kirrin migration",
    "the Wendmoor transfer", "the Alderhay switchover", "the Bracknell reissue",
    "the Corviston refit",
)

#: Steps. Also invented, and DELIBERATELY not verb-led: the step is an artefact name, so the
#: ordering prior cannot ride in on a familiar verb pair the way "back up before migrating" does.
STEPS = (
    "the Quorlory sync", "the Vreskade check", "the Zethisk reconciliation",
    "the Ondrey posting", "the Traymoor sweep", "the Draimune audit",
    "the Kelvaryn tally", "the Peskadd reading", "the Zannifer batch",
    "the Muldreth close", "the Ferrasque draw", "the Tovrekk lift",
)

#: The invented dependency, which is the whole construct. Each frame states WHY one step must
#: precede another, and the reason is a property of the two invented artefacts -- so the relation is
#: causally required inside the fiction and unguessable outside it.
DEPENDENCY_FRAMES = (
    "{a} has to run before {b}, because {b} consumes what {a} produces.",
    "{b} cannot start until {a} is done — it reads {a}'s output.",
    "Order matters here: {a} first, then {b}, since {b} is built from {a}'s figures.",
    "{a} feeds {b}, so {a} always goes first.",
    "We run {a} ahead of {b} — {b} has nothing to work on otherwise.",
)

#: Preconditions. A constraint that is neither a step nor a value: it is not part of the sequence
#: and it has no value to recall, which is what makes it unhomed elsewhere in the family.
CONDITIONS = (
    "the Halbrook clearance", "the Nettleford sign-off", "the Rowancross permit",
    "the Ashgate waiver", "the Denbury approval", "the Falstowe licence",
)
PRECOND_FRAMES = (
    "{proc} cannot start until {cond} is in place.",
    "Nothing on {proc} moves before {cond}.",
    "{cond} gates {proc} — no {cond}, no start.",
)
#: The SECOND hop. Names the condition and its own prerequisite, and never the procedure, so a
#: retriever working from the question can reach the first frame and not this one.
SUBCOND_FRAMES = (
    "{cond} depends on {sub} being signed off first.",
    "{cond} is not granted until {sub} clears.",
    "{sub} has to land before {cond} can be issued.",
)
SUBCONDITIONS = (
    "the Marlbeck survey", "the Wrenfield inspection", "the Culvert assay",
    "the Padgate review", "the Ilmery count", "the Sorrell test",
)

AMEND_FRAMES = (
    "{old} is out of {proc} — {new} replaces it, same position.",
    "Change to {proc}: where {old} used to run, it is {new} now.",
    "{proc} has been amended — {new} takes {old}'s place in the sequence.",
)
RETIRE_FRAMES = (
    "{step} has been dropped from {proc} altogether.",
    "We no longer run {step} as part of {proc}.",
    "{step} is out of {proc} — the sequence is shorter by one now.",
)

OPENERS = ("Noting this down.", "For the file.", "Worth recording.", "Quick update.",
           "One more thing.", "Setting this down.")
REPLIES = ("Noted.", "Filed.", "Understood.", "Got it.", "Recorded.", "On the record.")

FILLER = (
    "The parking barrier is sticking again.",
    "Someone has moved the recycling point.",
    "The kettle in the back office is dead.",
    "A delivery came for the floor above.",
    "The window blind on the south side will not sit straight.",
    "There is a new visitor book at the desk.",
)

#: Filler procedures and steps, so the DEPENDENCY and PRECONDITION frames are not gold-only
#: constructions. A frame only gold ever receives is a marker, not content -- the defect this family
#: has been caught by three times, in REPLIES, in CHOICES, and in the re-affirmation bank.
FILLER_PROCEDURES = ("the Yarrowgate routine", "the Selby wind-down", "the Hollin restart")
FILLER_STEPS = ("the Brayfoot log", "the Ennisk pass", "the Wexilune trim", "the Garrow note")
#: DISJOINT FROM `CONDITIONS`, and the check below enforces it at import.
#:
#: The first build drew filler preconditions from the gold bank, and check_procedural refused the
#: corpus on ten questions: "filler names 'the Ashgate waiver', which this question owns". A filler
#: naming the asked condition is a second, competing account of the same gate. Class parity means
#: the same CONSTRUCTION, not the same instances -- the distinction this family had to learn three
#: times (REPLIES, CHOICES, the re-affirmation bank).
FILLER_CONDITIONS = ("the Bicknell consent", "the Oakmere endorsement", "the Larkstone assent",
                     "the Vernham dispensation")

#: How a membership session lists a procedure's steps. ONE BANK, drawn by gold and filler alike.
#:
#: The first build hardcoded a single sentence -- "listing them as they come to mind, not in run
#: order" -- and the separability gate refused the corpus: 84 phrases recurring in 20%+ of questions
#: appeared in 60 GOLD sessions and ZERO distractors. A construction only gold ever receives is a
#: frame, not content, however little it says about the answer. That is the REPLIES/CHOICES/
#: re-affirmation defect for the fourth time in this family, and the fix is the one that worked the
#: previous three: spread the phrasing across a bank, and give filler the same bank.
#:
#: Every frame must state the steps WITHOUT implying they are in run order -- check_procedural
#: refuses a membership session whose listing happens to be the answer.
MEMBERSHIP_FRAMES = (
    "{proc} covers {listed} — listing them as they come to mind, not in run order.",
    "The pieces of {proc}: {listed}. That is the set, not the sequence.",
    "{proc} is made up of {listed}, in no particular order here.",
    "For the record, {proc} involves {listed} — order is written down elsewhere.",
    "What is in {proc}: {listed}. I have not put them in sequence.",
)
if set(FILLER_CONDITIONS) & set(CONDITIONS):
    raise AssertionError("filler condition bank overlaps the gold condition bank")
if set(FILLER_PROCEDURES) & set(PROCEDURES) or set(FILLER_STEPS) & set(STEPS):
    raise AssertionError("filler procedure/step bank overlaps a gold bank")


def _session(rng: random.Random, text: str, echoed: list[str],  # DevSkim: ignore DS148264 - deterministic corpus generation
             gold: bool, tag: str) -> tmc.Session:
    reply = tmc.weave_echo(rng.choice(REPLIES), echoed) if not gold else rng.choice(REPLIES)
    return tmc.Session(
        turns=[tmc.Turn("user", f"{rng.choice(OPENERS)} {text}", has_answer=gold),
               tmc.Turn("assistant", reply)],
        timestamp=_BASE, is_gold=gold, tag=tag)


def _filler(rng: random.Random, echoed: list[str],  # DevSkim: ignore DS148264 - deterministic corpus generation
            avoid: frozenset[str] = frozenset()) -> tmc.Session:
    """Non-gold, drawing from the SAME banks gold draws from, minus what this question owns.

    `avoid` is why this takes a parameter. Filler first used a disjoint step bank, and the
    separability gate refused the corpus a second time: 'traymoor sweep' in 13 gold sessions and
    ZERO distractors, and 60 more like it. Disjoint banks make the step VOCABULARY a gold marker
    even when every frame is shared -- the same lesson `conjunction/_filler` records, where drawing
    attributes from a disjoint bank put "regular printer" in 41 gold sessions and no distractors.

    So filler draws real steps, excluding the ones the asked question is about. Class parity with
    instance divergence: same construction, same vocabulary space, different instances.
    """
    steps = [x for x in STEPS if x not in avoid] + list(FILLER_STEPS)
    procs = [x for x in PROCEDURES if x not in avoid] + list(FILLER_PROCEDURES)
    conds = [x for x in CONDITIONS if x not in avoid] + list(FILLER_CONDITIONS)
    draw = rng.random()
    if draw < 0.20:
        # The MEMBERSHIP construction, about a procedure no question asks about. Without this the
        # frame marks gold perfectly -- see MEMBERSHIP_FRAMES.
        picks = rng.sample(steps, 3)
        text = rng.choice(MEMBERSHIP_FRAMES).format(
            proc=rng.choice(procs),
            listed=f"{', '.join(picks[:-1])} and {picks[-1]}")
        text = f"{text[0].upper()}{text[1:]}"
    elif draw < 0.32:
        # AMENDMENT and RETIREMENT, about steps no question asks about. Fourth application of the
        # same rule inside this one build, and the gate caught every instance: without this branch
        # "of the sequence" sat in 18 gold sessions and no distractors.
        picks = rng.sample(steps, 2)
        text = (rng.choice(AMEND_FRAMES).format(old=picks[0], new=picks[1], proc="the sequence")
                if rng.random() < 0.5
                else rng.choice(RETIRE_FRAMES).format(step=picks[0], proc="the sequence"))
    elif draw < 0.52:
        # A dependency between two steps of a procedure nobody asks about: the ordering frame
        # appears outside gold at the rate gold uses it.
        a, b = rng.sample(steps, 2)
        text = rng.choice(DEPENDENCY_FRAMES).format(a=a, b=b)
    elif draw < 0.50:
        # Likewise the precondition frame.
        text = rng.choice(PRECOND_FRAMES).format(
            proc=rng.choice(procs), cond=rng.choice(conds))
    else:
        text = rng.choice(FILLER)
    return _session(rng, text, echoed, gold=False, tag="filler")


def _lay_out(sessions: list[tmc.Session], ordinal: int) -> datetime:
    """Chronological stamps, and a query time after all of them."""
    stamps = tmc.spread(_BASE, len(sessions) + 1)
    for session, stamp in zip(sessions, stamps):
        session.timestamp = stamp
    return stamps[-1]


def _membership(rng: random.Random, proc: str, steps: list[str],  # DevSkim: ignore DS148264 - deterministic corpus generation
                echoed: list[str]) -> tmc.Session:
    """The one session tying the procedure to its steps -- and it states them UNORDERED.

    This is the half a retriever can reach, because the question names the procedure. The ordering
    lives in the dependency sessions, which name step PAIRS and never the procedure, so reaching
    them means reading this one first. That asymmetry is the shape's difficulty, and it is the
    technique `conjunction/conditional-branch` demonstrated: there the reachable half was retrieved
    15/15 and the unreachable half 0/15.

    Listed in an order that is never the answer -- check_procedural refuses it otherwise, because a
    membership session in answer order IS the answer.
    """
    listed = list(steps)
    for _ in range(20):
        rng.shuffle(listed)  # DevSkim: ignore DS148264 - deterministic corpus generation
        if [listed.index(s) for s in steps] != sorted(listed.index(s) for s in steps):
            break
    text = rng.choice(MEMBERSHIP_FRAMES).format(
        proc=proc, listed=f"{', '.join(listed[:-1])} and {listed[-1]}")
    return _session(rng, f"{text[0].upper()}{text[1:]}", echoed, gold=True, tag="membership")


def _step_order(index: int, echo: float, rng: random.Random) -> tmc.Question:  # DevSkim: ignore DS148264 - deterministic corpus generation
    """An order that must HOLD. Violating it is an error, not merely a wrong answer."""
    proc = PROCEDURES[index % len(PROCEDURES)]
    steps = rng.sample(list(STEPS), 4)
    qid = f"tme-prc-{index + 1:03d}"
    ask = f"In what order do the steps of {proc} run, earliest first?"
    echoed = tmc.echo_terms(ask, echo, random.Random(f"{qid}:{index}"))  # DevSkim: ignore DS148264 - deterministic corpus generation

    owned = frozenset({proc}) | set(steps)
    golds = [_membership(rng, proc, steps, echoed)]
    # One session per ADJACENT pair, so the full order needs every link. Drop one and the chain
    # splits into two segments whose members have no stated relation -- the same construction
    # `temporal/occurrence-order` had to be rebuilt to obtain, for the same reason.
    for a, b in zip(steps, steps[1:]):
        golds.append(_session(rng, rng.choice(DEPENDENCY_FRAMES).format(a=a, b=b),
                              echoed, gold=True, tag="dependency"))

    sessions = golds + [_filler(rng, echoed, owned) for _ in range(rng.randint(H_MIN, H_MAX))]
    rng.shuffle(sessions)  # DevSkim: ignore DS148264 - deterministic corpus generation
    date = _lay_out(sessions, index)
    return tmc.Question(
        qid, TYPE_ORDER, ask,
        f"{', then '.join(steps[:-1])}, then {steps[-1]}.",
        date, sessions,
        {"shape": SHAPE_ORDER, "procedure": proc, "steps": list(steps),
         "gold_components_load_bearing": True,
         # THE ANSWER MUST BE ABOUT THIS PROCEDURE, on the ablation arms only.
         #
         # The component V6 removes here is the membership session -- the only one
         # naming the procedure. A reader that loses it says so ("the conversations do
         # not mention a Verrin changeover by name") and then reports the nearest rival
         # edit, and an equivalence judge scores that as reaching a gold which names no
         # procedure. Eleven of twenty amended-step questions failed V6 that way.
         # `semantic/co-reference` met this first; the grader built for it applies here
         # unchanged, and this vertical only had to declare the entity.
         "answer_must_name": proc})


def _precondition(index: int, echo: float, rng: random.Random) -> tmc.Question:  # DevSkim: ignore DS148264 - deterministic corpus generation
    """A constraint that is neither a step nor a value -- the property ADR-029 SS3 left unhomed."""
    proc = PROCEDURES[index % len(PROCEDURES)]
    cond = CONDITIONS[index % len(CONDITIONS)]
    sub = SUBCONDITIONS[index % len(SUBCONDITIONS)]
    qid = f"tme-prc-{ORDER_QUESTIONS + index + 1:03d}"
    # ASKS FOR BOTH HOPS, because the gold answers both.
    #
    # The first form was "What has to be true before {proc} can start?" and V1 read 15/20. The five
    # failures were all CORRECT for the question as asked -- "The Rowancross permit has to be in
    # place before the Marrick handback can start" -- while the gold also names what the permit
    # itself depends on. The gold named something the question never asked for, which is the same
    # error as `prospective/due-window` and `forgetting/invalidated`, in the same direction: the
    # reader is marked wrong for answering the question put to it.
    ask = f"What has to be true before {proc} can start, and what does that depend on in turn?"
    echoed = tmc.echo_terms(ask, echo, random.Random(f"{qid}:{index}"))  # DevSkim: ignore DS148264 - deterministic corpus generation

    owned = frozenset({proc, cond, sub})
    golds = [
        # Names the procedure AND the condition: reachable from the question.
        _session(rng, rng.choice(PRECOND_FRAMES).format(proc=proc, cond=cond),
                 echoed, gold=True, tag="gate"),
        # Names the condition and its own prerequisite, NEVER the procedure: reachable only by
        # reading the gate first.
        _session(rng, rng.choice(SUBCOND_FRAMES).format(cond=cond, sub=sub),
                 echoed, gold=True, tag="subgate"),
    ]
    sessions = golds + [_filler(rng, echoed, owned) for _ in range(rng.randint(H_MIN, H_MAX))]
    rng.shuffle(sessions)  # DevSkim: ignore DS148264 - deterministic corpus generation
    date = _lay_out(sessions, ORDER_QUESTIONS + index)
    return tmc.Question(
        qid, TYPE_PRECOND, ask,
        f"{cond[0].upper()}{cond[1:]} has to be in place, and that in turn needs {sub}.",
        date, sessions,
        {"shape": SHAPE_PRECOND, "procedure": proc, "condition": cond, "subcondition": sub,
         "gold_components_load_bearing": True})


def _rival_edits(rng: random.Random, kind: str, owned: frozenset[str],  # DevSkim: ignore DS148264 - deterministic corpus generation
                 echoed: list[str], n: int = 2) -> list[tmc.Session]:
    """Edits of the SAME kind about steps this procedure does not contain.

    GUARANTEED, not left to the filler draw, and V6 is why. On the first build
    `amended-step` failed leave-one-out: drop the membership session and the reader answered
    perfectly from the amendment alone, 3 draws of 3, because that haystack happened to contain
    ZERO rival amendments. With only one edit in the corpus there is nothing to disambiguate, so
    membership is never consulted and the shape measures a single lookup.

    With rivals present, an edit names steps and the reader must check WHICH procedure owns them --
    which is what membership is for. Same defect and same repair as
    `conjunction/alias-then-count`'s decoy count and `semantic/co-reference`'s rival facts; the
    third time this family has met it, and the first time it was anticipated by a probe rather than
    by a consumer.
    """
    pool = [x for x in STEPS if x not in owned] + list(FILLER_STEPS)
    out = []
    for _ in range(n):
        picks = rng.sample(pool, 2)
        text = (rng.choice(AMEND_FRAMES).format(old=picks[0], new=picks[1], proc="the sequence")
                if kind == "amendment"
                else rng.choice(RETIRE_FRAMES).format(step=picks[0], proc="the sequence"))
        out.append(_session(rng, text, echoed, gold=False, tag="rival-edit"))
    return out


def _amended_step(index: int, echo: float, rng: random.Random) -> tmc.Question:  # DevSkim: ignore DS148264 - deterministic corpus generation
    """One element of a sequence replaced, the rest intact -- a partial revision, not a new list."""
    proc = PROCEDURES[index % len(PROCEDURES)]
    steps = rng.sample(list(STEPS), 4)
    old = steps[rng.randrange(len(steps))]
    new = rng.choice([s for s in STEPS if s not in steps])
    qid = f"tme-prc-{ORDER_QUESTIONS + PRECOND_QUESTIONS + index + 1:03d}"
    ask = f"One step of {proc} was swapped out. Which one, and what runs in its place now?"
    echoed = tmc.echo_terms(ask, echo, random.Random(f"{qid}:{index}"))  # DevSkim: ignore DS148264 - deterministic corpus generation

    owned = frozenset({proc, new}) | set(steps)
    golds = [
        _membership(rng, proc, steps, echoed),
        # Names the two STEPS and not the procedure, so the amendment is reachable only through
        # membership. Without that asymmetry the question retrieves its own answer in one hop.
        _session(rng, rng.choice(AMEND_FRAMES).format(old=old, new=new, proc="the sequence"),
                 echoed, gold=True, tag="amendment"),
    ]
    rivals = _rival_edits(rng, "amendment", owned, echoed)
    # Inside the haystack budget, so H does not drift with the fix.
    sessions = golds + rivals + [_filler(rng, echoed, owned)
                                 for _ in range(rng.randint(H_MIN, H_MAX) - len(rivals))]
    rng.shuffle(sessions)  # DevSkim: ignore DS148264 - deterministic corpus generation
    date = _lay_out(sessions, ORDER_QUESTIONS + PRECOND_QUESTIONS + index)
    return tmc.Question(
        qid, TYPE_AMENDED, ask,
        f"{old[0].upper()}{old[1:]} was swapped out; {new} runs in its place.",
        date, sessions,
        {"shape": SHAPE_AMENDED, "procedure": proc, "steps": list(steps),
         "retired_step": old, "replacement_step": new,
         "gold_components_load_bearing": True,
         # THE ANSWER MUST BE ABOUT THIS PROCEDURE, on the ablation arms only.
         #
         # The component V6 removes here is the membership session -- the only one
         # naming the procedure. A reader that loses it says so ("the conversations do
         # not mention a Verrin changeover by name") and then reports the nearest rival
         # edit, and an equivalence judge scores that as reaching a gold which names no
         # procedure. Eleven of twenty amended-step questions failed V6 that way.
         # `semantic/co-reference` met this first; the grader built for it applies here
         # unchanged, and this vertical only had to declare the entity.
         "answer_must_name": proc})


def _retired_step(index: int, echo: float, rng: random.Random) -> tmc.Question:  # DevSkim: ignore DS148264 - deterministic corpus generation
    """A POSITION removed from a sequence, not a fact invalidated.

    The distinction from `forgetting/invalidated` is the whole point: there a value is superseded by
    another value, here a step leaves and nothing takes its place, so the sequence is shorter by one.
    A system that stores procedures as opaque blobs keeps the dropped step alive.
    """
    proc = PROCEDURES[index % len(PROCEDURES)]
    steps = rng.sample(list(STEPS), 4)
    dropped = steps[rng.randrange(len(steps))]
    qid = f"tme-prc-{ORDER_QUESTIONS + PRECOND_QUESTIONS + AMENDED_QUESTIONS + index + 1:03d}"
    ask = f"Which step of {proc} is no longer run?"
    echoed = tmc.echo_terms(ask, echo, random.Random(f"{qid}:{index}"))  # DevSkim: ignore DS148264 - deterministic corpus generation

    owned = frozenset({proc}) | set(steps)
    golds = [
        _membership(rng, proc, steps, echoed),
        _session(rng, rng.choice(RETIRE_FRAMES).format(step=dropped, proc="the sequence"),
                 echoed, gold=True, tag="retirement"),
    ]
    rivals = _rival_edits(rng, "retirement", owned, echoed)
    # Inside the haystack budget, so H does not drift with the fix.
    sessions = golds + rivals + [_filler(rng, echoed, owned)
                                 for _ in range(rng.randint(H_MIN, H_MAX) - len(rivals))]
    rng.shuffle(sessions)  # DevSkim: ignore DS148264 - deterministic corpus generation
    date = _lay_out(sessions, ORDER_QUESTIONS + PRECOND_QUESTIONS + AMENDED_QUESTIONS + index)
    return tmc.Question(
        qid, TYPE_RETIRED, ask,
        f"{dropped[0].upper()}{dropped[1:]} — it was dropped from the sequence and nothing "
        f"replaced it.",
        date, sessions,
        {"shape": SHAPE_RETIRED, "procedure": proc, "steps": list(steps), "retired_step": dropped,
         "gold_components_load_bearing": True,
         # THE ANSWER MUST BE ABOUT THIS PROCEDURE, on the ablation arms only.
         #
         # The component V6 removes here is the membership session -- the only one
         # naming the procedure. A reader that loses it says so ("the conversations do
         # not mention a Verrin changeover by name") and then reports the nearest rival
         # edit, and an equivalence judge scores that as reaching a gold which names no
         # procedure. Eleven of twenty amended-step questions failed V6 that way.
         # `semantic/co-reference` met this first; the grader built for it applies here
         # unchanged, and this vertical only had to declare the entity.
         "answer_must_name": proc})


def build(echo, rng: random.Random) -> list[tmc.Question]:  # DevSkim: ignore DS148264 - deterministic corpus generation
    """`echo` is a float or, under per-shape calibration, a dict keyed by shape."""
    def knob(shape: str) -> float:
        return echo.get(shape, 0.0) if isinstance(echo, dict) else echo

    qs = [_step_order(i, knob(SHAPE_ORDER), rng) for i in range(ORDER_QUESTIONS)]
    qs += [_precondition(i, knob(SHAPE_PRECOND), rng) for i in range(PRECOND_QUESTIONS)]
    qs += [_amended_step(i, knob(SHAPE_AMENDED), rng) for i in range(AMENDED_QUESTIONS)]
    qs += [_retired_step(i, knob(SHAPE_RETIRED), rng) for i in range(RETIRED_QUESTIONS)]
    return qs


def check_procedural(questions: list[tmc.Question]) -> list[str]:
    """The vertical's own validity rules, all fatal.

    Each exists because breaking it produces a corpus that still LOOKS fine and silently measures
    something other than a procedure -- which is how three shapes in this family shipped with their
    difficulty manufactured by a defect rather than by their construct.
    """
    failures: list[str] = []
    counts: dict[str, int] = {}
    for q in questions:
        counts[q.extension["shape"]] = counts.get(q.extension["shape"], 0) + 1

    expected = {SHAPE_ORDER: ORDER_QUESTIONS, SHAPE_PRECOND: PRECOND_QUESTIONS,
                SHAPE_AMENDED: AMENDED_QUESTIONS, SHAPE_RETIRED: RETIRED_QUESTIONS}
    for shape, n in expected.items():
        if counts.get(shape, 0) != n:
            failures.append(f"shape {shape}: {counts.get(shape, 0)} questions, ADR-029 SS10 declares {n}")
    for shape in counts:
        if shape not in expected:
            failures.append(f"undeclared shape {shape!r}")

    for q in questions:
        shape = q.extension["shape"]
        proc = q.extension["procedure"]
        gold_idx = set(q.gold_indices)

        # (a) THE SECOND HOP MUST EXIST. Exactly one gold session may name the procedure -- the one
        #     a retriever can reach from the question. If a dependency, amendment or retirement also
        #     named it, the question would retrieve its own answer in one hop and the shape would
        #     measure lexical lookup, which is the SS7.4 structural cap that held
        #     `conjunction/order-then-value` at V9 15/15 for its whole life.
        # CASE-INSENSITIVE, because the prose legitimately capitalises a name at sentence start
        # ("The Ostley rollover covers...") while the bank stores it lowercase. The first version
        # compared exactly and reported 0 gold sessions naming the procedure on questions whose
        # membership session named it in every one -- a check that fails on its own formatting is
        # worse than no check, because the reflex is to loosen the rule it was testing.
        naming = [i for i in gold_idx if proc.lower() in q.sessions[i].text().lower()]
        if len(naming) != 1:
            failures.append(
                f"{q.question_id}: {len(naming)} gold sessions name {proc!r}; exactly one may, or "
                f"the question reaches its own answer without a second hop")

        # (b) NO FILLER MAY NAME THE ASKED PROCEDURE OR ITS STEPS. A filler that did would be a
        #     second, competing account of the same procedure.
        owned = {proc} | set(q.extension.get("steps") or [])
        for k in (q.extension.get("condition"), q.extension.get("subcondition"),
                  q.extension.get("retired_step"), q.extension.get("replacement_step")):
            if k:
                owned.add(k)
        for j, session in enumerate(q.sessions):
            if j in gold_idx:
                continue
            text = session.text()
            hit = next((o for o in owned if o.lower() in text.lower()), None)
            if hit:
                failures.append(f"{q.question_id} s{j}: filler names {hit!r}, which this question owns")

        if shape == SHAPE_ORDER:
            steps = q.extension["steps"]
            # (c) THE ANSWER MUST BE A TOTAL ORDER, and the dependency sessions must state exactly
            #     the adjacent pairs that produce it. A missing link leaves two segments that cannot
            #     be related; an extra one makes a link redundant and V6 will say so.
            deps = [i for i in gold_idx if q.sessions[i].tag == "dependency"]
            if len(deps) != len(steps) - 1:
                failures.append(
                    f"{q.question_id}: {len(deps)} dependency sessions for {len(steps)} steps; "
                    f"a chain of {len(steps)} needs exactly {len(steps) - 1}")
            for a, b in zip(steps, steps[1:]):
                if not any(a.lower() in q.sessions[i].text().lower()
                           and b.lower() in q.sessions[i].text().lower() for i in deps):
                    failures.append(f"{q.question_id}: no session states {a!r} before {b!r}")
            # (d) THE MEMBERSHIP SESSION MUST NOT STATE THE ANSWER. It lists the steps so the
            #     question can reach them; listing them in order would BE the answer.
            member = next((i for i in gold_idx if q.sessions[i].tag == "membership"), None)
            if member is not None:
                text = q.sessions[member].text()
                low = text.lower()
                positions = [low.index(s.lower()) for s in steps if s.lower() in low]
                if positions == sorted(positions):
                    failures.append(
                        f"{q.question_id}: the membership session lists the steps in ANSWER order, "
                        f"so the ordering can be read off without following a single dependency")

        if shape == SHAPE_PRECOND:
            # (e) THE SUBGATE MUST NOT NAME THE PROCEDURE -- it is the unreachable half.
            sub = next((i for i in gold_idx if q.sessions[i].tag == "subgate"), None)
            if sub is not None and proc.lower() in q.sessions[sub].text().lower():
                failures.append(f"{q.question_id}: the subgate names the procedure, collapsing the hop")

        if shape in (SHAPE_AMENDED, SHAPE_RETIRED):
            # (f) The amendment/retirement names the STEPS and never the procedure, for the same
            #     reason (a) exists. Asserted separately because the tag differs.
            tag = "amendment" if shape == SHAPE_AMENDED else "retirement"
            edit = next((i for i in gold_idx if q.sessions[i].tag == tag), None)
            if edit is not None and proc.lower() in q.sessions[edit].text().lower():
                failures.append(f"{q.question_id}: the {tag} names the procedure, collapsing the hop")
            # (g) A retired step must still be IN the membership list. The shape asks which step is
            #     no longer run; if membership never listed it, there is nothing to retire and the
            #     question is answerable by elimination.
            target = q.extension.get("retired_step")
            member = next((i for i in gold_idx if q.sessions[i].tag == "membership"), None)
            if target and member is not None and target.lower() not in q.sessions[member].text().lower():
                failures.append(
                    f"{q.question_id}: {target!r} is retired but was never in the membership list")

    return failures


if __name__ == "__main__":
    tmc.finalise(
        vertical="procedural",
        build=build,
        structure=tmc.StructureSpec(
            h_min=H_MIN, h_max=H_MAX, g_values={2, 4, 5},
            gold_position_shuffled=True,
            no_absolute_dates=False,
        ),
        generator_tool="tools/gen_typedmemeval_procedural.py",
        extra_checks=check_procedural,
        # Per shape from the start. The four shapes take the echo knob in different directions --
        # `step-order` carries five gold sessions and `precondition` two -- and a single knob set by
        # their average is the averaging defect that hid `arithmetic/duration` at 0.083 behind a
        # healthy vertical mean. Seven of the nine existing verticals had to be retro-fitted with
        # this; there is no reason for the tenth to ship needing it.
        shape_of=lambda q: (q.extension or {}).get("shape"),
    )
