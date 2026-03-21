# AgentEval Memory Benchmark — Reporting Architecture Proposals

> **Date:** March 21, 2026  
> **Author:** Design session — Jose Luis + Claude  
> **Status:** Proposal — ready for implementation review  
> **Context:** Built on top of the existing `MemoryBenchmarkRunner`, `MemoryBenchmarkResult`, `MemoryBaseline`, and the 8-category scoring system already implemented in AgentEval.Memory.

---

## 1. The Problem We're Solving

Today, `MemoryBenchmarkRunner` produces a `MemoryBenchmarkResult` — 8 category scores, an overall grade, and recommendations. Then it vanishes. There's no persistence, no comparison, no visualization, no way to answer "is this agent getting better?" or "which configuration is best for my use case?"

We need a reporting layer that:

- Saves benchmark results as named, timestamped baselines with full agent configuration metadata.
- Loads them dynamically into an interactive HTML report — no parameters, no build step.
- Supports multiple agents, multiple benchmarks, multiple folder roots.
- Compares configurations visually — hexagon overlays, timeline progressions, delta waterfalls.
- Provides context through agent archetypes — reference profiles that represent common agent patterns so developers can see how their agent compares to known profiles.
- Handles real-world complexity: agents with multiple context providers, custom reducers, mixed memory backends.

---

## 2. Folder Structure — The Discovery Mechanism

### 2.1 The Core Idea: Convention Over Configuration

The HTML report discovers its data through a `manifest.json` file. No URL parameters, no build step, no server-side rendering. Drop JSON files into the folder, regenerate the manifest, open `report.html`. Done.

### 2.2 Single-Agent, Single-Benchmark (Simplest Case)

```
.agenteval/
└── benchmarks/
    └── WeatherAssistant/
        ├── report.html              ← the static template (this file never changes)
        ├── manifest.json            ← auto-generated index of all baselines
        └── baselines/
            ├── 2026-03-01_v1.0.json
            ├── 2026-03-08_v2.0.json
            ├── 2026-03-15_v2.1.json
            └── 2026-03-20_v2.2.json
```

The report loads `./manifest.json` relative to itself. That's the only assumption — the manifest is a sibling of `report.html`. Everything else is described inside the manifest.

### 2.3 Multi-Agent (Same Repository)

When a repository contains multiple agents, each gets its own benchmark folder:

```
.agenteval/
└── benchmarks/
    ├── WeatherAssistant/
    │   ├── report.html
    │   ├── manifest.json
    │   └── baselines/
    │       ├── 2026-03-01_v1.0.json
    │       └── 2026-03-15_v2.1.json
    │
    ├── CustomerSupportBot/
    │   ├── report.html
    │   ├── manifest.json
    │   └── baselines/
    │       ├── 2026-03-10_gpt4o.json
    │       └── 2026-03-18_claude.json
    │
    └── index.html                   ← optional: agent selector landing page
```

Each agent has its own `report.html` + `manifest.json` pair. The optional `index.html` at the root lists all agents with links to their reports. This keeps things simple: each report is self-contained, each manifest describes one agent's history.

### 2.4 Multi-Benchmark (Different Presets, Different Scopes)

A single agent might have benchmarks run at different preset levels (Quick vs Standard vs Full), or custom-scoped benchmarks (e.g., "memory only" vs "memory + tool use"). Handle this by adding a `benchmark_id` to the manifest:

```
.agenteval/
└── benchmarks/
    └── WeatherAssistant/
        ├── report.html
        ├── manifest.json            ← contains ALL benchmarks for this agent
        └── baselines/
            ├── full/
            │   ├── 2026-03-01_v1.0.json
            │   └── 2026-03-15_v2.1.json
            └── quick/
                ├── 2026-03-05_nightly.json
                └── 2026-03-12_nightly.json
```

The manifest groups baselines by `benchmark_id`. The report shows a benchmark selector dropdown. Within each benchmark, baselines are comparable (same categories, same weights). Cross-benchmark comparison is explicitly not supported — comparing a Quick run to a Full run would be misleading.

### 2.5 CI Integration — Auto-Generated Folders

In CI pipelines, the benchmark runner should output to a predictable path:

```bash
# The runner writes the baseline JSON
dotnet agenteval benchmark run \
  --agent WeatherAssistant \
  --preset Full \
  --output .agenteval/benchmarks/WeatherAssistant/baselines/

# Then regenerate the manifest
dotnet agenteval benchmark manifest \
  --root .agenteval/benchmarks/WeatherAssistant/

# The report.html is copied from a template once and never regenerated
```

Alternatively, `MemoryBenchmarkRunner` can have a post-run hook:

