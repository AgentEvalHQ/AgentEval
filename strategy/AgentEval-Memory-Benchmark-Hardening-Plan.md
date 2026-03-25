# AgentEval Memory Benchmark Hardening Plan

## Executive Summary

Our native memory benchmark scores ~93% on GPT-4.1 Standard, even after adding session boundaries, timestamps, type-specific judges, and 80K token context pressure. The fundamental issue isn't context **size** — it's that we test **retrieval** (find a fact, return it) when we should test **reasoning** (combine fragments, resolve conflicts, infer unstated conclusions).

This document analyzes techniques from LongMemEval, MemGPT, LOCOMO, and cognitive science research, then proposes a concrete implementation plan to bring our benchmark scores down to meaningful levels (60-80% for strong models, 40-60% for weaker ones) without simply adding more tokens.

---

## Part 1: Technique Analysis

### 1.1 Multi-Hop Synthesis (from LongMemEval multi-session type)

**What:** The answer doesn't exist in ANY single turn. The agent must find 2-4 fragments scattered across different sessions and combine them.

**Why it's hard:** LongMemEval multi-session scores 38.5% on GPT-4.1 — the hardest type by far. Even at 115K tokens, models fail because they must:
1. Identify that the question requires multiple facts
2. Find each fragment (needle-in-haystack × N)
3. Combine them logically

**Our gap:** Every query in our benchmark can be answered from a single turn. Zero multi-hop reasoning required.

**Example:**
```
Session 2: "I'm training for a race next month"
Session 5: "The marathon is in Portland, super excited about it"
Session 8: "I'm aiming for sub-4-hours, my coach says it's ambitious"
Query: "Tell me about my upcoming athletic event — where is it, what kind, and what's my goal time?"
Expected: marathon + Portland + sub-4-hours (all three required)
```

**Difficulty:** Very High — fundamentally changes what we measure
**Implementation effort:** Medium — new `synthesis_facts` array in QueryDefinition

---

### 1.2 Competing / Confusable Facts (from cognitive interference research)

**What:** Plant facts about SIMILAR entities with SIMILAR attributes. The agent must discriminate precisely.

**Why it's hard:** LLMs use fuzzy semantic matching. When "Sarah in Seattle" and "Sandra in Sacramento" coexist, the model must match the exact entity to the exact attribute. This tests **precision**, not just recall.

**Our gap:** All our facts are about unique, non-confusable topics. There's zero interference between facts.

**Example:**
```
Fact 1: "My colleague Sarah lives in Seattle and works in marketing"
Fact 2: "My sister Sandra lives in Sacramento and works as a teacher"
Fact 3: "My friend Sam lives in San Francisco and works at a startup"
Query: "Where does my sister live and what does she do?"
Expected: Sacramento + teacher
Forbidden: Seattle, San Francisco, marketing, startup
```

**Difficulty:** High — trivial to confuse models
**Implementation effort:** Low — just add confusable fact clusters to existing scenarios

---

### 1.3 Correction Chains (from LongMemEval knowledge-update type, extended)

**What:** A fact changes 3+ times over the conversation. The agent must track the full evolution and return the LATEST version.

**Why it's hard:** Our current fact-update tests ONE correction (v1 → v2). Real conversations have evolving state: "I'm vegetarian" → "Started eating fish" → "But still no red meat" → "Actually went fully vegan last week."

**Our gap:** Single-correction tests. No chain tracking.

**Example:**
```
Session 1: "I just adopted a cat named Whiskers"
Session 4: "Renamed the cat to Luna, Whiskers didn't suit her"
Session 7: "Actually my daughter renamed her again — she's Mittens now"
Query: "What's your cat's name?"
Expected: Mittens
Forbidden: Whiskers, Luna (outdated versions)
```

**Difficulty:** High — requires tracking update chain
**Implementation effort:** Low — extend fact-update-handling.json with chain scenarios

---

### 1.4 Implicit Inference (from LongMemEval single-session-preference type)

**What:** The fact is NEVER directly stated. The agent must INFER it from behavioral evidence.

**Why it's hard:** This is the hardest type across ALL models in LongMemEval (SSP: 16-43%). The agent can't pattern-match because the answer doesn't appear as text anywhere.

**Our gap:** Our preference-extraction already does some of this, but the facts are still relatively explicit ("I always ask for window seats"). True implicit inference requires deduction from patterns.

**Example:**
```
Session 3: "Woke up at 5am for my run, then meditated, was at my desk by 7"
Session 6: "I get so drowsy after 3pm, can barely focus"
Session 9: "Scheduled all my important meetings before noon"
Query: "Am I a morning person or a night owl?"
Expected: morning person (never stated, must be inferred from behavioral pattern)
```

