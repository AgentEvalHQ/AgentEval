# MAF ↔ AgentEval.Memory Concept Mapping

This document explains how AgentEval.Memory evaluators work with Microsoft Agent Framework (MAF) 1.3.0's pipeline architecture, and why no code changes are needed.

## Architecture Overlap

MAF 1.3.0 and AgentEval.Memory both deal with conversation history and memory, but at different abstraction levels:

- **MAF** manages memory *inside the agent pipeline*: `ChatHistoryProvider`, `AIContextProvider`, `CompactionStrategy`
- **AgentEval.Memory** *evaluates* memory quality from *outside*: it sends prompts, resets sessions, and measures retention

The two systems are complementary, not competing.

## Concept Mapping Table

| AgentEval.Memory Concept | MAF 1.3.0 Equivalent | Relationship |
|---|---|---|
| `ISessionResettableAgent.ResetSessionAsync()` | `agent.CreateSessionAsync()` (new session) | **Same effect.** `MAFAgentAdapter.ResetSessionAsync()` calls `CreateSessionAsync()` internally. New session = fresh conversation history. |
| `IHistoryInjectableAgent.InjectConversationHistory()` | `ChatHistoryProvider.ProvideChatHistoryAsync()` | **Different purpose.** AgentEval injects *synthetic test data* to skip LLM setup calls. MAF providers manage *real* conversation history. `MAFAgentAdapter` implements both. |
| `ChatClientAgentAdapter._conversationHistory` | `InMemoryChatHistoryProvider` | **Reimplementation.** Both maintain `List<ChatMessage>`. `ChatClientAgentAdapter` does this outside MAF's pipeline (for raw `IChatClient` wrapping). |
| `LLMPersistentMemoryAgent` (Sample G5) | `AIContextProvider` subclass | **Same pattern, different implementation.** Manual memory management vs. pipeline-integrated. See Sample G6 for the MAF-native approach. |
| `ReducerEvaluator` | `CompactionStrategy` (experimental) | **Complementary.** `ReducerEvaluator` *measures* compression quality. `CompactionStrategy` *performs* compression. Different layers — one evaluates, the other executes. |
| `CrossSessionEvaluator` | `AgentSession` lifecycle | **Compatible.** Evaluator calls `ResetSessionAsync()` → adapter creates new session → `ChatHistoryProvider` loses history → `AIContextProvider` retains long-term memory. Correctly tests persistent memory. |
| `ReachBackEvaluator` | `InMemoryChatHistoryProvider` + reducers | **Compatible.** Noise turns fill context window → reducers may drop early turns → evaluator measures what the agent still recalls. |

## Key Insight: Why It Works Without Changes

AgentEval.Memory evaluators operate at the `IEvaluableAgent` abstraction level:

```
CrossSessionEvaluator
    ↓ calls
IEvaluableAgent.InvokeAsync(prompt)
ISessionResettableAgent.ResetSessionAsync()
    ↓ which is
MAFAgentAdapter
    ↓ delegates to
ChatClientAgent.RunAsync(messages, session)
    ↓ which triggers  
ChatHistoryProvider → AIContextProviders → IChatClient → LLM
```

The evaluators don't need to know about `ChatHistoryProvider`, `AIContextProvider`, or `CompactionStrategy`. They test *behavior* (does the agent recall facts?) not *mechanism* (how does it store them?).

## Session Lifecycle

```
┌────────────────────────────────────────────────┐
│  CrossSessionEvaluator / ReachBackEvaluator    │
│  Calls: InvokeAsync(), ResetSessionAsync()     │
└──────────────────┬─────────────────────────────┘
                   │
┌──────────────────▼─────────────────────────────┐
│  MAFAgentAdapter                               │
│  ResetSessionAsync() → CreateSessionAsync()    │
│  InvokeAsync() → agent.RunAsync(msg, session)  │
└──────────────────┬─────────────────────────────┘
                   │
┌──────────────────▼─────────────────────────────┐
│  MAF Agent Pipeline                            │
│  ChatHistoryProvider  → session-scoped history  │
│  AIContextProvider    → persistent memory       │
│  CompactionStrategy   → context window mgmt     │
│  IChatClient          → LLM API call            │
└────────────────────────────────────────────────┘
```

On `ResetSessionAsync()`:
- `ChatHistoryProvider` state is lost (new session, empty history) ✅
- `AIContextProvider` state **persists** (lives outside the session) ✅
- This correctly models: "conversation context" vs. "long-term memory"

## When to Use Which Adapter

| Scenario | Adapter | Why |
|---|---|---|
| MAF `ChatClientAgent` with pipeline features | `MAFAgentAdapter` | Gets `AIContextProvider`, `ChatHistoryProvider`, session management |
| Raw `IChatClient` without MAF pipeline | `ChatClientAgentAdapter` | Manages its own conversation history |
| Any `IChatClient` (quick setup) | `.AsEvaluableAgent()` | Extension method, wraps in `ChatClientAgentAdapter` |

## Samples

| Sample | Description |
|---|---|
| Sample A6 (Session Lifecycle) | Shows `CreateSessionAsync` → multi-turn → `ResetSessionAsync` → isolation |
| Sample G5 (Cross-Session Memory) | Manual memory: `LLMPersistentMemoryAgent` with `_longTermMemory` dict |
| Sample G6 (AIContextProvider Memory) | MAF-native: `PersistentMemoryProvider : AIContextProvider` in pipeline |

## Future Considerations

### Session Observability (Deferred)
Currently, `AgentSession.StateBag` is opaque to evaluators. For advanced scenarios, it might be useful to inspect what an `AIContextProvider` stored. Recommendation: defer unless users request it — evaluators should test behavior, not implementation details.

### CompactionStrategy Integration (Deferred)
`ReducerEvaluator` could be enhanced to configure a MAF agent with a specific `CompactionStrategy` and report pre/post-compaction message counts. Recommendation: defer — keep `AgentEval.Memory` framework-agnostic. Users configure their agents; the evaluator measures results.