```csharp
var result = await runner.RunBenchmarkAsync(agent, MemoryBenchmark.Full);
var baseline = result.ToBaseline("v2.1 Production", agentConfig);
await baselineStore.SaveAsync(baseline);   // writes JSON + updates manifest
```

### 2.6 Why This Structure Works

- **No server required.** `report.html` + `manifest.json` + baseline JSONs can be served by `dotnet serve`, `python -m http.server`, `npx serve`, or committed as GitHub Pages.
- **Git-friendly.** Baselines are individual JSON files — each one is a separate commit, diffable, reviewable.
- **CI-friendly.** The runner writes files; the report reads them. No coupling.
- **Scalable.** 100+ baselines work fine — the manifest is just an index, the report loads individual files on demand.

---

## 3. Manifest Schema — The Contract

### 3.1 Full Schema

```json
{
  "schema_version": "1.0",
  "generated_at": "2026-03-20T14:30:00Z",
  "generated_by": "AgentEval.Memory v1.0.0",

  "agent": {
    "name": "WeatherAssistant",
    "description": "Multi-turn weather assistant with location memory and preference tracking",
    "repository": "https://github.com/acme/weather-assistant",
    "team": "Platform Engineering"
  },

  "benchmarks": [
    {
      "benchmark_id": "memory-full",
      "preset": "Full",
      "categories": [
        "BasicRetention", "TemporalReasoning", "NoiseResilience",
        "ReachBackDepth", "FactUpdateHandling", "MultiTopic",
        "CrossSession", "ReducerFidelity"
      ],
      "baselines": [
        {
          "id": "bl-001",
          "file": "baselines/full/2026-03-01_v1.0.json",
          "name": "v1.0 gpt-3.5 MessageCount(10)",
          "timestamp": "2026-03-01T10:00:00Z",
          "overall_score": 37.8,
          "grade": "F",
          "tags": ["v1.0", "initial", "nightly"]
        },
        {
          "id": "bl-005",
          "file": "baselines/full/2026-03-15_v2.1.json",
          "name": "v2.1 gpt-4o SlidingWindow(50)",
          "timestamp": "2026-03-15T14:30:00Z",
          "overall_score": 80.3,
          "grade": "B",
          "tags": ["v2.1", "production"]
        }
      ]
    }
  ],

  "archetypes": "archetypes.json"
}
```

### 3.2 Design Decisions

**Why `benchmarks` is an array:** Supports multiple benchmark presets for the same agent. Each benchmark has its own set of baselines. The report shows a dropdown to switch between them.

**Why `overall_score` is in the manifest:** So the report can render the baseline selector (with scores) without loading every individual file. Individual files are fetched on demand when selected for display.

**Why `archetypes` is a separate file:** Archetypes are shared across agents. They live at a higher level and are referenced by path. More on this in Section 6.

---

## 4. Individual Baseline JSON — The Full Picture

### 4.1 Schema

Each baseline JSON maps 1:1 to the `MemoryBaseline` C# model:

```json
{
  "id": "bl-005",
  "name": "v2.1 gpt-4o SlidingWindow(50)",
  "description": "Production configuration with expanded context window and semantic search",
  "timestamp": "2026-03-15T14:30:00Z",

  "agent_config": {
    "agent_name": "WeatherAssistant",
    "agent_type": "MEAI ChatClientAgentAdapter",
    "model_id": "gpt-4o",
    "model_version": "2025-01-01",
    "temperature": 0.7,
    "max_tokens": 4096,
    "reducer_strategy": "SlidingWindowChatReducer(window:50)",
    "context_providers": [
      "InMemoryChatHistoryProvider",
      "SemanticSearchProvider",
      "UserPreferenceProvider"
    ],
    "memory_provider": "InMemoryChatHistoryProvider",
    "custom_config": {
      "embedding_model": "text-embedding-3-small",
      "vector_store": "Qdrant",
      "session_timeout_minutes": 30
    }
  },

  "benchmark": {
    "preset": "Full",
    "duration_ms": 12400,
    "total_llm_calls": 47,
    "estimated_cost_usd": 0.23,
    "scenario_depth": "Standard"
  },

  "overall_score": 80.3,
  "grade": "B",
  "stars": 4,

  "category_results": {
    "BasicRetention":     { "score": 95, "grade": "A+", "skipped": false, "scenario_count": 2, "recommendation": null },
    "TemporalReasoning":  { "score": 82, "grade": "B",  "skipped": false, "scenario_count": 2, "recommendation": "Consider adding temporal markers to system prompt" },
    "NoiseResilience":    { "score": 71, "grade": "C+", "skipped": false, "scenario_count": 2, "recommendation": "Noise resilience below 75% — increase chatty scenario depth" },
    "ReachBackDepth":     { "score": 88, "grade": "B+", "skipped": false, "scenario_count": 1, "recommendation": null },
    "FactUpdateHandling": { "score": 73, "grade": "C+", "skipped": false, "scenario_count": 2, "recommendation": "Fact correction handling needs attention" },
    "MultiTopic":         { "score": 80, "grade": "B",  "skipped": false, "scenario_count": 2, "recommendation": null },
    "CrossSession":       { "score": 85, "grade": "B+", "skipped": false, "scenario_count": 1, "recommendation": null },
    "ReducerFidelity":    { "score": 68, "grade": "C",  "skipped": false, "scenario_count": 1, "recommendation": "Reducer fidelity weak — try semantic chunking" }
  },

  "dimension_scores": {
    "Recall":       91.5,
    "Resilience":   71.0,
    "Temporal":     77.5,
    "Persistence":  85.0,
    "Organization": 80.0,
    "Compression":  68.0
  },

  "recommendations": [
    "Reducer fidelity is the weakest link — consider semantic chunking instead of sliding window.",
    "Noise resilience at 71% — the agent struggles with buried facts in chatty conversations.",
    "Temporal + Update scores suggest fact-correction handling needs improvement."
  ],

  "tags": ["v2.1", "production", "gpt-4o"]
}
```