**Difficulty:** Very High — requires reasoning, not retrieval
**Implementation effort:** Medium — need carefully crafted behavioral evidence chains

---

### 1.5 Temporal Ordering (from LongMemEval temporal-reasoning type)

**What:** Instead of "when did X happen?", ask "did X happen before or after Y?" — requiring the agent to find BOTH events and compare timestamps.

**Why it's hard:** LongMemEval temporal-reasoning scores 46% on GPT-4.1. Finding one event is easy; finding two and comparing them doubles the search difficulty.

**Our gap:** Our temporal queries ask about single events. No comparative temporal reasoning.

**Example:**
```
Session 2 (2025-08-15): "Just got promoted to senior engineer!"
Session 5 (2025-10-20): "Moved to the Portland office last week"
Query: "Did you get promoted before or after you moved to Portland?"
Expected: before (August < October)
```

**Difficulty:** High — requires dual-retrieval + comparison
**Implementation effort:** Low — add comparative queries to temporal-reasoning.json

---

### 1.6 Distractor Facts / Active Interference (from LOCOMO benchmark)

**What:** Plant TOPICALLY RELATED but WRONG information near the real fact. The filler isn't random — it actively interferes with retrieval.

**Why it's hard:** LLMs use semantic similarity for attention. If the query is about "anniversary date" and a nearby turn mentions "March 15 deadline," the model might retrieve the wrong March 15.

**Our gap:** Our corpus is generic ChatGPT conversation — topically unrelated to the planted facts. Zero active interference.

**Example:**
```
Turn 40: "The project deadline got moved to March 15th" (distractor)
Turn 42: "Oh and our wedding anniversary is March 15th too" (real fact)
Turn 44: "March is going to be crazy, the fiscal year also ends on the 15th" (distractor)
Query: "When's my wedding anniversary?"
Expected: March 15
```

Even though the answer is "correct," models often retrieve the wrong March 15 context and add irrelevant details.

**Difficulty:** High — attacks the core attention mechanism
**Implementation effort:** Medium — need to craft topically-related distractors per fact

---

### 1.7 Counterfactual Queries (from cognitive science — source monitoring)

**What:** The query contains a FALSE premise. The agent must correct it, not accept it.

**Why it's hard:** LLMs are sycophantic — they tend to agree with the user's framing. A query that says "You mentioned you have 3 kids" when the agent knows about 2 kids tests whether the model trusts its own memory over the user's claim.

**Our gap:** All our queries have true premises. We never test resistance to false framing.

**Example:**
```
Fact: "I have 2 children, ages 8 and 12"
Query: "You mentioned you have 3 kids — what are their ages?"
Expected: correction ("Actually you told me about 2 children, ages 8 and 12")
Forbidden: any fabricated third child
```

**Difficulty:** High — tests precision + resistance to suggestion
**Implementation effort:** Low — add counterfactual queries to existing categories

---

### 1.8 Proactive Memory (from MemGPT / long-term memory research)

**What:** The agent should volunteer relevant memories without being asked — e.g., "Remember, you mentioned you're allergic to peanuts" when the user says "Let's order Thai food."

**Why it's hard:** Most benchmarks only test REACTIVE recall (question → answer). Proactive memory requires the agent to recognize when past knowledge is relevant to the current context.

**Our gap:** Every query explicitly asks for information. Zero proactive recall testing.

**Example:**
```
Fact planted earlier: "I have a severe peanut allergy"
Trigger: "Thinking about trying that new Thai restaurant tonight"
Expected: Agent proactively mentions the peanut allergy concern
```

**Difficulty:** Very High — requires contextual relevance detection
**Implementation effort:** High — needs new evaluation framework (trigger → check for unprompted recall)

---

### 1.9 Semantic Drift (from longitudinal conversation research)

**What:** The same topic is discussed multiple times with gradually shifting meaning. The agent must track the semantic evolution.

**Example:**
```
Session 1: "I'm interested in Python" (programming language)
Session 4: "Been watching Python documentaries" (the snake)
Session 7: "My Python project is going well" (ambiguous — which one?)
Query: "What Python project are you working on?"
```

The agent must disambiguate based on recency and context.

**Difficulty:** High
**Implementation effort:** Medium

---

## Part 2: Prioritized Implementation Plan

### Tier 1 — Implement Now (Highest Impact, Lowest Effort)

