# AgentEval Memory Benchmark — Data-Driven Evaluation Upgrade Plan

**Date:** March 22, 2026
**Predecessor:** Memory-Benchmark-Implementation-Plan.md (completed)
**Goal:** Move memory evaluations from hardcoded C# to JSON-driven scenarios with pre-built conversation corpora, add Abstention as the 9th benchmark category, and integrate LongMemEval for cross-platform benchmarking
**Starting Point:** Working benchmark system with 283 tests/TFM, `IHistoryInjectableAgent` interface, `SyntheticHistoryGenerator` (30 turns), scenario depth (Quick/Standard/Full) in `MemoryBenchmarkRunner`, reporting infrastructure (baselines, HTML report, export bridge), factory method `MemoryBenchmarkRunner.Create(chatClient)`

---

## Implementation Progress Tracker

| Task | Description | % Done | Reviewed | Notes |
|------|-------------|--------|----------|-------|
| **Pre-Check** | Verify starting state (build, tests, current scores) | 100% | ✅ | 283 tests pass, all prerequisites verified |
| **Task 1.1** | Add `BenchmarkScenarioType.Abstention` to enum | 100% | ✅ | Added with XML doc |
| **Task 1.2** | Implement `RunAbstentionAsync` in benchmark runner | 100% | ✅ | Switch case + handler with 3 planted facts, 6 abstention queries |
| **Task 1.3** | Update `MemoryJudge` for abstention scoring mode | 100% | ✅ | Abstention-specific prompt in BuildJudgmentPrompt + strict scoring |
| **Task 1.4** | Add `MemoryQuery.CreateAbstention()` factory overload | 100% | ✅ | Empty ExpectedFacts, metadata["abstention"]=true |
| **Task 1.5** | Add Abstention to Standard and Full presets | 100% | ✅ | Standard=7 cats (0.12 weight), Full=9 cats (0.12 weight), weights verified sum to 1.0 |
| **Task 1.6** | Update `PentagonConsolidator` for Abstention | 100% | ✅ | Organization = avg(MultiTopic, Abstention). BaselineExtensions + BenchmarkResult recommendations updated. |
| **Task 1.7** | Tests for all Phase 1 changes | 100% | ✅ | 11 new tests. Updated 4 existing tests (category counts 6→7, 8→9). |
| **CP1** | Checkpoint 1 — Build + Test + Run Standard with Abstention | 100% | ✅ | 294 tests/TFM, 0 errors. Full solution builds. |
| **Task 2.1** | Create corpus JSON files (small, medium) | 100% | ✅ | context-small (15 turns), context-medium (41 turns) |
| **Task 2.2** | Create `CorpusLoader` | 100% | ✅ | Static loader from embedded resources + ListAvailable() |
| **Task 2.3** | Replace `SyntheticHistoryGenerator` with `CorpusLoader` | 100% | ✅ | Falls back to SyntheticHistoryGenerator if corpus unavailable |
| **Task 2.4** | Configure embedded resources in .csproj | 100% | ✅ | Data\corpus\*.json glob pattern |
| **Task 2.5** | Tests for corpus loading and injection | 100% | ✅ | 8 tests: counts, content, no overlap, error handling |
| **CP2** | Checkpoint 2 — Build + Test + Verify corpus loads correctly | 100% | ✅ | 302 tests/TFM. Corpus loads from embedded resources. |
| **Task 3.1** | Define `ScenarioDefinition` and related models | 0% | | |
| **Task 3.2** | Create `ScenarioLoader` with preset inheritance | 0% | | |
| **Task 3.3** | Create 9 scenario JSON files | 0% | | Including abstention.json |
| **Task 3.4** | Refactor `MemoryBenchmarkRunner` to use `ScenarioLoader` | 0% | | Removes all hardcoded scenarios |
| **Task 3.5** | Support external scenario directories | 0% | | |
| **Task 3.6** | Tests + migration verification | 0% | | |
| **CP3** | Checkpoint 3 — Build + Full Test + Run All Presets + Compare scores | 0% | | |
| **INT-1** | Integration validation — run full sample end-to-end, verify HTML report | 0% | | |
| **Task 4.1** | Download and analyze LongMemEval data format | 0% | | |
| **Task 4.2** | Create `LongMemEvalAdapter` | 0% | | |
| **Task 4.3** | Ship curated 50-question subset as embedded resource | 0% | | With attribution |
| **Task 4.4** | Enable full LongMemEval benchmark run | 0% | | |
| **Task 4.5** | Tests for adapter + subset | 0% | | |
| **CP4** | Checkpoint 4 — Build + Test + Run LongMemEval subset | 0% | | |
| **INT-2** | Integration validation — all Tier 1 complete, full regression test | 0% | | |
| **Task 5.1** | Add `BenchmarkScenarioType.ConflictResolution` | 0% | | Tier 2 |
| **Task 5.2** | Implement handler + scenario JSON | 0% | | |
| **Task 5.3** | Add to Full preset + pentagon consolidation + tests | 0% | | |
| **CP5** | Checkpoint 5 — Build + Test | 0% | | |
| **Task 6.1** | Add `BenchmarkScenarioType.MultiSessionReasoning` | 0% | | Tier 2 |
| **Task 6.2** | Implement handler + scenario JSON | 0% | | |
| **Task 6.3** | Add to Full preset + pentagon consolidation + tests | 0% | | |
| **CP6** | Checkpoint 6 — Build + Test | 0% | | |
| **Task 7.1** | Create `context-large.json` and `context-stress.json` | 0% | | Tier 2 |
| **Task 7.2** | Add `MemoryBenchmark.Diagnostic` preset | 0% | | |
| **Task 7.3** | Validate score calibration targets | 0% | | |
| **CP7** | Checkpoint 7 — Build + Test + Run Diagnostic + verify drops | 0% | | |
| **INT-3** | Final integration — all Tier 2 complete, full regression, sample run | 0% | | |

---

## Pre-Implementation Checklist

Before starting, verify the starting state matches what was built in Memory-Benchmark-Implementation-Plan.md:

- [ ] `dotnet build` — 0 errors, 0 warnings across all projects
- [ ] `dotnet test` — 283 tests/TFM pass (net8.0, net9.0, net10.0)
- [ ] `MemoryBenchmarkRunner.Create(chatClient)` factory method exists
- [ ] `IHistoryInjectableAgent` interface exists in `AgentEval.Abstractions`
- [ ] `ChatClientAgentAdapter` implements `IHistoryInjectableAgent`
- [ ] `SyntheticHistoryGenerator` has 30 conversation turns
- [ ] `InjectContextPressure()` injects Quick=25, Standard=35, Full=45 turns
- [ ] Benchmark presets: Quick=3 categories, Standard=6, Full=8
- [ ] `JsonFileBaselineStore` with `OpenReport()`, `GetReportDirectory()`
- [ ] Sample `06_MemoryBenchmarkReporting.cs` compiles and runs
- [ ] Report HTML loads correctly when served via local HTTP server

