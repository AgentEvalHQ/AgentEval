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

## Part 7: Data Gap Fixes (Detailed Implementation)

These are gaps in the EXISTING Tier 1 implementation — infrastructure is built but some scenario files are missing data.

### 7.1 Fix: Add Timestamps to 4 Scenario Files

**Why:** Without timestamps, the temporal judge tolerance clause never activates. Facts planted with timestamps get `[2026-01-15] fact text` prepended by the runner (MemoryBenchmarkRunner.cs line 482). Without timestamps, temporal ordering queries can't work.

#### basic-retention.json — Add timestamps to standard preset facts

Add `"timestamp"` to all 24 standard preset facts. Use dates spread over the last 6 months to create a realistic timeline:

```json
// Marathon training facts (synthesis group)
{ "content": "Training for a race next month", "timestamp": "2026-01-05", "session_id": 1, ... },
{ "content": "The marathon is in Portland", "timestamp": "2026-02-10", "session_id": 3, ... },
{ "content": "Aiming for sub-4-hours", "timestamp": "2026-03-01", "session_id": 5, ... },

// Home office project (synthesis group)
{ "content": "Converting spare bedroom to home office", "timestamp": "2025-12-15", "session_id": 2, ... },
{ "content": "Ordered a standing desk", "timestamp": "2026-01-20", "session_id": 4, ... },
{ "content": "Soundproofing for music recording", "timestamp": "2026-02-25", "session_id": 6, ... },

// Competing facts (S-names) — all in same time window to increase confusion
{ "content": "Colleague Sarah lives in Seattle", "timestamp": "2026-01-10", ... },
{ "content": "Sister Sandra lives in Sacramento", "timestamp": "2026-01-12", ... },
{ "content": "Friend Sam lives in San Francisco", "timestamp": "2026-01-14", ... },
{ "content": "Cousin Sofia is a nurse in San Diego", "timestamp": "2026-01-16", ... },
{ "content": "Neighbor Steve is a lawyer", "timestamp": "2026-01-18", ... }
```

#### noise-resilience.json — Add timestamps to standard preset facts

```json
// Distractor target facts
{ "content": "Wedding anniversary is March 15", "timestamp": "2025-10-20", ... },
{ "content": "Daughter Emma's birthday is June 22", "timestamp": "2025-11-15", ... },
{ "content": "Mother's maiden name is Andersen", "timestamp": "2025-12-05", ... }
```

#### abstention.json — Add timestamps to standard preset facts

```json
{ "content": "I have 2 kids, ages 8 and 12", "timestamp": "2026-01-20", ... },
{ "content": "Moved from Barcelona 3 years ago", "timestamp": "2026-02-05", ... },
{ "content": "Max is a golden retriever", "timestamp": "2026-02-15", ... }
```

#### preference-extraction.json — Add timestamps to standard preset facts

```json
{ "content": "Always takes the stairs, avoids elevators", "timestamp": "2026-01-08", ... },
{ "content": "Podcast backlog getting out of control", "timestamp": "2026-01-22", ... },
{ "content": "Prefers GUI tools over terminal", "timestamp": "2026-02-12", ... }
```

### 7.2 Fix: Add Forbidden Facts to 2 Scenario Files

#### multi-topic.json — Add forbidden_facts to standard queries

For each query, add facts from OTHER topics that could confuse the agent:

```json
{
  "question": "What programming languages do I use at work?",
  "expected_facts": ["C#", "Python"],
  "forbidden_facts": ["hiking", "Copenhagen", "guitar"],
  "query_type": "standard"
}
```

Add `forbidden_facts` to at least 5 of the 10 standard queries, each with 2-3 cross-topic distractors. Target the queries where topics overlap (e.g., "schedule" could pull from both work and personal hobbies).

#### preference-extraction.json — Add forbidden_facts to standard queries

```json
{
  "question": "How do I prefer to commute to work?",
  "expected_facts": ["bike", "avoids public transport"],
  "forbidden_facts": ["car", "subway", "bus"],
  "query_type": "preference"
}
```

Add `forbidden_facts` with plausible-but-wrong preferences to each query. The agent must not invent preferences that were never demonstrated.

### 7.3 Fix: Use correction_chain query_type in fact-update-handling.json

The judge already has a `correction_chain` variant (MemoryJudge.cs) that penalizes -30 per outdated value. But the JSON uses `"query_type": "update"` instead. Change the 3 correction chain queries:

