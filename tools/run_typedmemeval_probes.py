#!/usr/bin/env python3
"""
Runs the TypedMemEval validity probes (ADR-026 §5, V1-V6) against a reference model and stamps
the results into each corpus's .meta.json.

These are the checks a generator cannot do for itself, because they ask what a *model* can do with
the corpus rather than what the corpus contains:

  V1  oracle answerability   Given only the gold sessions, the reference model must answer
                             correctly. A question the ceiling cannot answer measures nothing.
  V1p pair flip              For paired questions, each arm must be answered correctly AND the two
                             answers must differ. A pair whose arms agree has no signal, and would
                             report a system as consistent when the corpus never asked it anything
                             different.
  V2  non-inferability       With NO context, k=10 samples; the question is rejected if 2 or more
                             produce the gold answer. An answer that can be guessed measures nothing.
  V3  gold-ablated           Given only the NON-gold sessions, the model must NOT produce the gold
                             answer. This is the dual of V1 and the only real defence against a
                             distractor that accidentally contains or paraphrases the answer -- the
                             zero-context probe cannot catch it, because it never sees the distractors.
  V6  leave-one-out          For multi-component questions, ablating any single gold component must
                             stop the model producing the gold. Without it, per-component coverage
                             reports components as load-bearing that are not.

MATCH DETECTION. Deciding "did this response produce the gold answer?" is itself a judgment, and
running an LLM judge over every sample would triple the cost of V2 for no gain: the corpus's facts
are randomly drawn, so a blind guess essentially never lands on one. So matching is a two-stage
screen, and both stages are recorded:

  1. A deliberately over-sensitive lexical screen looks for the gold's distinctive tokens (numbers,
     proper nouns, rare words). It is tuned to over-detect -- a false positive costs one judge call,
     a false negative would silently pass an invalid question.
  2. Anything the screen flags goes to the reference model as a yes/no equivalence judgment.

A question only counts as "matched" when stage 2 says so. Stage 1 alone never decides anything.

Credentials come from the environment (AZURE_OPENAI_ENDPOINT, AZURE_OPENAI_API_KEY,
AZURE_OPENAI_DEPLOYMENT). Nothing is written to the repository except the probe records.

Usage:
    python tools/run_typedmemeval_probes.py                  # all verticals
    python tools/run_typedmemeval_probes.py prospective      # one vertical
    python tools/run_typedmemeval_probes.py --limit 5        # smoke test
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import sys
import time
import urllib.error
import urllib.request
from collections import Counter
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timezone
from pathlib import Path
from threading import Lock

import typedmemeval_common as tmc

API_VERSION = "2024-12-01-preview"
V2_SAMPLES = 10
V2_REJECT_AT = 2

#: V3 and V6 sample too. A leak is a leak: one sample that reconstructs the answer from distractors
#: alone is enough to condemn a question, so unlike V2 there is no hit threshold. The reason for
#: sampling at all is the opposite of V2's — not to see whether a lucky guess recurs, but because a
#: single sample can MISS a leak that is there. The gutter/inspection leak in Prospective was caught
#: by one sample and could as easily have been missed by it.
ABLATION_SAMPLES = 3
CACHE_PATH = Path(__file__).resolve().parent / ".typedmemeval_probe_cache.json"

_cache: dict[str, str] = {}
_cache_lock = Lock()
_stats = Counter()


# --------------------------------------------------------------------------------------
# Provider
# --------------------------------------------------------------------------------------

def _config() -> tuple[str, str, str]:
    endpoint = os.environ.get("AZURE_OPENAI_ENDPOINT", "").rstrip("/")
    key = os.environ.get("AZURE_OPENAI_API_KEY", "")
    deployment = os.environ.get("AZURE_OPENAI_DEPLOYMENT", "")
    if not (endpoint and key and deployment):
        sys.exit(
            "Reference-model credentials are not set. The probes need AZURE_OPENAI_ENDPOINT, "
            "AZURE_OPENAI_API_KEY and AZURE_OPENAI_DEPLOYMENT.\n"
            "Without them the probe records stay 'not_run', which is the honest state -- a "
            "recorded pass that nothing produced would be worse than no record at all."
        )
    return endpoint, key, deployment


#: Completions between cache flushes. Small enough that a kill costs a minute of work, large
#: enough that the write is not itself a cost: the cache is ~1 MB, so this is a megabyte per
#: fifty calls against calls that take seconds each.
_CACHE_FLUSH_EVERY = 50


def _flush_cache() -> None:
    """Atomically persist the cache. Caller holds `_cache_lock`."""
    temporary = CACHE_PATH.with_suffix(".tmp")
    temporary.write_text(json.dumps(_cache, ensure_ascii=False), encoding="utf-8")
    temporary.replace(CACHE_PATH)


def complete(prompt: str, *, cache_key: str, max_tokens: int = 900) -> str:
    """One chat completion, cached so an interrupted run resumes instead of re-paying."""
    with _cache_lock:
        if cache_key in _cache:
            _stats["cache_hit"] += 1
            return _cache[cache_key]

    endpoint, key, deployment = _config()
    url = f"{endpoint}/openai/deployments/{deployment}/chat/completions?api-version={API_VERSION}"
    # Temperature is deliberately not sent: this deployment family rejects explicit values, and the
    # provider default is what V2 wants to sample at anyway.
    body = {"messages": [{"role": "user", "content": prompt}], "max_completion_tokens": max_tokens}

    last_error = None
    for attempt in range(5):
        try:
            request = urllib.request.Request(
                url,
                data=json.dumps(body).encode("utf-8"),
                headers={"Content-Type": "application/json", "api-key": key},
                method="POST",
            )
            with urllib.request.urlopen(request, timeout=180) as response:
                payload = json.loads(response.read().decode("utf-8"))
            text = (payload["choices"][0]["message"].get("content") or "").strip()
            with _cache_lock:
                _cache[cache_key] = text
                _stats["call"] += 1
                # Flushed periodically, not only at exit. A full pass is thousands of calls over
                # the better part of an hour, and a run that is killed partway -- which has
                # happened twice -- used to lose every answer it had bought, because the only
                # write was in the `finally`. Written to a temp file and moved into place so a
                # kill during the write cannot leave a half-written cache behind either.
                if _stats["call"] % _CACHE_FLUSH_EVERY == 0:
                    _flush_cache()
            return text
        except urllib.error.HTTPError as error:
            detail = error.read().decode("utf-8", "replace")[:400]
            if error.code in (429, 500, 502, 503, 504):
                time.sleep(min(2 ** attempt * 2, 45))
                last_error = detail
                continue
            if error.code == 400 and "max_completion_tokens" in detail:
                body.pop("max_completion_tokens", None)
                body["max_tokens"] = max_tokens
                continue
            raise SystemExit(f"reference model rejected a call ({error.code}): {detail}")
        except (urllib.error.URLError, TimeoutError) as error:
            last_error = str(error)
            time.sleep(min(2 ** attempt * 2, 45))
    raise SystemExit(f"reference model unreachable after retries: {last_error}")


# --------------------------------------------------------------------------------------
# Context assembly and match detection
# --------------------------------------------------------------------------------------

def render(sessions: list[dict], dates: list[str]) -> str:
    blocks = []
    for index, (session, date) in enumerate(zip(sessions, dates), start=1):
        turns = "\n".join(f"{t['role']}: {t['content']}" for t in session)
        blocks.append(f"### Session {index} ({date})\n{turns}")
    return "\n\n".join(blocks)


def ask(question: str, question_date: str, context: str) -> str:
    if context:
        return (
            f"Here is a record of earlier conversations.\n\n{context}\n\n"
            f"Current Date: {question_date}\n"
            f"Answer the question using only what those conversations tell you. If they do not "
            f"contain the answer, say so plainly.\n\nQuestion: {question}\nAnswer:"
        )
    return (
        f"Current Date: {question_date}\n"
        f"Answer the question. If you have no way of knowing, say so plainly.\n\n"
        f"Question: {question}\nAnswer:"
    )


# Thousands separators are part of a number ("1,200"); a sentence comma is not. The earlier
# pattern `\d[\d,]*\.?\d*` was greedy enough to swallow the trailing comma in "...on 14 April
# 2026, which puts...", producing the token "2026," -- which then failed to match the bare "2026"
# the prompt itself supplied, so the already-known subtraction below silently did nothing and a
# year the model was HANDED counted as evidence it had reached the corpus.
_NUMBER = re.compile(r"\d+(?:,\d{3})*(?:\.\d+)?")
_PROPER = re.compile(r"\b[A-Z][a-z]{3,}\b")

#: Calendar vocabulary is world knowledge, not corpus content. Every vertical here is about dates,
#: so month and weekday names appear throughout every haystack and a model reasoning out loud about
#: any timeline emits them unprompted. Counting them as "distinctive" let a gold-ablated answer that
#: explicitly said "the conversations don't say when a decision should arrive" clear the screen on
#: the word "June" and reach a judge lenient enough to call it a match. The date a gold fact turns
#: on is still protected: it is the day and year numerals that carry it, not the month's name.
_CALENDAR = frozenset("""
january february march april may june july august september october november december
monday tuesday wednesday thursday friday saturday sunday
""".split())


def distinctive(gold: str, already_known: str = "") -> list[str]:
    """Tokens whose presence in a response is evidence it reached the gold fact.

    Numbers and proper nouns, because those are what the generators randomize -- they are the part
    of an answer that cannot be produced by knowing the world, only by knowing the corpus.

    Tokens the model was ALREADY given are removed. The question and the current date are in every
    prompt, so a gold answer reading "...on 20 March 2026" shares "2026" with the prompt itself; a
    model with no evidence that echoes the year would clear a screen meant to detect that it had
    reached the answer. That is how the ablation probes reported a leak on a question whose gold is
    simply a negative.
    """
    known = set(_NUMBER.findall(already_known))
    known |= {w.lower() for w in _PROPER.findall(already_known)}

    numbers = {t for t in _NUMBER.findall(gold) if len(t) >= 2}
    words = {w for w in _PROPER.findall(gold)
             if len(w) > 2 and w.lower() not in tmc.STOPWORDS and w.lower() not in _CALENDAR}
    # Two digits, not three. A day of the month is exactly the kind of randomised content this
    # screen exists to look for -- "falls due on 14 April" turns on the 14 -- and the old
    # three-character floor threw every one of them away, which left most of Prospective's gold with
    # no distinctive content at all and made 30 of its 50 questions undecidable for V3. Single
    # digits stay out: they are too common to mean anything, and "2" appears in almost any answer
    # that mentions a date.
    return sorted(
        t for t in numbers | words
        if t not in known and t.lower() not in known)


def lexically_possible(response: str, gold: str, already_known: str = "") -> bool:
    """Over-sensitive on purpose: a false positive costs one judge call, a false negative would
    silently pass an invalid question."""
    tokens = distinctive(gold, already_known)
    if not tokens:
        # No distinctive token to key on, so the screen cannot rule anything out and every response
        # must be judged. Rare, and erring toward more judging is the safe direction.
        return True
    # Word boundaries, not substrings. Now that two-digit numbers count, a plain `in` test would
    # match "14" inside "2014" or "1400" and hand the judge a response that never named the value.
    lowered = response.lower()
    return any(re.search(rf"(?<![\w]){re.escape(token.lower())}(?![\w])", lowered)
               for token in tokens)


def judged_equivalent(question: str, gold: str, response: str, cache_key: str) -> bool:
    verdict = complete(
        "You are checking whether a response conveys the same answer as a reference answer.\n"
        "Ignore wording, length, and extra detail: a short answer that states the same fact is a "
        "match, and a long answer that never states it is not.\n"
        "Reply with exactly one word: yes or no.\n\n"
        f"QUESTION\n{question}\n\nREFERENCE ANSWER\n{gold}\n\nRESPONSE\n{response}\n\n"
        "Does the response convey the reference answer?",
        cache_key=cache_key,
        # Generous on purpose. On a reasoning deployment the completion budget covers reasoning
        # tokens too, so a tight budget returns an EMPTY message rather than a short one -- which
        # this parser would read as "no" and silently fail every question it was asked about.
        max_tokens=1500,
    )
    return verdict.strip().lower().startswith("yes")


def produced_gold(
    question: str,
    gold: str,
    response: str,
    cache_key: str,
    *,
    screen: bool = False,
    require_distinctive: bool = False,
    already_known: str = "",
) -> bool:
    """Whether a response reached the gold fact.

    `screen` enables the cheap lexical pre-filter, and is used only for V2, where ten samples per
    question make judging everything expensive and where a blind guess landing on a randomly-drawn
    fact is what the screen is looking for anyway. V1 judges unconditionally: a screen
    false-negative there would reject a valid question.

    V3 and V6 judge only when the gold's distinctive value is actually present in the response
    (`require_distinctive`). That is not a cost trade — it is the ablation probes' one defence
    against a lenient judge. Where gold names a specific value, "produced the gold" has to mean
    reproducing THAT; otherwise a gold answer phrased as a negative is satisfied by a model that saw
    nothing and said so, and the probe reports a leak where there is only an empty context.
    """
    if not response:
        return False
    if screen and not lexically_possible(response, gold, already_known):
        _stats["screen_rejected"] += 1
        return False
    # Ablation probes (V3, V6) ask whether removing evidence removes the answer. Where gold names a
    # specific value, "produced the gold" has to mean reproducing THAT -- otherwise a gold answer
    # phrased as a negative ("no longer valid", "no current car on record") is satisfied by a model
    # that saw nothing and said so, and the probe reports a leak where there is only an empty
    # context. The judge still decides; this only refuses to call a value-free answer a reproduction.
    if (require_distinctive and distinctive(gold, already_known)
            and not lexically_possible(response, gold, already_known)):
        _stats["distinctive_absent"] += 1
        return False
    _stats["escalated"] += 1
    return judged_equivalent(question, gold, response, cache_key)


# --------------------------------------------------------------------------------------
# Probes
# --------------------------------------------------------------------------------------

_NEGATIVE_GOLD = re.compile(
    r"^\s*(no|not|none)\b|no longer|already happened|not yet|no record|never (told|mentioned|recorded)",
    re.IGNORECASE)


#: Shapes whose answer is one of a NAMED, closed set small enough that guessing beats the probe.
#:
#: An ablation probe cannot tell "reached the evidence" from "flipped a coin" when the question hands
#: the model the candidates. Temporal's `occurrence-order` names two events and asks which came
#: first, so a model with the gold removed is right half the time by construction -- and it measured
#: 6 leaks in 20, BELOW the 50% chance rate, which is the signature of guessing rather than of a
#: leak. Reporting those as leaks would assert something this probe cannot see.
#:
#: The precedent is ADR-026's: Forgetting's two-way shape is bounded by V2 (ten zero-context samples)
#: rather than by V3, for exactly this reason. Scoped by SHAPE rather than by vertical, because it is
#: a property of the question form and the next vertical with a two-way shape will inherit it.
_V3_GUESSABLE_SHAPES = {"occurrence-order"}


def _v3_decidable(gold: str, already_known: str) -> bool:
    """Whether an ablation probe can tell "reached the answer" from "said nothing" for this gold.

    It cannot when BOTH are true: the gold carries no content the prompt did not already supply,
    and the gold is itself a negative. "No, it has already happened" is what a model with no
    evidence says when it has no record either, so a match proves nothing and a leak report would
    assert something the probe cannot see.

    Deliberately narrow. An earlier version abstained whenever the distinctive set was empty, which
    silently dropped V3 on 56 questions -- every list-order gold repeats items the question already
    named, and every attribution gold is a role word. Those are perfectly decidable by the judge,
    because "I don't know" is not equivalent to "the assistant said it".
    """
    if distinctive(gold, already_known):
        return True
    return not _NEGATIVE_GOLD.search(gold.strip())


def gold_indices(entry: dict) -> list[int]:
    ids = entry.get("haystack_session_ids", [])
    return [ids.index(a) for a in entry.get("answer_session_ids", []) if a in ids]


def subset(entry: dict, keep: list[int]) -> str:
    sessions = [entry["haystack_sessions"][i] for i in keep]
    dates = [entry["haystack_dates"][i] for i in keep]
    return render(sessions, dates)


def question_key(entry: dict) -> str:
    """Cache key prefix derived from the question's CONTENT, not its id.

    Question ids are deliberately stable across regenerations, so an id-keyed cache replays a
    previous corpus's answers into a rebuilt corpus's records -- which is exactly what happened
    once here, and it is invisible: the record binds to the new corpus hash while the answers
    inside it describe the old questions.
    """
    material = json.dumps(
        [entry["question"], entry["answer"], entry.get("question_date"),
         entry.get("haystack_sessions"), entry.get("haystack_dates")],
        sort_keys=True, ensure_ascii=False)
    return hashlib.sha256(material.encode("utf-8")).hexdigest()[:16]


def probe_question(entry: dict, vertical: str) -> dict:
    qid = entry["question_id"]
    key = question_key(entry)
    question = entry["question"]
    gold = entry["answer"]
    date = entry["question_date"]
    golds = gold_indices(entry)
    everything = list(range(len(entry["haystack_sessions"])))
    non_gold = [i for i in everything if i not in golds]
    record: dict = {"question_id": qid}

    # V1 -- the ceiling. A question the reference model cannot answer with perfect retrieval is
    # measuring the model, not the memory system, and does not belong in the corpus.
    if golds:
        answer = complete(ask(question, date, subset(entry, golds)), cache_key=f"{key}:v1")
        record["v1"] = produced_gold(question, gold, answer, f"{key}:v1:judge")
        record["v1_answer"] = answer[:300]
    else:
        # A never-known probe has no gold to retrieve; its ceiling behaviour is to abstain, which
        # V1 as written cannot express. Recorded as not-applicable rather than silently passed.
        record["v1"] = None

    # V8 -- interference cost, and the ONLY difference from V1 is the context. Same question, same
    # judge, same screen, same applicability rule, so V1 - V8 is a like-for-like difference on one
    # question set rather than two numbers that happen to share a scale.
    #
    #   V1  accuracy given the gold sessions alone      -- perfect retrieval
    #   V8  accuracy given the entire haystack          -- no retrieval at all
    #   V1 - V8  is the room retrieval quality has to matter.
    #
    # Where V8 == V1 a perfect retriever and no retriever produce the same answer, so no two
    # retrievers can be told apart on that corpus however good one of them is. That is LongMemEval's
    # saturation stated as a measurement instead of a complaint, and it is the quantity the consuming
    # project needs: their stack reads realised coverage 1.000 against our calibrated BM25 floor of
    # 0.636, and coverage is a proxy for exactly this.
    #
    # Not applicable wherever V1 is not applicable -- a never-known probe has no gold to reach, so
    # `produced_gold` has nothing to decide and a bare 0 would read as a failure rather than as an
    # undefined case.
    if golds:
        answer = complete(ask(question, date, subset(entry, everything)), cache_key=f"{key}:v8")
        record["v8"] = produced_gold(question, gold, answer, f"{key}:v8:judge")
        record["v8_answer"] = answer[:300]
    else:
        record["v8"] = None

    # V2 -- non-inferability. Sampled at the provider default temperature, k=10.
    #
    # Not applicable to a never-known probe. Its gold IS an abstention, and "I have no way of
    # knowing" is exactly what a model with no context says -- so the probe would reject every one
    # of them for being guessable, when what it actually measured is that the corpus asked for a
    # negative and got one. Recorded as not-applicable rather than scored, the same way V1 is.
    if golds:
        hits = 0
        for k in range(V2_SAMPLES):
            answer = complete(ask(question, date, ""), cache_key=f"{key}:v2:{k}", max_tokens=700)
            if produced_gold(question, gold, answer, f"{key}:v2:{k}:judge", screen=True,
                         already_known=f"{question} {date}"):
                hits += 1
        record["v2_hits"] = hits
        record["v2"] = hits < V2_REJECT_AT
    else:
        record["v2"] = None

    # V3 -- gold-ablated. The dual of V1: with every gold session removed, the answer must be gone.
    #
    # Not applicable when gold carries no content the prompt did not already supply. A gold answer
    # that is purely a negative -- "no, it has already happened" -- is indistinguishable from a model
    # with no evidence saying it has no record, and reporting that as a leak would assert something
    # this probe cannot see. Recorded as not-applicable, exactly as V1 and V2 are on a never-known
    # probe.
    decidable = (_v3_decidable(gold, f"{question} {date}")
                 and (entry.get("typedmemeval") or {}).get("shape") not in _V3_GUESSABLE_SHAPES)
    if non_gold and golds and decidable:
        leaked = False
        for k in range(ABLATION_SAMPLES):
            answer = complete(ask(question, date, subset(entry, non_gold)), cache_key=f"{key}:v3:{k}")
            if produced_gold(question, gold, answer, f"{key}:v3:{k}:judge",
                             require_distinctive=True, already_known=f"{question} {date}"):
                leaked = True
                break
        record["v3"] = not leaked
        record["ablation_samples"] = ABLATION_SAMPLES
    else:
        record["v3"] = None

    # V6 -- leave-one-out. Scoped to the two verticals the ADR defines it for (Arithmetic inputs,
    # Forgetting's statement + invalidation), because those are the ones whose per-component
    # coverage echo depends on every component being load-bearing. Elsewhere a question can have
    # two gold sessions without the design claiming both are individually necessary, and applying
    # the rule there would report a corpus defect the corpus never promised not to have.
    if len(golds) > 1 and vertical in ("arithmetic", "forgetting") and decidable:
        survived = []
        for dropped in golds:
            keep = [i for i in everything if i != dropped]
            # Sampled, for the same reason as V3: one draw can miss a component that is in fact
            # redundant, and a component wrongly called load-bearing inflates per-component coverage.
            # `already_known` matters here too — without it the screen counts the year and the
            # numbers the prompt itself supplied as evidence the model reached the gold, which is
            # precisely the false positive the subtraction exists to remove.
            for k in range(ABLATION_SAMPLES):
                answer = complete(ask(question, date, subset(entry, keep)),
                                  cache_key=f"{key}:v6:{dropped}:{k}")
                if produced_gold(
                        question, gold, answer, f"{key}:v6:{dropped}:{k}:judge",
                        require_distinctive=True, already_known=f"{question} {date}"):
                    survived.append(dropped)
                    break
        record["v6"] = not survived
        record["v6_redundant_components"] = survived
    else:
        record["v6"] = None

    return record


def _interference(records: list[dict]) -> dict:
    """V1 vs V8 on the questions both are defined on, and the gap between them."""
    both = [r for r in records if r.get("v1") is not None and r.get("v8") is not None]
    if not both:
        return {"applicable": 0, "v1_passed": 0, "v8_passed": 0, "interference_cost": None,
                "not_applicable_reason": "no question in this corpus has gold for V1 to be defined on"}
    v1 = sum(1 for r in both if r["v1"])
    v8 = sum(1 for r in both if r["v8"])
    return {
        "applicable": len(both),
        "v1_passed": v1,
        "v8_passed": v8,
        "interference_cost": round((v1 - v8) / len(both), 4),
        "regressed_under_interference": sorted(
            r["question_id"] for r in both if r["v1"] and not r["v8"]),
        "reading": ("V1 minus V8 as a share of the questions both are defined on. 0.0 means a "
                    "perfect retriever and no retriever produce the same answers here, so no two "
                    "retrievers can be distinguished on this corpus."),
    }


def probe_vertical(vertical: str, limit: int | None, workers: int) -> dict:
    corpus_id = f"agenteval-typedmemeval-{vertical}-{tmc.CORPUS_REVISION}"
    directory = tmc.DATA_ROOT / vertical
    corpus_text = (directory / f"{corpus_id}.json").read_text(encoding="utf-8")
    entries = json.loads(corpus_text)
    if limit:
        entries = entries[:limit]

    print(f"{vertical}: probing {len(entries)} questions ...", flush=True)
    with ThreadPoolExecutor(max_workers=workers) as pool:
        records = list(pool.map(lambda e: probe_question(e, vertical), entries))

    by_id = {r["question_id"]: r for r in records}
    shapes = {e["question_id"]: (e.get("typedmemeval") or {}).get("shape") for e in entries}

    # Pair flip (V1p). Both arms must be answerable AND their answers must differ, which is what
    # makes the before/after design capable of showing anything at all.
    pairs: dict[str, list[dict]] = {}
    for entry in entries:
        pid = entry.get("typedmemeval", {}).get("pair_id")
        if pid:
            pairs.setdefault(pid, []).append(entry)

    pair_records = []
    for pid, arms in sorted(pairs.items()):
        if len(arms) != 2:
            continue
        a, b = arms
        ra, rb = by_id[a["question_id"]], by_id[b["question_id"]]
        flipped = (ra.get("v1_answer", "").strip().lower() !=
                   rb.get("v1_answer", "").strip().lower())
        pair_records.append({
            "pair_id": pid,
            "both_answerable": bool(ra.get("v1")) and bool(rb.get("v1")),
            "answers_differ": flipped,
            "passed": bool(ra.get("v1")) and bool(rb.get("v1")) and flipped,
        })

    def tally(key):
        applicable = [r for r in records if r.get(key) is not None]
        return {
            "applicable": len(applicable),
            "passed": sum(1 for r in applicable if r[key]),
            "failed": sorted(r["question_id"] for r in applicable if not r[key]),
        }

    return {
        "status": "run",
        # The deployment NAME, which is what the caller controls and not a model identity: a
        # deployment can be renamed or repointed at a different model without the record changing.
        # Stated as what it is so nobody reads it as a pinned model version.
        "reference_deployment": os.environ.get("AZURE_OPENAI_DEPLOYMENT", "unknown"),
        "reference_model_note": (
            "Azure deployment name, not a model identity — a deployment may be repointed at a "
            "different model without this value changing."
        ),
        "run_at": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "questions_probed": len(entries),
        "match_detection": (
            "over-sensitive lexical screen on distinctive tokens, escalated to a reference-model "
            "yes/no equivalence judgment; only the judgment decides a match"
        ),
        "v1_oracle_answerability": tally("v1"),
        "v1_pair_flip": {
            "pairs": len(pair_records),
            "passed": sum(1 for p in pair_records if p["passed"]),
            "failed": sorted(p["pair_id"] for p in pair_records if not p["passed"]),
        },
        "ablation_samples_per_question": ABLATION_SAMPLES,
        "v2_non_inferability": {
            "samples_per_question": V2_SAMPLES,
            "reject_at_hits": V2_REJECT_AT,
            "temperature": "provider default",
            **tally("v2"),
        },
        "v3_gold_ablated": {
            **tally("v3"),
            "not_decidable": sorted(
                r["question_id"] for r in records
                if r.get("v3") is None and r.get("v1") is not None),
            "not_decidable_for_guessable_shapes": sorted(_V3_GUESSABLE_SHAPES),
            "not_decidable_reason": (
                "gold carries no content the prompt did not already supply, so a no-evidence answer "
                "and a correct one are indistinguishable to this probe"
            ),
        },
        # V6 is defined for Arithmetic and Forgetting only, and only where G > 1. Everywhere else
        # it is UNDEFINED rather than failed, so a bare `passed: 0` misreports it: Episodic
        # published 0 beside four verticals reporting real counts, which reads as "every question
        # failed" when the probe never ran. Says which of the two reasons applies.
        "v6_leave_one_out": {
            **tally("v6"),
            "applies_to": (
                "Arithmetic and Forgetting, on questions with more than one gold session. Those are "
                "the verticals whose design claims every gold component is load-bearing; elsewhere "
                "a question may have several gold sessions without the design promising each one is "
                "individually necessary, and ablating them would report a defect never promised "
                "against"
            ),
            **({"not_applicable_reason": (
                f"V6 is not defined for {vertical}" if vertical not in ("arithmetic", "forgetting")
                else "every question in this corpus has a single gold component, so leave-one-out "
                     "removes the only evidence there is")}
               if not [r for r in records if r.get("v6") is not None] else {}),
        },
        # Per SHAPE, not only per vertical. A vertical reported at 48/50 hides Arithmetic's
        # `duration` at 83% and Episodic's `participant-attribution` at 87%, and the consuming
        # project found a shape running at 50% on their answer model that our per-vertical
        # figure gave no way to see. Where a shape's V1 is well below the vertical's, its
        # numbers are answer-model variance more than memory signal, and a reader is entitled
        # to know that before quoting them.
        # The headline of ADR-027 SS6. `interference_cost` is V1 - V8 as a share of the questions
        # both are defined on: 0.0 means a perfect retriever buys nothing on this corpus.
        "v8_full_haystack": _interference(records),
        "by_shape": {
            shape: {
                "questions": len(group),
                "v1_applicable": sum(1 for r in group if r.get("v1") is not None),
                "v1_passed": sum(1 for r in group if r.get("v1") is True),
                "v3_applicable": sum(1 for r in group if r.get("v3") is not None),
                "v3_passed": sum(1 for r in group if r.get("v3") is True),
            }
            for shape, group in sorted(
                {s: [r for r in records if shapes.get(r["question_id"]) == s]
                 for s in sorted({v for v in shapes.values() if v})}.items())
        },
        "per_question": {
            r["question_id"]: {k: v for k, v in r.items()
                               if k not in ("question_id", "v1_answer")}
            for r in records
        },
    }


def self_test() -> None:
    """Asserts the evidence screen on cases that previously fooled it. Pure functions, no
    credentials, so CI can run it on every push while the probes themselves cannot.

    A probe is an instrument, and an instrument that has been wrong needs a calibration check of
    its own. Each case below is a real defect this screen shipped with, not a hypothetical.
    """
    failures: list[str] = []

    def check(label: str, actual, expected) -> None:
        if actual != expected:
            failures.append(f"{label}: expected {expected!r}, got {actual!r}")

    # A year the prompt itself supplied must never count as evidence, even when the gold sentence
    # punctuates it. The greedy number pattern used to yield "2026," here, which matched nothing in
    # the prompt and so survived the subtraction.
    gold = ("No. They quoted eight weeks from the application on 14 April 2026, which puts the "
            "decision around 9 June 2026 - roughly 2 weeks after the date you are asking.")
    known = "Should I have heard back about the visa application by now? 2026/05/26 (Tue) 09:00"
    # The day survives -- it is the randomised part of the fact -- while the year the prompt handed
    # over does not, and neither do the month names.
    check("day survives, prompt-supplied year does not", distinctive(gold, known), ["14"])

    # ...and the response that triggered the false leak still fails the screen, because it names
    # other numbers but never the day the gold answer turns on.
    ablated_numbers = ("probably not yet: the interview was said on May 7 and May 10 to be in three "
                       "weeks or so. Today is May 26.")
    check("wrong numbers do not clear the screen",
          lexically_possible(ablated_numbers, gold, known), False)

    # A two-digit token must match as a word, not inside a longer number.
    check("no substring match inside a longer number",
          lexically_possible("It was 2014 all along.", "Due on 20 May.", ""), False)

    # Thousands separators are still part of the number they punctuate.
    check("thousands separator survives", distinctive("The balance was 12,400 euros."), ["12,400"])

    # Month and weekday names are world knowledge in a corpus family made of dates.
    check("month name is not distinctive", distinctive("It renews in September."), [])
    check("weekday name is not distinctive", distinctive("She flies Thursday."), [])

    # ...but a genuinely corpus-specific proper noun still is.
    check("rare proper noun is distinctive", distinctive("The policy is with Aviva."), ["Aviva"])

    # The whole point of the screen: an ablated answer that disclaims knowledge must not clear it.
    # This exact response was judged a match and reported a leak that did not exist.
    ablated = ("Based on the most recent notes, probably not yet: the visa interview was said to be "
               "\"in three weeks or so\", which puts it around late May or early June. Today is May 26. "
               "The conversations don't say when a decision or reply should arrive, though.")
    check("disclaiming answer fails the screen",
          lexically_possible(ablated, gold, known) and bool(distinctive(gold, known)), False)

    # And with no distinctive content left, a negative gold is one the ablation probe must refuse to
    # judge rather than score -- "not yet" is what a model with no evidence says too.
    # With a distinctive day present, this one IS decidable again -- the probe can tell "named the
    # date" from "said nothing", which is the whole question.
    check("negative gold with distinctive content is decidable", _v3_decidable(gold, known), True)

    # ...but strip the content and it must abstain rather than score.
    bare = "No, not yet."
    check("negative gold with no content is undecidable", _v3_decidable(bare, known), False)

    if failures:
        for f in failures:
            print(f"self-test FAIL  {f}")
        raise SystemExit(1)
    print("self-test OK  (10 evidence-screen cases)")


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("verticals", nargs="*", default=[], help="verticals to probe; default all")
    parser.add_argument("--limit", type=int, default=None, help="probe only the first N questions")
    parser.add_argument("--workers", type=int, default=8)
    parser.add_argument("--self-test", action="store_true",
                        help="check the evidence screen against known-bad cases; needs no credentials")
    args = parser.parse_args()

    if args.self_test:
        self_test()
        return

    if CACHE_PATH.exists():
        _cache.update(json.loads(CACHE_PATH.read_text(encoding="utf-8")))
        print(f"resuming with {len(_cache)} cached completions")

    targets = args.verticals or list(tmc.VERTICALS)
    try:
        for vertical in targets:
            probes = probe_vertical(vertical, args.limit, args.workers)
            # A --limit run is a smoke test, and its record is a partial measurement. Writing it
            # REPLACED the full record: an 8-question run left Forgetting's metadata reading
            # "V1 8/8" where the shipped number is 35/35, with nothing in the file to say the
            # difference was a truncation rather than a corpus that shrank. Same failure as every
            # other one this family has had -- a partial measurement stored where a measurement is
            # expected -- so the smoke test now prints and writes nothing.
            if args.limit:
                print(f"{vertical}: --limit run, metadata NOT written (partial measurement)",
                      flush=True)
                continue
            corpus_id = f"agenteval-typedmemeval-{vertical}-{tmc.CORPUS_REVISION}"
            meta_path = tmc.DATA_ROOT / vertical / f"{corpus_id}.meta.json"
            metadata = json.loads(meta_path.read_text(encoding="utf-8"))

            # Bind the records to the corpus text they describe. Re-running the probes must never
            # move the corpus hash, and a metadata file describing a corpus that has since been
            # regenerated must be detectable rather than quietly believed.
            corpus_text = (tmc.DATA_ROOT / vertical / f"{corpus_id}.json").read_text(encoding="utf-8")
            probes["probed_corpus_sha256"] = tmc.sha256_normalized(corpus_text)

            # Merged, not replaced. V7 is stamped by a separate model-free tool, and replacing the
            # whole block silently dropped its record — a probe that vanishes when a neighbouring
            # probe re-runs is worse than one that was never taken.
            metadata.setdefault("probes", {}).update(probes)
            meta_path.write_text(
                json.dumps(metadata, indent=2, ensure_ascii=False) + "\n",
                encoding="utf-8", newline="\n")

            v1 = probes["v1_oracle_answerability"]
            v2 = probes["v2_non_inferability"]
            v3 = probes["v3_gold_ablated"]
            v6 = probes["v6_leave_one_out"]
            print(
                f"{vertical}: V1 {v1['passed']}/{v1['applicable']}  "
                f"V1pair {probes['v1_pair_flip']['passed']}/{probes['v1_pair_flip']['pairs']}  "
                f"V2 {v2['passed']}/{v2['applicable']}  "
                f"V3 {v3['passed']}/{v3['applicable']}  "
                f"V6 {v6['passed']}/{v6['applicable']}  "
                f"V8 {probes['v8_full_haystack'].get('v8_passed','-')}/"
                f"{probes['v8_full_haystack'].get('applicable','-')}  "
                f"interference {probes['v8_full_haystack'].get('interference_cost')}",
                flush=True)
    finally:
        with _cache_lock:
            _flush_cache()
        print(f"calls={_stats['call']} cached={_stats['cache_hit']} "
              f"screened-out={_stats['screen_rejected']} escalated={_stats['escalated']}")


if __name__ == "__main__":
    main()
