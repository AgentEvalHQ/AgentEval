# LongMemEval: How It Works & AgentEval Integration

> Reference: LongMemEval (ICLR 2025) by Di Wu et al.
> Repo: https://github.com/xiaowu0162/LongMemEval
> License: MIT

---

## 1. What LongMemEval Measures

LongMemEval is a benchmark for evaluating **long-term memory** in chat assistants. It tests whether an LLM can find and reason over facts buried in a long conversation history (~115K tokens for the S dataset).

### The 6 Question Types

| Type | Count | Abstention | What It Tests |
|------|:-----:|:----------:|---------------|
| `single-session-user` | 70 | 6 | Recall a fact the user mentioned in one session |
| `single-session-assistant` | 56 | 0 | Recall something the assistant previously said |
| `single-session-preference` | 30 | 0 | Infer a user preference from conversation context |
| `multi-session` | 133 | 12 | Synthesize facts scattered across 2+ sessions |
| `temporal-reasoning` | 133 | 6 | Order events or compute time between them |
| `knowledge-update` | 78 | 6 | Return the *latest* version of an updated fact |
| **Total** | **500** | **30** | |

30 questions are **abstention** tests (suffix `_abs` in `question_id`) where the correct answer is "I don't know."

---

## 2. The Three Dataset Modes

All three modes contain the **same 500 questions**. The difference is the haystack surrounding the evidence sessions.

| Mode | File | Sessions/Question | Tokens/Question | Purpose |
|------|------|:-----------------:|:---------------:|---------|
| **Oracle** | `longmemeval_oracle.json` | Only evidence (1-6) | ~2-10K | Upper bound — no retrieval needed |
| **S (Small)** | `longmemeval_s_cleaned.json` | ~40-62 (avg 48) | ~115K | Fits 128K context window |
| **M (Medium)** | `longmemeval_m_cleaned.json` | ~500 | >128K | Requires retrieval/RAG |

### Haystack Composition (S mode)

The haystack is built from 3 sources in a 50%/25%/25% ratio:
- **User-simulated sessions** — synthetic conversations matching the user persona
- **ShareGPT conversations** — real anonymized chat logs
- **UltraChat conversations** — synthetic multi-turn dialogues

Filler sessions are filtered to avoid conflicting user attributes. Evidence sessions (containing the answer) are identified by `answer_session_ids` and marked with `has_answer: true` on relevant turns (stripped before evaluation).

---

## 3. How the Official Benchmark Runs

### Evaluation Pipeline

```
1. Load dataset (oracle/S/M)
2. For each question:
   a. Format haystack as chat history
   b. Prepend system prompt: "I will give you several history chats..."
   c. Send history + question to LLM → get hypothesis
   d. Send (question, gold_answer, hypothesis) to LLM judge → yes/no
3. Aggregate scores per type and overall
```

### Scoring: Binary LLM Judge (not partial)

The official scoring is **binary** (0 or 1), not 0-100. The judge (GPT-4o) answers "yes" or "no" to whether the hypothesis matches the gold answer.

**Critical: Different judge prompts per question type:**

| Type | Judge Behavior |
|------|---------------|
| `single-session-user/assistant` | Strict — must contain correct answer, subset = fail |
| `single-session-preference` | Lenient — "correct as long as it recalls and utilizes the user's personal information" |
| `temporal-reasoning` | Tolerant — allows off-by-one errors on day/week/month counts |
| `knowledge-update` | Tolerant — old info alongside updated answer is still correct |
| Abstention (`_abs`) | Different goal — correct if model says "unanswerable" / "not mentioned" |

### Three Reading Methods

| Method | CoT | How It Works |
|--------|:---:|--------------|
| `direct` | No | Full history + question in one prompt |
| `con` | Yes | "First extract relevant info, then reason to get answer" — single prompt |
| `con-separate` | Yes | Two-stage: extract notes per session, then answer from assembled notes |

### Metrics Reported

- **Per-type accuracy**: Mean within each of the 6 types
- **Task-averaged accuracy**: Mean of the 6 per-type accuracies (macro-average)
- **Overall accuracy**: Mean across all 500 questions (micro-average)
- **Abstention accuracy**: Mean across the 30 abstention questions only

### Reference Scores (S mode, from the paper)

| Model | Overall | Task-Avg |
|-------|:-------:|:--------:|
| GPT-4o | 57.7% | — |
| GPT-4o-mini | 42.8% | — |
| Claude 3.5 Sonnet | 53.0% | — |
| Llama-3.1-70B | 39.8% | — |

---

## 4. How AgentEval Currently Runs LongMemEval

### What We Do (LongMemEvalAdapter)

```
1. LoadFromFile(path, maxQuestions: 10)     ← sequential .Take(N)
2. ConvertToScenario(entry)                 ← flatten all sessions into steps
3. RunEfficientAsync(agent, scenario, judge)
   a. InjectConversationHistory(haystack)   ← 0 LLM calls
   b. agent.InvokeAsync(question)           ← 1 LLM call
   c. judge.JudgeAsync(response, query)     ← 1 LLM call (0-100 score)
4. Aggregate by question_type, save baseline
```