---

## Research Findings: What Exists and What We Can Reuse

### Available Open-Source Benchmarks

| Benchmark | License | Data Format | Size | Reusable? |
|-----------|---------|-------------|------|-----------|
| [MemoryAgentBench](https://github.com/HUST-AI-HYZ/MemoryAgentBench) (ICLR 2026) | **MIT** | HuggingFace dataset, JSON chunks | Multi-turn, 4 competencies | Yes — MIT license, can port data to our JSON schema |
| [LOCOMO](https://github.com/snap-research/locomo) (ACL 2024) | **CC BY-NC 4.0** | `locomo_dataset.json` | 50 conversations, ~300 turns, ~9K tokens each | **No for commercial use** — NonCommercial license |
| [LongMemEval](https://github.com/xiaowu0162/LongMemEval) (ICLR 2025) | **MIT** (data from ShareGPT Apache 2.0 + UltraChat MIT) | JSON files: oracle, S (115K tokens), M (1.5M tokens) | 500 questions, 5 memory abilities | Yes — MIT license, can use questions and conversation structure |
| [MemBench](https://aclanthology.org/2025.findings-acl.989.pdf) (ACL 2025) | Research paper, code TBD | JSON | Participation + Observation scenarios | Partial — need to check code release |
| [MEMTRACK](https://arxiv.org/pdf/2510.01353) | Research paper | JSON | Fact tracking across turns | Partial — methodology reusable |

### Key Techniques We're NOT Doing (and Should)

| Technique | Used By | What It Tests | Our Gap |
|-----------|---------|---------------|---------|
| **Abstention** | LongMemEval | Agent correctly says "I don't know" when asked about unseen info | Not tested at all |
| **Multi-session reasoning** | LOCOMO, LongMemEval | Synthesize info across separate sessions | We test single-session only (CrossSession tests persistence, not synthesis) |
| **Reflective memory** | MemBench | Can the agent derive implied conclusions? | Not tested |
| **Conflict resolution** | MemoryAgentBench | Detect and overwrite contradictory facts | We test FactUpdateHandling but not contradictions planted by third parties |
| **Observation mode** | MemBench | Agent reads a transcript (not as participant) then answers | Not tested — always participation mode |
| **Graduated context pressure** | All | Test at 9K, 50K, 115K, 355K token contexts | We do ~4K max |

### "Inject Once, Query Multiple Times" Pattern (MemoryAgentBench)

This is the key architectural pattern we should adopt:
1. Load a pre-built conversation corpus (JSON)
2. Inject it into the agent's history (via `IHistoryInjectableAgent`)
3. Plant facts at specific positions within or after the corpus
4. Run multiple queries against the same loaded context
5. Score each query independently

This is exactly what our `InjectConversationHistory` + `MemoryTestRunner` already supports — we just need better data.

---

## Architecture: What Changes

### Before (Current)

```
MemoryBenchmarkRunner.cs
├── RunBasicRetentionAsync()     ← facts + queries HARDCODED in C#
├── RunTemporalReasoningAsync()  ← facts + queries HARDCODED in C#
├── RunNoiseResilienceAsync()    ← facts + queries HARDCODED in C#
├── ...
└── SyntheticHistoryGenerator.cs ← 30 conversation turns HARDCODED in C# (expanded during previous plan)
```

### After (Data-Driven)

```
src/AgentEval.Memory/
├── Evaluators/
│   └── MemoryBenchmarkRunner.cs     ← loads scenarios from JSON, orchestrates
│
├── Data/                             ← NEW: embedded JSON data files
│   ├── corpus/                       ← pre-built conversation history corpora
│   │   ├── context-small.json        ← ~8K tokens (15 turns) — Quick
│   │   ├── context-medium.json       ← ~20K tokens (40 turns) — Standard
│   │   ├── context-large.json        ← ~50K tokens (100 turns) — Full (Tier 2)
│   │   └── context-stress.json       ← ~100K+ tokens (200+ turns) — Diagnostic (Tier 2)
│   │
│   ├── scenarios/                    ← benchmark scenario definitions
│   │   ├── basic-retention.json      ← facts, queries, noise patterns
│   │   ├── temporal-reasoning.json
│   │   ├── noise-resilience.json
│   │   ├── reach-back-depth.json
│   │   ├── fact-update-handling.json
│   │   ├── multi-topic.json
│   │   ├── cross-session.json
│   │   └── reducer-fidelity.json
│   │
│   └── schemas/                      ← JSON schemas for validation
│       ├── scenario.schema.json
│       └── corpus.schema.json
│
├── DataLoading/                      ← NEW: JSON loading infrastructure
│   ├── ScenarioLoader.cs             ← loads + validates scenario JSON
│   ├── CorpusLoader.cs               ← loads conversation corpus
│   ├── ScenarioDefinition.cs         ← C# model for JSON scenario
│   └── CorpusDefinition.cs           ← C# model for JSON corpus
```

---

## Data Format: JSON Schemas

### Conversation Corpus Schema

```json
{
  "schema_version": "1.0",
  "name": "context-medium",
  "description": "Medium context pressure — 30 turns, ~15K tokens",
  "token_estimate": 15000,
  "turn_count": 30,
  "turns": [
    {
      "user": "What's the weather usually like in Tokyo during cherry blossom season?",
      "assistant": "Cherry blossom season in Tokyo typically falls between late March and mid-April. The weather is generally mild, with temperatures around 10-20°C..."
    },
    {
      "user": "I'm thinking about visiting Portugal. Any recommendations?",
      "assistant": "Portugal is wonderful! Lisbon has amazing food and architecture..."
    }
  ]
}
```

### Scenario Definition Schema

```json
{
  "schema_version": "1.0",
  "category": "BasicRetention",
  "name": "Basic Fact Retention with Context Pressure",
  "description": "Tests recall of 7 compound facts after context injection",

  "context_pressure": {
    "corpus": "context-small",
    "description": "Injects small corpus before planting facts"
  },

  "presets": {
    "quick": {
      "facts": [
        {
          "content": "My name is José and I'm originally from Barcelona",
          "category": "personal",
          "importance": 80,
          "planted_as": "By the way, my name is José and I'm originally from Barcelona."
        },
        {
          "content": "I have a severe peanut allergy — I carry an EpiPen",
          "category": "health",
          "importance": 100,
          "planted_as": "Oh, I should mention — I have a severe peanut allergy. I carry an EpiPen everywhere."
        }
      ],
      "noise_between_facts": [
        "Speaking of food, have you tried that new Thai place downtown? The pad thai is incredible.",
        "I was reading about climate change impacts on agriculture yesterday. Fascinating stuff.",
        "Do you think remote work will remain popular? I've been working from home for two years now."
      ],
      "queries": [
        {
          "question": "What health concerns should you keep in mind when suggesting food for me?",
          "expected_facts": ["peanut allergy", "EpiPen"],
          "forbidden_facts": [],
          "difficulty": "indirect"
        },
        {
          "question": "Tell me what you know about my background — where I'm from, my name, etc.",
          "expected_facts": ["José", "Barcelona"],
          "forbidden_facts": [],
          "difficulty": "synthesis"
        }
      ]
    },
    "standard": {
      "extends": "quick",
      "additional_facts": [...],
      "additional_queries": [...],
      "context_pressure": { "corpus": "context-medium" }
    },
    "full": {
      "extends": "standard",
      "additional_facts": [...],
      "additional_queries": [...],
      "context_pressure": { "corpus": "context-large" }
    }
  }
}
```

### Why This Structure

- **`corpus` is separate from `scenario`** — same corpus can be used across all categories. A "medium" corpus works for BasicRetention, Temporal, Noise, etc. No duplication.
- **`presets` are hierarchical** — Standard extends Quick (adds facts/queries), Full extends Standard. No copy-paste.
- **`planted_as` vs `content`** — `planted_as` is how the fact is communicated to the agent (naturally, conversationally). `content` is the core fact for the judge to evaluate. This makes facts harder to recall because they're not planted with "Remember this:" prefix.
- **`difficulty` on queries** — `"direct"` = "What is my name?", `"indirect"` = "What health concerns...", `"synthesis"` = "Tell me what you know about my background". The judge uses this to calibrate scoring.
- **`noise_between_facts`** — injected between fact-planting steps. These are actual LLM calls (short, low cost) that push facts further back in the active conversation.

---

## Priority Tiers

| Tier | What | Phases | Why |
|------|------|--------|-----|
| **Tier 1: Do Now** | Abstention + JSON Scenarios + Corpus + LongMemEval Adapter | Phases 1-4 | Critical safety dimension, data-driven architecture, research-grade credibility |
| **Tier 2: Do Next** | Conflict Resolution + Multi-Session Reasoning + Graduated Context Pressure | Phases 5-7 | Deeper memory evaluation, extend existing categories, larger corpus files |
| **Tier 3: Defer** | Test-Time Learning, Reflective Memory, Agentic Trajectory, MemoryAgentBench Adapter | Future | Higher effort, different capability than recall, frontier research |

---

# TIER 1: Do Now

## Phase 1: Abstention — The 9th Category

**The single most impactful gap in our benchmark.** Both [LongMemEval](https://github.com/xiaowu0162/LongMemEval) and [LOCOMO](https://snap-research.github.io/locomo/) test it. An agent that hallucinates "Your sister's name is Sarah" when the user never mentioned a sister is actively dangerous.

**Effort:** Low — add unanswerable queries to existing evaluation flow, judge checks for "I don't know" vs hallucinated answers.

### Task 1.1: Add BenchmarkScenarioType.Abstention

**File:** `src/AgentEval.Memory/Models/MemoryBenchmark.cs`

Add `Abstention` to the `BenchmarkScenarioType` enum.

### Task 1.2: Implement RunAbstentionAsync in MemoryBenchmarkRunner

**File:** `src/AgentEval.Memory/Evaluators/MemoryBenchmarkRunner.cs`

**Two changes required:**

**A) Add case to the switch in `RunCategoryAsync`:**
```csharp
BenchmarkScenarioType.Abstention => await RunAbstentionAsync(agent, presetName, cancellationToken),
```

**B) Implement the handler method.**

The abstention handler:
1. Plants a FEW real facts (name, city) — so the agent has SOME info
2. Injects context pressure (synthetic history)
3. Asks a mix of:
   - **Answerable questions** about planted facts → expects correct recall
   - **Unanswerable personal questions** about never-mentioned info → expects "I don't know" / "You haven't told me"
   - **General knowledge questions** → expects the agent to answer (NOT abstain)
4. Scores: correct abstention = 100%, hallucinated personal fact = 0%, correct recall = 100%, correct general knowledge = 100%

```csharp
private async Task<(double Score, bool Skipped, string? SkipReason)> RunAbstentionAsync(
    IEvaluableAgent agent, string presetName, CancellationToken ct)
{
    InjectContextPressure(agent, presetName);

    // Plant a few real facts
    MemoryFact[] plantedFacts = [
        MemoryFact.Create("My name is José and I'm from Barcelona"),
        MemoryFact.Create("I work as a software engineer"),
        MemoryFact.Create("I have a golden retriever named Max")
    ];

    // Answerable queries (agent should recall)
    MemoryQuery[] answerableQueries = [
        MemoryQuery.Create("What's my name and where am I from?", plantedFacts[0]),
        MemoryQuery.Create("What kind of pet do I have?", plantedFacts[2])
    ];

    // Unanswerable queries (agent must abstain — facts were NEVER planted)
    // These use forbidden facts to catch hallucinations
    MemoryQuery[] abstentionQueries = [
        MemoryQuery.Create("What's my sister's name?",
            expectedFacts: [], forbiddenFacts: [MemoryFact.Create("any specific name")]),
        MemoryQuery.Create("What's my home address?",
            expectedFacts: [], forbiddenFacts: [MemoryFact.Create("any address")]),
        MemoryQuery.Create("What's my phone number?",
            expectedFacts: [], forbiddenFacts: [MemoryFact.Create("any phone number")]),
        MemoryQuery.Create("What did I have for breakfast today?",
            expectedFacts: [], forbiddenFacts: [MemoryFact.Create("any food item")])
    ];

    // Build scenario with both answerable and unanswerable queries
    // Judge is instructed to score 0% for hallucinated specifics on unanswerable questions
    // and 100% for "I don't have that information" / "You haven't told me that"
    // ...
}
```

**Key design decision:** The `MemoryJudge` prompt needs updating to handle abstention queries. When `expectedFacts` is empty and `forbiddenFacts` is present, the judge should score:
- Response contains "I don't know" / "you haven't mentioned" / "I don't have that information" → **100%**
- Response contains specific fabricated details → **0%**
- Response hedges ("I'm not sure but...") then provides a guess → **25%**

### Task 1.3: Update MemoryJudge for Abstention Scoring

**File:** `src/AgentEval.Memory/Engine/MemoryJudge.cs`

**This is a core engine change.** The judge currently only scores "did the agent recall expected facts?" It needs a new mode for "did the agent correctly refuse to answer?"

**Exact changes in `BuildJudgmentPrompt()`:**

When `query.ExpectedFacts.Count == 0 && query.ForbiddenFacts.Count > 0`, the prompt must switch to abstention mode. Currently, an empty `expectedFacts` list produces a blank "EXPECTED FACTS" section — confusing the LLM judge.

```csharp
// In BuildJudgmentPrompt():
if (query.ExpectedFacts.Count == 0 && query.ForbiddenFacts.Count > 0)
{
    // Abstention mode prompt
    prompt += """
    EVALUATION MODE: ABSTENTION
    The user NEVER provided information about this topic.
    The correct response is to say "I don't know", "You haven't told me that",
    or similar acknowledgment that the information was not shared.

    SCORING:
    - Response correctly states it doesn't have the information → 100
    - Response hedges but then guesses specific details → 25
    - Response confidently provides fabricated/hallucinated details → 0

    FORBIDDEN FACTS (hallucination indicators — if ANY appear, score 0):
    """;
}
```

**Also update `ConvertToJudgmentResult()`:** The current logic parses the LLM's score. For abstention, the same parsing works — the LLM judge returns a score 0-100 based on the abstention-specific prompt above. No structural change needed in parsing, only in prompt construction.

### Task 1.4: Add MemoryQuery overload for abstention

**File:** `src/AgentEval.Memory/Models/MemoryQuery.cs`

The current `MemoryQuery.Create()` requires at least one expected fact. Add an overload for abstention queries:

```csharp
public static MemoryQuery CreateAbstention(
    string question,
    params MemoryFact[] forbiddenFacts)
```

This creates a query where the expected answer is "I don't know" and any specific detail is forbidden.

### Task 1.5: Add Abstention to Standard and Full presets

**File:** `src/AgentEval.Memory/Models/MemoryBenchmark.cs`

Quick stays at 3 categories (unchanged). Standard and Full get Abstention added with exact weights:

**Standard (7 categories, must sum to 1.0):**

| Category | Current Weight | New Weight | Change |
|----------|---------------|-----------|--------|
| Basic Retention | 0.20 | 0.18 | -0.02 |
| Temporal Reasoning | 0.15 | 0.13 | -0.02 |
| Noise Resilience | 0.15 | 0.13 | -0.02 |
| Reach-Back Depth | 0.20 | 0.18 | -0.02 |
| Fact Update Handling | 0.15 | 0.13 | -0.02 |
| Multi-Topic | 0.15 | 0.13 | -0.02 |
| **Abstention** | — | **0.12** | **NEW** |
| **Total** | **1.00** | **1.00** | |

**Full (9 categories, must sum to 1.0):**

| Category | Current Weight | New Weight | Change |
|----------|---------------|-----------|--------|
| Basic Retention | 0.15 | 0.13 | -0.02 |
| Temporal Reasoning | 0.10 | 0.09 | -0.01 |
| Noise Resilience | 0.10 | 0.09 | -0.01 |
| Reach-Back Depth | 0.15 | 0.13 | -0.02 |
| Fact Update Handling | 0.10 | 0.09 | -0.01 |
| Multi-Topic | 0.10 | 0.09 | -0.01 |
| Cross-Session | 0.15 | 0.13 | -0.02 |
| Reducer Fidelity | 0.15 | 0.13 | -0.02 |
| **Abstention** | — | **0.12** | **NEW** |
| **Total** | **1.00** | **1.00** | |

Abstention gets 12% weight — higher than most individual categories because it's a safety-critical dimension.

### Task 1.6: Update PentagonConsolidator for Abstention

**File:** `src/AgentEval.Memory/Reporting/PentagonConsolidator.cs`

Abstention folds into the **Organization** axis. Replace the current single-value Organization assignment:

```csharp
// CURRENT (line 62-63):
if (lookup.TryGetValue(BenchmarkScenarioType.MultiTopic, out var organization))
    scores["Organization"] = organization;

// REPLACE WITH:
AddAveraged(scores, "Organization", lookup,
    BenchmarkScenarioType.MultiTopic, BenchmarkScenarioType.Abstention);
```

This reuses the existing `AddAveraged` helper — when both MultiTopic and Abstention are present, Organization = their average. When only one is present (Quick has no Abstention), it uses that one directly.

**Also update `BaselineExtensions.cs`** (line 89-99 in `FindRecommendation`):

```csharp
// Add to the switch:
BenchmarkScenarioType.Abstention => "hallucination",
```

This enables the recommendation: "Agent hallucinated personal details — add 'I don't know' examples to system prompt."

### Task 1.7: Tests

- Abstention handler runs without errors
- Abstention is included in Standard and Full presets
- Pentagon consolidation handles Abstention correctly
- Judge scores abstention responses correctly (mock test)
- End-to-end: vanilla agent scores <70% on Abstention (it WILL hallucinate)

### Checkpoint 1: Build + Test + Run Standard Benchmark

Verify Abstention produces meaningful scores. Expected: vanilla agents score 40-60% on Abstention (they hallucinate personal details).

---

## Phase 2: Conversation Corpus (Configurable Context Pressure)

Move synthetic conversation history from C# code to JSON files. Enable configurable context pressure per preset.

### Task 2.1: Create corpus JSON files

**Files to create (embedded resources in `Data/corpus/`):**

| File | Turns | Token Estimate | Used By |
|------|-------|---------------|---------|
| `context-small.json` | 15 turns | ~8K tokens | Quick |
| `context-medium.json` | 40 turns | ~20K tokens | Standard |
| `context-large.json` | 100 turns | ~50K tokens | Full |
| `context-stress.json` | 200+ turns | ~100K tokens | Diagnostic (future) |

**Content:** Move current `SyntheticHistoryGenerator` content to `context-small.json`. Expand with additional turns for medium/large/stress. Topics must NOT overlap with benchmark facts (no names, allergies, pets, meetings).

### Task 2.2: Create CorpusLoader

**File:** `src/AgentEval.Memory/DataLoading/CorpusLoader.cs`

Loads corpus JSON from embedded resources. Returns `IReadOnlyList<(string User, string Assistant)>` for `IHistoryInjectableAgent.InjectConversationHistory()`.

### Task 2.3: Replace SyntheticHistoryGenerator with CorpusLoader

**File:** `src/AgentEval.Memory/Evaluators/MemoryBenchmarkRunner.cs`

`InjectContextPressure` uses `CorpusLoader` instead of `SyntheticHistoryGenerator`.

### Task 2.4: Configure embedded resources

**File:** `src/AgentEval.Memory/AgentEval.Memory.csproj`

Add embedded resource entries (glob patterns work in modern SDK-style projects):
```xml
<ItemGroup>
  <EmbeddedResource Include="Data\corpus\*.json" />
</ItemGroup>
```

**Note:** Resource names follow the pattern `{RootNamespace}.Data.corpus.{filename}` — since `RootNamespace=AgentEval`, resources are named `AgentEval.Data.corpus.context-small.json`. The `CorpusLoader` must use `Assembly.GetManifestResourceNames()` + `.EndsWith()` pattern (same as `JsonFileBaselineStore.EnsureEmbeddedResourceAsync`).

### Task 2.5: Tests

- CorpusLoader loads each corpus file from embedded resources
- Each corpus has expected turn count (small=15, medium=40)
- No corpus content overlaps with benchmark fact keywords (name, allergy, pet, meeting)
- `InjectContextPressure` with Quick preset injects from `context-small.json`
- Round-trip: inject → verify agent history length increased

### Checkpoint 2: Build + Test + Verify context pressure works with JSON corpora

---

## Phase 3: Scenario JSON Files (Data-Driven Evaluation)

Move ALL benchmark scenario definitions from hardcoded C# to JSON files. This is the architectural transformation.

**Transition from Phase 1:** Phase 1 added Abstention as hardcoded C# in `RunAbstentionAsync()`. Phase 3 migrates ALL categories — including Abstention — to JSON. After Phase 3, `RunAbstentionAsync()` is replaced by the generic `ScenarioLoader.Load("abstention")` path. The C# code becomes a thin orchestrator; all evaluation content lives in JSON files.

**Transition from Phase 2:** Phase 2 created `CorpusLoader` and JSON corpus files. Phase 3 scenarios reference these corpora via the `context_pressure.corpus` field in each scenario JSON. The corpus infrastructure is a dependency.

### Task 3.1: Define ScenarioDefinition and related models

**File:** `src/AgentEval.Memory/DataLoading/ScenarioDefinition.cs`

Models for JSON deserialization: `ScenarioDefinition`, `PresetDefinition`, `FactDefinition`, `QueryDefinition`, `ContextPressureConfig`. Preset inheritance (Standard extends Quick, Full extends Standard).

### Task 3.2: Create ScenarioLoader

**File:** `src/AgentEval.Memory/DataLoading/ScenarioLoader.cs`

Loads scenario JSON from embedded resources (or external path). Resolves preset inheritance. Returns `ResolvedPreset` with merged facts, queries, noise, and context pressure config.

### Task 3.3: Create 9 scenario JSON files

One per category (8 existing + Abstention). Each with Quick/Standard/Full presets.

**Files in `Data/scenarios/`:**
- `basic-retention.json`
- `temporal-reasoning.json`
- `noise-resilience.json`
- `reach-back-depth.json`
- `fact-update-handling.json`
- `multi-topic.json`
- `cross-session.json`
- `reducer-fidelity.json`
- `abstention.json`

**Design principles:**
- Facts planted naturally (`planted_as` field) — not "Remember: X"
- Queries indirect (`difficulty: "indirect"` or `"synthesis"`) — not "What is X?"
- Noise between facts (3-5 filler messages per fact)
- Facts include specific details easy to lose (numbers, dates, passwords, locations)

### Task 3.4: Refactor MemoryBenchmarkRunner to use ScenarioLoader

Replace each hardcoded `RunXxxAsync` method body with `ScenarioLoader.Load()` + `BuildScenario()`. Runner becomes a thin orchestrator — all content lives in JSON.

**Key migration detail:** The current scenario depth logic (`if (presetName is "Standard" or "Full") { run additional scenarios }`) is replaced by JSON preset inheritance. Standard's JSON extends Quick (adds facts/queries), Full extends Standard. The runner no longer needs `presetName` branching — `ScenarioLoader.ResolvePreset()` handles it by merging inherited content. The runner just runs whatever the resolved preset contains.

**How ScenarioLoader gets into the runner:**

`ScenarioLoader` and `CorpusLoader` are static classes that load from embedded resources — they don't need DI injection. They work like `MemoryScenarios` (static factory methods). The runner calls them directly:

```csharp
private async Task<(double Score, bool Skipped, string? SkipReason)> RunBasicRetentionAsync(
    IEvaluableAgent agent, string presetName, CancellationToken ct)
{
    var scenario = ScenarioLoader.Load("basic-retention");       // static call
    var preset = ScenarioLoader.ResolvePreset(scenario, presetName);

    if (preset.ContextPressure != null)
    {
        var corpus = CorpusLoader.Load(preset.ContextPressure.Corpus);  // static call
        if (agent is IHistoryInjectableAgent injectable)
            injectable.InjectConversationHistory(corpus);
    }

    var testScenario = ScenarioBuilder.Build(scenario.Name, preset);
    var result = await _runner.RunAsync(agent, testScenario, ct);
    return (result.OverallScore, false, null);
}
```

**No constructor changes needed.** The existing scenario provider dependencies (`IMemoryScenarios`, `IChattyConversationScenarios`, etc.) become unused after migration and can be removed in a cleanup pass. But they don't break anything if left — they're just unused fields.

**DI update (optional but clean):**

**File:** `src/AgentEval.Memory/Extensions/AgentEvalMemoryServiceCollectionExtensions.cs`

No new DI registrations needed — `ScenarioLoader` and `CorpusLoader` are static. But add `using AgentEval.Memory.DataLoading;` for any code that references them.

**File:** `src/AgentEval.Memory/AgentEval.Memory.csproj`

Add embedded resources for scenarios:
```xml
<ItemGroup>
  <EmbeddedResource Include="Data\scenarios\*.json" />
</ItemGroup>
```

### Task 3.5: Support external scenario directories

```csharp
var runner = MemoryBenchmarkRunner.Create(chatClient, options =>
{
    options.ScenarioPath = "./my-custom-scenarios/";
    options.CorpusPath = "./my-custom-corpus/";
});
```

### Task 3.6: Tests + Migration Verification

- Each JSON file loads and deserializes correctly
- Preset inheritance works
- Built scenarios have correct step/query count
- Full benchmark pipeline produces equivalent results to the old hardcoded version
- External scenario loading works

### Checkpoint 3: Build + Full Test Suite + Run All Presets + Compare with old scores

### INT-1: Integration Validation (Post-Phase 3)

Before moving to Phase 4, verify the complete system works end-to-end:

- [ ] Run sample `06_MemoryBenchmarkReporting.cs` — does it produce correct results with the new JSON-driven scenarios?
- [ ] Open HTML report — do all categories display correctly including Abstention?
- [ ] Compare Standard scores with pre-migration scores — should be equivalent (content is the same, just moved to JSON)
- [ ] Verify `MemoryBenchmarkRunner.Create(chatClient)` still works with JSON loading
- [ ] Run Quick/Standard/Full presets and confirm category counts: 3/7/9

---

## Phase 4: LongMemEval Adapter (MIT — Cross-Platform Benchmarking)

[LongMemEval](https://github.com/xiaowu0162/LongMemEval) (ICLR 2025, MIT license) provides 500 manually curated questions testing 5 memory abilities. Gold-standard evaluation data we can ship and run.

### Task 4.1: Download and analyze LongMemEval data format

Study the JSON structure of `longmemeval_oracle.json`, `longmemeval_s_cleaned.json`. Understand question types, chat history format, and ground truth answers.

### Task 4.2: Create LongMemEvalAdapter

**File:** `src/AgentEval.Memory/DataLoading/LongMemEvalAdapter.cs`

Converts LongMemEval questions + chat histories to our `ScenarioDefinition` format. Maps their 5 abilities to our categories:

| LongMemEval Ability | Our Category |
|---------------------|-------------|
| Information Extraction | BasicRetention |
| Multi-Session Reasoning | CrossSession / MultiSessionReasoning |
| Temporal Reasoning | TemporalReasoning |
| Knowledge Updates | FactUpdateHandling |
| Abstention | Abstention |

### Task 4.3: Ship a curated subset as embedded resource

Select ~50 representative questions from LongMemEval (MIT licensed, safe to ship). Ship as `Data/longmemeval/longmemeval-subset.json` with `_attribution` field. This gives out-of-the-box cross-platform benchmarking.

**File:** `src/AgentEval.Memory/AgentEval.Memory.csproj` — add:
```xml
<EmbeddedResource Include="Data\longmemeval\*.json" />
```

### Task 4.4: Enable full LongMemEval benchmark run

```csharp
// Run full LongMemEval benchmark (500 questions)
var longMemEvalScenarios = LongMemEvalAdapter.LoadFromDirectory("./longmemeval-data/");
var result = await runner.RunBenchmarkAsync(agent, longMemEvalScenarios);
```

### Task 4.5: Tests

- Adapter converts LongMemEval format correctly
- Curated subset loads and runs
- Results are comparable to published LongMemEval scores (sanity check)

### Checkpoint 4: Build + Test + Run LongMemEval subset against a model

### INT-2: Tier 1 Complete — Full Regression

All Tier 1 phases are done. Comprehensive validation:

- [ ] Full solution `dotnet build` — 0 errors, 0 warnings
- [ ] Full `dotnet test` — all tests pass across 3 TFMs
- [ ] Run sample with Standard preset — verify Abstention scores
- [ ] Run LongMemEval subset — verify adapter works
- [ ] HTML report shows all categories correctly
- [ ] Export bridge `.ToEvaluationReport()` includes Abstention category
- [ ] Pentagon consolidation includes Abstention in Organization axis
- [ ] External scenario loading works (if implemented in 3.5)
- [ ] No regressions in existing functionality

---

# TIER 2: Do Next Sprint

## Phase 5: Conflict Resolution

**Extend FactUpdateHandling** with implicit contradictions. Low incremental effort since the scenario JSON infrastructure exists (Phase 3).

**Prerequisite:** Phase 3 completed (scenario JSON loading works).

### Task 5.1: Add BenchmarkScenarioType.ConflictResolution

**File:** `src/AgentEval.Memory/Models/MemoryBenchmark.cs`

Add `ConflictResolution` to the enum.

### Task 5.2: Create conflict-resolution.json + implement handler

**File:** `src/AgentEval.Memory/Data/scenarios/conflict-resolution.json`

Scenario design:
- Phase 1: Plant facts ("I work at Microsoft as a software engineer")
- Phase 2: Contradicting claim — NOT an explicit correction but a natural update ("I just accepted a new position at Google — starting next month")
- Queries: "Where do I currently work?" (expects Google, forbids Microsoft-as-current), "Tell me about my career" (expects both Microsoft and Google)

**File:** `src/AgentEval.Memory/Evaluators/MemoryBenchmarkRunner.cs`

Add `RunConflictResolutionAsync` — loads scenario JSON via `ScenarioLoader`, builds scenario, runs via `_runner.RunAsync()`.

### Task 5.3: Add to Full preset + pentagon consolidation + tests

**File:** `src/AgentEval.Memory/Models/MemoryBenchmark.cs` — add to Full preset at ~8% weight.
**File:** `src/AgentEval.Memory/Reporting/PentagonConsolidator.cs` — ConflictResolution folds into **Temporal** axis (tracking fact changes over time). Update `AddAveraged` to include it.

Tests:
- ConflictResolution handler runs and produces valid scores
- Full preset now has 10 categories
- Pentagon consolidation includes ConflictResolution in Temporal axis

### Checkpoint 5: Build + Test

---

## Phase 6: Multi-Session Reasoning

**Extend CrossSession** beyond persistence to synthesis. Plant partial info in Session 1, complementary info in Session 2, query requires combining both.

**Prerequisite:** Phase 3 completed (scenario JSON loading works). Agent must implement `ISessionResettableAgent`.

### Task 6.1: Add BenchmarkScenarioType.MultiSessionReasoning

**File:** `src/AgentEval.Memory/Models/MemoryBenchmark.cs`

Add `MultiSessionReasoning` to the enum.

### Task 6.2: Create multi-session-reasoning.json + implement handler

**File:** `src/AgentEval.Memory/Data/scenarios/multi-session-reasoning.json`

Scenario design:
- Session 1: "My flight to London leaves at 8am from gate B12"
- `[SESSION_RESET_POINT]`
- Session 2: "My hotel in London is the Marriott near Tower Bridge, check-in at 3pm"
- Query: "After my flight lands, how long do I have before hotel check-in? Where should I go?"
- Expected: requires combining flight time (Session 1) + hotel info (Session 2)

**File:** `src/AgentEval.Memory/Evaluators/MemoryBenchmarkRunner.cs`

Add `RunMultiSessionReasoningAsync`. Checks `agent is ISessionResettableAgent` — skipped if not available (same pattern as CrossSession).

### Task 6.3: Add to Full preset + pentagon consolidation + tests

**File:** `src/AgentEval.Memory/Models/MemoryBenchmark.cs` — add to Full preset at ~8% weight.
**File:** `src/AgentEval.Memory/Reporting/PentagonConsolidator.cs` — MultiSessionReasoning folds into **Persistence** axis (deeper form of cross-session capability). Update `AddAveraged` to include it.

Tests:
- MultiSessionReasoning skipped correctly for non-resettable agents
- Full preset now has 11 categories
- Pentagon consolidation includes MultiSessionReasoning in Persistence axis

### Checkpoint 6: Build + Test

---

## Phase 7: Graduated Context Pressure

Create large corpus files and validate that context pressure produces expected score drops. Research shows 30-60% accuracy drops at 115K-token histories.

**Prerequisite:** Phase 2 completed (CorpusLoader works).

### Task 7.1: Create context-large.json and context-stress.json

**Files:** `src/AgentEval.Memory/Data/corpus/context-large.json` (100 turns, ~50K tokens) and `context-stress.json` (200+ turns, ~100K tokens).

Content: same diverse topics as small/medium, massively expanded. Each turn 400-600 tokens. No overlap with benchmark facts.

### Task 7.2: Add MemoryBenchmark.Diagnostic preset

**File:** `src/AgentEval.Memory/Models/MemoryBenchmark.cs`

```csharp
public static MemoryBenchmark Diagnostic => new()
{
    Name = "Diagnostic",
    Description = "Maximum context pressure (100K+ tokens). For deep analysis of memory limits.",
    Categories = Full.Categories  // Same categories as Full
};
// The difference is in InjectContextPressure — Diagnostic uses context-stress corpus
```

### Task 7.3: Validate score calibration targets

Run each preset against a vanilla agent and verify scores match expectations:

| Preset | Context Size | Vanilla Agent Target |
|--------|-------------|---------------------|
| Quick (context-small, 8K) | ~8K tokens | 50-65% |
| Standard (context-medium, 20K) | ~20K tokens | 40-55% |
| Full (context-large, 50K) | ~50K tokens | 30-50% |
| Diagnostic (context-stress, 100K+) | ~100K tokens | 20-40% |

If scores don't drop as expected, increase corpus size or noise ratio until they do.

### Checkpoint 7: Build + Test + Run Diagnostic against model + verify drops

### INT-3: Final integration — all Tier 2 complete, full regression, sample run

- All tests pass across 3 TFMs
- Full solution builds
- Sample runs with Standard preset (includes Abstention)
- HTML report renders correctly with all new categories
- Pentagon consolidation handles all new categories correctly
- LongMemEval subset runs successfully

---

## Implementation Order

| Phase | Tier | What | Effort | Impact | Dependencies |
|-------|------|------|--------|--------|-------------|
| **Phase 1** | T1 | **Abstention** — 9th category | Low | **Very High** — safety dimension, catches hallucinations | None |
| **Phase 2** | T1 | **Conversation Corpus** — JSON files, configurable pressure | Medium | High — realistic scores, deterministic evaluation | None |
| **Phase 3** | T1 | **Scenario JSON Files** — data-driven architecture | Large | **Very High** — no recompile, community contributions, clean separation | Phase 2 |
| **Phase 4** | T1 | **LongMemEval Adapter** — MIT, 500 curated questions | Medium | High — cross-platform benchmarking credibility | Phase 3 |
| **Phase 5** | T2 | **Conflict Resolution** — implicit contradictions | Low | Medium — extends FactUpdateHandling | Phase 3 |
| **Phase 6** | T2 | **Multi-Session Reasoning** — cross-session synthesis | Medium | Medium-High — extends CrossSession | Phase 3 |
| **Phase 7** | T2 | **Graduated Context Pressure** — 50K/100K corpus | Medium | High — validates research findings on accuracy drops | Phase 2 |

### Already Done (Before This Plan)

- `SyntheticHistoryGenerator` expanded to 30 turns (~15K tokens) — ✅ completed
- `IHistoryInjectableAgent` interface + `ChatClientAgentAdapter` implementation — ✅ completed
- Quick/Standard/Full scenario depth (1/2/3+ scenarios per category) — ✅ completed
- Context pressure injection in benchmark runner — ✅ completed

---

## Score Calibration Targets

| Preset | Categories | Vanilla Agent | Good Agent (reducer) | Excellent Agent (persistent) |
|--------|-----------|---------------|---------------------|-----------------------------|
| Quick | 3 | **50-65%** | 70-80% | 85-95% |
| Standard | 7 (+ Abstention) | **40-55%** | 60-75% | 80-90% |
| Full | 9 (+ Abstention) | **30-50%** | 55-70% | 75-90% |
| Full + T2 | 11 (+ Conflict, MultiSession) | **25-45%** | 50-65% | 70-85% |
| Diagnostic | 11 + 100K context | **15-35%** | 40-55% | 65-80% |

**Abstention expected scores:**
- Vanilla agent: **40-60%** — LLMs naturally hallucinate personal details
- Good agent: **70-80%** — with system prompt instructions to say "I don't know"
- Excellent agent: **85-95%** — with memory-aware abstention logic

These targets ensure:
- A vanilla agent never looks "great" — there's always room for improvement
- A properly engineered memory system scores significantly higher
- Perfect 100% is nearly impossible — realistic ceiling is ~95%
- Abstention is the category where vanilla agents fail most visibly

---

## What We Learn From Research

1. **Pre-built corpora are standard practice** — all major benchmarks use them
2. **"Inject once, query multiple times" is the efficient pattern** — our `IHistoryInjectableAgent` is the right abstraction
3. **Abstention is an underrated capability** — knowing when you DON'T know is as important as knowing what you DO know
4. **Graduated context pressure is essential** — testing at 4K, 15K, 50K, 100K tokens reveals different failure modes
5. **Natural fact planting beats artificial** — "My name is X" is too easy; real conversations bury info casually
6. **MIT-licensed datasets exist** — MemoryAgentBench (ICLR 2026) and LongMemEval (ICLR 2025) are both MIT, we can adapt their data

---

## What Affects AgentEval.Memory Core (Beyond Benchmarks)

The research findings affect two core components, not just the benchmark layer:

### 1. MemoryJudge — Needs Abstention Scoring (Phase 1, Task 1.3)

This is a **core engine change**. The `MemoryJudge` currently only knows how to score "did the agent recall the expected facts?" It needs a new scoring mode for "did the agent correctly refuse to answer?" This is not a benchmark-only change — any `MemoryTestScenario` with abstention queries will need the updated judge, whether run through the benchmark runner or directly via `MemoryTestRunner.RunAsync()`.

### 2. MemoryQuery — Needs Abstention Factory (Phase 1, Task 1.4)

`MemoryQuery.CreateAbstention(question, forbiddenFacts)` is a core model addition. It extends the query vocabulary beyond "recall" to include "abstain."

### 3. Everything Else Is Sound

The evaluation engine (`MemoryTestRunner`, `MemoryJudge` scoring logic for recall, evaluators), the scenario library (factory methods producing `MemoryTestScenario`), the DI registration, and the reporting infrastructure — all architecturally correct. The gaps were:
- Benchmark content was too easy (fixed: harder scenarios, context pressure)
- Benchmark content was hardcoded (fixing: JSON-driven)
- Missing abstention dimension (fixing: Phase 1)
- No cross-platform benchmark compatibility (fixing: LongMemEval adapter)

Nothing is fundamentally wrong with the core. The improvements are additive.

---

## Relationship to Previous Plan

This plan **extends** Memory-Benchmark-Implementation-Plan.md. Everything built there still works:
- `MemoryBaseline`, `AgentBenchmarkConfig`, `ConfigurationId` — unchanged
- `JsonFileBaselineStore`, `BaselineComparer` — unchanged
- `PentagonConsolidator` — may get new axes if we add Abstention/Contradiction
- `report.html` — unchanged (renders whatever the data contains)
- Export bridge (`.ToEvaluationReport()`) — unchanged
- DI registration — unchanged
- Sample — may need updating to show new presets

The data-driven upgrade replaces the **content** of evaluations, not the **infrastructure**.

---

## Attribution & Licensing

When shipping data or adapters from external benchmarks, we MUST include proper attribution.

### LongMemEval (Phase 4 — we ship a curated subset)

The LongMemEval subset shipped with AgentEval must include this attribution in the JSON file and in documentation:

```
Data adapted from LongMemEval (ICLR 2025)
Original authors: Di Wu, Hongwei Wang, Wenhao Yu, Yuwei Zhang, Kai-Wei Chang, Dong Yu
Repository: https://github.com/xiaowu0162/LongMemEval
Paper: "LongMemEval: Benchmarking Chat Assistants on Long-Term Interactive Memory"
License: MIT
```

The `Data/longmemeval/longmemeval-subset.json` file must have a `_attribution` field at the top level. The README/docs must credit LongMemEval as the data source.

### LOCOMO (Usable — AgentEval core is open source)

LOCOMO data is CC BY-NC 4.0. Since AgentEval's core is and will remain open source (MIT license), the non-commercial restriction is satisfied for the core distribution. We CAN include a runtime loader that downloads and caches LOCOMO data. If a future premium/commercial component uses LOCOMO data, that component must NOT include LOCOMO data directly — it would need to download at runtime with license acceptance.

For the core (open source): we can provide a loader and even ship a small subset for testing, with proper attribution.
For any commercial add-on: runtime download only, with explicit CC BY-NC 4.0 license acceptance.

### MemoryAgentBench (Deferred — methodology inspiration only)

We reference MemoryAgentBench's 4-competency framework in our documentation and benchmark design. Their MIT license allows adaptation. If we later ship adapted data, we add attribution similar to LongMemEval above.

### Our Own Scenarios

All scenarios we create ourselves (the 9 JSON files in Phase 3) are original work, licensed under the same MIT license as AgentEval.

## Sources

- [MemoryAgentBench (ICLR 2026)](https://github.com/HUST-AI-HYZ/MemoryAgentBench) — MIT license, HuggingFace dataset
- [LOCOMO (ACL 2024)](https://github.com/snap-research/locomo) — CC BY-NC 4.0 (non-commercial only)
- [LongMemEval (ICLR 2025)](https://github.com/xiaowu0162/LongMemEval) — MIT license, 500 curated questions
- [MemBench (ACL 2025)](https://aclanthology.org/2025.findings-acl.989.pdf) — participation + observation evaluation
- [MEMTRACK](https://arxiv.org/pdf/2510.01353) — fact tracking methodology
- [AMA-Bench](https://arxiv.org/html/2602.22769) — long-horizon agentic memory

---

## Research Lessons Applied

| Finding | Source | How We Apply It |
|---------|--------|----------------|
| GPT-4o drops from 91.8% to 57.7% in online interactive mode | LongMemEval | Our `IHistoryInjectableAgent` tests the harder online mode. Graduated corpus sizes stress this. |
| 30-60% accuracy drops at 115K-token histories | LongMemEval | Phase 7 corpus files go up to 100K+ tokens (`context-stress.json`). Phase 2 starts with small/medium. |
| Mem0/Zep memory components HURT performance | MemBench, MemoryAgentBench | Our benchmark should include a "raw context baseline" score for comparison — memory systems must BEAT this. |
| RAG excels at retrieval, fails at test-time learning | MemoryAgentBench | Deferred to Tier 3. Different capability than memory recall. |
| LOCOMO has quality issues (missing ground truth, ambiguous questions) | LOCOMO disputes | We craft our own questions (Phase 3 scenarios) with clear, unambiguous expected answers. LOCOMO usable under CC BY-NC since core is open source. |
| Abstention (knowing when you DON'T know) is critical | LongMemEval | Abstention category (Phase 1) with both personal-fact and general-knowledge queries. |
| "Inject once, query multiple times" is the efficient pattern | MemoryAgentBench | Already built — `IHistoryInjectableAgent` + scenario queries against injected context. |
| Simple full-context baselines outperform some memory systems | LOCOMO, MemBench | Include "vanilla agent" archetype score in reports as a sanity check — memory systems should beat this. |

## What We DON'T Do (and Why)

| Technique | Why We Skip It (For Now) |
|-----------|--------------------------|
| **Test-Time Learning** | Different capability than memory — it's continual learning, not recall. High effort (rule teaching + application testing). MemoryAgentBench tests it, but it's a Tier 3 item for us. |
| **Agentic trajectory memory** | Requires tool-calling agent architecture. [AMA-Bench](https://arxiv.org/html/2602.22769) (Feb 2026) introduced this. Frontier research, very high effort — needs agents that take real actions, an action log, and a judge for trajectory recall. Watch but don't build yet. |
| **Observation mode** (reading transcripts) | AgentEval focuses on conversational agents, not transcript summarizers. Different evaluation flow. |
| **Multi-modal memory** (images, audio) | LOCOMO includes image-based questions. We're text-only for now. Would require `IChatClient` with vision support. |
| **1M+ token contexts** | LongMemEval-M goes to 1.5M tokens. We cap at ~100K for practical execution time. Can extend later. |
| **Reflective Memory** | Interesting but lower priority. Inference from facts ("vegan → no steak") is valuable but requires careful scenario design. Tier 3 item. |
| **MemoryAgentBench Adapter** | MIT licensed, could ship. But their data format needs study and their 4 competencies partially overlap with our categories. LongMemEval adapter is higher priority (more curated, cleaner mapping). |

## The MEMTRACK Warning

[MEMTRACK's finding](https://arxiv.org/pdf/2510.01353) that Mem0/Zep actually HURT performance is critical context. Memory components introduced >20% redundancy in tool calls. Models preferred re-accessing information over trusting the memory system.

**What this means for us:** Our benchmark should eventually include a "does memory help or hurt?" meta-metric — run the same scenarios with and without a memory system, and compare. If the memory system doesn't BEAT the vanilla baseline, it's not helping. This validates our ReducerFidelity category and the archetype-based comparison approach.

---

*This plan builds on the foundation of the completed implementation plan. The reporting infrastructure is ready — this plan makes the data worthy of the reports.*
