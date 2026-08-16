#!/usr/bin/env python3
"""
Generates TypedMemEval-Forgetting v1 (ADR-026 §5.5) -- 50 questions.

What the vertical measures: whether a system knows what it *no longer* knows. Three
states have to be held apart, and only a corpus that contains all three can tell them
apart:

    Invalidated facts      20   G=2   gold is "no longer ...", citing the invalidation
    Still-valid controls   15   G=1   gold is the value itself
    Never-known probes     15   G=0   gold is never-known abstention (_abs ids)

Invalidated-only would reward a system that answers "no longer known" to everything, so
the still-valid controls exist to catch over-forgetting; they are paired to an
invalidated question of the *same kind of fact* via `pair_id`, differing only in whether
an invalidation event was ever spoken. The never-known probes catch the opposite
confabulation -- a system that reports a forgetting history it never had. A benchmark
that ran only the invalidated shape would score both failures as success.

The signature failure here is asymmetric retrieval: surfacing the statement but not the
invalidation produces a confident stale answer, which is a fabrication-shaped error and
the most dangerous outcome this family can report. That is why G=2 with the two
components named in `gold_components` and placed 4-15 sessions apart -- the two halves
have to be findable independently for "retrieved statement but not invalidation" to be a
measurable state rather than a story.

Validity rules and why they are what they are:

  * Exactly one invalidation event per fact, and the value vocabulary appears nowhere
    after it. A fact that is invalidated and then quietly re-validated has no single
    correct answer, so a corpus containing one measures the judge's mood.
  * The invalidation is an explicit stated event ("I sold it to a dealer up the coast"),
    never an implication. Inference about whether a fact lapsed is a different skill and
    would confound this vertical's numbers with reasoning ability.
  * Every value is an arbitrary invented two-word name drawn from the rng (V2). A car
    that turns out to be a Honda is answerable without any memory at all.
  * Gold answers are read back out of the emitted turn text (V5) rather than typed, so a
    template that stops emitting its value fails loudly instead of shipping an answer the
    conversations do not support.
  * Never-known probes filter their own distinctive noun out of the calibration echo. The
    echo exists to make filler compete lexically, and weaving the absent noun into filler
    would put the thing being denied into the haystack -- the probe would then be a
    lie about its own corpus.

Run:  python tools/gen_typedmemeval_forgetting.py
"""

from __future__ import annotations

import random  # DevSkim: ignore DS148264 - corpus generation must be replayable under a seed; a CSPRNG cannot be seeded to reproduce a draw, and this selects filler text, not secrets.
import re
from collections import Counter
from datetime import datetime, timedelta

import typedmemeval_common as tmc

SHAPE_INVALIDATED = "invalidated"
SHAPE_STILL_VALID = "still-valid"
SHAPE_NEVER_KNOWN = "never-known"

TYPE_INVALIDATED = "forgetting-invalidated"
TYPE_STILL_VALID = "forgetting-still-valid"
TYPE_NEVER_KNOWN = "forgetting-never-known"

#: How many of the twenty invalidated facts get a still-valid twin (ADR §5.5's 20/15/15).
PAIRED_FACTS = 15

#: Component separation in sessions, per ADR §5.5. Far enough apart that a K=5 retriever
#: cannot pick up both by accident of adjacency, which is the whole diagnostic.
GAP_MIN, GAP_MAX = 4, 15

H_MIN, H_MAX = 15, 25

_BASE = datetime(2026, 1, 6, 9, 15)

# Invented name parts. Deliberately not English brand names: an answer a model can guess
# from priors measures priors (V2). Kept lexically disjoint from the filler vocabulary so
# the no-re-validation check below is a real check and not a coincidence.
STEMS = ["Vantoro", "Kelbrick", "Orrindale", "Feskin", "Draythe", "Halbury", "Oswent",
         "Trellick", "Vintry", "Cambourne", "Neskett", "Ryhope", "Ulverston", "Pellow",
         "Brackmoor", "Wistan", "Quarrell", "Larkmead"]