| # | Technique | New Category? | Impact on Scores |
|---|-----------|:---:|:---:|
| 1 | **Multi-Hop Synthesis** | Weave into BasicRetention, MultiTopic, MultiSession | -15-25% |
| 2 | **Competing Facts** | Weave into BasicRetention, NoiseResilience | -10-15% |
| 3 | **Counterfactual Queries** | Weave into Abstention | -5-10% |
| 4 | **Temporal Ordering** (comparative) | Weave into TemporalReasoning | -10-15% |
| 5 | **Correction Chains** (3+ updates) | Weave into FactUpdateHandling | -10-15% |

### Tier 2 — Implement Next (High Impact, Medium Effort)

| # | Technique | New Category? | Impact on Scores |
|---|-----------|:---:|:---:|
| 6 | **Implicit Inference** | Enhance PreferenceExtraction | -15-25% |
| 7 | **Distractor Facts** | Weave into all categories | -10-20% |
| 8 | **Semantic Drift** | New category or weave into ConflictResolution | -5-10% |

### Tier 3 — Future (High Impact, High Effort)

| # | Technique | New Category? | Impact on Scores |
|---|-----------|:---:|:---:|
| 9 | **Proactive Memory** | New category + new evaluator | -20-30% |
| 10 | **Real Conversation Corpus** | Replace synthetic corpus | -5-10% |

---

## Part 3: Detailed Implementation — Tier 1

### 3.1 Multi-Hop Synthesis

**Files to modify:**
- `src/AgentEval.Memory/DataLoading/ScenarioDefinition.cs` — Add `synthesis_group` to `FactDefinition` and `synthesis_facts` to `QueryDefinition`
- `src/AgentEval.Memory/Data/scenarios/basic-retention.json` — Add 3-4 multi-hop queries in Standard/Full
- `src/AgentEval.Memory/Data/scenarios/multi-topic.json` — Add cross-topic synthesis queries
- `src/AgentEval.Memory/Engine/MemoryJudge.cs` — Add "synthesis" query type that requires ALL expected facts

**Schema changes:**
```json
// FactDefinition: group related fragments
{"content": "marathon", "planted_as": "Training for a race...", "synthesis_group": "athletic-event"}
{"content": "Portland", "planted_as": "The race is in Portland...", "synthesis_group": "athletic-event"}
{"content": "sub-4-hours", "planted_as": "Aiming for sub-4...", "synthesis_group": "athletic-event"}

// QueryDefinition: require all fragments
{
  "question": "Tell me about my upcoming athletic event",
  "expected_facts": ["marathon", "Portland", "sub-4-hours"],
  "query_type": "synthesis",
  "min_facts_required": 3
}
```

**Judge behavior for `query_type: "synthesis"`:**
- Score = (found_facts / expected_facts) × 100
- All-or-nothing bonus: if ALL facts found, +10 points
- Penalty for partial: finding 2/3 scores 55, not 67 (because incomplete synthesis is misleading)

### 3.2 Competing Facts

**Files to modify:**
- `src/AgentEval.Memory/Data/scenarios/basic-retention.json` — Add confusable fact clusters
- `src/AgentEval.Memory/Data/scenarios/noise-resilience.json` — Add interference facts

**No schema changes needed** — just add carefully crafted facts + forbidden_facts:

```json
// Plant 3 similar facts
{"content": "sister Sandra in Sacramento", "planted_as": "My sister Sandra just moved to Sacramento..."},
{"content": "colleague Sarah in Seattle", "planted_as": "My colleague Sarah lives in Seattle..."},
{"content": "friend Sam in San Francisco", "planted_as": "My buddy Sam's in San Francisco..."},

// Query with forbidden facts for precision
{
  "question": "Where does my sister live?",
  "expected_facts": ["Sacramento"],
  "forbidden_facts": ["Seattle", "San Francisco"],
  "query_type": "standard"
}
```

### 3.3 Counterfactual Queries

**Files to modify:**
- `src/AgentEval.Memory/Data/scenarios/abstention.json` — Add counterfactual queries
- `src/AgentEval.Memory/Engine/MemoryJudge.cs` — Add "counterfactual" query type

**New query type:**
```json
{
  "question": "You mentioned you have 3 kids — what are their ages?",
  "expected_facts": ["2 children", "ages 8 and 12"],
  "forbidden_facts": ["third child", "3 kids"],
  "query_type": "counterfactual"
}
```

**Judge behavior for `query_type: "counterfactual"`:**
- Must CORRECT the false premise (high score if it says "actually 2 kids")
- Must NOT accept the false premise (low score if it says "your 3 kids are...")
- Must still provide correct facts

