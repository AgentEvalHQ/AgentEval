#!/usr/bin/env python3
"""
Shared machinery for the TypedMemEval corpus generators (ADR-026).

Every vertical's generator imports this module, so the corpus format, the BM25
calibration gate, and the structural verification live in exactly one place. A
generator that wants to emit a corpus has to go through `finalise()`, and
`finalise()` refuses to write a corpus that fails its own rules -- that refusal is
the point. A benchmark whose generator can be talked into shipping a corpus that
misses its band is a benchmark whose numbers mean nothing.

Two files per vertical:

  <corpus_id>.json        the corpus itself -- a bare JSON array in LongMemEval's
                          own shape, so the existing loader reads it unchanged.
  <corpus_id>.meta.json   authoring provenance -- calibration values, structural
                          summary, and the V1-V6 probe records.

They are separate on purpose. The corpus hash pins the *questions*; re-running the
LLM probes rewrites metadata and must not move that hash, or every stored run would
report "different corpus" for a corpus nobody touched. The metadata carries the
corpus hash it describes, so a stale pairing is detectable rather than silent.

Usage sketch:

    import typedmemeval_common as tmc

    def build(echo: float, rng: random.Random) -> list[tmc.Question]:
        ...                       # emit questions, distractors echo `echo` of the
        ...                       # question's keywords
    tmc.finalise(
        vertical="prospective",
        build=build,
        structure=tmc.StructureSpec(h_min=12, h_max=20, g_values={1, 2},
                                    gold_position_shuffled=True),
        generator_tool="tools/gen_typedmemeval_prospective.py",
    )
"""

from __future__ import annotations

import hashlib
import json
import math
import random  # DevSkim: ignore DS148264 - corpus generation must be replayable under a seed; a CSPRNG cannot be seeded to reproduce a draw, and this selects filler text, not secrets.
import re
import unicodedata
from collections import Counter
from dataclasses import dataclass, field
from datetime import datetime, timedelta
from pathlib import Path

# --------------------------------------------------------------------------------------
# Constants fixed by ADR-026. Changing any of these changes what the benchmark measures.
# --------------------------------------------------------------------------------------

GENERATOR_VERSION = "1.0.0"

#: The dataset revision these generators emit. Stamped into every result, so two corpora with
#: different questions can never wear the same name.
CORPUS_REVISION = "v3"

#: What this revision replaces and why. A superseded corpus whose replacement does not say what was
#: wrong with it invites someone to keep citing the old numbers.
SUPERSEDES = {
    "revision": "v2",
    "shipped_in": None,
    "reason": (
        "v2 was never released; it existed only on an unmerged branch, and it carried the same class "
        "of defect it was created to fix. Its own V7 check passed because the check was wrong twice "
        "over: it pooled (gold, distractor) pairs ACROSS questions, which diluted a within-question "
        "tell in Forgetting from 0.903 to 0.616, and its refused-feature list covered neither "
        "punctuation nor sentence structure nor phrase markers on gold. Measured properly, v2 gold "
        "was recoverable from Forgetting at AUC 1.000 by the literal substring \"Noted\", at 0.95 on "
        "two verticals by the presence of an em dash, and at 0.990 in WorkingMemory by counting full "
        "stops. v3 equalises every session on all six raw counts the refused features are built "
        "from, so retrieval difficulty moved again and no v1 or v2 number is comparable with a v3 "
        "number. Neither should be cited."
    ),
    "also_supersedes": {
        "revision": "v1",
        "shipped_in": "0.22.0-beta",
        "reason": (
            "Gold carried the BM25 calibration clause 0 times in 501 sessions against ~99% for "
            "distractors, so a one-line string filter isolated every piece of evidence in every "
            "corpus."
        ),
    },
}

#: Reference retrieval budget, in sessions. Ratified uniform across verticals for v1
#: (joint review 2026-08-15, §10 Q4): it approximates the consuming project's real
#: evidence breadth.
K_REF = 5

#: Acceptance band for the BM25 calibration gate: mean realised gold coverage at K_REF.
#: Below the floor the corpus is unanswerable noise; above the ceiling it is saturated
#: and blind to every retrieval mechanism, which is the failure the family exists to fix.
BAND_LOW = 0.50
BAND_HIGH = 0.90

#: The deterministic reference retriever. Named in metadata so the number is re-derivable.
RETRIEVER_ID = "bm25-okapi-k1.5-b0.75"
BM25_K1 = 1.5
BM25_B = 0.75

#: Timestamp format shared with the LongMemEval harness (LongMemEvalTimestamps.TryParse).
DATE_FORMAT = "%Y/%m/%d (%a) %H:%M"

VERTICALS = {
    # vertical      abbrev   question count
    "prospective":  ("pro",  50),
    "episodic":     ("epi",  50),
    "arithmetic":   ("ari",  50),
    "workingmemory": ("wm",  60),
    "forgetting":   ("for",  50),
}

DATA_ROOT = Path(__file__).resolve().parent.parent / "src" / "AgentEval.Memory" / "Data" / "typedmemeval"

#: Words too common to carry retrieval signal; excluded from BM25 and from the
#: lexical-echo machinery so "echo" means real topical overlap, not shared glue.
STOPWORDS = frozenset("""
a an the and or but if then than that this these those there here of in on at to for from by with
without about into over under again further once is are was were be been being am do does did doing
have has had having i me my we our you your he him his she her it its they them their what which who
whom when where why how all any both each few more most other some such no nor not only own same so
too very can will just should now did don t s
""".split())


# --------------------------------------------------------------------------------------
# Corpus model
# --------------------------------------------------------------------------------------

@dataclass
class Turn:
    role: str
    content: str
    has_answer: bool = False

    def to_json(self) -> dict:
        return {"role": self.role, "content": self.content, "has_answer": self.has_answer}


@dataclass
class Session:
    """One conversation session. `is_gold` drives the coverage maths and the labels."""
    turns: list[Turn]
    timestamp: datetime
    is_gold: bool = False
    #: Free-form label for the generator's own bookkeeping; never emitted.
    tag: str = ""

    def text(self) -> str:
        return " ".join(t.content for t in self.turns)


@dataclass
class Question:
    question_id: str
    question_type: str
    question: str
    answer: str
    question_date: datetime
    sessions: list[Session]
    #: The family-owned extension block. `vertical` is filled in by `finalise()`.
    extension: dict = field(default_factory=dict)

    @property
    def gold_indices(self) -> list[int]:
        return [i for i, s in enumerate(self.sessions) if s.is_gold]

    @property
    def g(self) -> int:
        return len(self.gold_indices)

    @property
    def h(self) -> int:
        """Non-gold sessions. ADR §4: H counts non-gold only, never double-counting G."""
        return len(self.sessions) - self.g

    def to_json(self) -> dict:
        for turn in (t for s in self.sessions for t in s.turns):
            if turn.has_answer and not any(turn is t for s in self.sessions if s.is_gold for t in s.turns):
                raise AssertionError(f"{self.question_id}: has_answer turn outside a gold session")
        return {
            "question_id": self.question_id,
            "question_type": self.question_type,
            "question": self.question,
            "answer": self.answer,
            "question_date": self.question_date.strftime(DATE_FORMAT),
            "haystack_sessions": [[t.to_json() for t in s.turns] for s in self.sessions],
            "haystack_dates": [s.timestamp.strftime(DATE_FORMAT) for s in self.sessions],
            "haystack_session_ids": [f"{self.question_id}-s{i:03d}" for i in range(len(self.sessions))],
            "answer_session_ids": [f"{self.question_id}-s{i:03d}" for i in self.gold_indices],
            "typedmemeval": self.extension,
        }


@dataclass
class StructureSpec:
    """What the vertical claims about its own shape. CI re-checks every field."""
    h_min: int
    h_max: int
    g_values: set[int]
    #: False only where the ADR pins gold position by design (WorkingMemory §5.4).
    gold_position_shuffled: bool = True
    #: Verticals whose content must carry no absolute date (ADR V4).
    no_absolute_dates: bool = False
    #: Set when H is the vertical's independent variable (WorkingMemory) so the
    #: haystack-floor assertion is scoped rather than silently waived.
    h_is_independent_variable: bool = False
    #: Separability features this vertical is allowed to fail because the ADR pins them by design.
    #: WorkingMemory states its fact in session 0 on purpose (§5.4), so position separates gold
    #: perfectly and is supposed to -- the construct IS "how far back the memory sits". Named here
    #: so the exemption is a declaration with a reason rather than a threshold quietly raised.
    separability_exempt: frozenset = frozenset()


# --------------------------------------------------------------------------------------
# BM25 -- the deterministic reference retriever behind the calibration gate
# --------------------------------------------------------------------------------------

_TOKEN_RE = re.compile(r"[a-z0-9]+")


def tokenize_raw(text: str) -> list[str]:
    """Every word, keeping stopwords and single characters.

    `tokenize` drops both, which is right for retrieval — glue words carry no topical signal — and
    wrong for measuring vocabulary variety. The words a template repeats are mostly glue, so
    filtering them removes exactly the repetition that distinguishes template filler from gold's
    randomised content. Measured both ways on the same corpus, WorkingMemory's type-token ratio
    separated gold at 0.566 filtered and 0.797 raw; an attacker is not obliged to use our stopword
    list, so the raw figure is the one that counts.
    """
    return _TOKEN_RE.findall(unicodedata.normalize("NFKD", text).lower())