TAILS = ["Vantage", "Meridian", "Ardent", "Solace", "Quartz", "Verity", "Kindred",
         "Ember", "Pilot", "Reverie", "Anvil", "Cascade"]

# (noun, question, statement setup, invalidation setup, invalidation event)
#
# The statement session always ends "I went with <value>." and the invalidation session
# always ends "I <event>." -- one sentence each, because the gold answers are parsed back
# out of exactly those two clauses. Both sessions name the noun so a lexical retriever can
# reach either half on its own; only the statement carries the value, which is what makes
# stale recall (statement without invalidation) a distinguishable outcome.
FACTS = [
    ("car", "Which car am I driving at the moment?",
     "Signed the papers on the car this morning.",
     "The car has gone.", "sold it to a dealer up the coast"),
    ("gym", "Which gym is my membership with?",
     "Joined a gym at last.",
     "That is the end of the gym.", "cancelled the membership outright"),
    ("dentist", "Which dentist am I registered with?",
     "Registered with a dentist near the station.",
     "The dentist is off my list.", "came off their books when the practice closed"),
    ("broadband provider", "Who is my broadband provider?",
     "Switched broadband this week.",
     "The broadband is dead.", "left the provider mid-contract after the third outage"),
    ("letting agent", "Which letting agent handles my flat?",
     "Put the flat with a letting agent.",
     "The letting agent is out of the picture.", "took the flat off them and manage it myself now"),
    ("storage unit", "Which storage place holds my boxes?",
     "Took a storage unit for the boxes.",
     "The storage unit is empty.", "cleared it out and closed the account"),
    ("bike", "Which bike am I riding?",
     "Picked up a bike at the weekend.",
     "No bike here any more.", "had it stolen from outside the library and never replaced it"),
    ("accountant", "Who does my accounts?",
     "Found an accountant for the returns.",
     "The accounts are unattended.", "ended the engagement after the filing mess"),
    ("phone tariff", "Which phone tariff am I on?",
     "Moved my number onto a new tariff.",
     "That tariff is finished.", "let it lapse when the handset died"),
    ("home insurer", "Who insures the flat?",
     "Insured the flat this morning.",
     "The flat is uninsured.", "let the policy run out and did not renew"),
    ("cleaner", "Who cleans the flat?",
     "Booked a cleaner for fortnightly visits.",
     "Nobody is cleaning here now.", "stopped the visits when the rate went up"),
    ("coffee supplier", "Where do my coffee beans come from?",
     "Set up a standing order for coffee beans.",
     "No beans arriving now.", "stopped the standing order after the last bad batch"),
    ("physio", "Which physio am I seeing?",
     "Started with a physio for the shoulder.",
     "The physio work is over.", "was discharged at the final appointment"),
    ("guitar teacher", "Who teaches me guitar?",
     "Started guitar lessons.",
     "The guitar lessons have stopped.", "gave them up when the teacher moved away"),
    ("window firm", "Which firm is doing my windows?",
     "Signed for the window work.",
     "The window job is dead.", "pulled out of the contract before any work started"),
    ("vet", "Which vet do I use?",
     "Registered the cat with a vet.",
     "We have no vet at present.", "took her off their books when we stopped going"),
    ("hairdresser", "Which hairdresser do I go to?",
     "Found a hairdresser I like.",
     "That hairdresser is over.", "stopped going after the price rise"),
    ("running club", "Which running club am I in?",
     "Joined a running club.",
     "I am clubless for running now.", "let the membership lapse over the winter"),
    ("wine club", "Which wine club am I a member of?",
     "Signed up to a wine club.",
     "The wine deliveries have stopped.", "quit the club after the third duplicate case"),
    ("osteopath", "Which osteopath do I see?",
     "Booked in with an osteopath.",
     "No more osteopath appointments.", "closed the file when the course finished"),
]