```json
// BEFORE:
{ "question": "Where do I work now?", "query_type": "update", ... }

// AFTER:
{ "question": "Where do I work now?", "query_type": "correction_chain", ... }
```

Change these 3 queries in standard preset (lines ~192, ~210, ~228):
- "Where do I work now?" (Google → Microsoft → Apple chain)
- "What's the name of my cat?" (Whiskers → Luna → Mittens chain)
- "What diet do I follow?" (vegetarian → pescatarian → vegan chain)

The `correction_chain` variant scores:
- Only latest value → 90-100
- Latest + mentions update → 100
- Intermediate value → 0-20
- Original value → 0-10
- Each forbidden (outdated) value found → -30

This is much stricter than `update` which accepts "both old and new = 80+".

---

## Part 8: Additional Hardening Plan (Tier 2) — Full Implementation Details

### 8.1 Semantic Distractor Corpus

**Problem:** Our corpus files (`context-small.json`, `context-medium.json`, etc.) contain generic ChatGPT conversations about weather, movies, recipes. These are topically UNRELATED to planted facts, making needle-in-haystack trivially easy — the agent just looks for the one turn about "marathon" in a sea of movie reviews.

**Solution:** Create themed corpus files where the filler is topically SIMILAR to the planted facts.

#### Code Changes

**File: `src/AgentEval.Memory/DataLoading/ContextPressureConfig` (in ScenarioDefinition.cs)**

Add a new optional field:

```csharp
public class ContextPressureConfig
{
    [JsonPropertyName("corpus")]
    public string Corpus { get; set; } = "context-small";

    [JsonPropertyName("max_turns")]
    public int? MaxTurns { get; set; }

    [JsonPropertyName("sessions_count")]
    public int? SessionsCount { get; set; }

    // NEW: themed distractor turns injected ALONGSIDE the corpus
    [JsonPropertyName("distractor_turns")]
    public List<DistractorTurn>? DistractorTurns { get; set; }
}

public class DistractorTurn
{
    [JsonPropertyName("user")]
    public string User { get; set; } = "";

    [JsonPropertyName("assistant")]
    public string Assistant { get; set; } = "";

    [JsonPropertyName("topic")]
    public string? Topic { get; set; }  // matches fact category for targeted interference
}
```

**File: `src/AgentEval.Memory/Evaluators/MemoryBenchmarkRunner.cs`**

In `TryRunFromJsonAsync`, after loading corpus turns, interleave distractor turns:

```csharp
var corpusTurns = /* existing corpus loading */;

// Inject themed distractor turns at random positions in the corpus
if (preset.ContextPressure?.DistractorTurns is { Count: > 0 } distractors)
{
    var rng = new Random(42); // deterministic for reproducibility
    foreach (var d in distractors)
    {
        var insertAt = rng.Next(0, corpusTurns.Count);
        corpusTurns.Insert(insertAt, (d.User, d.Assistant));
    }
}
```

#### JSON Example for basic-retention.json

```json
"context_pressure": {
  "corpus": "context-stress",
  "max_turns": 100,
  "sessions_count": 8,
  "distractor_turns": [
    {
      "user": "My friend just ran the Berlin marathon in 3:45, incredible!",
      "assistant": "That's an impressive time! Berlin is known for being a fast course.",
      "topic": "marathon"
    },
    {
      "user": "I watched the Portland Trail Blazers game last night",
      "assistant": "How did they do? Portland has had an interesting season.",
      "topic": "portland"
    },
    {
      "user": "My coworker Sarah just got promoted to VP",
      "assistant": "That's great news! She must have worked hard for that.",
      "topic": "sarah"
    },
    {
      "user": "I'm thinking of converting my garage into a workshop",
      "assistant": "That's a popular home improvement project. What would you use it for?",
      "topic": "home_office"
    },
    {
      "user": "Sam from accounting told me about a great restaurant in San Jose",
      "assistant": "Oh nice, what kind of cuisine? San Jose has some great spots.",
      "topic": "sam_city"
    }
  ]
}
```

These distractors create confusion:
- "friend ran Berlin marathon" vs planted "I'm training for Portland marathon"
- "coworker Sarah got promoted" vs planted "colleague Sarah lives in Seattle"
- "Sam from accounting in San Jose" vs planted "friend Sam in San Francisco"

The agent must distinguish **my** facts from **other people's** similar facts.

#### Recommended distractor counts per category

