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


_NUMBER = re.compile(r"\d[\d,]*\.?\d*")
_PROPER = re.compile(r"\b[A-Z][a-z]{3,}\b")


def distinctive(gold: str) -> list[str]:
    """Tokens whose presence in a response is evidence it reached the gold fact.

    Numbers and proper nouns, because those are what the generators randomize -- they are the part
    of an answer that cannot be produced by knowing the world, only by knowing the corpus.
    """
    tokens = set(_NUMBER.findall(gold))
    tokens |= {w for w in _PROPER.findall(gold) if w.lower() not in tmc.STOPWORDS}
    return sorted(t for t in tokens if len(t) > 2)


def lexically_possible(response: str, gold: str) -> bool:
    """Over-sensitive on purpose: a false positive costs one judge call, a false negative would
    silently pass an invalid question."""
    tokens = distinctive(gold)
    if not tokens:
        # No distinctive token to key on, so the screen cannot rule anything out and every response
        # must be judged. Rare, and erring toward more judging is the safe direction.
        return True
    lowered = response.lower()
    return any(token.lower() in lowered for token in tokens)


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
) -> bool:
    """Whether a response reached the gold fact.

    `screen` enables the cheap lexical pre-filter, and is used only for V2, where ten samples per
    question make judging everything expensive and where a blind guess landing on a randomly-drawn
    fact is what the screen is looking for anyway. Every other probe judges unconditionally: for
    V1 a screen false-negative would reject a valid question, and for V3 and V6 it would PASS an
    invalid one, which is the direction that must never be traded for cost.
    """
    if not response:
        return False
    if screen and not lexically_possible(response, gold):
        _stats["screen_rejected"] += 1
        return False
    # Ablation probes (V3, V6) ask whether removing evidence removes the answer. Where gold names a
    # specific value, "produced the gold" has to mean reproducing THAT -- otherwise a gold answer
    # phrased as a negative ("no longer valid", "no current car on record") is satisfied by a model
    # that saw nothing and said so, and the probe reports a leak where there is only an empty
    # context. The judge still decides; this only refuses to call a value-free answer a reproduction.
    if require_distinctive and distinctive(gold) and not lexically_possible(response, gold):
        _stats["distinctive_absent"] += 1
        return False
    _stats["escalated"] += 1
    return judged_equivalent(question, gold, response, cache_key)


# --------------------------------------------------------------------------------------
# Probes
# --------------------------------------------------------------------------------------

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
            if produced_gold(question, gold, answer, f"{key}:v2:{k}:judge", screen=True):
                hits += 1
        record["v2_hits"] = hits
        record["v2"] = hits < V2_REJECT_AT
    else:
        record["v2"] = None

    # V3 -- gold-ablated. The dual of V1: with every gold session removed, the answer must be gone.
    if non_gold and golds:
        answer = complete(ask(question, date, subset(entry, non_gold)), cache_key=f"{key}:v3")
        record["v3"] = not produced_gold(
            question, gold, answer, f"{key}:v3:judge", require_distinctive=True)
    else:
        record["v3"] = None

    # V6 -- leave-one-out. Scoped to the two verticals the ADR defines it for (Arithmetic inputs,
    # Forgetting's statement + invalidation), because those are the ones whose per-component
    # coverage echo depends on every component being load-bearing. Elsewhere a question can have
    # two gold sessions without the design claiming both are individually necessary, and applying
    # the rule there would report a corpus defect the corpus never promised not to have.
    if len(golds) > 1 and vertical in ("arithmetic", "forgetting"):
        survived = []
        for dropped in golds:
            keep = [i for i in everything if i != dropped]
            answer = complete(ask(question, date, subset(entry, keep)), cache_key=f"{key}:v6:{dropped}")
            if produced_gold(
                    question, gold, answer, f"{key}:v6:{dropped}:judge",
                    require_distinctive=True):
                survived.append(dropped)
        record["v6"] = not survived
        record["v6_redundant_components"] = survived
    else:
        record["v6"] = None

    return record


def probe_vertical(vertical: str, limit: int | None, workers: int) -> dict:
    corpus_id = f"agenteval-typedmemeval-{vertical}-v1"
    directory = tmc.DATA_ROOT / vertical
    corpus_text = (directory / f"{corpus_id}.json").read_text(encoding="utf-8")
    entries = json.loads(corpus_text)
    if limit:
        entries = entries[:limit]

    print(f"{vertical}: probing {len(entries)} questions ...", flush=True)
    with ThreadPoolExecutor(max_workers=workers) as pool:
        records = list(pool.map(lambda e: probe_question(e, vertical), entries))

    by_id = {r["question_id"]: r for r in records}

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
        "v2_non_inferability": {
            "samples_per_question": V2_SAMPLES,
            "reject_at_hits": V2_REJECT_AT,
            "temperature": "provider default",
            **tally("v2"),
        },
        "v3_gold_ablated": tally("v3"),
        "v6_leave_one_out": tally("v6"),
        "per_question": {
            r["question_id"]: {k: v for k, v in r.items()
                               if k not in ("question_id", "v1_answer")}
            for r in records
        },
    }


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("verticals", nargs="*", default=[], help="verticals to probe; default all")
    parser.add_argument("--limit", type=int, default=None, help="probe only the first N questions")
    parser.add_argument("--workers", type=int, default=8)
    args = parser.parse_args()

    if CACHE_PATH.exists():
        _cache.update(json.loads(CACHE_PATH.read_text(encoding="utf-8")))
        print(f"resuming with {len(_cache)} cached completions")

    targets = args.verticals or list(tmc.VERTICALS)
    try:
        for vertical in targets:
            probes = probe_vertical(vertical, args.limit, args.workers)
            corpus_id = f"agenteval-typedmemeval-{vertical}-v1"
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
                f"V6 {v6['passed']}/{v6['applicable']}",
                flush=True)
    finally:
        CACHE_PATH.write_text(json.dumps(_cache, ensure_ascii=False), encoding="utf-8")
        print(f"calls={_stats['call']} cached={_stats['cache_hit']} "
              f"screened-out={_stats['screen_rejected']} escalated={_stats['escalated']}")


if __name__ == "__main__":
    main()