# (distinctive token, noun phrase, question). The token must appear in the question and
# nowhere in the haystack -- that absence *is* the gold, and the check below asserts it
# rather than trusting the templates. Chosen disjoint from both the fact nouns and the
# filler vocabulary so "never mentioned" is true of the whole corpus text, not just of the
# session that would have said it.
ABSENT = [
    ("kiteboard", "a kiteboard", "Which kiteboard did I end up with?"),
    ("harpsichord", "a harpsichord", "Who tunes my harpsichord?"),
    ("wetsuit", "a wetsuit", "Which wetsuit did I settle on?"),
    ("tandem", "a tandem", "Which tandem did I buy?"),
    ("kiln", "a kiln", "Which kiln did I put in the garage?"),
    ("drone", "a drone", "Which drone am I flying?"),
    ("greenhouse", "a greenhouse", "Which greenhouse did I order?"),
    ("banjo", "a banjo", "Which banjo did I pick up?"),
    ("treadmill", "a treadmill", "Which treadmill is in the spare room?"),
    ("aquarium", "an aquarium", "Which aquarium did I set up?"),
    ("motorbike", "a motorbike", "Which motorbike am I riding?"),
    ("overlocker", "an overlocker", "Which overlocker did I get for the sewing?"),
    ("espresso", "an espresso machine", "Which espresso machine did I choose?"),
    ("chainsaw", "a chainsaw", "Which chainsaw did I borrow permanently?"),
    ("paraglider", "a paraglider", "Which paraglider did I buy?"),
]

# Same-domain, same-register filler: other people's possessions and arrangements, and --
# importantly -- other things being cancelled, sold and given up. V3 asks for distractors
# that compete rather than a strawman, and in this vertical "competes" means the haystack
# is full of invalidation-shaped events belonging to other facts, so finding the word
# "cancelled" is not a shortcut to the answer. None of them names a fact noun, an absent
# noun, or any value token; the checks below would fail the corpus if one did.
FILLER = [
    ("{who} finally replaced the hallway rug {when}.", "That room needed it."),
    ("{who} gave up the box-set streaming {when}, never watched it.", "Easy money back, then."),
    ("The kettle packed in {when}; we are boiling pans for now.", "Not ideal in the mornings."),
    ("{who} handed the spare printer on to a neighbour {when}.", "Better than it gathering dust."),
    ("We stopped the veg box {when}, too much of it went off.", "Sensible enough."),
    ("{who} is still arguing with the doorbell people {when}.", "Some things take a while."),
    ("The stair carpet is being relaid {when}.", "Mind the dust."),
    ("{who} sold the old lawnmower {when} and borrows ours now.", "Neighbourly enough."),
    ("Our bookshelf finally turned up {when}, three weeks late.", "Late, but here."),
    ("{who} swapped the mattress {when} after all the back trouble.", "Hope it helps."),
    ("The curtains went to the charity shop {when}.", "They had had their day."),
    ("We are getting quotes for the shed roof {when}.", "Worth doing before the wet."),
    ("The kitchen tap started dripping {when}.", "A washer, probably."),
    ("{who} lent us a camping stove {when}.", "Handy for the trip."),
    ("The fridge is making that noise again {when}.", "Worth a look before it goes."),
    ("{who} moved the wheelbarrow into the side passage {when}.", "Out of the way, at least."),
    ("The radio in the kitchen died {when}.", "Silence, or a new one."),
    ("{who} repotted the big houseplant {when}.", "It was root-bound anyway."),
    ("{who} pulled out of the allotment waiting list {when}.", "Ten years was too long to wait."),
    ("The upstairs neighbours ended their parking permit {when}.", "One fewer car on the street."),
]

PEOPLE = ["Rosa", "Dev", "Priya", "Kit", "Marta", "Nadia", "Sam", "Jo", "Ravi", "Theo",
          "Ines", "Bruno"]
WHENS = ["last weekend", "the other week", "over the winter", "a few days ago",
         "at the end of the month", "earlier this week", "after months of dithering",
         "in the end"]