def tokenize(text: str) -> list[str]:
    text = unicodedata.normalize("NFKD", text).lower()
    return [t for t in _TOKEN_RE.findall(text) if t not in STOPWORDS and len(t) > 1]


def bm25_rank(query: str, documents: list[str]) -> list[int]:
    """Document indices ordered best-first under Okapi BM25. Ties break by index, so the
    ranking is a pure function of the text -- a gate that moved with dict ordering would
    not be a gate."""
    doc_tokens = [tokenize(d) for d in documents]
    n = len(doc_tokens)
    if n == 0:
        return []
    avgdl = sum(len(d) for d in doc_tokens) / n or 1.0

    df: Counter[str] = Counter()
    for tokens in doc_tokens:
        df.update(set(tokens))

    q_tokens = tokenize(query)
    scores = []
    for i, tokens in enumerate(doc_tokens):
        tf = Counter(tokens)
        dl = len(tokens)
        score = 0.0
        for term in q_tokens:
            if term not in tf:
                continue
            idf = math.log(1 + (n - df[term] + 0.5) / (df[term] + 0.5))
            freq = tf[term]
            score += idf * (freq * (BM25_K1 + 1)) / (
                freq + BM25_K1 * (1 - BM25_B + BM25_B * dl / avgdl)
            )
        scores.append((-score, i))
    scores.sort()
    return [i for _, i in scores]


def realised_coverage(question: Question, k: int = K_REF) -> float:
    """Share of the question's gold sessions a K-budget BM25 retriever surfaces.

    This is the *floor proxy* of ADR §4: a stronger (embedding) retriever will exceed it.
    It is not a claim about any real system, and the runtime echo exists because it isn't.
    """
    gold = set(question.gold_indices)
    if not gold:
        return 1.0
    ranked = bm25_rank(question.question, [s.text() for s in question.sessions])
    retrieved = set(ranked[:k])
    return len(gold & retrieved) / len(gold)


def structural_ceiling(g: int, k: int = K_REF) -> float:
    """min(1, K/G) -- the best coverage a K-budget system could reach on a G-dispersed
    question. Exactly 1.0 whenever G <= K, which is most of this family; ADR §4 says so
    plainly rather than dressing 1.0 up as a band."""
    return min(1.0, k / g) if g else 1.0


# --------------------------------------------------------------------------------------
# Lexical echo -- the knob the calibration gate turns
# --------------------------------------------------------------------------------------

def echo_terms(question_text: str, echo: float, rng: random.Random) -> list[str]:
    """A sample of the question's own content words, sized by `echo`.

    Distractors that share vocabulary with the question compete with gold for a lexical
    retriever's budget. Turning this up is how a generator drags realised coverage down
    into the band without touching what the question measures -- the distractors stay
    same-domain and answer-free (V3), they just stop being trivially ignorable.
    """
    terms = sorted(set(tokenize(question_text)))
    if not terms or echo <= 0:
        return []
    take = max(1, min(len(terms), round(len(terms) * echo)))
    return rng.sample(terms, take)


ECHO_LEAD = "Also on my mind:"


def weave_echo(sentence: str, terms: list[str]) -> str:
    """Appends echoed vocabulary as a natural-sounding trailing clause.

    Kept to a clause rather than sprinkled through the sentence so a human auditing the
    corpus can see exactly which text is calibration scaffolding and which is content.
    """
    if not terms:
        return sentence
    return f"{sentence} ({ECHO_LEAD} {', '.join(terms)}.)"


# --------------------------------------------------------------------------------------
# Verification -- rules that hold for every vertical (ADR §5, V1-V6 deterministic parts)
# --------------------------------------------------------------------------------------

_YEAR_RE = re.compile(r"\b(19|20)\d{2}\b")
_ISO_DATE_RE = re.compile(r"\b\d{1,4}[-/]\d{1,2}[-/]\d{1,4}\b")
_MONTH_DAY_RE = re.compile(
    r"\b(january|february|march|april|may|june|july|august|september|october|november|december)\s+\d{1,2}\b",
    re.IGNORECASE,
)


def check_no_absolute_dates(questions: list[Question]) -> list[str]:
    """ADR V4, inherited verbatim from the time-grounded corpus: if the conversations
    print dates, a system that stores no time at all still answers temporal questions,
    and the vertical measures nothing."""
    failures = []
    for q in questions:
        for si, session in enumerate(q.sessions):
            for ti, turn in enumerate(session.turns):
                for rx, label in ((_YEAR_RE, "four-digit year"),
                                  (_ISO_DATE_RE, "numeric date"),
                                  (_MONTH_DAY_RE, "month-and-day")):
                    if rx.search(turn.content):
                        failures.append(f"{q.question_id} s{si}t{ti}: {label} in content")
    return failures


def check_structure(questions: list[Question], spec: StructureSpec) -> list[str]:
    failures = []
    for q in questions:
        # A never-known probe (ADR §5.5) has no gold session on purpose: the correct answer
        # is that the corpus never contained it. Requiring evidence here would make the one
        # question type that tests for absent knowledge impossible to express.
        never_known = q.question_id.endswith("_abs")

        if q.g not in spec.g_values:
            failures.append(f"{q.question_id}: G={q.g} outside declared {sorted(spec.g_values)}")
        if not spec.h_is_independent_variable and not (spec.h_min <= q.h <= spec.h_max):
            failures.append(f"{q.question_id}: H={q.h} outside declared [{spec.h_min},{spec.h_max}]")
        if not never_known:
            if not q.gold_indices:
                failures.append(f"{q.question_id}: no gold session")
            elif not any(t.has_answer for i in q.gold_indices for t in q.sessions[i].turns):
                failures.append(f"{q.question_id}: no has_answer turn in any gold session")
        elif q.gold_indices:
            failures.append(f"{q.question_id}: never-known probe carries gold evidence")
        for i, session in enumerate(q.sessions):
            if not session.is_gold and any(t.has_answer for t in session.turns):
                failures.append(f"{q.question_id} s{i}: has_answer on a non-gold session")
        stamps = [s.timestamp for s in q.sessions]
        if stamps != sorted(stamps):
            failures.append(f"{q.question_id}: sessions are not in chronological order")
        if stamps and q.question_date <= stamps[-1]:
            failures.append(f"{q.question_id}: question_date does not follow the last session")

    if spec.gold_position_shuffled:
        # A corpus whose gold always sits first measures position, not retrieval. Checked
        # over the corpus rather than per question -- one question cannot be "shuffled" --
        # and only over questions that have gold at all.
        with_gold = [q for q in questions if q.gold_indices]
        firsts = sum(1 for q in with_gold if q.gold_indices[0] == 0)
        if with_gold and firsts / len(with_gold) > 0.5:
            failures.append(
                f"gold sits in session 0 for {firsts}/{len(with_gold)} questions -- "
                f"position artefact (ADR §4 layer 1)")
    return failures


def check_answer_not_verbatim(questions: list[Question]) -> list[str]:
    """The gold answer must never appear as a literal in the conversations.

    V5 says gold is *derived* from the sessions; a verbatim copy would make the question a
    string search rather than a memory task, and would defeat the prompt-leak guard the
    C# side runs for the same reason.
    """
    failures = []
    for q in questions:
        haystack = " ".join(s.text() for s in q.sessions).lower()
        answer = q.answer.strip().lower()
        if len(answer) >= 25 and answer in haystack:
            failures.append(f"{q.question_id}: gold answer appears verbatim in the haystack")
    return failures



def check_echo_parity(questions: list[Question]) -> list[str]:
    """Refuses a corpus where the calibration clause separates gold from distractors.

    This is the check that would have caught the first shipped build, where gold carried the clause
    in 0 of 501 sessions and distractors in ~99%. Parity is required per question, not per corpus:
    a corpus-wide average can look balanced while every individual question is still separable.
    """
    failures = []
    for q in questions:
        gold = [s for s in q.sessions if s.is_gold]
        other = [s for s in q.sessions if not s.is_gold]
        if not gold or not other:
            continue
        gold_rate = sum(ECHO_LEAD in s.text() for s in gold) / len(gold)
        other_rate = sum(ECHO_LEAD in s.text() for s in other) / len(other)
        if abs(gold_rate - other_rate) > 0.5:
            failures.append(
                f"{q.question_id}: the calibration clause is a gold tell "
                f"(gold {gold_rate:.2f} vs distractors {other_rate:.2f})")
    return failures


#: Syllables for invented names given to distractor sessions. Arbitrary by construction, so a name
#: here can never accidentally be a question's answer.
_NAME_HEADS = ["Bram", "Calder", "Denn", "Farrow", "Halden", "Ithe", "Kesse", "Lorrin", "Marth",
               "Norra", "Pell", "Quenn", "Rusk", "Sable", "Thorne", "Velle", "Wend", "Yarrow"]