### 4.2 Key Design Notes

**`agent_config.context_providers` is an array.** This is critical. A real agent often has multiple context providers stacked — chat history + semantic search + user preferences. The report renders these as rounded pill/chip cards (see Section 5). The `memory_provider` field is kept separate as the "primary" provider for quick identification, but the full list lives in `context_providers`.

**`agent_config.custom_config` is a freeform object.** Every team has agent-specific settings (embedding model, vector store, session timeouts, etc.). Rather than trying to enumerate all possibilities, we provide a bag for arbitrary key-value pairs. The report renders these as a collapsible "Advanced Config" section.

**`category_results` includes `recommendation` per category.** These come from `MemoryBenchmarkRunner`'s built-in recommendation engine. The report displays them inline on KPI scorecards so developers know exactly what to fix.

**`dimension_scores` is a pre-computed consolidation.** The 8→6 mapping (see Section 5.3) is computed by the runner and stored in the baseline, so the report doesn't need to know the consolidation formula. If the formula changes in a future version, old baselines still display correctly because they carry their own computed dimensions.

### 4.3 The C# Side — Producing This JSON

```csharp
// In MemoryBenchmarkRunner, after running the benchmark:
var result = await runner.RunBenchmarkAsync(agent, MemoryBenchmark.Full);

// Create the baseline with full config
var baseline = result.ToBaseline(
    name: "v2.1 gpt-4o SlidingWindow(50)",
    description: "Production configuration with expanded context window",
    agentConfig: new AgentBenchmarkConfig
    {
        AgentName = "WeatherAssistant",
        AgentType = "MEAI ChatClientAgentAdapter",
        ModelId = "gpt-4o",
        ModelVersion = "2025-01-01",
        Temperature = 0.7,
        MaxTokens = 4096,
        ReducerStrategy = "SlidingWindowChatReducer(window:50)",
        ContextProviders = ["InMemoryChatHistoryProvider", "SemanticSearchProvider", "UserPreferenceProvider"],
        MemoryProvider = "InMemoryChatHistoryProvider",
        CustomConfig = new Dictionary<string, string>
        {
            ["embedding_model"] = "text-embedding-3-small",
            ["vector_store"] = "Qdrant"
        }
    },
    tags: ["v2.1", "production", "gpt-4o"]
);

// Persist to the folder structure
await baselineStore.SaveAsync(baseline);
// This writes the JSON file AND updates manifest.json
```

---

## 5. Rendering Proposals — The Visual Language

### 5.1 Context Providers as Pill Cards

When `context_providers` contains multiple entries, the report renders them as a horizontal row of rounded pill/chip cards beneath the agent name:

```
WeatherAssistant · gpt-4o · v2.1

┌────────────────────────────┐  ┌──────────────────────┐  ┌────────────────────────┐
│ 💾 InMemoryChatHistory     │  │ 🔍 SemanticSearch     │  │ 👤 UserPreference       │
└────────────────────────────┘  └──────────────────────┘  └────────────────────────┘
```

Implementation: the `context_providers` array is split and rendered as individual chips with:
- A subtle background matching the agent's color in the hexagon.
- A small icon prefix (auto-assigned or from a configurable icon map).
- Rounded corners (border-radius: 20px), horizontal scroll if they overflow.
- On hover, show the full provider class name if it was truncated.