# Read-back patterns. Both anchor at the end of the turn, which is why the gold turns end
# with the clause that carries the payload: a loose match would happily lift the wrong
# clause and the corpus would ship a gold answer that quietly disagrees with its sessions.
#: How a gold statement names its value. One phrasing across every gold session made the verb
#: itself the tell -- the bare token "went" separated gold from filler at AUC 0.775, needing no
#: more than a substring search. Spread across a bank, no single phrasing reaches the share an
#: n-gram has to hold before it can discriminate.
CHOICES = [
    "I went with {value}.", "I settled on {value}.", "I chose {value}.",
    "I picked {value}.", "Ended up with {value}.", "It is {value} now.",
]

#: Derived from CHOICES rather than written alongside it. A hand-kept regex and a hand-kept bank
#: drift, and the failure mode is silent: the parser stops recognising a phrasing, the generator
#: raises, and whoever is in a hurry "fixes" it by narrowing the bank back down to one phrasing.
_VALUE_RE = re.compile(
    "(?:" + "|".join(
        re.escape(choice).replace(r"\{value\}", r"([^.]+)") for choice in CHOICES) + r")\s*$")
_EVENT_RE = re.compile(r"\bI ([^.]+)\.\s*$")


def _arbitrary_value(rng: random.Random) -> str:
    """Two invented words. Arbitrary by construction (V2) -- nothing about the fact makes
    one value likelier than another, so zero-context guessing has nothing to work with."""
    return f"{rng.choice(STEMS)} {rng.choice(TAILS)}"


def _filler_session(rng: random.Random, echoed: list[str], stamp: datetime) -> tmc.Session:
    user, assistant = rng.choice(FILLER)
    user = user.format(who=rng.choice(PEOPLE), when=rng.choice(WHENS))
    return tmc.make_session(stamp, (user, tmc.weave_echo(assistant, echoed)), tag="filler")


def _lay_out(sessions: list[tmc.Session], ordinal: int) -> datetime:
    """Stamps the haystack chronologically and returns a query time after all of it.

    Session order and timestamp order have to agree here for a reason the other verticals
    do not share: the statement-before-invalidation constraint is stated in *both* orders
    (ADR §5.5), so a corpus where they disagree would satisfy one reading and violate the
    other.
    """
    start = _BASE + timedelta(days=3 * ordinal)
    for session, stamp in zip(sessions, tmc.spread(start, len(sessions))):
        session.timestamp = stamp
    return sessions[-1].timestamp + timedelta(days=2, hours=5)


def _derive_value(session: tmc.Session) -> str:
    turn = next(t for t in session.turns if t.has_answer)
    match = _VALUE_RE.search(turn.content)
    if not match:
        raise AssertionError(f"statement session emits no readable value: {turn.content!r}")
    # One capture group per phrasing in the bank; exactly one of them matched.
    return next(group for group in match.groups() if group is not None)


def _derive_event(session: tmc.Session) -> str:
    turn = next(t for t in session.turns if t.has_answer)
    match = _EVENT_RE.search(turn.content)
    if not match:
        raise AssertionError(f"invalidation session emits no readable event: {turn.content!r}")
    return match.group(1)


#: Statement-to-invalidation gap -> band. Cut on the range the generator already emits
#: (GAP_MIN..GAP_MAX), so banding is bookkeeping over existing spread rather than new
#: generation. Diagnostics rather than claims: cells are far under the n >= 30 floor.
_GAP_BANDS = ((5, 1), (7, 2), (10, 3), (13, 4))


def _gap_band(gap: int) -> int:
    for ceiling, band in _GAP_BANDS:
        if gap <= ceiling:
            return band
    return 5


