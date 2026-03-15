# How Does Memory Actually Work in MAF? (RC3 Source-Level Analysis)

> **Date:** 2026-03-05
> **Based on:** Full source-code audit of MAFVnext (RC3)
> **Related:** [AgentEval-Feature-Analysis-MemoryEvaluation.md](../AgentEval-Feature-Analysis-MemoryEvaluation.md) | [RC3 Deep Analysis](../AgentEval-Feature-Analysis-MemoryEvaluation-RC3.md)
> **Author:** AgentEval Feature Research

---

## Purpose

This document answers the fundamental question: **How does memory retrieval and storage actually work in MAF?**

Specifically:
- How does an AIContextProvider know *what* to retrieve?
- How does Mem0 search its knowledge graph?
- How are memories written — what triggers it, what gets stored?
- Does memory from one turn carry over to the next?
- What's the difference between AIContextProvider and ChatHistoryProvider?
- What are the risks of the current approach?

---

## Table of Contents

1. [The Two Provider Families](#1-the-two-provider-families)
2. [The Full Agent Turn Lifecycle](#2-the-full-agent-turn-lifecycle)
3. [How Memories Are READ (Retrieved)](#3-how-memories-are-read-retrieved)
4. [How Memories Are WRITTEN (Stored)](#4-how-memories-are-written-stored)
5. [Turn Carryover: Does Memory Persist?](#5-turn-carryover-does-memory-persist)
6. [Provider-by-Provider Deep Dive](#6-provider-by-provider-deep-dive)
7. [The Two Search Modes](#7-the-two-search-modes)
8. [The No-Query-Rewriting Problem](#8-the-no-query-rewriting-problem)
9. [Implications for AgentEval](#9-implications-for-agenteval)

---

## 1. The Two Provider Families

MAF has **two distinct base classes** for memory, and they serve fundamentally different purposes:

```
┌──────────────────────────────────────────────────────────────────────────────┐
│                        MAF Memory Architecture                              │
│                                                                             │
│  ┌─────────────────────────────┐    ┌──────────────────────────────────┐    │
│  │   ChatHistoryProvider       │    │   AIContextProvider              │    │
│  │   (Conversation Memory)     │    │   (Semantic/Long-Term Memory)    │    │
│  │                             │    │                                  │    │
│  │   "What was said"           │    │   "What is known"               │    │
│  │                             │    │                                  │    │
│  │   • Stores raw messages     │    │   • Stores extracted knowledge  │    │
│  │   • Full conversation log   │    │   • Semantic search retrieval   │    │
│  │   • Ordered by time         │    │   • Relevance-based recall      │    │
│  │   • May use a reducer       │    │   • Cross-session reach         │    │
│  │                             │    │                                  │    │
│  │  InMemoryChatHistory ─────┐ │    │  ChatHistoryMemory ──────────┐  │    │
│  │  CosmosChatHistory ───────┤ │    │  Mem0Provider ───────────────┤  │    │
│  │  (custom implementations) ┘ │    │  TextSearchProvider ─────────┤  │    │
│  │                             │    │  (custom implementations) ───┘  │    │
│  └─────────────────────────────┘    └──────────────────────────────────┘    │
│                                                                             │
│  Both called by the agent on EVERY turn:                                    │
│  InvokingAsync() → READ       InvokedAsync() → WRITE                       │
└──────────────────────────────────────────────────────────────────────────────┘
```

### Key Difference

| Aspect | ChatHistoryProvider | AIContextProvider |
| --- | --- | --- |
| **What it stores** | Raw `ChatMessage` objects (full conversation) | Extracted knowledge / facts / embeddings |
| **How it retrieves** | Returns all (or reduced) history in order | Semantic search by relevance |
| **Message source tag** | `ChatHistory` | `AIContextProvider` |
| **Write filter (default)** | Excludes messages already from chat history (prevents duplication) | Only stores External messages (user input) + all responses |
| **Typical use** | "Show me the conversation so far" | "What do we know about this user?" |
| **Cross-session** | Per-session (unless externally persisted) | Can search across sessions (Mem0, vector store) |
| **Ordering** | Chronological | By relevance score |

### How They Complement Each Other

A MAF agent typically has **both** a ChatHistoryProvider AND one or more AIContextProviders:

```
Turn: User says "What about the Ferrari?"

┌─────────────────────────────────────────────────────────────────┐
│ STEP 1: ChatHistoryProvider.InvokingAsync()                     │
│                                                                 │
│   Returns: [msg1, msg2, msg3, ...]  ← Full conversation so far │
│   Source tag: ChatHistory                                       │
└─────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────┐
│ STEP 2: AIContextProvider.InvokingAsync()                       │
│                                                                 │
│   Filters input: Only External messages (user's current turn)   │
│   Query:   "What about the Ferrari?"  ← raw user text          │
│                                                                 │
│   Returns: "## Memories                                         │
│             • User owned a red Ferrari (March 2025)             │
│             • User sold Ferrari, bought Honda CBR (Nov 2025)"   │
│   Source tag: AIContextProvider                                 │
└─────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────┐
│ STEP 3: LLM sees everything merged:                             │
│                                                                 │
│   [System instructions]                                         │
│   [Chat history from ChatHistoryProvider]                       │
│   [Semantic memories from AIContextProvider]                    │
│   [Current user message: "What about the Ferrari?"]             │
└─────────────────────────────────────────────────────────────────┘
```

The **ChatHistoryProvider** gives the LLM the conversation context ("what was said").
The **AIContextProvider** gives it extracted knowledge ("what is known") via semantic search.

---

## 2. The Full Agent Turn Lifecycle

Here's what happens on **every single turn**, from the actual `ChatClientAgent.RunAsync()` source:

```
User sends: "What about the Ferrari?"
                    │
     ═══════════════╪══════════════════════════════
     READ PHASE     │  (InvokingAsync)
     ═══════════════╪══════════════════════════════
                    │
                    ▼
    ┌──────────────────────────────────────────────┐
    │ 1. ChatHistoryProvider.InvokingAsync()        │
    │    └─ ProvideChatHistoryAsync()               │
    │       └─ Returns accumulated messages         │
    │       └─ Tags them as "ChatHistory" source    │
    │    └─ Merges with current request messages    │
    └──────────────────────────────────────────────┘
                    │
                    ▼
    ┌──────────────────────────────────────────────┐
    │ 2. AIContextProvider.InvokingAsync()          │
    │    └─ Filters input: External messages only   │
    │    └─ ProvideAIContextAsync(filtered)         │
    │       └─ Builds search query from user text   │
    │       └─ Semantic search (vector/Mem0/custom) │
    │       └─ Returns memories as AIContext         │
    │    └─ Tags them as "AIContextProvider" source │
    │    └─ Merges into accumulated context          │
    └──────────────────────────────────────────────┘
                    │
     ═══════════════╪══════════════════════════════
     LLM CALL       │
     ═══════════════╪══════════════════════════════
                    │
                    ▼
    ┌──────────────────────────────────────────────┐
    │ 3. ChatClient.GetResponseAsync()              │
    │    └─ All messages merged + instructions      │
    │    └─ Tools from AIContextProviders           │
    │    └─ LLM generates response                  │
    └──────────────────────────────────────────────┘
                    │
     ═══════════════╪══════════════════════════════
     WRITE PHASE    │  (InvokedAsync)
     ═══════════════╪══════════════════════════════
                    │
                    ▼
    ┌──────────────────────────────────────────────┐
    │ 4. ChatHistoryProvider.InvokedAsync()          │
    │    └─ Filter: Exclude messages from ChatHistory│
    │    └─ StoreChatHistoryAsync(request+response) │
    │    └─ Appends to session state                │
    │    └─ Optionally runs IChatReducer            │
    └──────────────────────────────────────────────┘
                    │
                    ▼
    ┌──────────────────────────────────────────────┐
    │ 5. AIContextProvider.InvokedAsync()            │
    │    └─ Filter: External messages only           │
    │    └─ StoreAIContextAsync(request+response)   │
    │    └─ Send to vector store / Mem0 / custom    │
    └──────────────────────────────────────────────┘
                    │
                    ▼
              Agent Response returned to user

    ⚠️ ON FAILURE: If LLM call throws exception →
       WRITE PHASE IS SKIPPED ENTIRELY
       (both providers check InvokeException != null)
```

**Critical insight:** The READ and WRITE happen on **every successful turn**, automatically. There's no explicit "save memory" call — the lifecycle handles it.

---

## 3. How Memories Are READ (Retrieved)

### The Universal Retrieval Pattern

Every AIContextProvider follows the exact same pattern for building the search query:

```
User sends: "What about project X?"
                    │
                    ▼
    ┌──────────────────────────────────┐
    │ 1. Filter messages               │
    │    Default: External only        │ ← Only user-facing messages
    │    (excludes ChatHistory,        │    not system messages or
    │     AIContextProvider messages)  │    injected memories
    └──────────────────────────────────┘
                    │
                    ▼
    ┌──────────────────────────────────┐
    │ 2. Concatenate message texts     │
    │                                  │
    │    string.Join("\n",             │ ← That's it.
    │      messages.Select(m => m.Text)│    No rewriting.
    │    )                             │    No decomposition.
    └──────────────────────────────────┘
                    │
                    ▼
         queryText = "What about project X?"
                    │
                    ▼
    ┌──────────────────────────────────┐
    │ 3. Pass raw text to search API   │
    │                                  │
    │    • Vector store → embed + cosine similarity
    │    • Mem0 → POST /v1/memories/search/
    │    • TextSearch → user-provided delegate
    └──────────────────────────────────┘
                    │
                    ▼
    ┌──────────────────────────────────┐
    │ 4. Format results + inject       │
    │                                  │
    │    "## Memories                   │
    │     Consider the following..."    │
    │                                  │
    │    → ChatMessage(User, text)     │
    └──────────────────────────────────┘
```

### What About ChatHistoryProvider?

ChatHistoryProvider does **no search at all**. It simply returns the accumulated message list:

```
ChatHistoryProvider.InvokingAsync()
                    │
                    ▼
    ┌──────────────────────────────────┐
    │ state.Messages                   │
    │                                  │
    │ Returns ALL stored messages      │ ← No filtering
    │ in chronological order           │   No search query
    │                                  │   No relevance ranking
    │ (optionally reduced by           │
    │  IChatReducer if configured)     │
    └──────────────────────────────────┘
```

This is a fundamental difference:
- **ChatHistoryProvider**: Returns everything (or a reduced version) — **recall by recency**
- **AIContextProvider**: Returns relevant matches — **recall by relevance**

---

## 4. How Memories Are WRITTEN (Stored)

### What Triggers a Write?

The write happens **automatically after every successful LLM call**. The agent's `RunAsync()` method calls `InvokedAsync()` on both providers after the LLM responds. No explicit "save" is needed.

```
LLM responds successfully
        │
        ├──→ ChatHistoryProvider.InvokedAsync()
        │         │
        │         ▼
        │    ┌──────────────────────────────────────────┐
        │    │ Filter request messages:                  │
        │    │   Exclude source=ChatHistory              │ ← Prevents re-storing
        │    │   (keeps External + AIContextProvider)    │   old history
        │    │                                           │
        │    │ Include ALL response messages              │
        │    │                                           │
        │    │ StoreChatHistoryAsync:                    │
        │    │   state.Messages.AddRange(filtered)      │ ← Append to list
        │    │                                           │
        │    │ If IChatReducer configured:               │
        │    │   state.Messages = Reduce(messages)       │ ← Optional compression
        │    └──────────────────────────────────────────┘
        │
        └──→ AIContextProvider.InvokedAsync()
                  │
                  ▼
             ┌──────────────────────────────────────────┐
             │ Filter request messages:                  │
             │   Include source=External only            │ ← Only user's actual
             │   (excludes ChatHistory, other providers) │   input gets stored
             │                                           │
             │ Include ALL response messages              │
             │                                           │
             │ StoreAIContextAsync:                      │
             │   Provider-specific storage:              │
             │   • Vector store → embed + upsert         │
             │   • Mem0 → POST /v1/memories/ (per msg)   │
             │   • TextSearch → no storage (read-only)   │
             └──────────────────────────────────────────┘
```

### What Gets Stored (Per Provider)

| Provider | What Gets Written | How | Fact Extraction? |
| --- | --- | --- | --- |
| **InMemoryChatHistory** | Raw `ChatMessage` objects (user + assistant) | Appended to in-memory list | ❌ No — stores raw messages |
| **CosmosChatHistory** | Serialized `ChatMessage` as JSON documents | Cosmos DB transactional batch, with TTL (default 24h) | ❌ No — stores raw messages |
| **ChatHistoryMemory** | Message text → embedded in vector store | `collection.UpsertAsync()` with metadata (role, scope, timestamp) | ❌ No — stores raw text, embedding is auto-generated |
| **Mem0Provider** | Message text → sent to Mem0 service | `POST /v1/memories/` per message (User, Assistant, System roles) | ✅ **Yes — Mem0 extracts facts internally** |
| **TextSearchProvider** | Nothing stored in external system | Only updates recent-message buffer in session state | N/A — read-only provider |

### The Mem0 Difference

Mem0 is the **only provider that does intelligent fact extraction**. When MAF sends a message to Mem0:

```
MAF sends to Mem0:              Mem0 internally does:
                                
"I sold my Ferrari and          ┌────────────────────────────┐
 bought a Honda CBR"            │ 1. LLM extracts facts:     │
        │                       │    • "User sold Ferrari"    │
        │   POST /v1/memories/  │    • "User bought Honda CBR"│
        └──────────────────────►│                            │
                                │ 2. Deduplicates against     │
                                │    existing knowledge graph │
                                │                            │
                                │ 3. Updates graph:           │
                                │    • Marks "owns Ferrari"   │
                                │      as superseded          │
                                │    • Creates "owns Honda    │
                                │      CBR" node              │
                                │                            │
                                │ 4. Generates embeddings     │
                                │    for each extracted fact  │
                                └────────────────────────────┘
```

Every other provider stores **raw text** — the intelligence is in the search, not the storage.

### Write Failure Behavior

If the LLM call fails (throws an exception), **no storage happens at all**:

```csharp
// In both AIContextProvider.InvokedCoreAsync and ChatHistoryProvider.InvokedCoreAsync:
if (context.InvokeException is not null)
{
    return context.AIContext;  // Skip storage entirely
}
```

This means: if a turn fails, neither the user's message nor the (non-existent) response gets stored. The memory state remains as of the last successful turn.

---

## 5. Turn Carryover: Does Memory Persist?

**Yes — all providers maintain state across turns within the same session.**

### How It Works

State is stored in `AgentSession.StateBag`, which is a dictionary keyed by provider type name:

```
Session created → empty StateBag {}

Turn 1: User says "My name is José"
    ┌──────────────────────────────────────────────┐
    │ InMemoryChatHistory:                          │
    │   state.Messages = []                         │ ← Initialized empty
    │   After write: [user:"My name is José",       │
    │                 assistant:"Nice to meet you!"] │
    │                                               │
    │ Mem0Provider:                                  │
    │   Sends "My name is José" to Mem0 API         │
    │   Sends "Nice to meet you!" to Mem0 API       │
    │   Mem0 extracts: fact("user name = José")     │
    └──────────────────────────────────────────────┘
    
    StateBag = {
      "InMemoryChatHistoryProvider": { Messages: [msg1, msg2] },
      "Mem0Provider": { StorageScope: {...}, SearchScope: {...} }
    }

Turn 2: User says "I live in Copenhagen"
    ┌──────────────────────────────────────────────┐
    │ InMemoryChatHistory:                          │
    │   READ: Returns [msg1, msg2] from StateBag   │ ← Turn 1 messages!
    │   WRITE: Appends [msg3, msg4]                 │
    │   state.Messages = [msg1, msg2, msg3, msg4]   │
    │                                               │
    │ Mem0Provider:                                  │
    │   READ: Searches Mem0 for "I live in Copen."  │
    │   → Returns "user name = José" (if relevant)  │ ← Cross-turn recall!
    │   WRITE: Sends new messages to Mem0            │
    │   Mem0 extracts: fact("lives in Copenhagen")  │
    └──────────────────────────────────────────────┘
    
    StateBag = {
      "InMemoryChatHistoryProvider": { Messages: [msg1, msg2, msg3, msg4] },
      "Mem0Provider": { StorageScope: {...}, SearchScope: {...} }
    }

Turn 3: User says "What do you know about me?"
    ┌──────────────────────────────────────────────┐
    │ InMemoryChatHistory:                          │
    │   READ: Returns all 4 messages               │ ← Full conversation  
    │                                               │
    │ Mem0Provider:                                  │
    │   READ: Searches "What do you know about me?" │
    │   → Returns: "José", "lives in Copenhagen"    │ ← Accumulated facts
    └──────────────────────────────────────────────┘
```

### Session Persistence Across Restarts

Sessions can be serialized/deserialized for persistence across application restarts:

```csharp
// Serialize session to JSON
JsonElement serialized = await agent.SerializeSessionAsync(session);
// Store in database, file, etc.

// Later: Restore session
AgentSession restored = await agent.DeserializeSessionAsync(serialized);
// StateBag is fully restored — InMemoryChatHistory messages, scopes, etc.
```

**However:** This only works for providers that store state in `StateBag`:

| Provider | In-Session Carryover | Cross-Session Persistence |
| --- | --- | --- |
| **InMemoryChatHistory** | ✅ Via StateBag.Messages | ⚠️ Only if session serialized |
| **CosmosChatHistory** | ✅ Via Cosmos DB queries | ✅ Fully persisted (with TTL) |
| **ChatHistoryMemory** | ✅ Via vector store | ✅ Fully persisted |
| **Mem0Provider** | ✅ Via Mem0 service | ✅ Fully persisted |
| **TextSearchProvider** | ✅ Via StateBag (recent messages buffer) | ⚠️ Only if session serialized |

---

## 6. Provider-by-Provider Deep Dive

### 6.1 InMemoryChatHistoryProvider

**Type:** ChatHistoryProvider (conversation memory)

```
READ: Return all stored messages in order
WRITE: Append new messages to in-memory list

    Turn 1: "Hi"              Turn 2: "My name is José"      Turn 3: "What's my name?"
    ┌───────────────┐         ┌───────────────┐              ┌───────────────┐
    │ READ:  []     │         │ READ:  [Hi,   │              │ READ:  [Hi,   │
    │               │         │  Hello!]      │              │  Hello!, My   │
    │ LLM → "Hello!"│         │               │              │  name.., Got  │
    │               │         │ LLM → "Got it,│              │  it José]     │
    │ WRITE: [Hi,   │         │  José!"       │              │               │
    │  Hello!]      │         │               │              │ LLM → "José!" │
    └───────────────┘         │ WRITE: [Hi,   │              └───────────────┘
                              │  Hello!, My   │
                              │  name.., Got  │
                              │  it José]     │
                              └───────────────┘
```

**Optional reducer:** If an `IChatReducer` is configured (e.g., `MessageCountingChatReducer`), the message list is compressed after each write. This saves token cost but may lose information.

### 6.2 CosmosChatHistoryProvider

**Type:** ChatHistoryProvider (persistent conversation memory)

Same READ/WRITE pattern as InMemoryChatHistory but stores in Cosmos DB:
- **Write:** Transactional batch insert of serialized `ChatMessage` JSON documents
- **Read:** `SELECT * FROM c WHERE conversationId = @id ORDER BY timestamp ASC`
- **TTL:** Default 86,400 seconds (24 hours) — messages auto-expire
- **Partitioning:** Hierarchical: `(tenantId, userId, conversationId)`

### 6.3 ChatHistoryMemoryProvider

**Type:** AIContextProvider (semantic/long-term memory)

```
READ path:
    ┌─────────────────────────────────────┐
    │ 1. Get request messages (External)  │
    │ 2. queryText = Join("\n", texts)    │
    │ 3. VectorStore.SearchAsync(         │
    │      queryText,                     │
    │      top: 3,         ← MaxResults   │
    │      filter: searchScope)           │
    │ 4. Vector store generates embedding │
    │    for queryText internally         │
    │ 5. Cosine similarity search         │
    │ 6. Format: "## Memories\n..."       │
    └─────────────────────────────────────┘

WRITE path:
    ┌─────────────────────────────────────┐
    │ For each message (request+response):│
    │                                     │
    │   {                                 │
    │     Key: Guid.NewGuid(),            │
    │     Role: message.Role,             │
    │     Content: message.Text,  ←raw    │
    │     ContentEmbedding: message.Text, │
    │     ApplicationId: scope.AppId,     │
    │     AgentId: scope.AgentId,         │
    │     UserId: scope.UserId,           │
    │     SessionId: scope.SessionId,     │
    │     CreatedAt: DateTimeOffset.Now   │
    │   }                                 │
    │                                     │
    │   → VectorStore.UpsertAsync(item)   │
    │     (embedding auto-generated)      │
    └─────────────────────────────────────┘
```

**Dual-scope pattern:**
- **StorageScope**: Narrow — includes SessionId (store per-session)
- **SearchScope**: Wide — excludes SessionId (search across all sessions for same user)

This enables cross-session memory: what you told the agent last week is searchable this week.

### 6.4 Mem0Provider

**Type:** AIContextProvider (knowledge-graph memory)

```
READ path:
    ┌──────────────────────────────────────────┐
    │ 1. queryText = Join("\n", request texts) │
    │ 2. POST /v1/memories/search/             │
    │    {                                     │
    │      app_id: scope.ApplicationId,        │
    │      agent_id: scope.AgentId,            │
    │      run_id: scope.ThreadId,             │
    │      user_id: scope.UserId,              │
    │      query: queryText                    │
    │    }                                     │
    │                                          │
    │    ┌────────────────────────────────┐     │
    │    │ Mem0 BLACK BOX:               │     │
    │    │ • Generates query embedding   │     │
    │    │ • Searches knowledge graph    │     │
    │    │ • Ranks by relevance          │     │
    │    │ • Returns memory strings      │     │
    │    └────────────────────────────────┘     │
    │                                          │
    │ 3. Format: "## Memories\n..."            │
    └──────────────────────────────────────────┘

WRITE path:
    ┌──────────────────────────────────────────┐
    │ For EACH message (User, Assistant,       │
    │ System roles only; skips empty text):    │
    │                                          │
    │   POST /v1/memories/                     │
    │   {                                      │
    │     app_id, agent_id, run_id, user_id,   │
    │     text: message.Text,                  │
    │     role: message.Role                   │
    │   }                                      │
    │                                          │
    │    ┌────────────────────────────────┐     │
    │    │ Mem0 BLACK BOX:               │     │
    │    │ • LLM extracts facts          │     │
    │    │ • Deduplicates vs graph       │     │
    │    │ • Updates/supersedes old facts │     │
    │    │ • Generates embeddings        │     │
    │    │ • Stores in knowledge graph   │     │
    │    └────────────────────────────────┘     │
    └──────────────────────────────────────────┘
```

**Key difference from all other providers:** Mem0 is the only one that does **intelligent fact extraction**. Every other provider stores raw message text. Mem0's internal LLM processes the text and extracts structured facts, which get stored in a knowledge graph. This means:

- Mem0 can **deduplicate** — if you say "My name is José" twice, it stores one fact
- Mem0 can **supersede** — "I sold my Ferrari" marks the old "owns Ferrari" fact as stale
- Mem0's retrieval is against **extracted facts**, not raw message text
- But: **MAF has zero visibility** into what Mem0 extracted or how it searched

### 6.5 TextSearchProvider

**Type:** AIContextProvider (RAG/external search)

```
READ path:
    ┌──────────────────────────────────────────┐
    │ 1. Collect recent messages from state    │
    │    (RecentMessageMemoryLimit, default 0) │
    │ 2. Concatenate with current request text │
    │ 3. Call user-provided search delegate:   │
    │                                          │
    │    Func<string, CancellationToken,       │
    │         Task<IEnumerable<TextSearchResult>>>
    │                                          │
    │    This can be ANYTHING:                 │
    │    • Azure AI Search                     │
    │    • Bing Search API                     │
    │    • Custom RAG pipeline                 │
    │    • Your own vector DB                  │
    │                                          │
    │ 4. Format results with citations         │
    └──────────────────────────────────────────┘

WRITE path:
    ┌──────────────────────────────────────────┐
    │ NO external storage.                     │
    │                                          │
    │ Only updates session-local buffer:       │
    │   recentMessages = last N messages       │
    │   (N = RecentMessageMemoryLimit)         │
    │                                          │
    │ This buffer is used on NEXT turn to      │
    │ provide richer search context.           │
    └──────────────────────────────────────────┘
```

**Unique feature:** TextSearchProvider can maintain a sliding window of recent messages in session state. On the next turn, these recent messages are **prepended to the search query**, giving the search delegate more context. This is the closest thing to "query enrichment" in MAF — though it's just concatenation, not rewriting.

---

## 7. The Two Search Modes

Both ChatHistoryMemoryProvider and TextSearchProvider support two search modes:

```
┌───────────────────────────────────────────────────────────────────────────┐
│                                                                           │
│  Mode 1: BeforeAIInvoke (default)                                        │
│  ─────────────────────────────────                                       │
│                                                                           │
│  User message → Provider searches automatically → Results injected       │
│                 as a ChatMessage(User, "## Memories\n...")                │
│                                                                           │
│  ✅ Simple, works every turn                                              │
│  ❌ Searches even when not needed (wastes compute/tokens)                 │
│  ❌ Query is always raw user text (may be irrelevant)                     │
│                                                                           │
├───────────────────────────────────────────────────────────────────────────┤
│                                                                           │
│  Mode 2: OnDemandFunctionCalling                                         │
│  ────────────────────────────────                                        │
│                                                                           │
│  Provider exposes a Search(userQuestion) tool → LLM decides when to      │
│  call it → LLM writes the search query → Provider executes search         │
│                                                                           │
│  ✅ LLM decides when search is relevant                                   │
│  ✅ LLM can rewrite the query (it writes the tool argument)              │
│  ❌ Depends on model's tool-calling ability                               │
│  ❌ Adds latency (tool call round-trip)                                   │
│                                                                           │
│  Tool exposed:                                                            │
│    function Search(userQuestion: string) → string                        │
│                                                                           │
│  The LLM fills in "userQuestion" — this IS a form of query rewriting!    │
│                                                                           │
└───────────────────────────────────────────────────────────────────────────┘
```

**Important insight about OnDemandFunctionCalling:** In this mode, the LLM acts as a query rewriter! It sees the conversation and decides both *when* to search and *what to search for*. The `userQuestion` parameter it passes to the Search tool might be quite different from the raw user message. This partially addresses the no-query-rewriting problem — but only if this mode is enabled.

---

## 8. The No-Query-Rewriting Problem

### The Core Issue

In `BeforeAIInvoke` mode (the default), the search query is always the **raw concatenated user message text**. There is no:
- Query rewriting (transforming "that thing from last week" into a specific search)
- Query decomposition (splitting "allergies AND car?" into two searches)
- Query expansion (adding synonyms or context from conversation history)
- Multi-hop retrieval (search → refine → search again)

### Why This Matters

```
┌────────────────────────────────────────────────────────────────────────────┐
│ User says: "What about that thing we discussed?"                          │
│                                                                           │
│ BeforeAIInvoke:                                                          │
│   Query: "What about that thing we discussed?"                           │
│   Vector search: Embeds this vague text → poor similarity matches        │
│   Result: ❌ Retrieves wrong or no memories                               │
│                                                                           │
│ OnDemandFunctionCalling:                                                 │
│   LLM sees conversation history + user message                           │
│   LLM reasons: "They're asking about the Ferrari sale from earlier"      │
│   LLM calls: Search("Ferrari sale November 2025")                        │
│   Result: ✅ Retrieves relevant memories                                  │
│                                                                           │
├────────────────────────────────────────────────────────────────────────────┤
│                                                                           │
│ User says: "What allergies do I have, and when did I buy my car?"        │
│                                                                           │
│ BeforeAIInvoke:                                                          │
│   Query: "What allergies do I have, and when did I buy my car?"          │
│   Vector search: ONE search for this compound query                      │
│   Result: ⚠️ May find one topic but miss the other                       │
│                                                                           │
│ OnDemandFunctionCalling:                                                 │
│   LLM could call Search twice:                                           │
│     Search("user allergies")                                             │
│     Search("car purchase date")                                          │
│   Result: ✅ But depends on model sophistication                          │
│                                                                           │
├────────────────────────────────────────────────────────────────────────────┤
│                                                                           │
│ User says: "Book it"                                                      │
│                                                                           │
│ BeforeAIInvoke:                                                          │
│   Query: "Book it"                                                       │
│   Vector search: Almost no semantic content → garbage results             │
│   Result: ❌ Useless retrieval                                            │
│                                                                           │
│ ChatHistoryProvider provides the conversation context, so the LLM        │
│ can figure it out — but the MEMORY search was wasted.                    │
│                                                                           │
└────────────────────────────────────────────────────────────────────────────┘
```

### The Mem0 Exception

Mem0 **partially mitigates** this problem because:
1. It searches against **extracted facts**, not raw message text — so the embedding space is cleaner
2. Its knowledge graph structure enables **relation-based retrieval**, not just similarity
3. It may apply its own query processing internally

However, MAF still sends the raw user text as the query to Mem0. Mem0's internal intelligence must compensate for MAF's lack of query preparation.

### What Would Fix This

A query rewriting stage between message filtering and search:

```
Current:    UserMessage → Filter → Concatenate → Search
                                     ↑
                                  (raw text)

Improved:   UserMessage → Filter → QueryRewriter(LLM) → Search
                                        ↑
                                   "Given this conversation,
                                    what should we search for?"
                                        ↓
                                   Returns optimized query(ies)
```

This doesn't exist in MAF today. An agent developer would need to implement a custom AIContextProvider that includes this step, or use `OnDemandFunctionCalling` mode where the LLM effectively does the rewriting.

---

## 9. Implications for AgentEval

### What to Test

The architecture reveals several clear evaluation dimensions:

```
┌────────────────────────────────────────────────────────────────────────┐
│                     Memory Evaluation Test Matrix                      │
│                                                                        │
│  ┌─ READ PATH ──────────────────────────────────────────────────────┐  │
│  │                                                                  │  │
│  │  Query Quality                                                   │  │
│  │  • Does raw-text search find the right memories?                 │  │
│  │  • How does vague language affect retrieval? ("that thing")      │  │
│  │  • Do multi-topic queries retrieve ALL relevant facts?           │  │
│  │  • Does OnDemand mode improve retrieval vs BeforeAIInvoke?       │  │
│  │                                                                  │  │
│  │  Retrieval Relevance                                             │  │
│  │  • Are returned memories actually relevant to the query?         │  │
│  │  • What's the noise ratio? (irrelevant memories injected)        │  │
│  │  • Does MaxResults (default 3) miss important memories?          │  │
│  │                                                                  │  │
│  │  Cross-Session Recall                                            │  │
│  │  • Can the agent recall facts from previous sessions?            │  │
│  │  • Does the dual-scope pattern work (store narrow, search wide)? │  │
│  │                                                                  │  │
│  └──────────────────────────────────────────────────────────────────┘  │
│                                                                        │
│  ┌─ WRITE PATH ─────────────────────────────────────────────────────┐  │
│  │                                                                  │  │
│  │  Storage Completeness                                            │  │
│  │  • Are all messages being stored? (check write filters)          │  │
│  │  • Is the message filter excluding important context?             │  │
│  │  • For Mem0: Did it extract the right facts?                     │  │
│  │                                                                  │  │
│  │  Fact Extraction Quality (Mem0-specific)                         │  │
│  │  • Did Mem0 extract "user name is José" from "My name is José"? │  │
│  │  • Did it supersede "owns Ferrari" when told "sold Ferrari"?     │  │
│  │  • Did it handle implicit facts? ("heading to the gym at 8am")  │  │
│  │                                                                  │  │
│  │  Embedding Quality (ChatHistoryMemory-specific)                  │  │
│  │  • Does the embedding model capture semantic meaning?            │  │
│  │  • Are similar messages stored with similar embeddings?           │  │
│  │                                                                  │  │
│  └──────────────────────────────────────────────────────────────────┘  │
│                                                                        │
│  ┌─ CARRYOVER ──────────────────────────────────────────────────────┐  │
│  │                                                                  │  │
│  │  Turn-to-Turn Persistence                                        │  │
│  │  • Does turn 1's memory show up in turn 3?                       │  │
│  │  • After reducer compression, are key facts preserved?           │  │
│  │  • Does session serialization/deserialization preserve state?     │  │
│  │                                                                  │  │
│  │  Failure Recovery                                                │  │
│  │  • If turn 2 fails (LLM error), is turn 1 memory still intact?  │  │
│  │  • Does the "skip write on failure" behavior cause data loss?    │  │
│  │                                                                  │  │
│  └──────────────────────────────────────────────────────────────────┘  │
│                                                                        │
│  ┌─ PROVIDER-SPECIFIC ──────────────────────────────────────────────┐  │
│  │                                                                  │  │
│  │  ChatHistoryProvider vs AIContextProvider                        │  │
│  │  • Does the LLM use both sources effectively?                   │  │
│  │  • Does injected memory context conflict with chat history?      │  │
│  │  • Are source tags preserved correctly?                          │  │
│  │                                                                  │  │
│  │  Search Mode Impact                                              │  │
│  │  • BeforeAIInvoke vs OnDemandFunctionCalling — which is better? │  │
│  │  • Does the model use the Search tool effectively?               │  │
│  │  • How much latency does OnDemand mode add?                      │  │
│  │                                                                  │  │
│  └──────────────────────────────────────────────────────────────────┘  │
└────────────────────────────────────────────────────────────────────────┘
```

### Key Risks to Evaluate

| Risk | Cause | Severity | Provider(s) |
| --- | --- | --- | --- |
| **Vague query retrieval failure** | Raw text used as search query, no rewriting | High | All AIContextProviders |
| **Memory pollution** | Irrelevant memories injected every turn (BeforeAIInvoke) | Medium | ChatHistoryMemory, Mem0 |
| **Reducer information loss** | IChatReducer drops important facts | High | InMemoryChatHistory |
| **Mem0 extraction errors** | Mem0 misses or misinterprets facts | Medium | Mem0 only |
| **Dual-provider confusion** | LLM gets conflicting info from history vs memories | Medium | Agents with both providers |
| **Write filter misconfiguration** | Important messages filtered out of storage | High | All providers |
| **Failed turn data loss** | Write skipped on LLM failure | Low | All providers |
| **Session serialization gaps** | StateBag not serialized → memory lost | Medium | InMemory, TextSearch |

---

## Summary

MAF's memory architecture is **simple by design** — concatenate user text, search, inject results. This simplicity makes it predictable but creates clear evaluation opportunities:

1. **The query is always raw text** (except in OnDemand mode where the LLM rewrites)
2. **Writes happen automatically** after every successful turn
3. **State carries over** within sessions via StateBag
4. **Mem0 is the only intelligent storer** — all others store raw text
5. **ChatHistoryProvider and AIContextProvider serve different purposes** — one is "what was said", the other is "what is known"

For AgentEval, this means we can build targeted tests for each part of the pipeline: query quality, retrieval relevance, storage completeness, fact extraction, and carryover fidelity.