In the agent_config, context providers are stored as a simple comma-separated-friendly array. The report parses them. If the raw data comes in as a comma-separated string instead of an array (for backward compatibility), the report splits on commas:

```javascript
// Handle both array and comma-separated string
const providers = Array.isArray(config.context_providers)
  ? config.context_providers
  : config.context_providers.split(',').map(s => s.trim());
```

This is important because in many .NET configurations, provider lists come from `appsettings.json` as comma-separated strings.

### 5.2 Configuration Detail Cards

Each baseline in the hexagon comparison view is represented by a clickable card showing:

```
┌──────────────────────────────────────────────────────┐
│  ● v2.1 gpt-4o SlidingWindow(50)              80.3% │
│                                                      │
│  Model:    gpt-4o (2025-01-01)                       │
│  Reducer:  SlidingWindowChatReducer(window:50)       │
│  Provider: InMemoryChatHistoryProvider               │
│                                                      │
│  ┌──────────────┐ ┌─────────────┐ ┌───────────────┐ │
│  │ 💾 InMemory   │ │ 🔍 Semantic │ │ 👤 UserPref   │ │
│  └──────────────┘ └─────────────┘ └───────────────┘ │
│                                                      │
│  Tags: production · gpt-4o · v2.1                    │
│  Run: Mar 15, 2026 · Full preset · 12.4s · $0.23    │
└──────────────────────────────────────────────────────┘
```

The colored dot (●) matches the hexagon overlay color. Clicking the card toggles it on/off the hexagon chart. The score in the top-right is color-coded by grade (green/amber/red).

### 5.3 The 8→6 Hexagon Consolidation

**Why 6?** An octagon (8 axes) is visually cluttered — the labels overlap and the shape differences become hard to read when comparing. A pentagon (5) loses too much resolution. A hexagon (6) is the sweet spot: readable labels, distinct shape, and each axis still maps cleanly to the underlying categories.

**The consolidation formula:**

| Hexagon Axis | Source Categories | Formula | Rationale |
|---|---|---|---|
| **Recall** | BasicRetention + ReachBackDepth | `(retention + depth) / 2` | Both measure "can it remember?" — breadth (how many facts) vs. depth (how far back). They are the two halves of recall capability. |
| **Resilience** | NoiseResilience | direct (1:1) | Noise resilience is a unique dimension — the ability to extract signal from conversational noise. No other category measures this. Kept standalone. |
| **Temporal** | TemporalReasoning + FactUpdateHandling | `(temporal + updates) / 2` | Both involve reasoning about change over time. Temporal = when things happened. Updates = handling corrections. They stress the same underlying capability: tracking fact evolution. |
| **Persistence** | CrossSession | direct (1:1) | Session boundary survival is a binary, architectural capability. Either memory persists or it doesn't. Unique enough to stand alone. |
| **Organization** | MultiTopic | direct (1:1) | Cross-domain fact management is orthogonal to all other dimensions. An agent can have great recall but terrible topic separation. Kept standalone. |
| **Compression** | ReducerFidelity | direct (1:1) | Compression measures what survives the IChatReducer. This is distinct from noise resilience (input-side problem) — compression is a reducer-side/output-side problem. |

**Why not merge Resilience + Compression?** They seem similar (both are about information loss) but they test completely different failure modes. Noise resilience = the agent can't extract the fact because it's buried in chatter. Compression fidelity = the fact existed in memory but was discarded by the reducer. The fixes are different: noise resilience improves with better attention mechanisms; compression improves with better reducers. Merging them would hide which fix is needed.

**The `dimension_scores` object in the baseline JSON stores the pre-computed hexagon values.** This means:
- The report doesn't need to know the consolidation formula.
- If we change the formula in v2 (e.g., weighted average instead of simple average), old baselines still render correctly.
- Custom dimensions can be added without breaking existing baselines.

### 5.4 When to Use What Shape

| Scenario | Shape | Axes | Why |
|---|---|---|---|
| Single agent, quick overview | Hexagon (6) | Consolidated dimensions | Clean, readable, shows the "shape" of memory |
| Single agent, detailed drill-down | Octagon (8) | All raw categories | Full resolution, used in the KPI scorecard section |
| Comparing 2–4 configs | Hexagon overlay (6) | Consolidated | Overlaid shapes are readable at 6 axes, cluttered at 8 |
| Comparing 5+ configs | Bar chart | All 8 categories | Too many overlaid shapes become unreadable. Switch to grouped bars. |
| Archetype comparison | Hexagon (6) | Consolidated + archetype reference | The archetype is a semi-transparent reference shape behind the agent's shape |