_NAME_TAILS = ["qvist", "zell", "xby", "vund", "kjar", "wraith", "zorn", "quorn", "phex", "yrn"]
#: Grammatical padding. The first version emitted a bag of words to hit an exact character count,
#: which met the separability target and produced corpora ending in "Still Given These these later
#: since those those aside" — a corpus nobody would read twice, and one no reviewer would trust.
#: Sentences instead, chosen to fit, with a tolerance rather than an exact landing.
_PAD_PLAIN = [
    "It had been on my mind for a while before that.",
    "Nothing much came of it either way.",
    "I keep meaning to write these things down sooner.",
    "That is roughly where it was left.",
    "It seemed worth mentioning at the time.",
    "There was no particular reason for the delay.",
    "I had half expected it to go the other way.",
    "It made more sense once I stopped thinking about it.",
    "The whole thing took less effort than I feared.",
    "I will probably feel differently about it next week.",
]
#: Same register, one invented name each, so capitalisation can be raised without wrecking the prose.
_PAD_NAMED = [
    "{n} had said something similar a while back.",
    "I should really ask {n} about it.",
    "{n} would have an opinion, naturally.",
    "That is exactly the sort of thing {n} notices.",
    "{n} mentioned it again the other day.",
]
#: For sessions whose gold sets a high capitalisation target: two names in the same breath, which
#: is still a sentence rather than a heap of capitalised tokens.
_PAD_DENSE = [
    "{n} and {m} had both said as much.",
    "That came up with {n}, and again with {m}.",
    "Between {n} and {m} the story was consistent.",
]
#: A sentence that costs almost no characters. A session that starts long but sparsely punctuated
#: has to reach the sentence target without overshooting the character target; without these the
#: only way to add a sentence was to add a full clause, which pushed gold past its length target and
#: separated it at 0.979 in the other direction.
_PAD_SHORT = [
    "Anyway.", "So it goes.", "Fine.", "Right.", "Either way.",
    "Still.", "Even so.", "No matter.", "As ever.", "Quite.",
]
#: Lower-case continuations. A tail lengthens a sentence without starting a new one and without
#: introducing a capital — the only lever that moves characters and punctuation while leaving the
#: sentence and capital counts alone.
#: Deliberately spread across lengths, from a couple of words to most of a clause. Only one tail is
#: allowed per sentence, so a single one has to be able to close a large character gap on its own —
#: when they were all short, the search stacked them instead, and 15% of turns ended up reading
#: "…, for what it is worth, at least for now, in the end."
_PAD_TAILS = [
    "more or less", "as it turned out", "in the end", "for what it is worth",
    "though it hardly mattered", "at least for now", "one way or another",
    "and that was fine by me", "as far as any of it went", "or so it seemed at the time",
    "roughly how these things tend to go",
    "though I doubt anyone else gave it a second thought",
    "and that seemed a reasonable enough place to leave it",
    "though it took rather longer to sort out than it should have",
    "though by then the whole business had stopped being interesting",
    "and nobody has raised it with me since, so far as I can remember",
]
#: A tail that carries a name. Capitals otherwise arrive only at the start of a sentence or inside
#: one, so a session a single capital short of its target had no way to close that gap: every
#: candidate that supplied a capital also supplied a sentence, and trading a one-capital error for a
#: one-sentence error is not a trade worth making. That left WorkingMemory's gold one capital light
#: in almost every question and separable on capitals-per-character at 0.760.
_PAD_NAME_TAILS = [
    "as {n} put it", "according to {n}", "and {n} says the same", "or so {n} reckons",
]


def _role_text(session: Session, role: str) -> str:
    """The text one speaker contributes to a session."""
    return " ".join(t.content for t in session.turns if t.role == role)


def _slot_index(session: Session, role: str, ordinal: int) -> int | None:
    """Index of a session's `ordinal`-th turn in the given role, or None if it has none."""
    seen = 0
    for i, turn in enumerate(session.turns):
        if turn.role != role:
            continue
        if seen == ordinal:
            return i
        seen += 1
    return None


def _slot_text(session: Session, role: str, ordinal: int) -> str:
    index = _slot_index(session, role, ordinal)
    return session.turns[index].content if index is not None else ""


def _pad_target(session: Session, role: str = "assistant") -> int:
    """Index of the turn padding of the given role lands on.

    Never a turn carrying the answer. The old rule was "the last assistant turn", justified as
    keeping padding out of an answer-bearing USER turn -- true as far as it went, and Episodic puts
    `has_answer` on the ASSISTANT turn for its attribution and assistant-stated shapes, so 26 of its
    117 answer-bearing turns were being handed an acknowledgement and a sentence of invented names.
    That is not merely untidy: the attribution shape's whole design is that the statement clause is
    byte-identical whichever speaker carries it, and "X and Y had both said as much" appended to a
    who-said-it question's evidence is a cue in the answer itself.
    """
    free = [i for i, turn in enumerate(session.turns)
            if turn.role == role and not turn.has_answer]
    if free:
        return free[-1]
    session.turns.append(Turn(role, ""))
    return len(session.turns) - 1


def _ensure_padding_turns(session: Session) -> None:
    """Gives every session a free user turn and a free assistant turn to absorb padding.

    Unconditional, and that is the whole point. Adding the exchange only where a free turn was
    missing meant adding it only to gold — gold is where `has_answer` lives — and turn count is
    itself a refused feature, so it traded a length tell for a turn-count tell at AUC 1.000. The
    gate caught it immediately, which is the argument for having the gate. With the exchange present
    on every session, both roles can be padded to their own targets, no answer-bearing turn is ever
    written to, and the turn count moves by the same amount everywhere.
    """
    for role in ("user", "assistant"):
        session.turns.append(Turn(role, ""))


def _normalise_turn_counts(question: Question) -> None:
    """Gives every session in a question the same number of turns, in the same roles.

    Adding a fixed exchange to every session preserves whatever difference was already there:
    Episodic's assistant-stated and attribution gold carry one more base turn than filler, so
    27 gold sessions ended on 5 turns against 989 distractors on 4. Pooled turn_count read a
    harmless 0.615 while the count separated gold PERFECTLY in 54% of questions -- the exact
    shape of defect the bimodality rule exists to catch, and it was structural rather than
    incidental.
    """
    # Per ROLE, not just per total. Equalising the total alone leaves the composition free:
    # gold ended (u,a,u,a,a) and filler (u,a,u,a,u) -- the same five turns, different roles --
    # so gold owned an ('assistant', 2) slot that no distractor had. A slot only one side
    # possesses cannot be equalised by a per-slot pass, and it separated gold outright.
    for role in ("user", "assistant"):
        target = max(sum(1 for t in s.turns if t.role == role) for s in question.sessions)
        for session in question.sessions:
            while sum(1 for t in session.turns if t.role == role) < target:
                session.turns.append(Turn(role, ""))


#: Lower-case continuations. A tail lengthens a sentence without starting a new one and without
#: introducing a capital, which is what makes the three axes independently solvable: sentence count
#: is set by how many sentences are emitted, capitals by how many of them carry names, and the
#: character count is then closed with these. Without a lever that moves length alone, hitting the
#: character target means emitting more sentences, which moves the sentence count off its target --
#: the coupling that let gold end up systematically the shortest session in its own haystack.
#: Assistant acknowledgements, drawn for EVERY session rather than written per template.
#: Generators naturally give gold a considered reply ("Noted — I have that down.") and filler a
#: content-specific one, which made the reply itself the marker: the single token "noted" found
#: Forgetting's gold at AUC 1.000, and "Noted" or "Understood" together did it exactly. That is the
#: v1 calibration clause again with a different string, so the fix is the same one — one bank, drawn
#: for gold and filler alike, so the acknowledgement carries no information about which is which.
REPLIES = [
    "Noted.", "Logged.", "Got it.", "Recorded.", "Filed.", "Understood.",
    "Right you are.", "Fair enough.", "Makes sense.", "Duly noted.",
    "That is on the record.", "Fine by me.",
]


def equalise_reply(questions: list[Question], rng: random.Random) -> None:  # DevSkim: ignore DS148264 - deterministic corpus generation, not a security function
    """Gives every session's assistant turn an acknowledgement from one shared bank.

    Same argument as equalise_echo, applied to a different marker. Parity has to be structural: a
    generator that writes gold's reply in one place and filler's in another will drift apart again
    the moment someone edits one of them, and the drift is invisible until a classifier finds it.
    """
    for question in questions:
        for session in question.sessions:
            index = _pad_target(session)
            existing = session.turns[index].content.strip()
            reply = rng.choice(REPLIES)  # DevSkim: ignore DS148264 - deterministic corpus generation
            session.turns[index].content = f"{reply} {existing}".strip()


_PAD_MAX_ROUNDS = 8
#: Cap on sentences one padding call may emit; a backstop on the greedy search, not a target.
_PAD_MAX_PIECES = 24
#: Tails one sentence may carry. Two is prose; six is what the search does when nothing stops it.
_PAD_MAX_TAILS_PER_SENTENCE = 2
#: Per-session jitter added to each axis of the target, sized at roughly one padding clause. Its job
#: is to decorrelate the unavoidable whole-clause overshoot from where a session started.
_PAD_JITTER = {"chars": 55, "caps": 4, "sentences": 1, "punct": 4, "tokens": 10, "types": 8}
#: Head-room added to every axis of a turn slot's target, expressed in characters and applied
#: proportionally to the other axes so the target keeps the proportions of a real turn. Enough
#: that every turn needs some padding: a target AT the maximum leaves the largest turn
#: untouched, and an untouched turn keeps whatever made it an outlier.
_PAD_SLOT_MARGIN_CHARS = 70