| Category | Distractor Turns | Topics to Match |
|---|:---:|---|
| basic-retention | 8-10 | Match each synthesis/competing fact group |
| temporal-reasoning | 5-6 | Career events at similar companies |
| noise-resilience | 6-8 | Similar dates, similar names |
| multi-topic | 8-10 | Cross-domain confusion (work↔hobby) |
| abstention | 4-5 | Similar but different personal details |
| preference-extraction | 5-6 | Opposite preferences from "friends" |

### 8.2 Paraphrase Variation Queries — Full Implementation

**Problem:** Queries use the same keywords as planted facts. "I'm allergic to shellfish" → "What am I allergic to?" is keyword matching.

**No code changes needed.** This is purely a JSON data improvement. Replace direct-match queries with semantic-comprehension queries.

#### Exact replacements in basic-retention.json (standard preset)

```json
// BEFORE (keyword match):
{
  "question": "Where does my sister live?",
  "expected_facts": ["Sacramento"],
  "forbidden_facts": ["Seattle", "San Francisco"]
}

// AFTER (paraphrase — requires inference):
{
  "question": "I need to ship a birthday gift to my sister. What city should I send it to?",
  "expected_facts": ["Sacramento"],
  "forbidden_facts": ["Seattle", "San Francisco"],
  "difficulty": "discrimination",
  "query_type": "standard"
}
```

```json
// BEFORE:
{
  "question": "What am I training for?",
  "expected_facts": ["marathon", "Portland", "sub-4-hours"]
}

// AFTER:
{
  "question": "My physical therapist wants to know about my upcoming athletic commitments. What should I tell them?",
  "expected_facts": ["marathon", "Portland", "sub-4-hours"],
  "difficulty": "synthesis",
  "query_type": "synthesis"
}
```

#### Exact replacements in noise-resilience.json (standard preset)

```json
// BEFORE:
{
  "question": "When is my wedding anniversary?",
  "expected_facts": ["March 15"]
}

// AFTER:
{
  "question": "I'm trying to book a surprise dinner reservation for a very special date with my spouse. When should I book it for?",
  "expected_facts": ["March 15"],
  "forbidden_facts": ["deadline", "fiscal year"],
  "difficulty": "discrimination",
  "query_type": "standard"
}
```

#### Exact replacements in preference-extraction.json (standard preset)

```json
// BEFORE:
{
  "question": "How do I prefer to commute?",
  "expected_facts": ["bike"]
}

// AFTER:
{
  "question": "It's raining heavily tomorrow. Should I adjust my morning routine or will my commute method handle it?",
  "expected_facts": ["bike", "might get wet"],
  "query_type": "preference"
}
```

### 8.3 Negation Traps — Full Implementation

**Add negation facts and trap queries to 2 scenario files.**

#### abstention.json — Add to standard preset facts

```json
{
  "content": "I stopped drinking coffee 6 months ago",
  "category": "preference",
  "importance": 60,
  "planted_as": "I actually stopped drinking coffee about 6 months ago — sleeping way better now.",
  "session_id": 3,
  "timestamp": "2025-09-20"
},
{
  "content": "I sold my car and bike everywhere now",
  "category": "lifestyle",
  "importance": 55,
  "planted_as": "Best decision ever — I sold my car last year and just bike everywhere. No more parking stress.",
  "session_id": 5,
  "timestamp": "2025-06-15"
},
{
  "content": "I quit social media completely",
  "category": "lifestyle",
  "importance": 50,
  "planted_as": "I deleted all my social media accounts — Instagram, Twitter, everything. Don't miss it at all.",
  "session_id": 4,
  "timestamp": "2025-11-01"
}
```

#### abstention.json — Add negation trap queries to standard preset

```json
{
  "question": "What's my usual coffee order? I need to tell the new barista.",
  "expected_facts": ["stopped drinking coffee", "quit", "6 months"],
  "forbidden_facts": ["latte", "espresso", "americano", "cappuccino"],
  "difficulty": "negation",
  "query_type": "counterfactual"
},
{
  "question": "Where do I usually park when I drive to work?",
  "expected_facts": ["sold car", "bike"],
  "forbidden_facts": ["garage", "parking lot", "street parking"],
  "difficulty": "negation",
  "query_type": "counterfactual"
},
{
  "question": "Can you check my Instagram for that restaurant recommendation?",
  "expected_facts": ["deleted", "quit social media"],
  "forbidden_facts": ["instagram.com", "DM", "story", "post"],
  "difficulty": "negation",
  "query_type": "counterfactual"
}
```

#### preference-extraction.json — Add to standard preset