---

## 6. Agent Archetypes — Reference Profiles

### 6.1 The Concept

Agent archetypes are pre-defined benchmark profiles representing common types of agents. They serve as reference points: "How does my agent's memory compare to a typical customer support bot? A coding assistant? A personal assistant?"

Archetypes are NOT real benchmark runs. They are hand-crafted reference profiles based on industry knowledge, published benchmarks, and AgentEval's own testing experience. They represent "what you'd expect" from a well-built agent of that type.

### 6.2 Why Archetypes Are Useful

1. **Context for scores.** A noise resilience score of 65% means nothing in isolation. But if the "Customer Support Agent" archetype expects 80%+ because support conversations are inherently chatty, suddenly 65% is clearly a problem.

2. **Goal setting.** Archetypes give teams a target shape to aim for. "We're building a personal assistant, so we should match or exceed the Personal Assistant archetype on Recall and Temporal."

3. **Trade-off awareness.** The Coding Assistant archetype has high Recall but low Persistence (code context is session-bound). The Personal Assistant archetype has the opposite shape. Seeing your agent's shape next to the archetype reveals whether your trade-offs are intentional or accidental.

### 6.3 Proposed Archetypes

```json
{
  "schema_version": "1.0",
  "archetypes": [
    {
      "id": "customer-support",
      "name": "Customer Support Agent",
      "icon": "🎧",
      "color": "#38bdf8",
      "description": "Handles multi-turn support conversations with high noise (frustrated users, repeated info, emotional messages). Needs strong noise resilience and cross-session persistence for ticket continuity.",
      "typical_config": {
        "models": ["gpt-4o", "claude-3-sonnet"],
        "reducers": ["SlidingWindow(30-50)", "SummarizingReducer"],
        "context_providers": ["TicketHistoryProvider", "KnowledgeBaseProvider", "CustomerProfileProvider"]
      },
      "expected_scores": {
        "Recall": 85,
        "Resilience": 82,
        "Temporal": 70,
        "Persistence": 90,
        "Organization": 75,
        "Compression": 72
      },
      "critical_dimensions": ["Resilience", "Persistence"],
      "acceptable_weaknesses": ["Temporal"],
      "rationale": "Support agents must remember customer details across sessions (high Persistence) and extract key issues from frustrated, chatty conversations (high Resilience). Temporal reasoning is less critical — most queries are about current state, not historical sequence."
    },
    {
      "id": "personal-assistant",
      "name": "Personal Assistant",
      "icon": "🧑‍💼",
      "color": "#a78bfa",
      "description": "Long-running personal assistant that remembers preferences, schedules, relationships, and life events across months of conversation. Deep recall and temporal awareness are essential.",
      "typical_config": {
        "models": ["gpt-4o", "claude-3.5-sonnet"],
        "reducers": ["SummarizingReducer", "SemanticChunkingReducer"],
        "context_providers": ["LongTermMemoryProvider", "CalendarProvider", "PreferenceStore"]
      },
      "expected_scores": {
        "Recall": 92,
        "Resilience": 70,
        "Temporal": 90,
        "Persistence": 95,
        "Organization": 88,
        "Compression": 80
      },
      "critical_dimensions": ["Recall", "Temporal", "Persistence"],
      "acceptable_weaknesses": ["Resilience"],
      "rationale": "Personal assistants must remember everything — names, dates, preferences, life events — and reason about when things happened. Session persistence is non-negotiable. Noise resilience is less critical because personal conversations tend to be more information-dense than support chats."
    },
    {
      "id": "coding-assistant",
      "name": "Coding Assistant",
      "icon": "💻",
      "color": "#34d399",
      "description": "Session-bound coding assistant that tracks code context, file structures, and debugging state within a single session. High recall and organization, but persistence across sessions is less important.",
      "typical_config": {
        "models": ["gpt-4o", "claude-3.5-sonnet", "codestral"],
        "reducers": ["SlidingWindow(100)", "CodeAwareReducer"],
        "context_providers": ["FileContextProvider", "GitDiffProvider", "SymbolIndexProvider"]
      },
      "expected_scores": {
        "Recall": 90,
        "Resilience": 60,
        "Temporal": 65,
        "Persistence": 45,
        "Organization": 92,
        "Compression": 85
      },
      "critical_dimensions": ["Recall", "Organization", "Compression"],
      "acceptable_weaknesses": ["Persistence", "Temporal"],
      "rationale": "Coding assistants need to track multiple files, functions, and types simultaneously (high Organization) and recall code context accurately (high Recall). Sessions are typically short — cross-session persistence matters less. Conversations are low-noise (code-focused), so Resilience is naturally less stressed."
    },
    {
      "id": "research-analyst",
      "name": "Research Analyst",
      "icon": "📊",
      "color": "#fb923c",
      "description": "Ingests large document sets, compares sources, tracks claims with provenance. Needs exceptional compression fidelity and temporal reasoning for tracking when information was published.",
      "typical_config": {
        "models": ["gpt-4o", "claude-3-opus"],
        "reducers": ["SummarizingReducer", "HierarchicalReducer"],
        "context_providers": ["DocumentStoreProvider", "CitationProvider", "RAGProvider"]
      },
      "expected_scores": {
        "Recall": 88,
        "Resilience": 65,
        "Temporal": 85,
        "Persistence": 70,
        "Organization": 85,
        "Compression": 90
      },
      "critical_dimensions": ["Compression", "Temporal", "Organization"],
      "acceptable_weaknesses": ["Resilience"],
      "rationale": "Research agents process massive contexts that must be compressed without losing critical facts (high Compression). They must track when information was published and how claims evolved (high Temporal). Multi-topic organization is essential for cross-source comparison."
    },
    {
      "id": "healthcare-assistant",
      "name": "Healthcare Assistant",
      "icon": "🏥",
      "color": "#f472b6",
      "description": "Patient-facing assistant that tracks medical history, medications, allergies, and appointments. Every dimension is critical — a forgotten allergy is a safety hazard.",
      "typical_config": {
        "models": ["gpt-4o", "med-specialized models"],
        "reducers": ["PriorityAwareReducer", "NeverForgetReducer"],
        "context_providers": ["PatientRecordProvider", "MedicationProvider", "AllergyProvider", "AppointmentProvider"]
      },
      "expected_scores": {
        "Recall": 98,
        "Resilience": 85,
        "Temporal": 92,
        "Persistence": 98,
        "Organization": 95,
        "Compression": 90
      },
      "critical_dimensions": ["Recall", "Persistence", "Organization", "Temporal"],
      "acceptable_weaknesses": [],
      "rationale": "Healthcare has zero tolerance for memory failures. A forgotten allergy, a missed medication interaction, or a confused patient history is a safety incident. Every dimension must be near-perfect. The expected scores are aggressive because they must be."
    },
    {
      "id": "minimal-chatbot",
      "name": "Minimal Chatbot (Baseline)",
      "icon": "💬",
      "color": "#64748b",
      "description": "A basic chatbot with no persistent memory, no reducer, and a small context window. This is the floor — the minimum viable agent. Use it as a sanity check: your agent should always beat this.",
      "typical_config": {
        "models": ["gpt-3.5-turbo"],
        "reducers": ["MessageCountingChatReducer(keep:5)"],
        "context_providers": ["InMemoryChatHistoryProvider"]
      },
      "expected_scores": {
        "Recall": 45,
        "Resilience": 30,
        "Temporal": 25,
        "Persistence": 0,
        "Organization": 30,
        "Compression": 20
      },
      "critical_dimensions": [],
      "acceptable_weaknesses": ["Persistence", "Compression", "Temporal"],
      "rationale": "This is what a naive chatbot looks like. Small context window means terrible depth. No persistent memory means zero cross-session. Aggressive message counting means the reducer destroys information. If your agent scores below this, something is fundamentally broken."
    }
  ]
}
```

