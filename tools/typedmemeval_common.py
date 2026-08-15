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
    "workingmemory": ("wm",  48),
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
_NAME_TAILS = ["qvist", "zell", "xby", "vund", "kjar", "wraith", "zorn", "quay0", "phex", "yrn"]
#: Raises capitalisation density: mostly names, little connective text.
_SHAPE_CLAUSES_DENSE = [
    "{a} {b} agreed. {a} {b} would too.",
    "Ask {a} {b} — {a} {b} knows.",
    "{a} {b}, {a} {b}, same story.",
]
#: Lowers it: connective text, no names.
_SHAPE_CLAUSES_PLAIN = [
    "it seemed worth putting down somewhere before it slipped away again.",
    "that is roughly where the thought ended up after turning it over a while.",
    "nothing much turns on it either way but it stuck in the mind.",
]


def _shape_profile(session: Session) -> tuple[int, int]:
    text = session.text()
    return len(text), sum(c.isupper() for c in text)


_PAD_WORDS = ["noted", "again", "later", "aside", "still", "quite", "there", "about", "under",
              "since", "along", "after", "while", "among", "these", "those", "given", "taken"]


def _pad_target(session: Session) -> int:
    """Index of the turn padding lands on -- the last assistant turn where there is one, so an
    answer-bearing user turn is never rewritten."""
    return next(
        (i for i in range(len(session.turns) - 1, -1, -1) if session.turns[i].role == "assistant"),
        len(session.turns) - 1)


def _padding(chars: int, capitals: int, rng: random.Random) -> str:  # DevSkim: ignore DS148264 - deterministic corpus generation
    """Neutral text of a given length carrying a given number of capitals.

    Both knobs matter: length and capitalisation DENSITY are separate separability features, and
    padding that fixes one while moving the other just relocates the tell.
    """
    out: list[str] = []
    total = 0
    while total < chars:
        word = rng.choice(_PAD_WORDS)
        if capitals > 0:
            word = word.capitalize()
            capitals -= 1
        out.append(word)
        total += len(word) + 1
    return " ".join(out)[:max(0, chars)].strip() or "noted"


def equalise_shape(questions: list[Question], rng: random.Random) -> None:  # DevSkim: ignore DS148264 - deterministic corpus generation, not a security function
    """Pads EVERY session toward a common shape, so no shape feature can tell gold from filler.

    Gold states an arbitrary named fact, because V2 requires the answer to be unguessable; filler
    states everyday things. Measured across the first five shipped corpora, the consequence was that
    gold carried visibly more capital letters and more text than its distractors -- Forgetting
    separated at AUC 0.99 on capitalisation alone and Episodic at 0.96 on length. A classifier that
    counts capitals finds the evidence without reading a word of it.

    Padding only the filler does not fix it: the metric folds direction, so overshooting separates
    exactly as well as undershooting. Both sides are therefore padded toward the same per-question
    target, with invented names built from syllables that appear in no question -- the shape
    converges, the content cannot. Gold padding lands on an assistant turn so the answer-bearing
    user turn is never touched.
    """
    for question in questions:
        if len(question.sessions) < 2:
            continue

        lengths = [_shape_profile(s)[0] for s in question.sessions]
        uppers = [_shape_profile(s)[1] for s in question.sessions]
        # Above the longest session, not equal to it. Padding only ever adds text, so a target set
        # AT the maximum leaves that one session untouched — and an untouched session keeps whatever
        # capitalisation density made it an outlier, which is the tell surviving its own fix.
        # A margin means every session gets corrected on both axes.
        target_len = max(lengths) + 60
        # The HIGHEST density, not the median. Padding can only add capitals, so a median target
        # leaves every session above it — which is gold, because gold is where the arbitrary proper
        # nouns live — permanently above the line. Raising the distractors to meet gold is the only
        # direction available: lowering gold would mean editing the names that ARE the fact.
        densities = sorted(u / max(1, l) for l, u in zip(lengths, uppers))
        target_density = densities[-1]

        for session in question.sessions:
            length, upper = _shape_profile(session)
            needed = target_len - length
            if needed <= 0:
                continue

            # Padded to the target EXACTLY, with capitals inserted at the target density. Clause-
            # sized padding leaves an overshoot that varies with how many clauses a session needed,
            # and that overshoot is itself a shape feature -- the short sessions take more clauses
            # and land further past the line, so the tell survives its own fix.
            wanted_upper = max(0, round(target_density * target_len) - upper)
            session.turns[_pad_target(session)].content += " " + _padding(needed, wanted_upper, rng)


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
})

#: Phrase recurrence is MEASURED but does not refuse, and the distinction is not a convenience.
#: Filler is template-generated: a few dozen sentence patterns spread across fifty questions, so
#: every filler phrase recurs and no gold phrase does, and a phrase-match classifier separates them
#: at an AUC that says "this filler came from templates" rather than "this corpus hides a tell".
#: Driving it to chance needs filler with the variety of real conversation, which is a corpus
#: revision and not a check. Recorded at full value so the number is visible and can be argued with.
SEPARABILITY_REPORTED_FEATURES = frozenset({"boilerplate_ngram"})