```json
// Facts:
{
  "content": "I can't stand spicy food, even mild salsa is too much",
  "category": "preference",
  "importance": 60,
  "planted_as": "Ugh, even mild salsa is too much for me. I just can't handle any spice at all.",
  "session_id": 4,
  "timestamp": "2026-01-25"
}

// Query:
{
  "question": "We're ordering Thai food for lunch — what spice level should I get for you?",
  "expected_facts": ["can't stand spicy", "no spice", "mild is too much"],
  "forbidden_facts": ["medium", "hot", "extra spicy"],
  "difficulty": "negation",
  "query_type": "preference"
}
```

### 8.4 Quantity/Specificity Attacks — Full Implementation

**Test whether the agent invents details it was never told.**

#### basic-retention.json — Add to standard preset

```json
// Facts (add):
{
  "content": "I have 2 cats at home",
  "category": "personal",
  "importance": 50,
  "planted_as": "Yeah, I have 2 cats at home. They're a handful but I love them.",
  "session_id": 4,
  "timestamp": "2026-01-28"
},
{
  "content": "I went to college in the Midwest",
  "category": "education",
  "importance": 45,
  "planted_as": "I went to college somewhere in the Midwest — feels like a lifetime ago.",
  "session_id": 2,
  "timestamp": "2025-11-10"
}

// Queries (add):
{
  "question": "What are my cats' names and how old are they?",
  "expected_facts": ["2 cats"],
  "forbidden_facts": ["Whiskers", "Luna", "Mittens", "Max", "Bella", "Oliver", "1 year", "2 years", "3 years", "kitten"],
  "difficulty": "specificity_attack",
  "query_type": "counterfactual"
},
{
  "question": "Which university did I attend and what was my major?",
  "expected_facts": ["college", "Midwest"],
  "forbidden_facts": ["University of", "Ohio State", "Michigan", "Illinois", "engineering", "business", "computer science", "biology"],
  "difficulty": "specificity_attack",
  "query_type": "counterfactual"
}
```

The `counterfactual` judge variant handles this well: the agent should confirm what it knows ("2 cats", "Midwest college") but say "I don't know" for the details never mentioned. Hallucinating names/ages/universities triggers forbidden fact penalties.

#### abstention.json — Add to standard preset

```json
{
  "question": "What's the name of the startup where I work and how many employees does it have?",
  "expected_facts": ["startup", "2 years"],
  "forbidden_facts": ["TechCorp", "InnovateCo", "50 employees", "100 employees", "Series A"],
  "difficulty": "specificity_attack",
  "query_type": "counterfactual"
}
```

### 8.5 Early-Position Fact Burial — Full Implementation

**Trivial change — just modify `position` values in JSON.**

The runner interprets `position: 0.02` as "place this fact at 2% through the corpus" (BuildInterleavedHistory line 430). With 100 turns, position 0.02 = turn 2. The model must attend to the very start of a long context.

#### basic-retention.json — Change 2 existing standard facts

```json
// BEFORE:
{ "content": "Colleague Sarah lives in Seattle", "position": 0.3, ... }

// AFTER:
{ "content": "Colleague Sarah lives in Seattle", "position": 0.02, ... }
```

```json
// BEFORE:
{ "content": "Converting spare bedroom to home office", "position": 0.4, ... }

// AFTER:
{ "content": "Converting spare bedroom to home office", "position": 0.03, ... }
```

#### temporal-reasoning.json — Move 1 critical fact to early position

```json
// BEFORE:
{ "content": "Started in customer support at a telecom company", "position": "early", ... }

// AFTER (more specific):
{ "content": "Started in customer support at a telecom company", "position": 0.01, ... }
```

#### noise-resilience.json — Bury 1 fact extremely early

```json
// BEFORE:
{ "content": "Wedding anniversary is March 15", "position": 0.3, ... }

// AFTER:
{ "content": "Wedding anniversary is March 15", "position": 0.02, ... }
```

### 8.6 Red Herring Sessions — Full Implementation

**Add noise turns that match the TOPIC of planted facts but with WRONG details.**

#### noise-resilience.json — Add to noise_between_facts in standard preset

```json
"noise_between_facts": [
  // EXISTING noise...

  // NEW: Red herrings that match topics of planted facts
  "My coworker mentioned her anniversary is coming up on March 12th — she's planning a big party.",
  "The building manager said the March 15th maintenance window will affect the elevators.",
  "My friend's daughter is named Emma too! Hers just turned 6 in June.",
  "I was reading about the Andersen fairy tale museum in Denmark — our team is planning a visit.",
  "There's a new restaurant opening at Vesterbrogade 130, just a few doors down from us.",
  "My neighbor in apartment 4A was asking if we'd heard the construction noise lately."
]
```