### 6.4 How Archetypes Display in the Report

**In the hexagon view:** Archetypes appear as toggleable reference shapes. When enabled, the archetype renders as a dashed, semi-transparent shape behind the agent's solid shapes. This creates a "target zone" visual — you can see where your agent exceeds the archetype and where it falls short.

```
Select archetypes to compare against:

┌──────────────┐ ┌──────────────┐ ┌──────────────┐
│ 🎧 Customer  │ │ 🧑‍💼 Personal │ │ 💻 Coding    │
│ Support      │ │ Assistant    │ │ Assistant    │
└──────────────┘ └──────────────┘ └──────────────┘
┌──────────────┐ ┌──────────────┐ ┌──────────────┐
│ 📊 Research  │ │ 🏥 Healthcare│ │ 💬 Minimal   │
│ Analyst      │ │ Assistant    │ │ (Baseline)   │
└──────────────┘ └──────────────┘ └──────────────┘
```

**In the KPI scorecard:** Each category card can optionally show the archetype's expected score as a reference marker on the progress bar. "Your noise resilience is 71%. The Customer Support archetype expects 82%."

**In the comparer:** Archetypes can be selected as one side of a comparison. "Compare my agent vs the Personal Assistant archetype" produces a waterfall showing where you exceed and where you fall short of the archetype expectations.

### 6.5 Archetype File Location