def _invalidated_question(fact, qid: str, pair_id: str | None, ordinal: int,
                          rng: random.Random, echo: float) -> tmc.Question:
    noun, question, statement_setup, invalidation_setup, event = fact
    value = _arbitrary_value(rng)
    echoed = tmc.echo_terms(question, echo, rng)

    h = rng.randint(H_MIN, H_MAX)
    sessions = [_filler_session(rng, echoed, _BASE) for _ in range(h)]

    # The value lives in the statement session and nowhere else, so a system that surfaces
    # only the invalidation cannot reconstruct the stale answer and a system that surfaces
    # only the statement gives one confidently. Both halves are separately diagnostic.
    statement = tmc.make_session(
        _BASE, (f"{statement_setup} {rng.choice(CHOICES).format(value=value)}", ""),
        gold_turn=0, tag=f"statement:{value}")
    invalidation = tmc.make_session(
        _BASE, (f"{invalidation_setup} I {event}.", ""),
        gold_turn=0, tag="invalidation")

    # Gap first, then placement. Drawing the statement position first and the invalidation
    # after it clamps the gap whenever the statement lands late, which piles the corpus up
    # at the 4-session minimum -- and the separation is precisely the variable the
    # asymmetric-retrieval diagnostic is read against.
    gap = rng.randint(GAP_MIN, min(GAP_MAX, h + 1))
    statement_index = rng.randint(0, h + 1 - gap)
    invalidation_index = statement_index + gap
    sessions.insert(statement_index, statement)
    sessions.insert(invalidation_index, invalidation)
    question_date = _lay_out(sessions, ordinal)

    read_value = _derive_value(statement)
    read_event = _derive_event(invalidation)
    answer = (f"No longer valid. Your {noun} was {read_value}, but that is out of date: you "
              f"{read_event}. There is no current {noun} on record.")

    extension = {
        "shape": SHAPE_INVALIDATED,
        "gold_components": [
            {"kind": "statement", "session_index": statement_index},
            {"kind": "invalidation", "session_index": invalidation_index},
        ],
        # Discrimination is this vertical's dial: how far apart the statement and the thing
        # that cancels it sit. The spread was already there and evenly filled (4-15 sessions);
        # it simply stratified nothing, so a run's score could not be read against it.
        "difficulty": _gap_band(gap),
        "difficulty_dial": "discrimination", "difficulty_validated": False,
        "gap_sessions": gap,
    }
    if pair_id:
        extension["pair_id"] = pair_id
        extension["arm"] = "invalidated"
    return tmc.Question(qid, TYPE_INVALIDATED, question, answer, question_date, sessions, extension)


def _control_question(fact, qid: str, pair_id: str, ordinal: int,
                      rng: random.Random, echo: float) -> tmc.Question:
    """The over-forgetting control: same fact kind, same question text, same statement --
    the only difference in the whole haystack is that nothing ever invalidates it.

    Holding the question text identical to its pair is deliberate. If the control asked a
    differently-worded question, a system that answered "no longer known" to one and the
    value to the other could be discriminating on phrasing rather than on evidence, and
    the pair would stop being a control.
    """
    noun, question, statement_setup, _, _ = fact
    value = _arbitrary_value(rng)
    echoed = tmc.echo_terms(question, echo, rng)

    h = rng.randint(H_MIN, H_MAX)
    sessions = [_filler_session(rng, echoed, _BASE) for _ in range(h)]
    statement = tmc.make_session(
        _BASE, (f"{statement_setup} {rng.choice(CHOICES).format(value=value)}", ""),
        gold_turn=0, tag=f"statement:{value}")
    # A re-affirmation, so the control arm carries G=2 exactly as its invalidated twin does.
    # Without it the arms were not comparable: the treatment arm earned partial credit for
    # finding either of two gold sessions while the control's single session scored 0 or 1,
    # and the control came out as the harder retrieval band in the whole family (0.40 against
    # 0.68) -- on the arm whose entire job is to be the easy case. It also does the work the
    # shape wanted anyway: with two mentions and no invalidation, answering requires reading
    # that nothing cancelled the fact rather than simply finding one statement.
    reaffirm = tmc.make_session(
        _BASE, (f"Still the same {noun}, for the record: {value}.", ""),
        gold_turn=0, tag=f"reaffirmation:{value}")
    statement_index = rng.randint(0, h)
    sessions.insert(statement_index, statement)
    sessions.insert(rng.randint(statement_index + 1, h + 1), reaffirm)
    question_date = _lay_out(sessions, ordinal)

    read_value = _derive_value(statement)
    answer = (f"{read_value}. That is still your {noun} — nothing in the record has cancelled "
              f"or replaced it.")
    return tmc.Question(qid, TYPE_STILL_VALID, question, answer, question_date, sessions,
                        {"shape": SHAPE_STILL_VALID, "pair_id": pair_id, "arm": "control"})