#: The axes padding balances, and what one unit of each is worth in characters when they are traded
#: off. Every refused numeric feature is a ratio of two of these, so bringing the RAW counts to a
#: common target brings every ratio built from them to a common value as well. Equalising the counts
#: without equalising them jointly is what kept relocating the tell: matching characters alone left
#: capitals per character separating at 0.89, and matching capitals alone left sentences per
#: character separating at 1.000.
_PAD_AXES = ("chars", "caps", "sentences", "punct", "tokens", "types")
_PAD_WEIGHTS = {"chars": 1.0, "caps": 22.0, "sentences": 40.0,
                "punct": 30.0, "tokens": 8.0, "types": 8.0}


def _profile_vector(text: str) -> dict[str, int]:
    """Every raw count the refused features are built from."""
    tokens = tokenize_raw(text)
    return {
        "chars": len(text),
        "caps": sum(c.isupper() for c in text),
        "sentences": sum(text.count(c) for c in ".!?"),
        "punct": sum(not (c.isalnum() or c.isspace()) for c in text),
        "tokens": len(tokens),
        "types": len(set(tokens)),
    }


def _pad_greedy(deficit: dict[str, int],
                rng: random.Random) -> str:  # DevSkim: ignore DS148264 - deterministic corpus generation
    """Chooses padding one sentence at a time, closing whichever axis is furthest from its target.

    The formula this replaced computed a sentence mix from the capital budget and hoped the other
    counts followed. They did not, and could not: the axes are coupled through the sentence bank, so
    fixing one analytically moved another. Successive attempts fixed length and broke sentence
    count, then fixed sentence count and broke capitalisation density, each time relocating the tell
    rather than removing it.

    So the mix is searched rather than derived. Each candidate is scored by the residual it leaves
    across every axis at once, overshoot counted as harshly as shortfall -- a session padded PAST
    the common target separates exactly as well as one left short of it, because the metric folds
    direction. Whichever candidate leaves the least residual is appended, and the loop re-measures.
    """
    def name() -> str:
        return f"{rng.choice(_NAME_HEADS)} {rng.choice(_NAME_HEADS)}{rng.choice(_NAME_TAILS)}"

    def cost(remaining: dict[str, float]) -> float:
        return sum(abs(remaining[axis]) * _PAD_WEIGHTS[axis] for axis in _PAD_AXES)

    def minus(remaining: dict[str, float], text: str, joined: bool) -> dict[str, float]:
        added = _profile_vector((" " if joined else "") + text)
        return {axis: remaining[axis] - added[axis] for axis in _PAD_AXES}

    pieces: list[str] = []
    tailed: Counter[int] = Counter()
    remaining = {axis: float(deficit.get(axis, 0)) for axis in _PAD_AXES}

    for _ in range(_PAD_MAX_PIECES):
        if all(remaining[axis] <= 0 for axis in _PAD_AXES):
            break
        candidates = [
            rng.choice(_PAD_SHORT),
            rng.choice(_PAD_PLAIN),
            rng.choice(_PAD_NAMED).format(n=name()),
            rng.choice(_PAD_DENSE).format(n=name(), m=name()),
        ]
        best_cost, best_action = cost(remaining), None
        for candidate in candidates:
            after = cost(minus(remaining, candidate, bool(pieces)))
            if after < best_cost:
                best_cost, best_action = after, ("append", candidate)
        # A tail lengthens an existing sentence: characters and punctuation without a new sentence
        # and without a capital, the only lever that moves those axes apart.
        for index, existing in enumerate(pieces):
            # At most two tails per sentence. Unbounded, the search stacks them — each one still
            # reduces the residual — and 15% of turns ended up with two or more, the worst running
            # to six: "…, for what it is worth, at least for now, in the end." Filler nobody can
            # read without wincing invites the reader to distrust the questions too. One turned out
            # to be too tight a budget for the shape targets; two reads perfectly naturally.
            if not existing.endswith(".") or tailed[index] >= _PAD_MAX_TAILS_PER_SENTENCE:
                continue
            candidate_tails = [rng.choice(_PAD_TAILS) for _ in range(4)]
            candidate_tails.append(rng.choice(_PAD_NAME_TAILS).format(n=name()))
            for tail in candidate_tails:
                after = cost(minus(remaining, f", {tail}", False))
                if after < best_cost:
                    best_cost, best_action = after, ("tail", index, tail)

        if best_action is None:      # nothing improves the residual; stop rather than pad for its own sake
            break
        if best_action[0] == "append":
            remaining = minus(remaining, best_action[1], bool(pieces))
            pieces.append(best_action[1])
        else:
            _, index, tail = best_action
            remaining = minus(remaining, f", {tail}", False)
            pieces[index] = f"{pieces[index][:-1]}, {tail}."
            tailed[index] += 1

    rng.shuffle(pieces)  # DevSkim: ignore DS148264 - corpus generation must be replayable under a seed; a CSPRNG cannot be seeded to reproduce a draw, and this shuffles neutral padding sentences, not secrets.
    return " ".join(pieces)


def equalise_shape(questions: list[Question], rng: random.Random) -> None:  # DevSkim: ignore DS148264 - deterministic corpus generation, not a security function
    """Pads every TURN toward a common shape, so no cheap feature can tell gold from filler.

    Gold states an arbitrary named fact, because V2 requires the answer to be unguessable; filler
    states everyday things. The consequence, measured on the shipped corpora, was that gold carried
    visibly more capital letters, fewer sentences and less text than its distractors -- Forgetting
    separated on raw length at AUC 0.903 and WorkingMemory on a count of full stops at 1.000. A
    classifier that counts full stops finds the evidence without reading a word of it.

    Three things follow, and each was learned by measuring the previous fix.

    Padding only the filler does not work: the metric folds direction, so overshooting separates
    exactly as well as undershooting. Both sides are padded toward a common target.

    The target is a VECTOR, not a length -- characters, capitals, sentences, punctuation, tokens and
    distinct tokens together -- because every refused feature is a ratio of two of those counts, and
    matching them one at a time just moves the tell into whichever ratio was not being matched.

    And the target is per TURN SLOT, not per session. Padding lands on one turn, so equalising the
    pooled session leaves every other slice exactly as the generator wrote it: with the pooled totals
    balanced, gold was still recoverable from user-turn length alone at AUC 1.000, and after the
    role totals were balanced too, from the FIRST user turn's length at 1.000 again. The
    evidence-bearing turn is the one that differs, so it is the one that has to be matched.

    Each target is the elementwise maximum over the sessions that have that slot, scaled up by one
    margin on every axis. Padding only ever adds, so a target AT the maximum would leave the largest
    turn untouched -- and an untouched turn keeps whatever made it an outlier, which is the tell
    surviving its own fix. Scaling every axis by a single factor keeps the target's proportions
    those of a turn that actually exists rather than inventing a shape nothing in the corpus has.
    """
    for question in questions:
        if len(question.sessions) < 2:
            continue

        for session in question.sessions:
            _ensure_padding_turns(session)
        _normalise_turn_counts(question)

        # Targets are per TURN SLOT -- (role, position within that role) -- not per session and not
        # per role. Each narrowing was forced by the last one failing. Equalising the pooled session
        # left the user turns untouched and WorkingMemory's gold recoverable from user-turn length
        # at AUC 1.000. Equalising each role then left the FIRST user turn untouched, because the
        # padding went to the free turn at the end, and WorkingMemory's gold was recoverable from
        # first-user-turn length at 1.000 again. The evidence-bearing turn is the one that differs,
        # so it is the one that has to be matched; anything coarser hides the difference in an
        # aggregate while leaving it in plain sight in the slice.
        slots: dict[tuple[str, int], list[Session]] = {}
        for session in question.sessions:
            seen: Counter[str] = Counter()
            for turn in session.turns:
                slots.setdefault((turn.role, seen[turn.role]), []).append(session)
                seen[turn.role] += 1

        for (role, ordinal), members in slots.items():
            vectors = [_profile_vector(_slot_text(s, role, ordinal)) for s in members]
            peak_slot = {axis: max(v[axis] for v in vectors) for axis in _PAD_AXES}
            slot_margin = ((peak_slot["chars"] + _PAD_SLOT_MARGIN_CHARS)
                           / max(1, peak_slot["chars"]))
            slot_target = {axis: int(round(peak_slot[axis] * slot_margin)) for axis in _PAD_AXES}

            for session in members:
                index = _slot_index(session, role, ordinal)
                if index is None:
                    continue
                # Per-slot jitter, for the same reason as before: padding is whole clauses, so the
                # overshoot is granular, and an overshoot that tracks where a turn started is a
                # signature. Jittering makes it a coin flip.
                jitter = {axis: rng.randint(0, _PAD_JITTER.get(axis, 0)) for axis in _PAD_AXES}  # DevSkim: ignore DS148264 - deterministic corpus generation
                for _ in range(_PAD_MAX_ROUNDS):
                    current = _profile_vector(session.turns[index].content)
                    deficit = {axis: slot_target[axis] + jitter[axis] - current[axis]
                               for axis in _PAD_AXES}
                    if all(value <= 0 for value in deficit.values()):
                        break
                    addition = _pad_greedy(deficit, rng)
                    if not addition:
                        break
                    joiner = " " if session.turns[index].content else ""
                    session.turns[index].content += joiner + addition




# --------------------------------------------------------------------------------------
# V7 -- adversarial separability
# --------------------------------------------------------------------------------------