Archetypes are shared across agents, so they live at the benchmarks root:

```
.agenteval/
└── benchmarks/
    ├── archetypes.json              ← shared archetype definitions
    ├── WeatherAssistant/
    │   ├── report.html
    │   └── manifest.json            ← references "../archetypes.json"
    └── CustomerSupportBot/
        ├── report.html
        └── manifest.json            ← references "../archetypes.json"
```

The manifest's `"archetypes"` field points to the file. The report loads it and renders the archetype selector.

### 6.6 Custom Archetypes

Teams can add their own archetypes to the file. The schema is the same. This is useful for:
- **Internal baselines:** "Our Q4 target profile" as an archetype with specific score expectations.
- **Competitor benchmarks:** If you benchmark a competitor's agent, save the results as an archetype for ongoing comparison.
- **Regulatory targets:** Healthcare or finance teams might define minimum acceptable scores per dimension as an archetype.

---

## 7. Report Generation Workflow

### 7.1 The Three Pieces

1. **`report.html`** — The static template. Ships with AgentEval as a resource. Copied once to the benchmark folder. Never regenerated unless the user upgrades AgentEval and wants the latest report template.

2. **Baseline JSON files** — Generated by `MemoryBenchmarkRunner` each time a benchmark runs. One file per run.

3. **`manifest.json`** — Regenerated every time a new baseline is added. This is the glue between the static template and the dynamic data.

### 7.2 Workflow: Manual

```bash
# 1. Run benchmark (your test code does this)
dotnet test --filter "MemoryBenchmark"

# 2. The test writes a baseline JSON (via baselineStore.SaveAsync)
#    → .agenteval/benchmarks/WeatherAssistant/baselines/2026-03-20_v2.2.json

# 3. Regenerate manifest
dotnet agenteval manifest rebuild .agenteval/benchmarks/WeatherAssistant/

# 4. Open report
open .agenteval/benchmarks/WeatherAssistant/report.html
# (or: cd .agenteval/benchmarks/WeatherAssistant && python -m http.server 8080)
```

### 7.3 Workflow: CI/CD

```yaml
# GitHub Actions example
- name: Run Memory Benchmark
  run: dotnet test --filter "MemoryBenchmark"

- name: Rebuild Manifest
  run: dotnet agenteval manifest rebuild .agenteval/benchmarks/${{ env.AGENT_NAME }}/

- name: Upload Report
  uses: actions/upload-artifact@v4
  with:
    name: memory-benchmark-report
    path: .agenteval/benchmarks/${{ env.AGENT_NAME }}/

# Optional: publish to GitHub Pages
- name: Deploy to Pages
  uses: peaceiris/actions-gh-pages@v3
  with:
    publish_dir: .agenteval/benchmarks/
```

### 7.4 Workflow: Integrated (No CLI Step)

The `JsonFileBaselineStore` can auto-regenerate the manifest on save:

```csharp
public class JsonFileBaselineStore : IBaselineStore
{
    private readonly string _rootPath;

    public async Task SaveAsync(MemoryBaseline baseline)
    {
        // 1. Write baseline JSON
        var filename = $"{baseline.Timestamp:yyyy-MM-dd}_{Slugify(baseline.Name)}.json";
        var path = Path.Combine(_rootPath, "baselines", filename);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(baseline));

        // 2. Auto-rebuild manifest
        await RebuildManifestAsync();
    }

    public async Task RebuildManifestAsync()
    {
        var baselines = Directory.GetFiles(Path.Combine(_rootPath, "baselines"), "*.json")
            .Select(f => JsonSerializer.Deserialize<MemoryBaseline>(File.ReadAllText(f)))
            .OrderBy(b => b.Timestamp)
            .ToList();

        var manifest = new BenchmarkManifest
        {
            SchemaVersion = "1.0",
            GeneratedAt = DateTimeOffset.UtcNow,
            Agent = baselines.First().AgentConfig,
            Benchmarks = /* group by preset */,
        };

        await File.WriteAllTextAsync(
            Path.Combine(_rootPath, "manifest.json"),
            JsonSerializer.Serialize(manifest));
    }

    // 3. Copy report.html if it doesn't exist
    public async Task EnsureReportTemplateAsync()
    {
        var reportPath = Path.Combine(_rootPath, "report.html");
        if (!File.Exists(reportPath))
        {
            var template = GetEmbeddedResource("AgentEval.Memory.Report.report.html");
            await File.WriteAllTextAsync(reportPath, template);
        }
    }
}
```

This way, the developer never needs to run a separate CLI command. After every `SaveAsync`, the manifest is updated and the report template is present. Just open the HTML.