### 3.4 Comparative Temporal Ordering

**Files to modify:**
- `src/AgentEval.Memory/Data/scenarios/temporal-reasoning.json` — Add comparison queries

**No schema changes needed:**
```json
{
  "question": "Did I get promoted before or after I moved to Portland?",
  "expected_facts": ["before", "promoted in August", "moved in October"],
  "query_type": "temporal"
}
```

### 3.5 Correction Chains

**Files to modify:**
- `src/AgentEval.Memory/Data/scenarios/fact-update-handling.json` — Add 3-step chains

```json
// 3-step correction chain
{"content": "cat named Whiskers", "planted_as": "Just adopted a cat, named her Whiskers!", "timestamp": "2025-06-01", "session_id": 1},
{"content": "cat renamed to Luna", "planted_as": "Renamed the cat to Luna...", "timestamp": "2025-08-15", "session_id": 4},
{"content": "cat renamed to Mittens", "planted_as": "My daughter renamed her again — Mittens now", "timestamp": "2025-10-20", "session_id": 7},

{
  "question": "What's your cat's name?",
  "expected_facts": ["Mittens"],
  "forbidden_facts": ["Whiskers", "Luna"],
  "query_type": "update"
}
```

---

## Part 4: Expected Score Impact (Post-Tier-1)

| Category | GPT-4.1 Current | After Tier 1 | GPT-4o-mini After |
|----------|:---------------:|:------------:|:-----------------:|
| Basic Retention | 94% | **70-80%** | **55-65%** |
| Temporal Reasoning | 91% | **65-75%** | **50-60%** |
| Noise Resilience | 98% | **75-85%** | **60-70%** |
| Fact Update Handling | 89% | **60-70%** | **45-55%** |
| Multi-Topic | 100% | **70-80%** | **55-65%** |
| Abstention | 82% | **65-75%** | **50-60%** |
| Preference Extraction | 96% | **50-65%** | **35-50%** |
| **Overall** | **94%** | **68-78%** | **52-62%** |

These projections align with LongMemEval's published results (GPT-4o: 57.7%, GPT-4o-mini: 42.8%) and represent a genuinely challenging benchmark that differentiates model capabilities.

---

## Part 5: Verification Plan

1. `dotnet test` — all tests pass
2. Run Quick — scores ~unchanged (no new techniques in Quick)
3. Run Standard GPT-4.1 — expect 68-78%
4. Run Standard GPT-4o-mini — expect 52-62%
5. Run Standard GPT-4o — expect 60-70%
6. Verify model ordering: GPT-4.1 > GPT-4o > GPT-4o-mini (if not, benchmark is broken)
7. Compare to LongMemEval baselines for sanity check

---

## Part 6: Implementation Audit (Post Tier-1)

### What Was Implemented

| Technique | Files Modified | Facts Added | Queries Added |
|-----------|:---:|:---:|:---:|
| Multi-hop synthesis | basic-retention.json | 6 (across sessions) | 4 |
| Competing facts | basic-retention.json | 5 (S-names/cities) | 3 |
| Counterfactual queries | abstention.json | 3 | 4 |
| Correction chains | fact-update-handling.json | 9 (3 chains × 3) | 3 |
| Temporal ordering | temporal-reasoning.json | 0 (reuse existing) | 4 |
| Distractor facts | noise-resilience.json | 3 + 6 noise lines | 3 |

### Judge Variants Added

| query_type | Behavior |
|---|---|
| `synthesis` | All-or-nothing: partial fragments score proportionally lower |
| `counterfactual` | Must correct false premise AND provide correct info |
| `correction_chain` | Only latest value correct, -30 per outdated value |

### What's Still Missing (Data Gaps)

| Gap | Affected Files | Priority |
|---|---|---|
| Timestamps | basic-retention, noise-resilience, abstention, preference-extraction | Medium |
| Forbidden facts | multi-topic, preference-extraction | Medium |
| `correction_chain` query_type in JSON | fact-update-handling (uses `update` instead) | Low |

---

## Part 7: Additional Hardening Plan (Tier 2)

### 7.1 Semantic Distractor Corpus

**Problem:** Our context pressure uses generic ChatGPT filler that's topically unrelated to planted facts. A query about cooking is easy to find when surrounded by conversation about weather and movies.

**Solution:** For each category, generate corpus turns that are topically RELATED to the planted facts but contain different specific details. If the fact is "I run marathons", the corpus should mention "my friend runs ultramarathons", "I watched the Boston marathon on TV", "running shoes are so expensive". The agent must distinguish MY facts from topically similar filler.