#: A SHAPE feature must not separate gold from distractors better than this. 0.5 is chance; 1.0 is
#: a perfect tell. The clause defect that survived every other check scored ~0.99 here.
SEPARABILITY_MAX_AUC = 0.75

#: Shape artifacts: properties a session has regardless of what it is about. A classifier using one
#: of these finds the evidence without reading it, which is the failure V7 exists to refuse.
SEPARABILITY_REFUSED_FEATURES = frozenset({
    "session_length_chars", "turn_count", "position_in_haystack",
    "digit_density", "uppercase_density",
    # Added in corpus v3. The v2 corpora passed on the five features above while gold remained
    # recoverable from Forgetting at AUC 1.000 by the literal substring "Noted" and at 0.95 on two
    # verticals by the presence of an em dash -- the gold acknowledgement templates used punctuation
    # and vocabulary that filler never used. Refusing only the five shape features we happened to
    # think of first reproduces the v1 failure with a different marker, so the families a generator
    # can accidentally make gold-only -- punctuation, sentence structure, turn shape, vocabulary
    # variety -- are refused as families rather than as the specific glyphs that caught us out.
    "sentence_count", "punctuation_density", "em_dash_density",
    "mean_turn_chars", "type_token_ratio",
    # Both phrase directions. A phrase carried by gold and not by filler marks the evidence; one
    # carried by filler and not by gold marks the evidence by its ABSENCE, which is neither better
    # nor different — it is the v1 calibration clause exactly. Prospective sat at 0.990 on the
    # filler side while every other feature was comfortable, and the only thing keeping that off
    # the gate was a carve-out with no ceiling in it.
    "gold_marker_ngram", "boilerplate_ngram",
    # ...and each of those axes again, sliced by speaker. See separability_report for why.
    *(f"{role}_{axis}" for role in ("user", "assistant")
      for axis in ("length_chars", "uppercase_density", "sentence_count",
                   "punctuation_density", "type_token_ratio", "mean_turn_chars")),
    # ...and the first turn of each role on its own, which is where the evidence actually sits.
    *(f"first_{role}_{axis}" for role in ("user", "assistant")
      for axis in ("length_chars", "uppercase_density", "sentence_count")),
})

#: Nothing is measured-but-not-refused any more. The carve-out that used to live here excused
#: phrase recurrence on the grounds that template filler repeats itself and real variety needs a
#: corpus revision rather than a check. The reasoning was sound and the consequence was not: an
#: exemption with no ceiling cannot tell 0.60 from 0.99, and it sat quietly over a Forgetting
#: corpus whose gold the substring "Noted" recovered outright. Both phrase directions now refuse
#: at the same bar as every other feature, and the corpora meet it.
SEPARABILITY_REPORTED_FEATURES: frozenset[str] = frozenset()

#: An n-gram counts as boilerplate -- and therefore as a candidate tell -- only when it recurs
#: across this share of the corpus's questions. Anything rarer is question CONTENT: "Riverside
#: Fitness" appears in one question's gold and nowhere else, and flagging that would be flagging
#: the corpus for being about something.
BOILERPLATE_MIN_QUESTION_SHARE = 0.20


def _auc_within(strata: list[tuple[list[float], list[float]]], fold: bool = True) -> float:
    """AUC over pairs formed INSIDE a question, pooled across questions, then folded.

    The attacker this metric models is handed one question's haystack and asked which session holds
    the evidence. So the only comparisons that mean anything are between sessions competing with
    each other. Pairing every gold against every distractor in the whole CORPUS answers a different
    and much easier question, and it dilutes in two ways that both happened here:

      * Scale drift. Forgetting's sessions run 188-301 chars depending on the question. Gold is the
        shortest session in 19 of its 35 gold-bearing questions (chance is about 13%), yet pooling
        ranks a long question's gold above a short question's distractors and the tell averages out
        -- 0.616 pooled against 0.903 within.
      * Abstention questions. Forgetting's 15 never-known questions have no gold at all, so they
        contribute distractor-only values that pair against every other question's gold. Dropping
        them moved the pooled figure 0.616 -> 0.826. A pooled metric gets *weaker* the more
        abstention questions a vertical adds, which is precisely backwards.

    Folding happens once, after pooling, so a feature pointing opposite ways in different questions
    cancels instead of each direction inflating to 0.5 or more on its own.
    """
    wins = 0.0
    pairs = 0
    for gold_values, other_values in strata:
        if not gold_values or not other_values:
            continue
        for g in gold_values:
            for o in other_values:
                wins += 1.0 if g > o else 0.5 if g == o else 0.0
        pairs += len(gold_values) * len(other_values)
    if not pairs:
        return 0.5
    auc = wins / pairs
    return max(auc, 1.0 - auc) if fold else auc


#: A feature may pass on pooled AUC and still separate gold PERFECTLY in a large minority of
#: questions -- a bimodal split that a mean cannot show. The consuming project's probe surfaced it:
#: Episodic's turn_count pools to 0.615 while turn count alone identifies the gold in 54% of its
#: questions. Refused as a distribution, not a mean.
#:
#: Measured against CHANCE rather than a flat share, which matters in both directions. With one gold
#: and H distractors, a question's folded AUC is 1.0 whenever gold is strictly top or strictly
#: bottom -- probability 2/(H+1). WorkingMemory varies H as its independent variable and a twelfth
#: of its questions have H=1, where the answer is 1.0 by construction; its chance rate is 38% and a
#: flat 25% bar would refuse it for its own design. Episodic's chance rate is 9% against 54%
#: observed. The excess is the signal.
PERFECT_SEPARATION_MULTIPLE = 2.0
PERFECT_SEPARATION_FLOOR = 0.20


def _perfect_share(strata: list[tuple[list[float], list[float]]]) -> tuple[float, float]:
    """Share of questions this feature separates perfectly, and the share expected by chance."""
    perfect = scored = 0
    expected = 0.0
    for gold_values, other_values in strata:
        if not gold_values or not other_values:
            continue
        scored += 1
        wins = sum(1.0 if g > o else 0.5 if g == o else 0.0
                   for g in gold_values for o in other_values)
        auc = wins / (len(gold_values) * len(other_values))
        if max(auc, 1.0 - auc) >= 1.0:
            perfect += 1
        # P(all gold strictly above every distractor, or all strictly below), the two orderings
        # out of the C(n, g) equally likely rank assignments that give a folded AUC of exactly 1.
        total = len(gold_values) + len(other_values)
        ways = math.comb(total, len(gold_values))
        expected += 2.0 / ways if ways else 1.0
    if not scored:
        return 0.0, 0.0
    return perfect / scored, expected / scored