Each red herring matches a planted fact's key detail but in a different context:
- "March 12th" / "March 15th maintenance" vs real "anniversary March 15"
- "daughter Emma turned 6 in June" vs real "daughter Emma's birthday June 22"
- "Andersen fairy tale museum" vs real "mother's maiden name Andersen"
- "Vesterbrogade 130" vs real "Vesterbrogade 127"
- "apartment 4A" vs real "apartment 4B"

#### basic-retention.json — Add to noise_between_facts in standard preset

```json
"noise_between_facts": [
  // EXISTING noise...

  // NEW: Red herrings for competing facts
  "My manager just got back from a trip to Sacramento — said the weather was perfect.",
  "The Seattle office is hiring 3 new marketing people, Sarah from HR told me.",
  "I saw on LinkedIn that someone named Sandra just moved to San Francisco.",
  "There's a nursing conference in San Diego next month, my aunt Sofia is presenting.",
  "Our building lawyer Steve is handling the HOA dispute with the adjacent property.",
  "My running club friend is doing the Portland half-marathon — she's aiming for 1:45."
]
```

These create maximum confusion:
- "trip to Sacramento" vs real "sister Sandra in Sacramento"
- "Sarah from HR" vs real "colleague Sarah in Seattle marketing"
- "Sandra moved to San Francisco" vs real "Sandra in Sacramento" + "Sam in San Francisco"
- "Portland half-marathon 1:45" vs real "Portland full marathon sub-4-hours"

### 8.7 New Judge Variant: `specificity_attack`

The `counterfactual` variant works for quantity/specificity attacks, but a dedicated variant would be cleaner.

**File: `src/AgentEval.Memory/Engine/MemoryJudge.cs`**

Add to the switch statement in `BuildJudgmentPrompt`:

```csharp
"specificity_attack" => @"

SPECIFICITY ATTACK SCORING: The question asks for DETAILS that were never provided (names, ages, specific institutions, etc.). The agent was only told a general fact.
- Agent confirms the general fact AND says 'I don't have those specific details': score 90-100
- Agent confirms the general fact but stays silent on unasked details: score 70-80
- Agent INVENTS specific details (names, ages, schools) not in memory: score 0-20
- Each fabricated specific detail in forbidden_facts: -30 points
The goal is to catch HALLUCINATION of plausible but fictional details.",
```

### 8.8 Cumulative Impact Projection

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

### 8.9 Implementation Priority & Effort Matrix

| Priority | Technique | Effort | Impact | Code Changes | Data Changes |
|:---:|---|:---:|:---:|---|---|
| 0 | Data gap fixes (§7.1-7.3) | Trivial | Medium | None | Add timestamps, forbidden_facts, fix query_type |
| 1 | Early-position burial (§8.5) | Trivial | Medium | None | Change position values |
| 2 | Red herring sessions (§8.6) | Low | High | None | Add noise_between_facts entries |
| 3 | Paraphrase variation (§8.2) | Low | Medium | None | Rewrite query strings |
| 4 | Negation traps (§8.3) | Low | Medium | None | Add facts + queries to 2 files |
| 5 | Quantity/specificity attacks (§8.4) | Low | Medium | 1 judge variant (optional) | Add facts + queries to 2 files |
| 6 | Semantic distractor corpus (§8.1) | Medium | High | Add DistractorTurn model + runner interleaving | Add distractor_turns to 6 JSON files |

**Total effort for Tier 2:** ~2-3 hours of JSON editing + 30 min of C# code changes.

### 8.10 Verification Checklist

After implementing Tier 2:

1. `dotnet test` — all tests pass (update fact count assertions as needed)
2. Run Quick — scores unchanged (Tier 2 only targets Standard+)
3. Run Standard GPT-4.1 — expect **55-70%** (vs 93% before hardening)
4. Run Standard GPT-4o-mini — expect **35-50%** (vs 89% before)
5. Run Standard GPT-4o — expect **45-60%** (between 4.1 and mini)
6. Verify model ordering: GPT-4.1 > GPT-4o > GPT-4o-mini
7. Run Full GPT-4.1 — expect **50-65%** (harder due to CrossSession + Conflict)
8. Compare to LongMemEval: our Standard ≈ LongMemEval-S difficulty
9. No category scores 100% on any model (if it does, that category needs more hardening)
10. No category scores <20% on GPT-4.1 (if it does, the test is broken, not hard)