**Implementation:**
- Add `themed_corpus` field to ContextPressureConfig (corpus name or inline turns)
- Create 3-4 themed corpus files (health, career, personal, hobbies)
- CorpusLoader selects themed corpus when available

**Expected impact:** -10-15% across all categories

### 7.2 Paraphrase Variation Queries

**Problem:** We plant "I'm allergic to shellfish" and query "What am I allergic to?" This is keyword matching, not comprehension.

**Solution:** Query with very different wording that requires semantic understanding:
- Planted: "I'm allergic to shellfish"
- Query: "My team wants to order lobster bisque for the holiday dinner. Should I be concerned?"
- Expected: "Yes, shellfish allergy"

**Implementation:** Add `paraphrased_query` field to QueryDefinition, or simply write better queries in the JSON. No code changes needed — just better test data.

**Expected impact:** -5-10% on basic retention and multi-topic

### 7.3 Negation Traps

**Problem:** We test what the agent KNOWS but not whether it understands NEGATION. "I don't eat red meat" should make the agent refuse steak recommendations.

**Solution:** Plant negated facts and query with positive assumptions:
- Fact: "I stopped drinking coffee 6 months ago"
- Query: "What's my usual coffee order?"
- Expected: abstention or correction ("You stopped drinking coffee")

**Implementation:** Add negation queries to abstention.json and preference-extraction.json. Use counterfactual judge variant.

**Expected impact:** -5-10% on abstention and preference categories

### 7.4 Quantity/Specificity Attacks

**Problem:** We never test whether the agent INVENTS details not in memory. "I have 2 cats" → agent might fabricate names.

**Solution:** Query for details never provided:
- Fact: "I have 2 cats"
- Query: "What are your cats' names and ages?"
- Expected: "2 cats" + abstention on names/ages
- Forbidden: any specific name or age (hallucination)

**Implementation:** Add to basic-retention.json and abstention.json. Combine counterfactual + abstention judge logic.

**Expected impact:** -5-10% on basic retention (catches hallucination)

### 7.5 Early-Position Fact Burial

**Problem:** Most facts are planted at position 0.3-0.7 (middle of context). Models attend well to the middle. Facts at the very beginning (position 0.01-0.05) are harder to recall — the "lost in the beginning" problem.

**Solution:** Plant critical facts at extreme early positions (first 5% of context). These are the hardest to attend to in long contexts.

**Implementation:** Set `position: 0.02` on 2-3 facts per category in Standard preset.

**Expected impact:** -5-10% on categories with early-buried facts

### 7.6 Red Herring Sessions

**Problem:** Session boundaries help the agent organize memory. But what if some sessions are deliberately misleading?

**Solution:** Add sessions that discuss the SAME topic as a planted fact but with WRONG details:
- Session 3 (real): "My anniversary is March 15"
- Session 7 (red herring): Discussion about planning a party "on March 15" for a coworker

The agent must distinguish whose March 15 event is being asked about.

**Implementation:** Add red herring turns to noise_between_facts with matching session_id placement. Already partially done in noise-resilience.json.

**Expected impact:** -5-10% on noise resilience

### 7.7 Cumulative Impact Projection

| Technique | GPT-4.1 Impact | GPT-4o-mini Impact |
|-----------|:-:|:-:|
| Tier 1 (implemented) | -10-15% | -15-20% |
| Semantic distractor corpus | -10-15% | -15-20% |
| Paraphrase variation | -5-10% | -10-15% |
| Negation traps | -5-10% | -10-15% |
| Quantity/specificity attacks | -5-10% | -10-15% |
| Early-position burial | -5-10% | -10-15% |
| Red herring sessions | -5-10% | -10-15% |
| **Cumulative (Tier 1 + 2)** | **55-70%** | **35-50%** |

This would align our native benchmark with LongMemEval difficulty levels (GPT-4o: 57.7%) while testing fundamentally different cognitive abilities.

### 7.8 Implementation Priority

| Priority | Technique | Effort | Impact | Dependencies |
|:---:|---|:---:|:---:|---|
| 1 | Semantic distractor corpus | Medium | High | New corpus files |
| 2 | Paraphrase variation | Low | Medium | JSON data only |
| 3 | Negation traps | Low | Medium | JSON data + counterfactual judge |
| 4 | Quantity/specificity attacks | Low | Medium | JSON data + abstention judge |
| 5 | Early-position burial | Trivial | Medium | Change position values in JSON |
| 6 | Red herring sessions | Medium | Medium | Themed noise_between_facts |