def separability_report(questions: list[Question], exempt: frozenset = frozenset()) -> dict:
    """Tries cheap single-feature classifiers at telling gold sessions from distractors.

    The clause-parity check that preceded this one was specific to a marker string the generators
    happened to add. It would not have caught the same defect wearing any other shape -- a gold
    session that is systematically longer, or always earlier, or the only one with a digit in it.
    This asks the general question instead: can any cheap feature that carries no information about
    the QUESTION still find the gold?

    Question relevance is deliberately exempt and is not a feature here. Gold is supposed to be more
    relevant to its question than a distractor is -- if it were not, the question would be
    unanswerable -- so a relevance feature separating gold is the benchmark working, not a tell. How
    *easy* that is to exploit is bounded by the BM25 calibration gate, which is a different
    instrument for a different question.
    """
    def _ratio(count: float, total: float) -> float:
        return count / max(1.0, total)

    def _role(session: Session, role: str) -> str:
        return " ".join(t.content for t in session.turns if t.role == role)

    def _role_turns(session: Session, role: str) -> int:
        return sum(1 for t in session.turns if t.role == role)

    # Per-role slices of the same axes. Padding lands on one turn, so equalising the POOLED session
    # leaves the other role exactly as the generator wrote it — and a role label is as cheap a filter
    # as a character count. Measured after the pooled axes were equalised, WorkingMemory's gold was
    # still recoverable from user-turn length alone at AUC 1.000, and Forgetting's from user-turn
    # capitals and sentence count, both at 1.000, while every pooled figure sat comfortably under the
    # bar. Twice before, this family shipped a corpus whose aggregate was balanced and whose slice
    # was not; this is the slice.
    def _slot(session: Session, role: str, ordinal: int) -> str:
        return _slot_text(session, role, ordinal)

    role_features: dict = {}
    # The first turn of each role, on its own. Equalising the role total still left the FIRST user
    # turn — the one that actually carries the evidence — untouched, and WorkingMemory's gold was
    # recoverable from its length alone at AUC 1.000 with every role total comfortably balanced.
    for role in ("user", "assistant"):
        role_features |= {
            f"first_{role}_length_chars": lambda s, i, n, r=role: float(len(_slot(s, r, 0))),
            f"first_{role}_uppercase_density": lambda s, i, n, r=role: _ratio(
                sum(c.isupper() for c in _slot(s, r, 0)), len(_slot(s, r, 0))),
            f"first_{role}_sentence_count": lambda s, i, n, r=role: float(
                sum(_slot(s, r, 0).count(c) for c in ".!?")),
        }
    for role in ("user", "assistant"):
        role_features |= {
            f"{role}_length_chars": lambda s, i, n, r=role: float(len(_role(s, r))),
            f"{role}_uppercase_density": lambda s, i, n, r=role: _ratio(
                sum(c.isupper() for c in _role(s, r)), len(_role(s, r))),
            f"{role}_sentence_count": lambda s, i, n, r=role: float(
                sum(_role(s, r).count(c) for c in ".!?")),
            f"{role}_punctuation_density": lambda s, i, n, r=role: _ratio(
                sum(not (c.isalnum() or c.isspace()) for c in _role(s, r)), len(_role(s, r))),
            f"{role}_type_token_ratio": lambda s, i, n, r=role: _ratio(
                len(set(tokenize_raw(_role(s, r)))), len(tokenize_raw(_role(s, r)))),
            f"{role}_mean_turn_chars": lambda s, i, n, r=role: _ratio(
                len(_role(s, r)), _role_turns(s, r)),
        }

    features = {
        "session_length_chars": lambda s, i, n: float(len(s.text())),
        "turn_count": lambda s, i, n: float(len(s.turns)),
        "position_in_haystack": lambda s, i, n: i / max(1, n - 1),
        "digit_density": lambda s, i, n: _ratio(sum(c.isdigit() for c in s.text()), len(s.text())),
        "uppercase_density": lambda s, i, n: _ratio(sum(c.isupper() for c in s.text()), len(s.text())),
        # Sentence structure. WorkingMemory's gold was separable at 0.990 on this alone, because
        # equalise_shape matched character counts without matching how many sentences those
        # characters were spread across.
        "sentence_count": lambda s, i, n: float(sum(s.text().count(c) for c in ".!?")),
        # Punctuation as a family, and the em dash on its own because it is the one that actually
        # caught us: the gold acknowledgement templates spelled "Noted — I have that down." while
        # no filler sentence used the glyph at all.
        "punctuation_density": lambda s, i, n: _ratio(
            sum(not (c.isalnum() or c.isspace()) for c in s.text()), len(s.text())),
        "em_dash_density": lambda s, i, n: _ratio(s.text().count("—"), len(s.text())),
        "mean_turn_chars": lambda s, i, n: _ratio(len(s.text()), len(s.turns)),
        "type_token_ratio": lambda s, i, n: _ratio(
            len(set(tokenize_raw(s.text()))), len(tokenize_raw(s.text()))),
    } | role_features

    scores: dict[str, float] = {}
    perfect: dict[str, dict[str, float]] = {}
    for name, extract in features.items():
        strata = []
        for q in questions:
            n = len(q.sessions)
            gold_values, other_values = [], []
            for i, session in enumerate(q.sessions):
                (gold_values if session.is_gold else other_values).append(extract(session, i, n))
            strata.append((gold_values, other_values))
        scores[name] = round(_auc_within(strata), 4)
        share, chance = _perfect_share(strata)
        perfect[name] = {"share": round(share, 4), "chance": round(chance, 4)}

    gold_gram, gold_gram_score, filler_gram, filler_gram_score = _worst_boilerplate_ngram(questions)
    scores["gold_marker_ngram"] = round(gold_gram_score, 4)
    scores["boilerplate_ngram"] = round(filler_gram_score, 4)

    unknown = exempt - SEPARABILITY_REFUSED_FEATURES
    if unknown:
        raise SystemExit(
            f"separability: exemption named a feature that is not refused: {sorted(unknown)}. "
            "A typo here silently exempts nothing while reading as though it exempted something.")

    bimodal = {
        name: stats for name, stats in perfect.items()
        if name in SEPARABILITY_REFUSED_FEATURES and name not in exempt
        and stats["share"] >= PERFECT_SEPARATION_FLOOR
        and stats["share"] > PERFECT_SEPARATION_MULTIPLE * stats["chance"]
    }

    refused = {k: v for k, v in scores.items()
               if k in SEPARABILITY_REFUSED_FEATURES and k not in exempt}
    # No fallback to the full score set. If every refused feature is exempt there is nothing left to
    # refuse on, and ranging over `scores` would quietly promote boilerplate_ngram -- documented as
    # reported-not-refused -- into the gate, failing a corpus for a reason the ADR says is not one.
    worst = max(refused, key=refused.get) if refused else None
    return {
        "method": (
            "single-feature AUC over (gold, distractor) pairs formed WITHIN a question, pooled "
            "across questions and folded once to [0.5, 1.0]; questions with no gold contribute no "
            "pairs; boilerplate n-grams (sizes 1-3) restricted to phrases recurring in at least "
            f"{int(BOILERPLATE_MIN_QUESTION_SHARE * 100)}% of questions so per-question content is "
            "not mistaken for a marker"
        ),
        "exempt": [
            "question relevance — gold is supposed to be more relevant than a distractor; the BM25 "
            "calibration gate bounds how easily that is exploited"
        ],
        "threshold_auc": SEPARABILITY_MAX_AUC,
        "refused_features": sorted(SEPARABILITY_REFUSED_FEATURES - exempt),
        "exempt_features": sorted(exempt),
        "reported_only_features": sorted(SEPARABILITY_REPORTED_FEATURES),
        "features": scores,
        "worst_refused_feature": worst,
        "worst_refused_auc": scores[worst] if worst else 0.5,
        "worst_gold_marker_ngram": gold_gram,
        "gold_marker_ngram_auc": scores["gold_marker_ngram"],
        "worst_boilerplate_ngram": filler_gram,
        "boilerplate_ngram_auc": scores["boilerplate_ngram"],
        "perfect_separation": perfect,
        "bimodal_features": {
            name: {**stats, "excess_over_chance": round(stats["share"] - stats["chance"], 4)}
            for name, stats in sorted(bimodal.items())
        },
        "passed": (worst is None or scores[worst] < SEPARABILITY_MAX_AUC) and not bimodal,
    }


def _worst_boilerplate_ngram(
        questions: list[Question]) -> tuple[str | None, float, str | None, float]:
    """The recurring phrases that best predict gold and filler, and how well each does.

    Scored as an AUC on a 0/1 feature so it sits on the same scale as the numeric features: a
    phrase in every distractor and no gold scores 1.0, exactly as the calibration clause did.
    """
    question_count = len(questions) or 1
    appears_in: Counter[str] = Counter()
    for q in questions:
        seen = set()
        for session in q.sessions:
            # RAW tokens. `tokenize` drops stopwords, which is right for retrieval and fatal here:
            # a gram made of stopwords cannot be a candidate at all, so it is not scored low, it is
            # unrepresentable. "on the" marked Episodic's gold at AUC 0.763 -- above the refusal
            # bar -- and this screen could not see it in principle. The identical blindness was
            # found and fixed for type_token_ratio one revision earlier and never propagated here.
            words = tokenize_raw(session.text())
            # Unigrams included: the single token "noted" scored as high as the best bigram on
            # Forgetting, and a one-word filter is the cheapest classifier there is.
            for size in (1, 2, 3):
                for i in range(len(words) - size + 1):
                    seen.add(" ".join(words[i:i + size]))
        appears_in.update(seen)

    # Grams each QUESTION uses itself, indexed per question rather than pooled. Relevance is the
    # family's one declared exemption -- gold is supposed to match its own question, and how easily
    # that is exploited is bounded by the BM25 gate, not by this screen. But the exemption has to be
    # applied where it applies: dropping a gram corpus-wide because ANY question used it hid
    # Episodic's "on the" (0.763) on the strength of 2 questions in 50, while the case the
    # exemption exists for -- WorkingMemory's "have", in 100% of its questions -- needs dropping
    # everywhere. So a question whose own text contains the gram contributes no pairs for it, and
    # every other question still does.
    own_grams: list[set[str]] = []
    for q in questions:
        words = tokenize_raw(q.question)
        own_grams.append({" ".join(words[i:i + size])
                          for size in (1, 2, 3)
                          for i in range(len(words) - size + 1)})

    candidates = [
        gram for gram, count in appears_in.items()
        if count / question_count >= BOILERPLATE_MIN_QUESTION_SHARE
    ]
    if not candidates:
        # Four values, matching the normal path. Returning two here raised ValueError in the
        # caller on any corpus small enough that no gram clears the recurrence floor -- a
        # --limit run, or a unit test, which is exactly where someone checks this code.
        return None, 0.5, None, 0.5

    gold_gram, gold_score = None, 0.5
    filler_gram, filler_score = None, 0.5
    for gram in candidates:
        # Padded on both sides so "list also" cannot match inside "checklist also".
        needle = f" {gram} "
        strata = []
        for index, q in enumerate(questions):
            if gram in own_grams[index]:
                continue          # relevance channel for THIS question; contributes no pairs
            gold_values, other_values = [], []
            for session in q.sessions:
                haystack = f" {' '.join(tokenize_raw(session.text()))} "
                value = 1.0 if needle in haystack else 0.0
                (gold_values if session.is_gold else other_values).append(value)
            strata.append((gold_values, other_values))
        raw = _auc_within(strata, fold=False)
        # Direction is the whole point here, which is why this one metric is not folded. A phrase
        # carried by FILLER and not by gold is the template artifact the ADR argues cannot be fixed
        # without writing filler with the variety of real conversation. A phrase carried by GOLD and
        # not by filler is a marker on the evidence — the v1 calibration-clause defect exactly — and
        # calling those two the same number let "Noted" sit at 1.0 under a heading that reads
        # "this filler came from templates".
        if raw > gold_score:
            gold_gram, gold_score = gram, raw
        if (1.0 - raw) > filler_score:
            filler_gram, filler_score = gram, 1.0 - raw
    return gold_gram, gold_score, filler_gram, filler_score