def _never_known_question(entry, qid: str, ordinal: int,
                          rng: random.Random, echo: float) -> tmc.Question:
    """A probe with no gold session at all: the corpus never contained the thing asked for.

    The distinctive noun is stripped from the echo terms before any filler is built. The
    echo is what makes filler compete lexically, but here it would compete by *naming the
    absent thing*, and a haystack that mentions the kiteboard cannot support "you never
    mentioned a kiteboard". Losing a little retrieval pressure is the cheaper error.
    """
    token, noun_phrase, question = entry
    absent = set(tmc.tokenize(f"{token} {noun_phrase}"))
    echoed = [t for t in tmc.echo_terms(question, echo, rng) if t not in absent]

    h = rng.randint(H_MIN, H_MAX)
    sessions = [_filler_session(rng, echoed, _BASE) for _ in range(h)]
    question_date = _lay_out(sessions, ordinal)
    # Carried on the session tag, which is generator bookkeeping and never serialized, so
    # the check can assert the absence without the corpus advertising what is missing.
    sessions[0].tag = f"absent:{token}"

    answer = (f"No record at all. You have never mentioned {noun_phrase} in anything you have "
              f"told me, so this is a gap that was never filled rather than something that "
              f"stopped being true.")
    return tmc.Question(qid, TYPE_NEVER_KNOWN, question, answer, question_date, sessions,
                        {"shape": SHAPE_NEVER_KNOWN})


def build(echo: float, rng: random.Random) -> list[tmc.Question]:
    questions: list[tmc.Question] = []
    ordinal = 0

    for i, fact in enumerate(FACTS):
        pair_id = f"tme-for-p{i + 1:02d}" if i < PAIRED_FACTS else None
        questions.append(_invalidated_question(fact, f"tme-for-{i + 1:03d}", pair_id,
                                               ordinal, rng, echo))
        ordinal += 1

    for i in range(PAIRED_FACTS):
        questions.append(_control_question(FACTS[i], f"tme-for-{21 + i:03d}",
                                           f"tme-for-p{i + 1:02d}", ordinal, rng, echo))
        ordinal += 1

    for i, entry in enumerate(ABSENT):
        questions.append(_never_known_question(entry, f"tme-for-{36 + i:03d}_abs",
                                               ordinal, rng, echo))
        ordinal += 1

    return questions