### Key Differences from Official

| Aspect | Official LongMemEval | AgentEval Current |
|--------|---------------------|-------------------|
| **Sampling** | All 500 questions | `.Take(N)` — first N only |
| **Scoring** | Binary (0 or 1) | Continuous (0-100) |
| **Judge prompts** | 5 different prompts per type | Single generic MemoryJudge prompt |
| **Abstention handling** | Dedicated judge prompt | `CreateAbstention()` with forbidden facts |
| **History format** | JSON-formatted chat sessions | Flattened user/assistant turns (sessions lost) |
| **Session boundaries** | Preserved — each session is distinct | Flattened — no session markers |
| **Temporal context** | `haystack_dates` timestamps per session | Not used — timestamps discarded |
| **`has_answer` field** | Stripped before eval (exists in data) | Not read |
| **Reading method** | Configurable (direct/con/con-separate) | Direct only |

### Known Issues

1. **Biased sampling**: Dataset is sorted by type. First 70 entries are all `single-session-user` (easiest). Loading 10 = testing only the easiest type → inflated 95.5% score.

2. **No type-specific judge prompts**: Our `MemoryJudge` uses one generic prompt. The official benchmark uses 5 different prompts because:
   - `temporal-reasoning` needs off-by-one tolerance
   - `knowledge-update` needs to accept "old + new" answers
   - `single-session-preference` needs flexible matching
   - Abstention needs "identifies as unanswerable" detection

3. **Session boundaries lost**: We flatten all sessions into one stream. For `multi-session` and `temporal-reasoning` types, session boundaries carry meaning (which session had which fact, temporal ordering between sessions).

4. **Timestamps discarded**: `haystack_dates` and `question_date` are not used. This matters for `temporal-reasoning` (order events) and `knowledge-update` (which version is latest).

5. **Scoring scale mismatch**: Official uses binary 0/1 (comparable to published scores). We use 0-100 (not comparable).

---

## 5. What to Fix for Faithful LongMemEval in AgentEval

### P0 — Stratified Sampling

Replace `.Take(N)` with proportional sampling from each type:

```csharp
// Instead of entries.Take(maxQuestions)
var stratified = entries
    .GroupBy(e => e.QuestionType)
    .SelectMany(g => g.OrderBy(_ => Random.Shared.Next())
        .Take(Math.Max(1, (int)(maxQuestions * g.Count() / (double)entries.Count))))
    .ToList();
```

### P1 — Type-Specific Judge Prompts

Add judge prompt variants matching the official 5 templates:
- Standard (single-session-user, single-session-assistant, multi-session)
- Preference (single-session-preference): flexible matching
- Temporal (temporal-reasoning): off-by-one tolerance
- Update (knowledge-update): accept old+new if new is present
- Abstention: detect "unanswerable" responses

### P2 — Preserve Session Boundaries

Instead of flattening all turns, inject session markers:
```
--- Session 1 (2023-05-20) ---
User: ...
Assistant: ...
--- Session 2 (2023-06-15) ---
User: ...
```

This matters for multi-session synthesis and temporal reasoning.

### P3 — Include Timestamps

Use `haystack_dates` to add temporal context. The official prompt includes these timestamps so the model can reason about "when" events happened.

### P4 — Binary Scoring Mode

Add a `binaryScoring` option so results are comparable to published baselines. Score = 100 if judge says "yes", 0 if "no".

### P5 — Report Both Metrics

Report **task-averaged accuracy** (macro, mean of 6 type means) alongside **overall accuracy** (micro, mean of all questions). The official paper reports both; they differ because type sizes are unequal.

---

## 6. Mapping to AgentEval Report Visualization

### Category Mapping (Current)

| LongMemEval Type | AgentEval BenchmarkScenarioType | Report Dimension |
|-----------------|-------------------------------|-----------------|
| `single-session-user` | BasicRetention | Recall |
| `single-session-assistant` | BasicRetention | Recall |
| `single-session-preference` | BasicRetention | Recall |
| `multi-session` | CrossSession | Persistence |
| `temporal-reasoning` | TemporalReasoning | Temporal |
| `knowledge-update` | FactUpdateHandling | Temporal |
| `abstention` | Abstention | Organization |

### Can We Show LongMemEval in the Same Report?

**Partially.** The mapping covers 5 of our 11 categories and 4 of 5 dimensions. Missing:
- **Resilience** dimension (Noise Resilience, Reducer Fidelity) — LongMemEval doesn't test noise filtering or context compression directly, though the large haystack provides implicit noise.
- **Reach-Back Depth**, **Multi-Topic**, **Conflict Resolution** — not explicitly tested.

**Recommendation**: Show LongMemEval as a separate benchmark in the report (it already saves to a different agent name). Don't merge its scores into the pentagon — the methodologies are too different (binary vs continuous, different judge prompts, ~115K context vs our ~2-15K). Instead, display it as a "cross-platform reference" card alongside the AgentEval-native benchmark.