### 7.5 Report Loading Sequence

When `report.html` opens in a browser:

```javascript
// 1. Fetch manifest (relative to report.html)
const manifest = await fetch('./manifest.json').then(r => r.json());

// 2. Render agent info, benchmark selector, baseline list
renderHeader(manifest.agent);
renderBenchmarkSelector(manifest.benchmarks);

// 3. Fetch the latest baseline in full (for the overview)
const latest = manifest.benchmarks[0].baselines.at(-1);
const latestData = await fetch(latest.file).then(r => r.json());
renderOverview(latestData);

// 4. Fetch archetypes (if referenced)
if (manifest.archetypes) {
  const archetypes = await fetch(manifest.archetypes).then(r => r.json());
  renderArchetypeSelector(archetypes);
}

// 5. On user interaction (selecting baselines for comparison),
//    fetch individual baseline files on demand
async function onBaselineSelected(baseline) {
  const data = await fetch(baseline.file).then(r => r.json());
  addToHexagon(data);
}
```

This means the report only loads what it needs. With 50 baselines, it fetches the manifest (small) + the latest baseline (one file) on page load. Additional baselines are fetched when the user clicks them.

---

## 8. Multi-Root and Cross-Agent Comparison

### 8.1 The Problem

Sometimes you want to compare agents from different folders — "How does WeatherAssistant compare to CustomerSupportBot?" The single-agent report can't do this because each report only sees its own manifest.

### 8.2 The Solution: An Index Report

At the top level of `.agenteval/benchmarks/`, generate an `index.html` that:

1. Scans all subdirectories for `manifest.json` files.
2. Loads the latest baseline from each agent.
3. Renders a cross-agent hexagon comparison.

```
.agenteval/
└── benchmarks/
    ├── index.html                   ← cross-agent comparison
    ├── index-manifest.json          ← auto-generated, lists all agents
    ├── archetypes.json
    ├── WeatherAssistant/
    │   ├── report.html
    │   └── manifest.json
    └── CustomerSupportBot/
        ├── report.html
        └── manifest.json
```

The `index-manifest.json` is simply:

```json
{
  "agents": [
    { "name": "WeatherAssistant", "path": "WeatherAssistant/manifest.json", "latest_score": 80.3 },
    { "name": "CustomerSupportBot", "path": "CustomerSupportBot/manifest.json", "latest_score": 72.1 }
  ]
}
```

The index report fetches this, then on demand fetches individual baselines when the user selects agents for comparison. This enables "which of our agents has the best memory?" views.

---

## 9. Summary of All Proposals

| Proposal | What | Impact | Effort |
|---|---|---|---|
| Folder structure with manifest | Convention-based discovery, no parameters | Foundation for everything else | Medium |
| Baseline JSON schema | Full agent config, scores, recommendations, dimension pre-computation | Enables all visualization | Medium |
| Context providers as pill cards | Comma-separated → rendered as rounded chips | Better UX for complex configs | Small |
| 8→6 hexagon consolidation | Readable shape comparison for multi-config overlays | Core visualization | Small (formula only) |
| Agent archetypes | Pre-defined reference profiles for 6 common agent types | Context for scores, goal setting | Medium |
| Configuration detail cards | Clickable cards showing model, reducer, providers, score | Enables multi-config comparison | Small |
| Dynamic loading (fetch on demand) | Report loads manifest first, individual baselines on click | Scales to 100+ baselines | Small |
| Multi-agent index report | Cross-agent comparison at the benchmarks root | Enterprise use case | Medium |
| CI integration | Auto-generate manifest, upload as artifact | Continuous memory quality tracking | Small |
| Integrated `JsonFileBaselineStore` | Auto-rebuild manifest on save, copy template | Zero-CLI workflow | Medium |

---

## 10. What I Would Do First

**Phase 1 (ship in days):**
- Define the baseline JSON schema (Section 4).
- Implement `JsonFileBaselineStore` with auto-manifest rebuild (Section 7.4).
- Ship `report.html` as an embedded resource in the AgentEval.Memory NuGet.
- The report supports: overview scorecard, hexagon, baselines timeline, two-config comparison.

**Phase 2 (ship in a week):**
- Add archetypes.json with the 6 proposed profiles (Section 6.3).
- Archetype selector in the hexagon view.
- Context provider pill rendering.
- Expand configuration detail cards.

**Phase 3 (ship when needed):**
- Multi-benchmark support (Quick vs Full in the same agent folder).
- Cross-agent index report.
- CI/CD templates (GitHub Actions, Azure DevOps).

---

*This document is ready to hand to the implementing model. It contains every schema, every rendering decision, and every workflow — with rationale for each choice.*