#: An n-gram counts as boilerplate -- and therefore as a candidate tell -- only when it recurs
#: across this share of the corpus's questions. Anything rarer is question CONTENT: "Riverside
#: Fitness" appears in one question's gold and nowhere else, and flagging that would be flagging
#: the corpus for being about something.
BOILERPLATE_MIN_QUESTION_SHARE = 0.20


def _auc(gold_values: list[float], other_values: list[float]) -> float:
    """Probability a random gold session outranks a random distractor on this feature.

    Returned folded to [0.5, 1.0]: a feature that separates in either direction is equally a tell,
    and which way it points is not the question being asked.
    """
    if not gold_values or not other_values:
        return 0.5
    wins = ties = 0
    for g in gold_values:
        for o in other_values:
            if g > o:
                wins += 1
            elif g == o:
                ties += 1
    auc = (wins + 0.5 * ties) / (len(gold_values) * len(other_values))
    return max(auc, 1.0 - auc)


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
    features = {
        "session_length_chars": lambda s, i, n: float(len(s.text())),
        "turn_count": lambda s, i, n: float(len(s.turns)),
        "position_in_haystack": lambda s, i, n: i / max(1, n - 1),
        "digit_density": lambda s, i, n: sum(c.isdigit() for c in s.text()) / max(1, len(s.text())),
        "uppercase_density": lambda s, i, n: sum(c.isupper() for c in s.text()) / max(1, len(s.text())),
    }

    scores: dict[str, float] = {}
    for name, extract in features.items():
        gold_values, other_values = [], []
        for q in questions:
            n = len(q.sessions)
            for i, session in enumerate(q.sessions):
                (gold_values if session.is_gold else other_values).append(extract(session, i, n))
        scores[name] = round(_auc(gold_values, other_values), 4)

    ngram, ngram_score = _worst_boilerplate_ngram(questions)
    scores["boilerplate_ngram"] = round(ngram_score, 4)

    refused = {k: v for k, v in scores.items()
               if k in SEPARABILITY_REFUSED_FEATURES and k not in exempt}
    worst = max(refused, key=refused.get) if refused else max(scores, key=scores.get)
    return {
        "method": (
            "single-feature AUC over (session, is_gold) pairs, folded to [0.5, 1.0]; boilerplate "
            "n-grams restricted to phrases recurring in at least "
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
        "worst_refused_auc": scores[worst],
        "worst_boilerplate_ngram": ngram,
        "boilerplate_ngram_auc": scores["boilerplate_ngram"],
        "passed": scores[worst] < SEPARABILITY_MAX_AUC,
    }


def _worst_boilerplate_ngram(questions: list[Question]) -> tuple[str | None, float]:
    """The recurring phrase that best predicts gold, and how well it does.

    Scored as an AUC on a 0/1 feature so it sits on the same scale as the numeric features: a
    phrase in every distractor and no gold scores 1.0, exactly as the calibration clause did.
    """
    question_count = len(questions) or 1
    appears_in: Counter[str] = Counter()
    for q in questions:
        seen = set()
        for session in q.sessions:
            words = tokenize(session.text())
            for size in (2, 3):
                for i in range(len(words) - size + 1):
                    seen.add(" ".join(words[i:i + size]))
        appears_in.update(seen)

    candidates = [
        gram for gram, count in appears_in.items()
        if count / question_count >= BOILERPLATE_MIN_QUESTION_SHARE
    ]
    if not candidates:
        return None, 0.5

    best_gram, best_score = None, 0.5
    for gram in candidates:
        gold_values, other_values = [], []
        for q in questions:
            for session in q.sessions:
                value = 1.0 if gram in " ".join(tokenize(session.text())) else 0.0
                (gold_values if session.is_gold else other_values).append(value)
        score = _auc(gold_values, other_values)
        if score > best_score:
            best_gram, best_score = gram, score
    return best_gram, best_score


def check_separability(questions: list[Question], exempt: frozenset = frozenset()) -> list[str]:
    """Refuses a corpus any cheap artifact feature can separate (ADR-026 V7)."""
    report = separability_report(questions, exempt)
    if report["passed"]:
        return []
    return [
        f"separability: '{report['worst_refused_feature']}' separates gold from distractors at AUC "
        f"{report['worst_refused_auc']:.3f}, at or above the {SEPARABILITY_MAX_AUC} refusal "
        f"threshold. "
        f"Evidence a cheap classifier can find without reading it is evidence the benchmark is not "
        f"measuring retrieval."
    ]

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
    pool = sorted({term for q in questions for term in tokenize(q.question)})
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

        for session in question.sessions:
            if not session.is_gold or ECHO_LEAD in session.text():
                continue
            if rng.random() > rate:
                continue
            take = max(1, min(len(neutral), round(len(own) * echo)))
            terms = rng.sample(neutral, take)
            # Appended to the assistant turn where there is one, so the clause never lands inside
            # the user sentence a question's answer is derived from.
            index = next(
                (i for i in range(len(session.turns) - 1, -1, -1)
                 if session.turns[i].role == "assistant"),
                len(session.turns) - 1)
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
    corpus_id = f"agenteval-typedmemeval-{vertical}-v2"

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
        "revision": "v2",
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
        # Filled in by tools/run_typedmemeval_probes.py. "not_run" is the honest initial
        # state: these probes need a reference model, and a record that claimed otherwise
        # would be the exact dishonesty this family is built to avoid.
        "probes": {"status": "not_run"},
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