def check_forgetting(questions: list[tmc.Question]) -> list[str]:
    """The vertical's own validity rules, all of them fatal.

    Each one exists because breaking it produces a corpus that still *looks* fine and
    silently reports something other than forgetting: a re-validated fact has two
    defensible answers, a never-known probe whose noun is in the haystack is a
    mislabelled invalidation, and an unpaired control cannot catch over-forgetting.
    """
    failures: list[str] = []
    counts = Counter(q.extension.get("shape") for q in questions)
    expected = {SHAPE_INVALIDATED: 20, SHAPE_STILL_VALID: 15, SHAPE_NEVER_KNOWN: 15}
    for shape, n in expected.items():
        if counts.get(shape, 0) != n:
            failures.append(f"shape {shape}: {counts.get(shape, 0)} questions, ADR §5.5 declares {n}")
    for shape in counts:
        if shape not in expected:
            failures.append(f"undeclared shape {shape!r}")

    pairs: dict[str, list[tmc.Question]] = {}
    for q in questions:
        pid = q.extension.get("pair_id")
        if pid:
            pairs.setdefault(pid, []).append(q)

    for q in questions:
        shape = q.extension.get("shape")

        if shape == SHAPE_INVALIDATED:
            session_tokens = [set(tmc.tokenize(s.text())) for s in q.sessions]
            components = q.extension.get("gold_components", [])
            if len(components) != 2 or [c["kind"] for c in components] != ["statement", "invalidation"]:
                failures.append(f"{q.question_id}: gold_components is not [statement, invalidation]")
                continue
            si, ii = components[0]["session_index"], components[1]["session_index"]
            if si >= ii:
                failures.append(f"{q.question_id}: statement s{si} does not precede invalidation s{ii}")
            if si not in q.gold_indices or ii not in q.gold_indices:
                failures.append(f"{q.question_id}: components {si},{ii} are not both gold sessions")
                continue
            if not (GAP_MIN <= ii - si <= GAP_MAX):
                failures.append(f"{q.question_id}: component distance {ii - si} outside "
                                f"[{GAP_MIN},{GAP_MAX}] (ADR §5.5)")
            if q.sessions[si].timestamp >= q.sessions[ii].timestamp:
                failures.append(f"{q.question_id}: statement is not earlier in time than the invalidation")

            # No re-validation, and no leak: the value is a fact of exactly one session.
            # After the invalidation its vocabulary must be gone, or the question has a
            # second, later, contradicting answer and no gold at all.
            value = q.sessions[si].tag.split(":", 1)[1]
            value_tokens = set(tmc.tokenize(value))
            for j in range(ii + 1, len(q.sessions)):
                if value_tokens & session_tokens[j]:
                    failures.append(f"{q.question_id} s{j}: value vocabulary reappears after "
                                    f"the invalidation (re-validation)")
            for j, tokens in enumerate(session_tokens):
                if j != si and value_tokens & tokens:
                    failures.append(f"{q.question_id} s{j}: value vocabulary leaks outside "
                                    f"the statement session")

        elif shape == SHAPE_NEVER_KNOWN:
            if not q.question_id.endswith("_abs"):
                failures.append(f"{q.question_id}: never-known probe without the _abs suffix")
            if q.gold_indices:
                failures.append(f"{q.question_id}: never-known probe carries gold sessions")
            # Substring rather than token match: "kiteboards" or "kiteboarding" in a filler
            # sentence would defeat the probe just as thoroughly as the bare noun, and a
            # token-equality check would wave both through.
            token = q.sessions[0].tag.split(":", 1)[1]
            for j, session in enumerate(q.sessions):
                if token in session.text().lower():
                    failures.append(f"{q.question_id} s{j}: '{token}' is present in a haystack "
                                    f"that claims never to have heard of it")
        else:
            if q.question_id.endswith("_abs"):
                failures.append(f"{q.question_id}: _abs suffix on a {shape} question")

    if len(pairs) != PAIRED_FACTS:
        failures.append(f"{len(pairs)} pair ids, ADR §5.5 declares {PAIRED_FACTS}")
    for pid, arms in sorted(pairs.items()):
        kinds = sorted(a.extension.get("arm") for a in arms)
        if kinds != ["control", "invalidated"]:
            failures.append(f"{pid}: arms {kinds}, expected one control and one invalidated")
            continue
        control = next(a for a in arms if a.extension["arm"] == "control")
        invalidated = next(a for a in arms if a.extension["arm"] == "invalidated")
        if control.question != invalidated.question:
            failures.append(f"{pid}: the control asks a different question from its invalidated twin")
        if control.answer == invalidated.answer:
            failures.append(f"{pid}: control and invalidated golds do not differ")
    return failures


if __name__ == "__main__":
    tmc.finalise(
        vertical="forgetting",
        build=build,
        structure=tmc.StructureSpec(
            h_min=H_MIN, h_max=H_MAX, g_values={0, 1, 2}, gold_position_shuffled=True,
            no_absolute_dates=False,
        ),
        generator_tool="tools/gen_typedmemeval_forgetting.py",
        extra_checks=check_forgetting,
    )