def check_separability(questions: list[Question], exempt: frozenset = frozenset()) -> list[str]:
    """Refuses a corpus any cheap artifact feature can separate (ADR-026 V7)."""
    report = separability_report(questions, exempt)
    if report["passed"]:
        return []

    failures = []
    worst, auc = report["worst_refused_feature"], report["worst_refused_auc"]
    if worst is not None and auc >= SEPARABILITY_MAX_AUC:
        failures.append(
            f"separability: '{worst}' separates gold from distractors at AUC {auc:.3f}, at or "
            f"above the {SEPARABILITY_MAX_AUC} refusal threshold. Evidence a cheap classifier can "
            f"find without reading it is evidence the benchmark is not measuring retrieval.")
    # Reported separately, because the pooled AUC can sit comfortably under the bar while the
    # feature still separates PERFECTLY in a large minority of questions. Naming the pooled
    # number as the reason for a bimodality failure sends the reader to the wrong measurement.
    for name, stats in report.get("bimodal_features", {}).items():
        failures.append(
            f"separability: '{name}' separates gold perfectly in {stats['share']:.0%} of questions "
            f"against a chance rate of {stats['chance']:.0%} (pooled AUC "
            f"{report['features'][name]:.3f} is under the bar; the distribution is not).")
    return failures

# --------------------------------------------------------------------------------------
# The calibration gate
# --------------------------------------------------------------------------------------

@dataclass
class Calibration:
    echo: float
    mean: float
    iterations: int
    per_question: dict[str, float]
    trace: list[tuple[float, float]]



def equalise_echo(questions: list[Question], echo: float, rng: random.Random) -> None:  # DevSkim: ignore DS148264 - deterministic corpus generation, not a security function
    """Gives gold sessions the same calibration clause the distractors carry, with NEUTRAL terms.

    Generators weave the clause into filler only, which is the natural way to write them and is a
    fatal mistake: it makes the clause a perfect gold/distractor tell. Measured on the first shipped
    build, gold carried it 0 times in 501 sessions while distractors carried it in ~99%, so
    `ECHO_LEAD not in session` isolated every piece of gold evidence in every corpus with a one-line
    string filter. A benchmark whose evidence is separable without reading it measures nothing.

    The terms matter as much as the clause. Echoing the question's OWN vocabulary into gold removes
    the tell but hands gold the query's keywords, which lifts its retrieval score -- every corpus
    busted the calibration ceiling when it was tried that way. So gold receives the same scaffolding
    built from OTHER questions' vocabulary: the marker stops discriminating, and gold gains no
    advantage it did not already have.

    Applied centrally, after build and before scoring, so no generator can reintroduce the tell by
    forgetting it.
    """
    # Built from question text, minus every token that appears in ANY question's ANSWER.
    # Episodic's attribution questions embed the statement they ask about, so a value that is
    # one question's gold answer can appear in another question's text -- and weaving it into a
    # user turn makes a filler session state an answer, which the assistant-stated leak guard
    # then reports against a question that never went near it. Latent since the pool was
    # introduced; it surfaced when an unrelated change shifted which terms got sampled.
    answers = {term for q in questions for term in tokenize(q.answer)}
    pool = sorted({term for q in questions for term in tokenize(q.question)} - answers)
    if not pool:
        return

    for question in questions:
        marked = sum(
            1 for s in question.sessions if not s.is_gold and ECHO_LEAD in s.text())
        rate = marked / max(1, question.h)
        own = set(tokenize(question.question))
        neutral = [term for term in pool if term not in own]
        if not neutral:
            continue

        # How many gold sessions get the clause is COUNTED, not drawn per session. A per-session
        # coin flip at the distractors' rate is right on average and wrong where it matters: a
        # question with one gold session and a 0.92 rate leaves that session bare 8% of the time,
        # and a bare gold session in a haystack where every distractor carries the clause is the v1
        # tell in miniature. Corpus-wide it looked fine; per question it was a lottery, and which
        # questions lost depended on how much randomness earlier questions happened to consume.
        unmarked = [s for s in question.sessions if s.is_gold and ECHO_LEAD not in s.text()]
        if not unmarked:
            continue
        wanted = round(rate * len(unmarked))
        if rate >= 0.5:
            wanted = max(1, wanted)
        for session in rng.sample(unmarked, min(len(unmarked), wanted)):
            take = max(1, min(len(neutral), round(len(own) * echo)))
            # Some terms are drawn from words ALREADY in this gold session, minus the question's own
            # vocabulary. A distractor's clause repeats the question's keywords, and those keywords
            # are also in the distractor's prose, so a distractor says several words twice; gold,
            # echoing a foreign vocabulary, repeated nothing and ended up with a visibly higher
            # type-token ratio -- 0.978 against 0.929, separable at AUC 0.869. Repeating gold's own
            # NON-query words restores the repetition without moving its retrieval score, because a
            # term the query never mentions cannot change how well the query matches.
            # Minus this question's own answer. The session that carries the answer is the one
            # we are drawing from, so without this the clause can weave the answer into the GOLD
            # USER turn -- and for Episodic's assistant-stated shape, the whole design is that
            # only the assistant states it. The generator's leak guard caught it.
            answer_terms = set(tokenize(question.answer))
            mine = [t for t in sorted(set(tokenize(session.text())))
                    if t not in own and t not in STOPWORDS and t not in answer_terms]
            # How many of them, solved rather than guessed. A repeated term adds a token but no new
            # type; a foreign one adds both. So with `take` terms of which k are repeats, gold lands
            # at (types + take - k) / (tokens + take), and k follows from the ratio the distractors
            # in THIS question happen to sit at. A fixed fraction cannot work: half repeats brought
            # WorkingMemory's gold down to its distractors and pushed Prospective's straight past
            # them, separating just as well in the opposite direction.
            distractor_ratios = [
                len(set(tokenize_raw(other.text()))) / max(1, len(tokenize_raw(other.text())))
                for other in question.sessions if not other.is_gold]
            tokens = tokenize_raw(session.text())
            types_now, tokens_now = len(set(tokens)), len(tokens)
            if distractor_ratios:
                target_ratio = sum(distractor_ratios) / len(distractor_ratios)
                k = round(types_now + take - target_ratio * (tokens_now + take))
            else:
                k = take // 2
            k = max(0, min(take, len(mine), k))
            from_self = rng.sample(mine, k) if mine and k else []
            terms = from_self + rng.sample(neutral, max(0, take - len(from_self)))
            # Shuffled so the repeated terms are not all at the front, which would itself be a
            # position tell inside the clause.
            rng.shuffle(terms)  # DevSkim: ignore DS148264 - corpus generation must be replayable under a seed; a CSPRNG cannot be seeded to reproduce a draw, and this orders neutral vocabulary, not secrets.
            # Never inside a turn the answer is derived from, and otherwise on the assistant turn.
            # WHICH turn carries the clause is itself a signal: filler wove it into the user turn
            # while gold always received it on the assistant turn, so in Prospective the trigram
            # "weeks also mind" -- a filler `{when}` phrase butted against the clause -- marked
            # distractors at AUC 0.990, which is to say its ABSENCE marked gold. Preferring a
            # non-answer turn also makes the placement alternate with the speaker in Episodic's
            # attribution shape, which is the balance that vertical needs for its own reasons.
            free = [i for i, turn in enumerate(session.turns) if not turn.has_answer]
            index = next(
                (i for i in reversed(free) if session.turns[i].role == "assistant"),
                free[-1] if free else len(session.turns) - 1)
            session.turns[index].content = weave_echo(session.turns[index].content, terms)


def calibrate(build, seed: int, max_iterations: int = 24) -> tuple[list[Question], Calibration]:
    """Binary-searches the lexical-echo knob until mean realised coverage lands in band.

    `build(echo, rng)` must be a pure function of its arguments: same echo and same seed,
    same corpus. Without that the search does not converge and the recorded number does
    not describe the shipped file.

    Coverage falls as echo rises (distractors compete harder), so the search brackets on
    that monotone. If even echo=1.0 leaves the corpus above the band the gate fails --
    ADR §4: "a corpus that cannot be tuned into its band is redesigned, not shipped with
    a footnote."
    """
    trace: list[tuple[float, float]] = []

    def attempt(echo: float) -> tuple[list[Question], float, dict[str, float]]:
        questions = build(echo, random.Random(seed))  # DevSkim: ignore DS148264 - corpus generation must be replayable under a seed; a CSPRNG cannot be seeded to reproduce a draw, and this selects filler text, not secrets.
        equalise_echo(questions, echo, random.Random(seed + 1))  # DevSkim: ignore DS148264 - deterministic corpus generation
        # Replies before shape: the acknowledgement is text, so equalising it after the shape pass
        # would put characters back that the shape pass had already balanced around.
        equalise_reply(questions, random.Random(seed + 3))  # DevSkim: ignore DS148264 - see above
        equalise_shape(questions, random.Random(seed + 2))  # DevSkim: ignore DS148264 - see above
        per_q = {q.question_id: realised_coverage(q) for q in questions}
        mean = sum(per_q.values()) / len(per_q) if per_q else 0.0
        trace.append((round(echo, 4), round(mean, 4)))
        return questions, mean, per_q

    lo, hi = 0.0, 1.0
    questions, mean, per_q = attempt(hi)
    if mean > BAND_HIGH:
        raise SystemExit(
            f"calibration gate: even at maximum distractor echo the mean realised coverage is "
            f"{mean:.3f}, above the {BAND_HIGH} ceiling. The corpus is saturated by construction "
            f"and must be redesigned (wider haystack, harder distractors) rather than shipped.")

    best = (questions, mean, per_q, hi)
    iterations = 1
    for _ in range(max_iterations):
        if BAND_LOW <= best[1] <= BAND_HIGH:
            break
        mid = (lo + hi) / 2
        questions, mean, per_q = attempt(mid)
        iterations += 1
        if mean > BAND_HIGH:
            lo = mid          # still too easy to find -- echo harder
        else:
            hi = mid          # in band or too hard -- back off
        if abs(mean - (BAND_LOW + BAND_HIGH) / 2) < abs(best[1] - (BAND_LOW + BAND_HIGH) / 2):
            best = (questions, mean, per_q, mid)

    questions, mean, per_q, echo = best
    if not (BAND_LOW <= mean <= BAND_HIGH):
        raise SystemExit(
            f"calibration gate: converged on mean realised coverage {mean:.3f}, outside the "
            f"[{BAND_LOW}, {BAND_HIGH}] band after {iterations} iterations. Not shipping it.")
    return questions, Calibration(round(echo, 4), round(mean, 4), iterations, per_q, trace)


# --------------------------------------------------------------------------------------
# Emission
# --------------------------------------------------------------------------------------

def sha256_normalized(text: str) -> str:
    """Newline-normalized, matching LongMemEvalTimeGroundedCorpus.ComputeSha256 -- the hash
    identifies the corpus *text*, not the line endings of one checkout."""
    return hashlib.sha256(text.replace("\r\n", "\n").encode("utf-8")).hexdigest()


def _dump(payload) -> str:
    return json.dumps(payload, indent=2, ensure_ascii=False) + "\n"


def finalise(
    vertical: str,
    build,
    structure: StructureSpec,
    generator_tool: str,
    seed: int = 20260815,
    extra_checks=None,
) -> None:
    """Calibrates, verifies, and writes the corpus and its metadata sidecar.

    Every failure here is fatal by design. The generators are the only thing standing
    between an authored corpus and a benchmark that reports confident numbers about
    nothing, so they refuse rather than warn.
    """
    abbrev, expected_count = VERTICALS[vertical]
    corpus_id = f"agenteval-typedmemeval-{vertical}-{CORPUS_REVISION}"

    questions, calibration = calibrate(build, seed)

    if len(questions) != expected_count:
        raise SystemExit(
            f"{vertical}: generated {len(questions)} questions, ADR-026 declares {expected_count}")

    ids = [q.question_id for q in questions]
    if len(set(ids)) != len(ids):
        raise SystemExit(f"{vertical}: duplicate question ids")
    for qid in ids:
        if not re.fullmatch(rf"tme-{abbrev}-\d{{3}}(_abs)?", qid):
            raise SystemExit(f"{vertical}: question id '{qid}' breaks the ADR §1 naming scheme")

    for q in questions:
        q.extension["vertical"] = vertical

    failures = check_structure(questions, structure)
    failures += check_answer_not_verbatim(questions)
    failures += check_echo_parity(questions)
    failures += check_separability(questions, structure.separability_exempt)
    if structure.no_absolute_dates:
        failures += check_no_absolute_dates(questions)
    if extra_checks:
        failures += extra_checks(questions)
    if failures:
        raise SystemExit(f"{vertical}: {len(failures)} validity failures\n  " + "\n  ".join(failures[:40]))

    corpus_json = _dump([q.to_json() for q in questions])
    corpus_sha = sha256_normalized(corpus_json)

    g_counts = Counter(q.g for q in questions)
    ceilings = {str(g): round(structural_ceiling(g), 4) for g in sorted(g_counts)}

    metadata = {
        "corpus_id": corpus_id,
        "vertical": vertical,
        "revision": CORPUS_REVISION,
        # Carried by the generator, not patched in afterwards. It was patched in once and lost on
        # the next regeneration, which is exactly how a supersession notice stops being told.
        "supersedes": SUPERSEDES,
        "question_count": len(questions),
        # Binds this metadata to the exact corpus text it describes. A mismatch means one
        # of the two was regenerated alone, and CI fails rather than trusting either.
        "corpus_sha256": corpus_sha,
        "generator": {
            "tool": generator_tool,
            "version": GENERATOR_VERSION,
            "seed": seed,
        },
        "k_ref": K_REF,
        "coverage": {
            "retriever": RETRIEVER_ID,
            "band_low": BAND_LOW,
            "band_high": BAND_HIGH,
            "mean_realised": calibration.mean,
            "echo": calibration.echo,
            "iterations": calibration.iterations,
            "search_trace": calibration.trace,
            "per_question": {k: round(v, 4) for k, v in sorted(calibration.per_question.items())},
        },
        "ceiling": {
            "k_ref": K_REF,
            "by_g": ceilings,
            "min": min(ceilings.values()) if ceilings else 1.0,
            # False for every G <= K_REF vertical. Stated rather than implied: ADR §4
            # refuses to present a 1.0 ceiling as if it were a band.
            "structural_below_one": any(v < 1.0 for v in ceilings.values()),
        },
        "structure": {
            "g_distribution": {str(g): c for g, c in sorted(g_counts.items())},
            "h_min": min(q.h for q in questions),
            "h_max": max(q.h for q in questions),
            "gold_position_shuffled": structure.gold_position_shuffled,
            "h_is_independent_variable": structure.h_is_independent_variable,
            "no_absolute_dates": structure.no_absolute_dates,
        },
        # V1-V6 are filled in by tools/run_typedmemeval_probes.py. "not_run" is the honest
        # initial state: those probes need a reference model, and a record that claimed
        # otherwise would be the exact dishonesty this family is built to avoid.
        #
        # V7 is written HERE, because it is model-free and the generator has already computed
        # it — the gate above refuses to write a corpus that fails it. Leaving it to the stamper
        # meant every regeneration silently dropped the record, and the CI check that reads it
        # then failed with "carries no V7 record" until somebody remembered a second command.
        # A record the build can produce for itself should not depend on remembering.
        "probes": {
            "status": "not_run",
            "v7_separability": {
                **separability_report(questions, structure.separability_exempt),
                "status": "run",
                # No wall-clock stamp: the generator is deterministic and a timestamp here would
                # make every regeneration a diff. The corpus hash is what binds the record to what
                # it describes, and it is right below.
                "measured_by": "generator",
                "probed_corpus_sha256": corpus_sha,
            },
        },
    }

    out_dir = DATA_ROOT / vertical
    out_dir.mkdir(parents=True, exist_ok=True)
    (out_dir / f"{corpus_id}.json").write_text(corpus_json, encoding="utf-8", newline="\n")
    (out_dir / f"{corpus_id}.meta.json").write_text(_dump(metadata), encoding="utf-8", newline="\n")

    print(f"{corpus_id}: {len(questions)} questions, "
          f"BM25@{K_REF} mean coverage {calibration.mean:.3f} "
          f"(echo {calibration.echo:.3f}, {calibration.iterations} iterations), "
          f"G={dict(sorted(g_counts.items()))}, H=[{metadata['structure']['h_min']},"
          f"{metadata['structure']['h_max']}], sha {corpus_sha[:12]}")


# --------------------------------------------------------------------------------------
# Session-building helpers shared by the generators
# --------------------------------------------------------------------------------------

def make_session(timestamp: datetime, *turns: tuple[str, str], gold_turn: int | None = None,
                 tag: str = "") -> Session:
    """Builds a session from (user, assistant) content pairs.

    `gold_turn` marks which user turn carries the answer. A session with a gold turn is
    a gold session; the two cannot drift apart because they are set together here.
    """
    built: list[Turn] = []
    for i, (user, assistant) in enumerate(turns):
        built.append(Turn("user", user, has_answer=(gold_turn == i)))
        built.append(Turn("assistant", assistant))
    return Session(built, timestamp, is_gold=gold_turn is not None, tag=tag)


def spread(start: datetime, count: int, hours: int = 30) -> list[datetime]:
    """Evenly spaced session timestamps. Fixed spacing matters for WorkingMemory (§5.4:
    distance-in-sessions must not vary independently of distance-in-time) and is harmless
    everywhere else, so every vertical uses it."""
    return [start + timedelta(hours=hours * i) for i in range(count)]


def interleave(rng: random.Random, gold: list[Session], filler: list[Session]) -> list[Session]:
    """Places gold sessions at random positions among the filler, then re-stamps every
    session in chronological order.

    Position is randomized because a corpus whose gold always sits first (or last) is
    measuring position; timestamps are re-stamped afterwards because the harness and every
    order-sensitive vertical assume session order matches time order.

    NOT usable by an order-sensitive vertical. Gold sessions are inserted one at a time, so their
    order relative to one another is permuted -- and where that order IS the answer (Episodic
    list-order), permuting it destroys the question. Such a vertical must draw all its gold slots
    up front and keep them sorted; see gen_typedmemeval_episodic.py.
    """
    sessions = list(filler)
    for session in gold:
        sessions.insert(rng.randint(0, len(sessions)), session)
    stamps = spread(datetime(2026, 1, 5, 9, 0), len(sessions))
    for session, stamp in zip(sessions, stamps):
        session.timestamp = stamp
    return sessions
