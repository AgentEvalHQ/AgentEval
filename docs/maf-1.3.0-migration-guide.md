# Microsoft Agent Framework (MAF) 1.3.0 — Complete Migration Guide

> **Purpose:** Complete reference for implementing with MAF 1.3.0. Shows the final target patterns — apply regardless of your starting point.  
> **Audience:** LLM agents performing code migration. This document is optimized for machine consumption.  
> **Last verified:** April 2026 against `Microsoft.Agents.AI 1.3.0` ([NuGet](https://www.nuget.org/packages/Microsoft.Agents.AI/1.3.0)), API surface verified via `dotnet-inspect 0.7.6`  
> **Official docs:** [learn.microsoft.com/agent-framework](https://learn.microsoft.com/en-us/agent-framework/)  
> **Release notes:** [dotnet-1.3.0](https://github.com/microsoft/agent-framework/releases/tag/dotnet-1.3.0) · [Changelog vs 1.2.0](https://github.com/microsoft/agent-framework/compare/dotnet-1.2.0...dotnet-1.3.0)

---

## Sources & Verification Tools

### NuGet Packages

All MAF packages are published on [nuget.org](https://www.nuget.org/profiles/aspaborern). Key package links:

| Package | NuGet Link |
|---------|------------|
| `Microsoft.Agents.AI` | [nuget.org/packages/Microsoft.Agents.AI](https://www.nuget.org/packages/Microsoft.Agents.AI/) |
| `Microsoft.Agents.AI.Abstractions` | [nuget.org/packages/Microsoft.Agents.AI.Abstractions](https://www.nuget.org/packages/Microsoft.Agents.AI.Abstractions/) |
| `Microsoft.Agents.AI.OpenAI` | [nuget.org/packages/Microsoft.Agents.AI.OpenAI](https://www.nuget.org/packages/Microsoft.Agents.AI.OpenAI/) |
| `Microsoft.Agents.AI.Workflows` | [nuget.org/packages/Microsoft.Agents.AI.Workflows](https://www.nuget.org/packages/Microsoft.Agents.AI.Workflows/) |
| `Microsoft.Agents.AI.Workflows.Generators` | [nuget.org/packages/Microsoft.Agents.AI.Workflows.Generators](https://www.nuget.org/packages/Microsoft.Agents.AI.Workflows.Generators/) |
| `Microsoft.Agents.AI.Foundry` | [nuget.org/packages/Microsoft.Agents.AI.Foundry](https://www.nuget.org/packages/Microsoft.Agents.AI.Foundry/) |
| `Microsoft.Agents.AI.A2A` | [nuget.org/packages/Microsoft.Agents.AI.A2A](https://www.nuget.org/packages/Microsoft.Agents.AI.A2A/) |
| `Microsoft.Agents.AI.Hosting.A2A` | [nuget.org/packages/Microsoft.Agents.AI.Hosting.A2A](https://www.nuget.org/packages/Microsoft.Agents.AI.Hosting.A2A/) |
| `Microsoft.Agents.AI.Hosting.A2A.AspNetCore` | [nuget.org/packages/Microsoft.Agents.AI.Hosting.A2A.AspNetCore](https://www.nuget.org/packages/Microsoft.Agents.AI.Hosting.A2A.AspNetCore/) |
| `Microsoft.Extensions.AI` | [nuget.org/packages/Microsoft.Extensions.AI](https://www.nuget.org/packages/Microsoft.Extensions.AI/) |
| `Microsoft.Extensions.AI.OpenAI` | [nuget.org/packages/Microsoft.Extensions.AI.OpenAI](https://www.nuget.org/packages/Microsoft.Extensions.AI.OpenAI/) |

### Source Code Repository

- **GitHub:** [github.com/microsoft/agent-framework](https://github.com/microsoft/agent-framework)
- **.NET source:** [`dotnet/src/`](https://github.com/microsoft/agent-framework/tree/main/dotnet/src)
- **.NET samples:** [`dotnet/samples/`](https://github.com/microsoft/agent-framework/tree/main/dotnet/samples)
- **MAFVnext branch:** [`MAFVnext`](https://github.com/microsoft/agent-framework/tree/MAFVnext) — development branch with latest work-in-progress

### API Verification with `dotnet-inspect`

When patterns in this guide are unclear or you suspect an API has changed, use the `dotnet-inspect` CLI tool to inspect the actual package API surface at runtime:

```bash
# Install and run dotnet-inspect (requires .NET SDK)
dnx dotnet-inspect@0.7.6 -y --source https://api.nuget.org/v3/index.json -- <command> --package <PackageName>@<Version> --source https://api.nuget.org/v3/index.json
```

> **Important:** The `--source https://api.nuget.org/v3/index.json` flag must appear on **both** the `dnx` command (tool installation) **and** the inspect command (package resolution). This is required when the workspace has custom NuGet feeds that don't host these packages.

Example commands:

```bash
# List all types in a package
dnx dotnet-inspect@0.7.6 -y --source https://api.nuget.org/v3/index.json -- types --package Microsoft.Agents.AI@1.3.0 --source https://api.nuget.org/v3/index.json

# Inspect a specific type's API surface
dnx dotnet-inspect@0.7.6 -y --source https://api.nuget.org/v3/index.json -- apis --package Microsoft.Agents.AI@1.3.0 --type ChatClientAgent --source https://api.nuget.org/v3/index.json

# Check package dependency tree
dnx dotnet-inspect@0.7.6 -y --source https://api.nuget.org/v3/index.json -- depends --package Microsoft.Agents.AI@1.3.0 --source https://api.nuget.org/v3/index.json
```

> **Skill reference:** For full `dotnet-inspect` documentation and advanced usage, see `.github/skills/dotnet-inspect/SKILL.md` in this repository.

#### Installing the `dotnet-inspect` Copilot Skill

The `dotnet-inspect` Copilot skill is maintained in the official repository by [@richlander](https://github.com/richlander):

- **Repository:** [github.com/richlander/dotnet-inspect](https://github.com/richlander/dotnet-inspect)
- **Skill file:** [skills/dotnet-inspect/SKILL.md](https://github.com/richlander/dotnet-inspect/tree/main/skills/dotnet-inspect)

The skill teaches GitHub Copilot and other LLM agents how to use the `dotnet-inspect` CLI effectively — including the quick decision tree, key patterns, command reference, and important caveats.

**Install or update the skill in your repository** by copying `SKILL.md` from the official source into `.github/skills/dotnet-inspect/SKILL.md`:

```powershell
# Download the latest SKILL.md from the official repository
$skillDir = ".github/skills/dotnet-inspect"
New-Item -ItemType Directory -Force -Path $skillDir
Invoke-WebRequest `
  -Uri "https://raw.githubusercontent.com/richlander/dotnet-inspect/main/skills/dotnet-inspect/SKILL.md" `
  -OutFile "$skillDir/SKILL.md"
```

Or manually:
1. Open [https://github.com/richlander/dotnet-inspect/blob/main/skills/dotnet-inspect/SKILL.md](https://github.com/richlander/dotnet-inspect/blob/main/skills/dotnet-inspect/SKILL.md)
2. Copy the raw content
3. Save to `.github/skills/dotnet-inspect/SKILL.md` in your repository

> **This repository** already has the skill installed at [`.github/skills/dotnet-inspect/SKILL.md`](../.github/skills/dotnet-inspect/SKILL.md) (version 0.7.6).

#### Installing the `dotnet-inspect` CLI Tool

Install the CLI globally via `dotnet tool`:

```bash
dotnet tool install -g dotnet-inspect
```

Or run on-demand without installing (like `npx`), which will automatically install the latest version:

```bash
dnx dotnet-inspect -y -- <command>
```

To pin to a specific version (recommended for reproducible CI):

```bash
dnx dotnet-inspect@0.7.6 -y --source https://api.nuget.org/v3/index.json -- <command>
```

---

## Best Practices — Top Patterns to Follow

These are the highest-priority patterns recommended by the official MAF documentation and demonstrated in the MAFVnext reference samples. **Implement these first.**

### 1. Use `AsAIAgent()` Extensions Over Manual Construction
Prefer SDK-specific extensions (`.AsAIAgent()`) rather than constructing `ChatClientAgent` directly. This ensures correct provider setup, middleware, and defaults.
> *Source: [Agent Types — Simple agents based on inference services](https://learn.microsoft.com/en-us/agent-framework/agents/?pivots=programming-language-csharp#simple-agents-based-on-inference-services)*

### 2. Use `ManagedIdentityCredential` in Production
`DefaultAzureCredential` is convenient for development but introduces latency, unintended credential probing, and security risks in production. Use a specific credential (e.g., `ManagedIdentityCredential`) for deployed services.
> *Source: [Agent Types — Azure and OpenAI SDK Options Reference](https://learn.microsoft.com/en-us/agent-framework/agents/?pivots=programming-language-csharp#azure-and-openai-sdk-options-reference)*

### 3. Always Use Sessions for Multi-Turn Conversations
Create sessions with `await agent.CreateSessionAsync()` and pass them to every `RunAsync()`/`RunStreamingAsync()` call. Serialize sessions with `await agent.SerializeSessionAsync(session)` for persistence.
> *Source: [Sessions](https://learn.microsoft.com/en-us/agent-framework/agents/conversations/session?pivots=programming-language-csharp)*

### 4. Implement `ChatHistoryProvider` for Conversation Storage
Don't manage `List<ChatMessage>` manually. Use `InMemoryChatHistoryProvider` (built-in) or implement a custom `ChatHistoryProvider` with `ProviderSessionState<T>` for database-backed storage.
> *Source: [Chat History Storage](https://learn.microsoft.com/en-us/agent-framework/agents/conversations/storage?pivots=programming-language-csharp)*

### 5. Use the Pipeline Architecture — Place Logic at the Right Layer
MAF has three middleware layers. Place logic at the correct level:
- **Agent middleware** (`.AsBuilder().Use(...)`) — for cross-cutting concerns (logging, auth, guardrails)
- **Context providers** (`AIContextProvider`) — for memory, RAG, dynamic instructions
- **Chat client middleware** (`.AsBuilder().Use(...)` on `IChatClient`) — for inference-level concerns (retry, tracing)
> *Source: [Agent Pipeline Architecture](https://learn.microsoft.com/en-us/agent-framework/agents/agent-pipeline?pivots=programming-language-csharp)*

### 6. Enable OpenTelemetry Observability
Instrument both chat clients and agents for traces, metrics, and logs. Use `UseOpenTelemetry()` on the chat client builder, and `agent.AsBuilder().UseOpenTelemetry(...).Build()` on the agent.
> *Source: [Observability](https://learn.microsoft.com/en-us/agent-framework/agents/observability?pivots=programming-language-csharp)*

### 7. Use Structured Output (`RunAsync<T>`) for Type-Safe Responses
When you need typed results, use `RunAsync<T>()` or set `ResponseFormat = ChatResponseFormat.ForJsonSchema<T>()`. This gives compile-time safety and eliminates manual JSON parsing.
> *Source: [Structured Output](https://learn.microsoft.com/en-us/agent-framework/agents/structured-output?pivots=programming-language-csharp)*

### 8. Wrap Sensitive Tools with `ApprovalRequiredAIFunction`
For any tool that modifies state, charges money, or has side effects, wrap it in `ApprovalRequiredAIFunction` to require human approval before execution.
> *Source: [Tool Approval](https://learn.microsoft.com/en-us/agent-framework/agents/tools/tool-approval?pivots=programming-language-csharp)*

### 9. Use Compaction for Long-Running Conversations
Apply `CompactionProvider` with a `PipelineCompactionStrategy` to prevent token overflow. Use `ToolResultCompactionStrategy` → `SummarizationCompactionStrategy` → `SlidingWindowCompactionStrategy` → `TruncationCompactionStrategy` in sequence.
> *Source: [Compaction](https://learn.microsoft.com/en-us/agent-framework/agents/conversations/compaction?pivots=programming-language-csharp)*

### 10. Provide Both Streaming and Non-Streaming Middleware
When registering agent middleware, always provide both `runFunc` and `runStreamingFunc`. Providing only non-streaming middleware causes streaming to fall back to non-streaming mode, losing real-time output.
> *Source: [Middleware](https://learn.microsoft.com/en-us/agent-framework/agents/middleware/?pivots=programming-language-csharp)*

---

## Table of Contents

- [**Sources & Verification Tools**](#sources--verification-tools)
- [**Best Practices — Top Patterns to Follow**](#best-practices--top-patterns-to-follow)
1. [Package References & Dependencies](#1-package-references--dependencies)
2. [Namespace Changes](#2-namespace-changes)
3. [Creating Agents — The `AsAIAgent()` Pattern](#3-creating-agents--the-asaiagent-pattern)
4. [Running Agents — `RunAsync` & `RunStreamingAsync`](#4-running-agents--runasync--runstreamingasync)
5. [Function Tools](#5-function-tools)
6. [Agent-as-a-Tool Composition](#6-agent-as-a-tool-composition)
7. [Sessions & Multi-Turn Conversations](#7-sessions--multi-turn-conversations)
8. [Context Providers & Agent Memory (`AIContextProvider`)](#8-context-providers--agent-memory-aicontextprovider)
9. [Chat History Management (`ChatHistoryProvider`)](#9-chat-history-management-chathistoryprovider)
10. [The Agent Pipeline Architecture](#10-the-agent-pipeline-architecture)
11. [Middleware](#11-middleware)
12. [Workflows — `WorkflowBuilder` & Executors](#12-workflows--workflowbuilder--executors)
13. [Source-Generated Executors (`[MessageHandler]`)](#13-source-generated-executors-messagehandler)
14. [Agents in Workflows](#14-agents-in-workflows)
15. [Workflow Execution & Events](#15-workflow-execution--events)
16. [Response & Content Types](#16-response--content-types)
17. [Streaming Patterns](#17-streaming-patterns)
17.5. [Compaction Strategies (Experimental)](#175-compaction-strategies-experimental)
17.6. [Structured Output](#176-structured-output)
17.7. [Tool Approval — Human-in-the-Loop](#177-tool-approval--human-in-the-loop)
17.8. [Observability — OpenTelemetry](#178-observability--opentelemetry)
17.9. [Multimodal — Images](#179-multimodal--images)
17.10. [A2A Agent — Remote Protocol Proxies](#1710-a2a-agent--remote-protocol-proxies)
17.11. [Dynamic Tool Expansion (New in 1.3.0)](#1711-dynamic-tool-expansion-new-in-130)
17.12. [Server-Side Foundry Toolbox (New in 1.3.0)](#1712-server-side-foundry-toolbox-new-in-130)
18. [Session Serialization & Replay](#18-session-serialization--replay)
19. [API Patterns Summary](#19-api-patterns-summary)
20. [Migration Checklist](#20-migration-checklist)
- [**Misalignments — Document vs. Code Reality**](#misalignments--document-vs-code-reality)

---

## 1. Package References & Dependencies

### Prerequisites

- **.NET SDK:** 8.0 or later (MAF 1.3.0 targets `net8.0`+; `net9.0` and `net10.0` are also supported)
- **Azure.AI.OpenAI:** `2.8.0-beta.1` or later (if using Azure OpenAI)
- **Azure.Identity:** latest stable (if using Azure credential providers)

### Removing Old / Superseded Packages

If migrating from an older agent framework, remove these superseded packages before adding MAF 1.3.0 references:

```xml
<!-- Remove all of these if present — they are superseded by Microsoft.Agents.AI* -->
<PackageReference Include="Microsoft.Agents.Builder" />
<PackageReference Include="Microsoft.Agents.Builder.OpenAI" />
<PackageReference Include="Microsoft.Agents.Extensions.*" />
<PackageReference Include="Microsoft.SemanticKernel" />
<PackageReference Include="Microsoft.SemanticKernel.*" />
<PackageReference Include="Microsoft.Bot.Builder" />
<PackageReference Include="Microsoft.Bot.Builder.*" />
```

> **Note:** MAF replaces both Semantic Kernel's agent abstractions and the older Bot Framework SDK. If your project uses Semantic Kernel for non-agent features (e.g., connectors, memory), those can coexist — but all agent-related code should migrate to `Microsoft.Agents.AI`.

### NuGet Packages (1.3.0)

```xml
<!-- Core agent framework -->
<PackageReference Include="Microsoft.Agents.AI" Version="1.3.0" />

<!-- Provider-specific (pick what you use) -->
<PackageReference Include="Microsoft.Agents.AI.OpenAI" Version="1.3.0" />

<!-- Workflows (if using multi-agent orchestration) -->
<PackageReference Include="Microsoft.Agents.AI.Workflows" Version="1.3.0" />

<!-- Source-generated executors (if using [MessageHandler] pattern) -->
<PackageReference Include="Microsoft.Agents.AI.Workflows.Generators" Version="1.3.0" />

<!-- Foundry integration (if using Azure AI Foundry) -->
<PackageReference Include="Microsoft.Agents.AI.Foundry" Version="1.3.0" />

<!-- A2A agent proxy + hosting (if using A2A protocol) -->
<PackageReference Include="Microsoft.Agents.AI.A2A" Version="1.3.0" />
<PackageReference Include="Microsoft.Agents.AI.Hosting.A2A" Version="1.3.0" />
<PackageReference Include="Microsoft.Agents.AI.Hosting.A2A.AspNetCore" Version="1.3.0" />
```

### Required Companion Dependencies

MAF 1.3.0 requires these minimum versions of Microsoft.Extensions.AI:

```xml
<PackageReference Include="Microsoft.Extensions.AI" Version="10.5.0" />
<PackageReference Include="Microsoft.Extensions.AI.OpenAI" Version="10.5.0" />
```

> **Tip:** Check the [NuGet release page](https://www.nuget.org/packages/Microsoft.Agents.AI/1.3.0) for the exact `Microsoft.Extensions.AI` transitive requirement — newer patch versions of MEAI release frequently alongside MAF.

### Central Package Management (recommended)

If using `Directory.Packages.props`:

```xml
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup>
    <PackageVersion Include="Microsoft.Agents.AI" Version="1.3.0" />
    <PackageVersion Include="Microsoft.Agents.AI.OpenAI" Version="1.3.0" />
    <PackageVersion Include="Microsoft.Agents.AI.Workflows" Version="1.3.0" />
    <PackageVersion Include="Microsoft.Agents.AI.Workflows.Generators" Version="1.3.0" />
    <PackageVersion Include="Microsoft.Agents.AI.Foundry" Version="1.3.0" />
    <PackageVersion Include="Microsoft.Agents.AI.A2A" Version="1.3.0" />
    <PackageVersion Include="Microsoft.Agents.AI.Hosting.A2A" Version="1.3.0" />
    <PackageVersion Include="Microsoft.Agents.AI.Hosting.A2A.AspNetCore" Version="1.3.0" />
    <PackageVersion Include="Microsoft.Extensions.AI" Version="10.5.0" />
    <PackageVersion Include="Microsoft.Extensions.AI.OpenAI" Version="10.5.0" />
    <PackageVersion Include="Azure.AI.OpenAI" Version="2.8.0-beta.1" />
  </ItemGroup>
</Project>
```

### After Updating

```powershell
dotnet restore --force
dotnet build
dotnet test
```

> **Tip:** If you see `NU1605: Detected package downgrade: System.Numerics.Tensors` after bumping MEAI to `10.5.0`, pin it to `≥ 10.0.6`. MEAI `10.5.0` raised the transitive minimum from `10.0.4` → `10.0.6`; earlier pins will cause a restore/build error.

---

## 2. Namespace Changes

MAF 1.3.0 consolidated namespaces. The key using statements are:

```csharp
// Core agent types
using Microsoft.Agents.AI;

// Microsoft.Extensions.AI types (ChatMessage, IChatClient, etc.)
using Microsoft.Extensions.AI;

// Workflows (if used)
using Microsoft.Agents.AI.Workflows;
```

**Old namespaces to remove** (if still referenced from prior versions):
```csharp
// Remove — superseded by Microsoft.Agents.AI
using Microsoft.Agents.Builder;
using Microsoft.Agents.Extensions;

// Remove — superseded by Microsoft.Extensions.AI
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.ChatCompletion;

// Remove — superseded by Microsoft.Agents.AI
using Microsoft.Bot.Builder;
using Microsoft.Bot.Schema;
```

**Key types and namespaces:**
- `Microsoft.Agents.AI.ChatClientAgent` — primary simple agent class
- `Microsoft.Agents.AI.AIAgent` — base class for all agents
- `Microsoft.Agents.AI.AgentSession` — conversation session state
- `Microsoft.Agents.AI.AgentResponse` / `AgentResponseUpdate` — response types

---

## 3. Creating Agents — The `AsAIAgent()` Pattern

> *Source: [Agent Types](https://learn.microsoft.com/en-us/agent-framework/agents/?pivots=programming-language-csharp) · [Your First Agent](https://learn.microsoft.com/en-us/agent-framework/get-started/your-first-agent?pivots=programming-language-csharp)*

### Idiomatic Pattern (Recommended)

The `.AsAIAgent()` extension method is available on all supported SDK clients. This is the simplest and recommended way to create agents.

**Azure OpenAI (Responses API):**

```csharp
using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Agents.AI;

var endpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT")!;
var deploymentName = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT_NAME")!;

AIAgent agent = new AzureOpenAIClient(new Uri(endpoint), new AzureCliCredential())
    .GetResponsesClient(deploymentName)
    .AsAIAgent(
        name: "MyAgent",
        instructions: "You are a helpful assistant.");
```

**Azure AI Foundry:**

```csharp
using Azure.AI.Projects;
using Azure.Identity;
using Microsoft.Agents.AI;

AIAgent agent = new AIProjectClient(
        new Uri("https://your-foundry-service.services.ai.azure.com/api/projects/your-project"),
        new AzureCliCredential())
    .AsAIAgent(
        model: "gpt-4o",
        name: "MyAgent",
        instructions: "You are a helpful assistant.");
```

**OpenAI Direct:**

```csharp
using OpenAI;
using Microsoft.Agents.AI;

AIAgent agent = new OpenAIClient("your-api-key")
    .GetResponsesClient()
    .AsAIAgent(
        model: "gpt-4o-mini",
        name: "MyAgent",
        instructions: "You are a helpful assistant.");
```

**From any IChatClient:**

```csharp
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

IChatClient chatClient = /* your chat client */;

AIAgent agent = chatClient.AsAIAgent(
    name: "MyAgent",
    instructions: "You are a helpful assistant.",
    tools: [AIFunctionFactory.Create(MyToolMethod)]);
```

### Simple `ChatClientAgent` (Direct Construction)

```csharp
var agent = new ChatClientAgent(chatClient, instructions: "You are a helpful assistant");
```

### Explicit `ChatClientAgent` (When You Need More Control)

Use `ChatClientAgentOptions` for full control over history, context providers, and chat options:

```csharp
var agent = new ChatClientAgent(chatClient, new ChatClientAgentOptions
{
    Name = "MyAgent",
    ChatOptions = new ChatOptions
    {
        Instructions = "You are a helpful assistant.",
        Tools = [AIFunctionFactory.Create(GetWeather)]
    },
    ChatHistoryProvider = new InMemoryChatHistoryProvider(),
    AIContextProviders = [new MyMemoryProvider()],
});
```

> **Note:** `ChatOptions` is a **nested property** inside `ChatClientAgentOptions`. Instructions and tools go inside `ChatOptions`, not directly on the agent options object.

---

## 4. Running Agents — `RunAsync` & `RunStreamingAsync`

> *Source: [Running Agents](https://learn.microsoft.com/en-us/agent-framework/agents/running-agents?pivots=programming-language-csharp)*

### Non-Streaming

```csharp
// Simple — just get the text
Console.WriteLine(await agent.RunAsync("What is the capital of France?"));

// With session for multi-turn
AgentSession session = await agent.CreateSessionAsync();
AgentResponse response = await agent.RunAsync("Hello, my name is Alice.", session);

Console.WriteLine(response.Text);              // Aggregated text result
Console.WriteLine(response.Messages.Count);     // All produced messages
Console.WriteLine(response.Usage?.InputTokenCount);  // Token usage (if available)
```

### Streaming

```csharp
await foreach (AgentResponseUpdate update in agent.RunStreamingAsync("Tell me a story."))
{
    Console.Write(update.Text);  // Partial text as it arrives
}
```

### Streaming with Session

```csharp
AgentSession session = await agent.CreateSessionAsync();

await foreach (var update in agent.RunStreamingAsync("Hello!", session))
{
    Console.Write(update.Text);
}
```

### Per-Run Options

```csharp
var chatOptions = new ChatOptions
{
    Tools = [AIFunctionFactory.Create(GetWeather)]
};

var response = await agent.RunAsync(
    "What's the weather?",
    options: new ChatClientAgentRunOptions(chatOptions));
```

---

## 5. Function Tools

> *Source: [Function Tools](https://learn.microsoft.com/en-us/agent-framework/agents/tools/function-tools?pivots=programming-language-csharp)*

### Defining Function Tools

Use `AIFunctionFactory.Create` to convert any C# method into a tool:

```csharp
using System.ComponentModel;
using Microsoft.Extensions.AI;

[Description("Get the weather for a given location.")]
static string GetWeather(
    [Description("The location to get the weather for.")] string location)
    => $"The weather in {location} is cloudy with a high of 15°C.";
```

### Providing Tools to an Agent

```csharp
AIAgent agent = chatClient.AsAIAgent(
    name: "WeatherBot",
    instructions: "You help with weather queries.",
    tools: [AIFunctionFactory.Create(GetWeather)]);

Console.WriteLine(await agent.RunAsync("What is the weather like in Amsterdam?"));
```

### Multiple Tools

```csharp
AIAgent agent = chatClient.AsAIAgent(
    name: "TravelBot",
    instructions: "You help plan travel.",
    tools: [
        AIFunctionFactory.Create(GetWeather),
        AIFunctionFactory.Create(SearchFlights),
        AIFunctionFactory.Create(BookHotel)
    ]);
```

### Tool Types Supported

| Tool Type | Description |
|-----------|-------------|
| Function Tools | Custom C# methods via `AIFunctionFactory.Create` |
| Tool Approval | Human-in-the-loop approval for tool calls |
| Code Interpreter | Sandboxed code execution (provider-dependent) |
| File Search | Search uploaded files (provider-dependent) |
| Web Search | Web search (provider-dependent) |
| Hosted MCP Tools | MCP tools hosted by Microsoft Foundry |
| Local MCP Tools | MCP tools on custom servers |

---

## 6. Agent-as-a-Tool Composition

> *Source: [Tools Overview — Using an Agent as a Function Tool](https://learn.microsoft.com/en-us/agent-framework/agents/tools/?pivots=programming-language-csharp#using-an-agent-as-a-function-tool)*

An agent can be used as a function tool for another agent using `.AsAIFunction()`:

```csharp
// Create a specialist agent
AIAgent weatherAgent = chatClient.AsAIAgent(
    name: "WeatherAgent",
    description: "An agent that answers questions about the weather.",
    instructions: "You answer questions about the weather.",
    tools: [AIFunctionFactory.Create(GetWeather)]);

// Create an orchestrator agent that delegates to the specialist
AIAgent orchestrator = chatClient.AsAIAgent(
    name: "Orchestrator",
    instructions: "You are a helpful assistant. Use available tools.",
    tools: [weatherAgent.AsAIFunction()]);

Console.WriteLine(await orchestrator.RunAsync("What's the weather in Paris?"));
```

---

## 7. Sessions & Multi-Turn Conversations

> *Source: [Sessions](https://learn.microsoft.com/en-us/agent-framework/agents/conversations/session?pivots=programming-language-csharp)*

### What Is `AgentSession`?

`AgentSession` is the conversation state container. It holds a `StateBag` for arbitrary state and is passed to `RunAsync`/`RunStreamingAsync` to maintain context across turns.

### Basic Multi-Turn Pattern

```csharp
AgentSession session = await agent.CreateSessionAsync();

// Turn 1
var r1 = await agent.RunAsync("My name is Alice.", session);
Console.WriteLine(r1.Text);

// Turn 2 — agent remembers the name from turn 1
var r2 = await agent.RunAsync("What is my name?", session);
Console.WriteLine(r2.Text);  // "Your name is Alice."
```

### Session Reset (Fresh Conversation)

```csharp
// Create a new session — conversation history is cleared
session = await agent.CreateSessionAsync();

var r3 = await agent.RunAsync("What is my name?", session);
// Agent no longer knows the name (new session = fresh history)
```

### Creating Session from Existing Conversation ID

```csharp
// ChatClientAgent
AgentSession session = await chatClientAgent.CreateSessionAsync(conversationId);

// A2A Agent
AgentSession session = await a2aAgent.CreateSessionAsync(contextId, taskId);
```

### Session Serialization & Restoration

```csharp
// Serialize for persistence (returns JsonElement)
JsonElement serialized = await agent.SerializeSessionAsync(session);

// Restore later
AgentSession resumed = await agent.DeserializeSessionAsync(serialized);
```

> **Important:** Sessions are agent/service-specific. Do not reuse a session with a different agent configuration or provider.

---

## 8. Context Providers & Agent Memory (`AIContextProvider`)

> *Source: [Context Providers](https://learn.microsoft.com/en-us/agent-framework/agents/conversations/context-providers?pivots=programming-language-csharp)*

### Overview

`AIContextProvider` is the MAF pipeline extension point for injecting dynamic context (memories, RAG results, policies, tools) before each LLM call, and extracting/storing information after each call.

Key points:
- Context providers run **around each agent invocation** (before + after)
- They persist **across session resets** (unlike `ChatHistoryProvider`)
- They are the correct way to implement **long-term memory** in MAF

### Pipeline Position

```
[ChatHistoryProvider]     ← manages per-session conversation history
        ↓
[AIContextProviders (N)]  ← inject memories, RAG, tools, policies
        ↓
[IChatClient → LLM]      ← actual inference call
```

### Registering Context Providers

```csharp
AIAgent agent = chatClient.AsAIAgent(new ChatClientAgentOptions
{
    ChatOptions = new() { Instructions = "You are a helpful assistant." },
    AIContextProviders = [
        new MyCustomMemoryProvider(),
        new MyRAGProvider(),
    ],
});
```

Context providers can also be registered via the builder pattern (useful for chat-client level registration):

```csharp
// Agent-level registration
var agent = originalAgent
    .AsBuilder()
    .UseAIContextProviders(new MyMemoryProvider())
    .Build();

// Chat client-level registration (runs inside tool-calling loop)
var agent = chatClient
    .AsBuilder()
    .UseAIContextProviders(new MyContextProvider())
    .BuildAIAgent(new ChatClientAgentOptions
    {
        Name = "MyAgent",
        ChatOptions = new() { Instructions = "You are a helpful assistant." }
    });
```

> **Note:** When registered via `ChatClientAgentOptions.AIContextProviders`, providers run at the agent level. When registered via `chatClient.AsBuilder().UseAIContextProviders(...)`, they run inside the tool-calling loop (closer to inference), which is important for compaction and per-call context enrichment.

### Simple Custom `AIContextProvider`

Override two methods:
- `ProvideAIContextAsync` — called **before** LLM invocation (inject context)
- `StoreAIContextAsync` — called **after** LLM invocation (extract & persist)

> **Warning:** Do not store session-specific state in provider instance fields. A single `AIContextProvider` instance is shared across all sessions. Use `ProviderSessionState<T>` for session-scoped data (shown in the next section). The example below uses `ConcurrentDictionary` only for simplicity — production code should use `ProviderSessionState<T>`.

```csharp
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

internal sealed class PersistentMemoryProvider : AIContextProvider
{
    private readonly ConcurrentDictionary<string, List<string>> _memories = new();

    protected override ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context, CancellationToken cancellationToken = default)
    {
        // Retrieve stored memories for this session
        var sessionId = context.Session?.ToString() ?? "default";
        if (_memories.TryGetValue(sessionId, out var facts) && facts.Count > 0)
        {
            var memoryMessage = new ChatMessage(ChatRole.System,
                "Known facts from previous conversations:\n" +
                string.Join("\n", facts.Select(f => $"- {f}")));

            return new ValueTask<AIContext>(new AIContext
            {
                Messages = [memoryMessage]
            });
        }

        return new ValueTask<AIContext>(new AIContext());
    }

    protected override ValueTask StoreAIContextAsync(
        InvokedContext context, CancellationToken cancellationToken = default)
    {
        // Extract and store facts from the response
        var sessionId = context.Session?.ToString() ?? "default";
        var memories = _memories.GetOrAdd(sessionId, _ => new List<string>());

        // Example: Store any assistant messages as facts
        if (context.ResponseMessages is not null)
        {
            foreach (var msg in context.ResponseMessages.Where(m => m.Role == ChatRole.Assistant))
            {
                if (!string.IsNullOrEmpty(msg.Text))
                {
                    memories.Add(msg.Text);
                }
            }
        }

        return default;
    }
}
```

### `AIContextProvider` State Management

Context providers should **not** store session-specific state in instance fields (one provider serves all sessions). Use `ProviderSessionState<T>` instead:

```csharp
internal sealed class ServiceBackedMemoryProvider : AIContextProvider
{
    private readonly ProviderSessionState<MyState> _sessionState;
    private readonly IMemoryService _client;

    public ServiceBackedMemoryProvider(IMemoryService client) : base(null, null)
    {
        _sessionState = new ProviderSessionState<MyState>(
            stateInitializer: _ => new MyState(),
            stateKey: GetType().Name);
        _client = client;
    }

    public override string StateKey => _sessionState.StateKey;

    protected override ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context, CancellationToken ct)
    {
        var state = _sessionState.GetOrInitializeState(context.Session);

        if (state.MemoryId is null)
            return new ValueTask<AIContext>(new AIContext());

        var memories = _client.LoadMemories(state.MemoryId,
            string.Join("\n", context.AIContext.Messages?.Select(x => x.Text) ?? []));

        return new ValueTask<AIContext>(new AIContext
        {
            Messages = [new ChatMessage(ChatRole.User,
                "Relevant memories: " + string.Join("\n", memories.Select(x => x.Text)))]
        });
    }

    protected override async ValueTask StoreAIContextAsync(
        InvokedContext context, CancellationToken ct)
    {
        var state = _sessionState.GetOrInitializeState(context.Session);
        state.MemoryId ??= _client.CreateMemoryContainer();
        _sessionState.SaveState(context.Session, state);

        await _client.StoreMemoriesAsync(state.MemoryId,
            context.RequestMessages.Concat(context.ResponseMessages ?? []), ct);
    }

    public class MyState
    {
        public string? MemoryId { get; set; }
    }
}
```

### Advanced: Override `InvokingCoreAsync` / `InvokedCoreAsync`

For full control over message filtering and merging (e.g., exclude chat-history messages, filter by source):

```csharp
protected override async ValueTask<AIContext> InvokingCoreAsync(
    InvokingContext context, CancellationToken ct)
{
    // Filter to only external (user) messages, exclude chat history
    var filtered = context.AIContext.Messages?
        .Where(m => m.GetAgentRequestMessageSourceType() == AgentRequestMessageSourceType.External);

    var memories = _client.LoadMemories(
        string.Join("\n", filtered?.Select(x => x.Text) ?? []));

    // Stamp messages with source info
    var memoryMessages = new[] { new ChatMessage(ChatRole.User, "Memories: " + memories) }
        .Select(m => m.WithAgentRequestMessageSource(
            AgentRequestMessageSourceType.AIContextProvider,
            GetType().FullName!));

    return new AIContext
    {
        Instructions = context.AIContext.Instructions,
        Messages = context.AIContext.Messages.Concat(memoryMessages),
        Tools = context.AIContext.Tools
    };
}
```

### Key Behavior: Context Providers Persist Across Session Resets

This is critical for memory systems:
- `ChatHistoryProvider` — loses history on session reset (as expected)
- `AIContextProvider` — **retains its state** across session resets
- This means long-term memory (stored via `AIContextProvider`) survives `CreateSessionAsync()` calls

---

## 9. Chat History Management (`ChatHistoryProvider`)

> *Source: [Chat History Storage](https://learn.microsoft.com/en-us/agent-framework/agents/conversations/storage?pivots=programming-language-csharp)*

### Built-in: `InMemoryChatHistoryProvider`

MAF provides `InMemoryChatHistoryProvider` out of the box. It stores conversation history in memory per session and is automatically used when no provider is specified.

### Custom History Provider

The simplest `ChatHistoryProvider` overrides two methods:
- `ProvideChatHistoryAsync(InvokingContext)` — load chat history before LLM call
- `StoreChatHistoryAsync(InvokedContext)` — persist new messages after LLM call

Use `ProviderSessionState<T>` to store session-specific state (same pattern as `AIContextProvider`):

```csharp
public sealed class SimpleInMemoryChatHistoryProvider : ChatHistoryProvider
{
    private readonly ProviderSessionState<State> _sessionState;

    public SimpleInMemoryChatHistoryProvider(
        Func<AgentSession?, State>? stateInitializer = null,
        string? stateKey = null)
    {
        this._sessionState = new ProviderSessionState<State>(
            stateInitializer ?? (_ => new State()),
            stateKey ?? this.GetType().Name);
    }

    public override string StateKey => this._sessionState.StateKey;

    protected override ValueTask<IEnumerable<ChatMessage>> ProvideChatHistoryAsync(
        InvokingContext context, CancellationToken cancellationToken = default) =>
        new(this._sessionState.GetOrInitializeState(context.Session).Messages);

    protected override ValueTask StoreChatHistoryAsync(
        InvokedContext context, CancellationToken cancellationToken = default)
    {
        var state = this._sessionState.GetOrInitializeState(context.Session);
        var allNewMessages = context.RequestMessages.Concat(context.ResponseMessages ?? []);
        state.Messages.AddRange(allNewMessages);
        this._sessionState.SaveState(context.Session, state);
        return default;
    }

    public sealed class State
    {
        [JsonPropertyName("messages")]
        public List<ChatMessage> Messages { get; set; } = [];
    }
}
```

### Reducing In-Memory History Size

```csharp
AIAgent agent = chatClient.AsAIAgent(new ChatClientAgentOptions
{
    Name = "Assistant",
    ChatOptions = new() { Instructions = "You are a helpful assistant." },
    ChatHistoryProvider = new InMemoryChatHistoryProvider(new InMemoryChatHistoryProviderOptions
    {
        ChatReducer = new MessageCountingChatReducer(20)
    })
});
```

### Accessing In-Memory History

```csharp
var provider = agent.GetService<InMemoryChatHistoryProvider>();
List<ChatMessage>? messages = provider?.GetMessages(session);
```
```

---

## 10. The Agent Pipeline Architecture

> *Source: [Agent Pipeline](https://learn.microsoft.com/en-us/agent-framework/agents/agent-pipeline?pivots=programming-language-csharp)*

MAF 1.3.0 has a layered pipeline that executes on each `RunAsync`/`RunStreamingAsync` call:

```
┌─ Agent Middleware Layer ───────────────────────────────────────┐
│  ┌─ Context Layer ───────────────────────────────────────────┐│
│  │  ChatHistoryProvider (1)      Load/store conversation     ││
│  │  AIContextProviders (N)       Inject memories/RAG/tools   ││
│  │  ┌─ Chat Client Layer ──────────────────────────────────┐ ││
│  │  │  IChatClient middleware    Logging, retry, etc.       │ ││
│  │  │  IChatClient → LLM        Actual inference call       │ ││
│  │  └──────────────────────────────────────────────────────┘ ││
│  └───────────────────────────────────────────────────────────┘│
└───────────────────────────────────────────────────────────────┘
```

### Execution Flow Per Invocation

1. `agent.RunAsync(input, session)` — user call
2. **Agent Middleware** — intercept/modify input/output
3. **ChatHistoryProvider.InvokingCoreAsync()** — load conversation history
4. **AIContextProvider[].InvokingCoreAsync()** — inject memories, RAG, tools
5. **IChatClient middleware** — logging, retry, etc.
6. **IChatClient → LLM** — actual API call
7. **AIContextProvider[].InvokedCoreAsync()** — extract & store context
8. **ChatHistoryProvider.InvokedCoreAsync()** — persist conversation
9. Return `AgentResponse`

---

## 11. Middleware

> *Source: [Middleware](https://learn.microsoft.com/en-us/agent-framework/agents/middleware/?pivots=programming-language-csharp) · [Defining Middleware](https://learn.microsoft.com/en-us/agent-framework/agents/middleware/defining-middleware?pivots=programming-language-csharp)*

MAF supports three middleware layers:

### Agent Run Middleware

Intercepts agent runs (input/output):

```csharp
async Task<AgentResponse> LoggingMiddleware(
    IEnumerable<ChatMessage> messages,
    AgentSession? session,
    AgentRunOptions? options,
    AIAgent innerAgent,
    CancellationToken cancellationToken)
{
    Console.WriteLine($"Input messages: {messages.Count()}");
    var response = await innerAgent.RunAsync(messages, session, options, cancellationToken);
    Console.WriteLine($"Output messages: {response.Messages.Count}");
    return response;
}
```

### Agent Run Streaming Middleware

```csharp
async IAsyncEnumerable<AgentResponseUpdate> StreamingMiddleware(
    IEnumerable<ChatMessage> messages,
    AgentSession? session,
    AgentRunOptions? options,
    AIAgent innerAgent,
    [EnumeratorCancellation] CancellationToken cancellationToken)
{
    await foreach (var update in innerAgent.RunStreamingAsync(
        messages, session, options, cancellationToken))
    {
        // Log, transform, or filter updates
        yield return update;
    }
}
```

### Function Calling Middleware

```csharp
async ValueTask<object?> AuditFunctionMiddleware(
    AIAgent agent,
    FunctionInvocationContext context,
    Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>> next,
    CancellationToken cancellationToken)
{
    Console.WriteLine($"Calling function: {context.Function.Name}");
    var result = await next(context, cancellationToken);
    Console.WriteLine($"Result: {result}");
    return result;
}
```

### Registering Middleware via Builder Pattern

```csharp
var agentWithMiddleware = agent
    .AsBuilder()
        .Use(runFunc: LoggingMiddleware, runStreamingFunc: StreamingMiddleware)
        .Use(AuditFunctionMiddleware)
    .Build();
```

### IChatClient Middleware

IChatClient middleware intercepts calls from the agent to the inference service. Register it on the chat client **before** passing it to the agent:

```csharp
async Task<ChatResponse> CustomChatClientMiddleware(
    IEnumerable<ChatMessage> messages,
    ChatOptions? options,
    IChatClient innerChatClient,
    CancellationToken cancellationToken)
{
    Console.WriteLine($"Input: {messages.Count()}");
    var response = await innerChatClient.GetResponseAsync(messages, options, cancellationToken);
    Console.WriteLine($"Output: {response.Messages.Count}");
    return response;
}
```

**Pattern 1: Apply to `IChatClient`, then create agent:**

```csharp
var chatClient = new AIProjectClient(new Uri(endpoint), new DefaultAzureCredential())
    .GetProjectOpenAIClient()
    .GetProjectResponsesClient()
    .AsIChatClient(deploymentName);

var middlewareEnabledChatClient = chatClient
    .AsBuilder()
        .Use(getResponseFunc: CustomChatClientMiddleware, getStreamingResponseFunc: null)
    .Build();

var agent = new ChatClientAgent(middlewareEnabledChatClient, instructions: "You are a helpful assistant.");
```

**Pattern 2: Use `clientFactory` on `AsAIAgent`:**

```csharp
var agent = new AIProjectClient(new Uri(endpoint), new DefaultAzureCredential())
    .AsAIAgent(
        model: deploymentName,
        instructions: "You are helpful.",
        clientFactory: chatClient => chatClient
            .AsBuilder()
                .Use(getResponseFunc: CustomChatClientMiddleware, getStreamingResponseFunc: null)
            .Build());
```

> **Important:** Ideally provide both `runFunc` and `runStreamingFunc` for agent middleware. When only providing non-streaming middleware, the agent uses it for both modes — but streaming will run in non-streaming mode. There is also a `Use(sharedFunc: ...)` overload that works for both modes without blocking streaming, but cannot intercept or modify output.

---

## 12. Workflows — `WorkflowBuilder` & Executors

> *Source: [Workflows](https://learn.microsoft.com/en-us/agent-framework/workflows/workflows?pivots=programming-language-csharp)*

### What Are Workflows?

Workflows connect **executors** (processing units) with **edges** (connections) into a directed graph. Unlike agents (LLM-driven, dynamic), workflows have **explicitly defined** execution paths.

### Building a Basic Workflow

```csharp
using Microsoft.Agents.AI.Workflows;

var processor = new DataProcessor();
var validator = new Validator();
var formatter = new Formatter();

WorkflowBuilder builder = new(processor);       // Set starting executor
builder.AddEdge(processor, validator);
builder.AddEdge(validator, formatter);
var workflow = builder.Build();
```

### Execution Model: Supersteps (BSP)

Workflows use Bulk Synchronous Parallel execution:
1. Collect pending messages from previous superstep
2. Route messages to target executors based on edges
3. Run all target executors **concurrently** within the superstep
4. Wait for **all** executors to complete (synchronization barrier)
5. Queue new messages for next superstep

This guarantees:
- **Deterministic execution** — same input = same execution order
- **Reliable checkpointing** — state saved at superstep boundaries
- **No race conditions** between supersteps

---

## 13. Source-Generated Executors (`[MessageHandler]`)

> *Source: [Executors](https://learn.microsoft.com/en-us/agent-framework/workflows/executors?pivots=programming-language-csharp)*

### Breaking Change from 1.1.0: `StreamsMessageAttribute` and `YieldsMessageAttribute` Removed

`StreamsMessageAttribute` and `YieldsMessageAttribute` were **removed** from `Microsoft.Agents.AI.Workflows` in 1.3.0. If your executors used these attributes (1.1.0 or earlier), remove them — they will cause build errors (`CS0246`). The functionality they controlled is now expressed explicitly via `IWorkflowContext`:

| Removed (1.1.0) | Replacement (1.3.0) |
|---|---|
| `[StreamsMessage]` on a `ValueTask<T>` handler | Return `ValueTask<T>` from `[MessageHandler]` — the result is automatically sent to connected executors |
| `[YieldsMessage]` on a `ValueTask<T>` handler | Call `await context.YieldOutputAsync(value)` inside a `void`/`ValueTask` `[MessageHandler]` |

> **⚠️ Common confusion:** Do **not** confuse `[YieldsMessage]` (removed) with `[YieldsOutput(typeof(T))]` (still present and supported in 1.3.0). The latter is a class-level attribute on a `partial Executor` declaring the executor's external output type for the source generator — it is NOT removed. When auditing, search for the exact strings `StreamsMessage` and `YieldsMessage` (without the `Output` suffix) to avoid false positives.

### The Recommended Pattern

Use `[MessageHandler]` on methods in a `partial class` deriving from `Executor`. This is the recommended approach — it provides compile-time validation, better performance, and Native AOT compatibility.

**Required package:**

```xml
<PackageReference Include="Microsoft.Agents.AI.Workflows.Generators" Version="1.3.0" />
```

### Basic Executor

```csharp
using Microsoft.Agents.AI.Workflows;

internal sealed partial class UppercaseExecutor() : Executor("Uppercase")
{
    [MessageHandler]
    private ValueTask<string> HandleAsync(string message, IWorkflowContext context)
    {
        return ValueTask.FromResult(message.ToUpperInvariant());
    }
}
```

### Multiple Input Types

```csharp
internal sealed partial class MultiTypeExecutor() : Executor("MultiType")
{
    [MessageHandler]
    private ValueTask<string> HandleStringAsync(string message, IWorkflowContext context)
        => ValueTask.FromResult(message.ToUpperInvariant());

    [MessageHandler]
    private ValueTask<int> HandleIntAsync(int message, IWorkflowContext context)
        => ValueTask.FromResult(message * 2);
}
```

### Using `IWorkflowContext`

```csharp
internal sealed partial class OutputExecutor() : Executor("Output")
{
    [MessageHandler]
    private async ValueTask HandleAsync(string message, IWorkflowContext context)
    {
        // Send to connected executors
        await context.SendMessageAsync(message.ToUpper());

        // Produce workflow output (returned/streamed to caller)
        await context.YieldOutputAsync($"Processed: {message}");
    }
}
```

### Function-Based Executors (Quick & Simple)

For simple transformations, skip the class — bind a function directly:

```csharp
Func<string, string> toUpper = s => s.ToUpperInvariant();
var uppercaseExecutor = toUpper.BindExecutor("Uppercase");

var workflow = new WorkflowBuilder(uppercaseExecutor)
    .AddEdge(uppercaseExecutor, nextExecutor)
    .Build();
```

> **Note:** `ReflectingExecutor` is obsoleted in favor of source-generated `[MessageHandler]` executors. Use the `partial class` + `[MessageHandler]` pattern instead.

---

## 14. Agents in Workflows

### Binding an Agent as an Executor

Use `.BindAsExecutor()` to place an `AIAgent` inside a workflow:

```csharp
AIAgent plannerAgent = chatClient.AsAIAgent(
    name: "Planner",
    instructions: "You create step-by-step plans.");

AIAgent writerAgent = chatClient.AsAIAgent(
    name: "Writer",
    instructions: "You write content based on the plan provided.");

// Bind agents as workflow executors
var plannerExecutor = plannerAgent.BindAsExecutor(emitEvents: true);
var writerExecutor = writerAgent.BindAsExecutor(emitEvents: true);

// Build multi-agent workflow
var workflow = new WorkflowBuilder(plannerExecutor)
    .AddEdge(plannerExecutor, writerExecutor)
    .Build();
```

### Combining Agents and Custom Executors

```csharp
var sanitizer = new SanitizerExecutor();  // Custom [MessageHandler] executor
var agent = chatClient.AsAIAgent(name: "Analyzer", instructions: "...");
var agentExecutor = agent.BindAsExecutor(emitEvents: true);

var workflow = new WorkflowBuilder(sanitizer)
    .AddEdge(sanitizer, agentExecutor)
    .Build();
```

---

## 15. Workflow Execution & Events

> *Source: [Workflows](https://learn.microsoft.com/en-us/agent-framework/workflows/workflows?pivots=programming-language-csharp)*

### Streaming Execution (Recommended)

```csharp
using Microsoft.Agents.AI.Workflows;

StreamingRun run = await InProcessExecution.RunStreamingAsync(workflow, "Process this input");

await foreach (WorkflowEvent evt in run.WatchStreamAsync())
{
    switch (evt)
    {
        case ExecutorCompletedEvent completed:
            Console.WriteLine($"[{completed.ExecutorId}]: {completed.Data}");
            break;

        case AgentResponseEvent agentResponse:
            Console.WriteLine($"Agent response: {agentResponse.Data}");
            break;

        case WorkflowOutputEvent output:
            Console.WriteLine($"Final output: {output.Data}");
            break;
    }
}
```

### Non-Streaming Execution

```csharp
Run result = await InProcessExecution.RunAsync(workflow, "Process this input");

foreach (WorkflowEvent evt in result.NewEvents)
{
    if (evt is WorkflowOutputEvent output)
    {
        Console.WriteLine($"Result: {output.Data}");
    }
}
```

### Event Types

| Event | Description |
|-------|-------------|
| `ExecutorCompletedEvent` | An executor finished processing |
| `AgentResponseEvent` | An AI agent produced a response (includes token usage) |
| `WorkflowOutputEvent` | The workflow produced a final output |
| `ExecutorStartedEvent` | An executor began processing |

---

## 16. Response & Content Types

> *Source: [Running Agents — Response types](https://learn.microsoft.com/en-us/agent-framework/agents/running-agents?pivots=programming-language-csharp#response-types) · [Message types](https://learn.microsoft.com/en-us/agent-framework/agents/running-agents?pivots=programming-language-csharp#message-types)*

### `AgentResponse` (Non-Streaming)

```csharp
AgentResponse response = await agent.RunAsync("Hello");

response.Text;              // Aggregated text from all TextContent items
response.Messages;          // All ChatMessage objects produced
response.Usage;             // Token usage (InputTokenCount, OutputTokenCount)
```

### `AgentResponseUpdate` (Streaming)

```csharp
await foreach (AgentResponseUpdate update in agent.RunStreamingAsync("Hello"))
{
    update.Text;            // Partial text in this update
    update.Contents;        // All AIContent items in this update (TextContent, FunctionCallContent, etc.)
}
```

### Content Types (from `Microsoft.Extensions.AI`)

| Type | Description |
|------|-------------|
| `TextContent` | Text content (input/output) |
| `DataContent` | Binary content (images, audio, video) |
| `UriContent` | URL to hosted content |
| `FunctionCallContent` | Request to invoke a function tool |
| `FunctionResultContent` | Result of a function tool invocation |
| `UsageContent` | Token usage information (provider-specific, may not be present in all providers) |

### Processing Tool Calls in Streaming

```csharp
await foreach (var update in agent.RunStreamingAsync("What's the weather?"))
{
    foreach (var content in update.Contents)
    {
        switch (content)
        {
            case TextContent text:
                Console.Write(text.Text);
                break;

            case FunctionCallContent call:
                Console.WriteLine($"Tool call: {call.Name}({call.CallId})");
                break;

            case FunctionResultContent result:
                Console.WriteLine($"Tool result: {result.CallId} → {result.Result}");
                break;

            case UsageContent usage:
                Console.WriteLine($"Tokens: {usage.Details.InputTokenCount} in, " +
                                  $"{usage.Details.OutputTokenCount} out");
                break;
        }
    }
}
```

---

## 17. Streaming Patterns

> *Source: [Running Agents — Streaming and non-streaming](https://learn.microsoft.com/en-us/agent-framework/agents/running-agents?pivots=programming-language-csharp#streaming-and-non-streaming)*

### Pattern 1: Simple Text Streaming

```csharp
await foreach (var update in agent.RunStreamingAsync("Tell me a joke."))
{
    Console.Write(update.Text);
}
Console.WriteLine();
```

### Pattern 2: Full Content Processing

```csharp
var allMessages = new List<ChatMessage>();

await foreach (var update in agent.RunStreamingAsync("Query", session))
{
    // Real-time text output
    if (!string.IsNullOrEmpty(update.Text))
        Console.Write(update.Text);

    // Collect all messages for post-processing
    // (AgentResponseUpdate can be converted to AgentResponse)
}
```

### Pattern 3: Aggregating Streaming to Response

Use the extension method on a list of updates:

```csharp
List<AgentResponseUpdate> updates = [];
await foreach (var update in agent.RunStreamingAsync("Hello"))
{
    updates.Add(update);
}

AgentResponse aggregated = updates.ToAgentResponse();
Console.WriteLine(aggregated.Text);
Console.WriteLine(aggregated.Messages.Count);
```

---

## 17.5. Compaction Strategies (Experimental)

> **Note:** The compaction framework is currently experimental. Add `#pragma warning disable MAAI001` to use it.

As conversations grow, token counts can exceed model limits and increase costs. MAF provides compaction strategies to reduce history size while preserving important context.

### Strategy Types

| Strategy | Aggressiveness | Description |
|----------|----------------|-------------|
| `ToolResultCompactionStrategy` | Low | Collapses old tool-call groups into summaries |
| `SummarizationCompactionStrategy` | Medium | Uses an LLM to summarize older conversation parts |
| `SlidingWindowCompactionStrategy` | High | Drops entire turns, keeping only recent window |
| `TruncationCompactionStrategy` | High | Removes oldest message groups |
| `PipelineCompactionStrategy` | Configurable | Chains multiple strategies in sequence |

### Triggers

```csharp
CompactionTrigger trigger = CompactionTriggers.All(
    CompactionTriggers.HasToolCalls(),
    CompactionTriggers.TokensExceed(2000));
```

Available triggers: `Always`, `Never`, `TokensExceed(n)`, `MessagesExceed(n)`, `TurnsExceed(n)`, `GroupsExceed(n)`, `HasToolCalls()`. Combine with `All(...)` (AND) or `Any(...)` (OR).

### Using Compaction with an Agent

Wrap a strategy in `CompactionProvider` and register as an `AIContextProvider`:

```csharp
#pragma warning disable MAAI001

IChatClient agentChatClient = openAIClient.GetChatClient(deploymentName).AsIChatClient();
IChatClient summarizerChatClient = openAIClient.GetChatClient(deploymentName).AsIChatClient();

PipelineCompactionStrategy compactionPipeline = new(
    new ToolResultCompactionStrategy(CompactionTriggers.TokensExceed(0x200)),
    new SummarizationCompactionStrategy(summarizerChatClient, CompactionTriggers.TokensExceed(0x500)),
    new SlidingWindowCompactionStrategy(CompactionTriggers.TurnsExceed(4)),
    new TruncationCompactionStrategy(CompactionTriggers.TokensExceed(0x8000)));

// Register on chat client builder (recommended — runs inside tool-calling loop)
AIAgent agent = agentChatClient
    .AsBuilder()
    .UseAIContextProviders(new CompactionProvider(compactionPipeline))
    .BuildAIAgent(new ChatClientAgentOptions
    {
        Name = "ShoppingAssistant",
        ChatOptions = new()
        {
            Instructions = "You are a helpful shopping assistant.",
            Tools = [AIFunctionFactory.Create(LookupPrice)],
        },
    });
```

> **Tip:** Use a smaller model (e.g., `gpt-4o-mini`) for the summarization chat client to reduce costs.

### Compaction Applicability

Compaction applies **only** to agents with in-memory history. Service-managed context agents (Foundry Agents, Responses API with store enabled) handle context server-side — compaction strategies have no effect on them.

---

## 17.6. Structured Output

> *Source: [Structured Output](https://learn.microsoft.com/en-us/agent-framework/agents/structured-output?pivots=programming-language-csharp)*

MAF supports type-safe structured output in three ways:

### Pattern 1: Generic `RunAsync<T>()` (Recommended)

```csharp
public class CityInfo
{
    public string? Name { get; set; }
    public string? Country { get; set; }
    public int? Population { get; set; }
}

AgentResponse<CityInfo> response = await agent.RunAsync<CityInfo>(
    "Tell me about Amsterdam.");

CityInfo city = response.Result;
Console.WriteLine($"{city.Name}, {city.Country} — pop. {city.Population}");
```

### Pattern 2: `ResponseFormat` on `AgentRunOptions`

```csharp
AgentRunOptions runOptions = new()
{
    ResponseFormat = ChatResponseFormat.ForJsonSchema<CityInfo>()
};

AgentResponse response = await agent.RunAsync("Tell me about Amsterdam.", options: runOptions);
CityInfo city = JsonSerializer.Deserialize<CityInfo>(response.Text)!;
```

### Pattern 3: Structured Output with Streaming

```csharp
IAsyncEnumerable<AgentResponseUpdate> updates = agent.RunStreamingAsync(
    "Tell me about Amsterdam.");

AgentResponse response = await updates.ToAgentResponseAsync();
CityInfo city = JsonSerializer.Deserialize<CityInfo>(response.Text)!;
```

> **Note:** For agents that don't natively support structured output, use the `UseStructuredOutput()` middleware decorator pattern as a wrapper (see the [StructuredOutputAgent sample](https://github.com/microsoft/agent-framework/blob/main/dotnet/samples/02-agents/Agents/Agent_Step02_StructuredOutput)).

---

## 17.7. Tool Approval — Human-in-the-Loop

> *Source: [Tool Approval](https://learn.microsoft.com/en-us/agent-framework/agents/tools/tool-approval?pivots=programming-language-csharp)*

Wrap any sensitive tool in `ApprovalRequiredAIFunction` to require user confirmation before execution:

```csharp
AIFunction weatherFunction = AIFunctionFactory.Create(GetWeather);
AIFunction approvalRequired = new ApprovalRequiredAIFunction(weatherFunction);

AIAgent agent = chatClient.AsAIAgent(
    instructions: "You are a helpful assistant.",
    tools: [approvalRequired]);
```

### Handling Approval Requests

```csharp
AgentSession session = await agent.CreateSessionAsync();
AgentResponse response = await agent.RunAsync("What's the weather in Amsterdam?", session);

// Check for approval requests in the response
var approvalRequests = response.Messages
    .SelectMany(m => m.Contents)
    .OfType<FunctionApprovalRequestContent>()
    .ToList();

if (approvalRequests.Any())
{
    var request = approvalRequests.First();
    Console.WriteLine($"Approve '{request.FunctionCall.Name}'? (Y/N)");
    bool approved = Console.ReadLine()?.Trim().ToUpper() == "Y";

    // Send approval/rejection back to the agent
    var approvalMessage = new ChatMessage(ChatRole.User,
        [request.CreateResponse(approved)]);
    response = await agent.RunAsync(approvalMessage, session);
}

Console.WriteLine(response.Text);
```

---

## 17.8. Observability — OpenTelemetry

> *Source: [Observability](https://learn.microsoft.com/en-us/agent-framework/agents/observability?pivots=programming-language-csharp)*

Instrument both chat client and agent for traces, metrics, and logs:

```csharp
const string SourceName = "MyApplication";

// Instrument the chat client
var instrumentedChatClient = chatClient
    .AsBuilder()
    .UseOpenTelemetry(sourceName: SourceName,
        configure: cfg => cfg.EnableSensitiveData = true)
    .Build();

// Instrument the agent
AIAgent agent = new ChatClientAgent(
    instrumentedChatClient,
    name: "MyAgent",
    instructions: "You are a helpful assistant.",
    tools: [AIFunctionFactory.Create(GetWeather)])
    .AsBuilder()
    .UseOpenTelemetry(sourceName: SourceName,
        configure: cfg => cfg.EnableSensitiveData = true)
    .Build();
```

### Configure OpenTelemetry Exporters

```csharp
using var tracerProvider = Sdk.CreateTracerProviderBuilder()
    .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("MyService"))
    .AddSource(SourceName)
    .AddOtlpExporter(o => o.Endpoint = new Uri("http://localhost:4317")) // Aspire Dashboard
    .Build();
```

> **Warning:** Only enable `EnableSensitiveData` in development/testing — it captures prompts, responses, and function call arguments in traces.

> **Tip:** If you don't specify a source name, it defaults to `Experimental.Microsoft.Agents.AI`. Use `AddSource("Experimental.Microsoft.Agents.AI")` in that case.

---

## 17.9. Multimodal — Images

> *Source: [Multimodal](https://learn.microsoft.com/en-us/agent-framework/agents/multimodal?pivots=programming-language-csharp)*

Pass images to agents using `DataContent` or `UriContent`:

```csharp
// From a local file
DataContent imageContent = await DataContent.LoadFromAsync("photo.jpg");

// Or from a URL
var imageUrl = new UriContent(
    "https://example.com/photo.jpg", "image/jpeg");

// Create a message with text + image
ChatMessage message = new(ChatRole.User, [
    new TextContent("What do you see in this image?"),
    imageContent  // or imageUrl
]);

Console.WriteLine(await agent.RunAsync(message));
```

> **Note:** Requires a vision-capable model (e.g., `gpt-4o`, `gpt-4o-mini`). Not all providers support multimodal input.

---

## 17.10. A2A Agent — Remote Protocol Proxies

> *Source: PR [#5423](https://github.com/microsoft/agent-framework/pull/5423) — "Migrate A2A agent and hosting to A2A SDK v1"*

> The A2A agent and hosting stack uses A2A SDK `1.0.0-preview2`. All hosting extension methods, DI registration APIs, and the client type follow the v1 SDK patterns below.

### Packages (1.3.0)

```xml
<PackageReference Include="Microsoft.Agents.AI.A2A" Version="1.3.0" />
<PackageReference Include="Microsoft.Agents.AI.Hosting.A2A" Version="1.3.0" />
<PackageReference Include="Microsoft.Agents.AI.Hosting.A2A.AspNetCore" Version="1.3.0" />
```

### What Changed

| Area | 1.3.0 Pattern |
|------|---------------|
| A2A SDK dependency | `A2ASdk 1.0.0-preview2` |
| Client type | `IA2AClient` (interface) via `GetService(typeof(IA2AClient))` |
| Client factory | `A2AClientFactory.Create()` |
| Hosting DI registration | `A2AServerServiceCollectionExtensions.AddA2AServer(agent, options)` |
| Endpoint mapping | `A2AEndpointRouteBuilderExtensions.MapA2AHttpJson(path)` / `MapA2AJsonRpc(path)` |
| Agent handler | `A2AAgentHandler` |
| Server configuration | `A2AServerRegistrationOptions` |
| SSE reconnection | Built-in using continuation tokens |
| Streaming | SSE streaming supported in agent handler |
| Samples location | `samples/02-agents/A2A/` |

### DI Registration — Server Side

```csharp
// 1. Register the A2A server in DI
services.AddA2AServer(agent, new A2AServerRegistrationOptions
{
    AgentCard = agentCard
});

// 2. Map protocol-specific endpoints
app.MapA2AHttpJson("/agents/myagent");   // HTTP+JSON protocol (preferred)
// or
app.MapA2AJsonRpc("/agents/myagent");    // JSON-RPC protocol
// or both — one per protocol binding, same agent
```

### Client Side — IA2AClient Interface

```csharp
// Resolve the A2A client via the IA2AClient interface
IA2AClient client = (IA2AClient)agent.GetService(typeof(IA2AClient));
```

### Agent Card Extensions — Client Factory

```csharp
// A2AAgentCardExtensions and A2ACardResolverExtensions
// use A2AClientFactory.Create() and accept optional A2AClientOptions

// Resolve a remote agent from its agent card URL
var options = new A2AClientOptions
{
    PreferredProtocol = A2AProtocol.HttpJson  // or A2AProtocol.JsonRpc
};

AIAgent remoteAgent = await A2ACardResolver.GetAgentAsync(agentCardUrl, options);
```

### SSE Stream Reconnection

Stream reconnection is **built-in**. The `A2AAgent` automatically uses continuation tokens to recover interrupted SSE connections.

```csharp
// Stream reconnection is transparent — no code changes needed
AIAgent remoteAgent = await A2ACardResolver.GetAgentAsync(agentCardUrl);
AgentSession session = await remoteAgent.CreateSessionAsync();

// If the SSE stream is interrupted, A2AAgent reconnects automatically
// using the last received continuation token
await foreach (var update in remoteAgent.RunStreamingAsync("Hello!", session))
{
    Console.Write(update.Text);
}
```

### New Samples

| Sample | Description |
|--------|-------------|
| `A2AAgent_ProtocolSelection` | Demonstrates selecting HTTP+JSON vs JSON-RPC protocol |
| `A2AAgent_StreamReconnection` | Demonstrates SSE stream reconnection with continuation tokens |
| `A2AClientServer` | Updated for v1 SDK APIs |
| `AgentWebChat` | Updated for v1 SDK APIs |

---

## 17.11. Dynamic Tool Expansion (New in 1.3.0)

> *Source: PR [#5425](https://github.com/microsoft/agent-framework/pull/5425) · Sample: [Agent_Step20_DynamicFunctionTools](https://github.com/microsoft/agent-framework/tree/main/dotnet/samples/02-agents/Agents/Agent_Step20_DynamicFunctionTools)*

MAF 1.3.0 introduces a sample demonstrating how to expand the set of available tools **during** a function-calling loop using `FunctionInvokingChatClient.CurrentContext`. This is a **progressive disclosure** pattern: register a lightweight catalog tool upfront, and let the LLM request additional tools on-demand.

### Pattern: Progressive Tool Loading

Instead of loading all tools on every request (inflating token counts), start with just a `RequestTools` tool pointing to a catalog. When the LLM needs a capability, it calls `RequestTools`, which dynamically adds the real tool to the current function-calling loop.

```csharp
using Microsoft.Extensions.AI;
using System.ComponentModel;

// Full catalog — not registered initially
var toolCatalog = new Dictionary<string, AIFunction>
{
    ["GetWeather"] = AIFunctionFactory.Create(GetWeather),
    ["GetLocalTime"] = AIFunctionFactory.Create(GetLocalTime),
    ["ConvertTemperature"] = AIFunctionFactory.Create(ConvertTemperature),
};

// "RequestTools" is the gatekeeper — the only tool registered at startup
AIFunction requestToolsFunc = AIFunctionFactory.Create(
    ([Description("Names of the tools to make available")] string[] toolNames) =>
    {
        // FunctionInvokingChatClient.CurrentContext exposes the active ChatOptions
        var context = FunctionInvokingChatClient.CurrentContext;
        if (context?.Options?.Tools is not null)
        {
            foreach (var name in toolNames)
            {
                if (toolCatalog.TryGetValue(name, out var tool) &&
                    !context.Options.Tools.OfType<AIFunction>().Any(t => t.Name == name))
                {
                    context.Options.Tools.Add(tool);
                }
            }
        }
        // Return empty string to avoid confusing the LLM with tool names in the result
        return string.Empty;
    },
    name: "RequestTools",
    description: "Make additional tools available. Call this before using any specialized tool. " +
                 "Available tools: GetWeather, GetLocalTime, ConvertTemperature.");

// Build agent with only RequestTools initially
AIAgent agent = chatClient
    .AsBuilder()
    .BuildAIAgent(new ChatClientAgentOptions
    {
        Name = "LazyToolAgent",
        ChatOptions = new()
        {
            Instructions = "You are a helpful assistant. Use RequestTools to request a tool before calling it.",
            Tools = [requestToolsFunc]  // Only the catalog tool at startup
        }
    });

// Run multi-question session
AgentSession session = await agent.CreateSessionAsync();
string[] prompts = [
    "What's the weather like in Seattle and London?",
    "What time is it in New York?",
    "Can you convert those temperatures to Celsius?"
];
foreach (var prompt in prompts)
    Console.WriteLine(await agent.RunAsync(prompt, session));
```

> **⚠️ Important limitation:** Tools added via `FunctionInvokingChatClient.CurrentContext` are only active for the **current LLM turn** (the current function-calling loop iteration for a single `RunAsync` call). They are **not** automatically persisted across subsequent `RunAsync` calls to the same session.

> **Production pattern:** To persist dynamically loaded tools across turns, maintain a session-level cache in an `AIContextProvider` and re-inject the already-loaded tools on each invocation. See below.

### Session-Persistent Dynamic Tools via `AIContextProvider`

```csharp
internal sealed class DynamicToolContextProvider : AIContextProvider
{
    private readonly ProviderSessionState<State> _sessionState;
    private readonly Dictionary<string, AIFunction> _catalog;

    public DynamicToolContextProvider(Dictionary<string, AIFunction> catalog)
        : base(null, null)
    {
        _catalog = catalog;
        _sessionState = new ProviderSessionState<State>(_ => new State(), GetType().Name);
    }

    public override string StateKey => _sessionState.StateKey;

    protected override ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context, CancellationToken ct)
    {
        var state = _sessionState.GetOrInitializeState(context.Session);
        // Re-inject tools that were loaded in previous turns
        var persistedTools = state.LoadedToolNames
            .Select(n => _catalog.TryGetValue(n, out var t) ? t : null)
            .Where(t => t is not null)
            .Cast<AIFunction>()
            .ToList<AITool>();

        return new(new AIContext { Tools = persistedTools });
    }

    protected override ValueTask StoreAIContextAsync(InvokedContext context, CancellationToken ct)
    {
        // Track which tools the LLM actually called in this turn
        var state = _sessionState.GetOrInitializeState(context.Session);
        var calledTools = context.ResponseMessages
            .SelectMany(m => m.Contents.OfType<FunctionCallContent>())
            .Select(c => c.Name)
            .Where(name => _catalog.ContainsKey(name));
        foreach (var name in calledTools)
            if (!state.LoadedToolNames.Contains(name))
                state.LoadedToolNames.Add(name);
        _sessionState.SaveState(context.Session, state);
        return default;
    }

    public class State
    {
        public List<string> LoadedToolNames { get; set; } = [];
    }
}
```

---

## 17.12. Server-Side Foundry Toolbox (New in 1.3.0)

> *Source: PR [#5450](https://github.com/microsoft/agent-framework/pull/5450) — "Add server-side Foundry Toolbox support"*

MAF 1.3.0 adds server-side **Foundry Toolbox** support to `Microsoft.Agents.AI.Foundry`. Foundry Toolboxes are collections of tools hosted and executed by the Azure AI Foundry service — no local C# function implementations needed. This is distinct from local `AIFunctionFactory.Create`-based tools.

### Package

```xml
<PackageReference Include="Microsoft.Agents.AI.Foundry" Version="1.3.0" />
```

### What Is a Foundry Toolbox?

A Foundry Toolbox is a server-managed collection of pre-built tools (e.g., Bing web search, document retrieval, code evaluation) that the Foundry service executes on your behalf. The Foundry service handles tool invocation — your code never sees the tool call.

### Example — Foundry Agent with Server-Side Toolbox

```csharp
using Azure.AI.Projects;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Foundry;

// Create a Foundry-backed agent. Fondry Toolbox tools are configured in the
// Azure AI Foundry portal and attached at the model/deployment level.
AIAgent agent = new AIProjectClient(
        new Uri("https://ai-foundry-<resource>.services.ai.azure.com/api/projects/ai-project-<project>"),
        new DefaultAzureCredential())
    .AsAIAgent(
        model: "gpt-5.4-mini",
        instructions: "You are a helpful research assistant. Use available tools.",
        name: "FoundryToolboxAgent");

// Foundry-managed tools (e.g., Bing search) are invoked server-side automatically
Console.WriteLine(await agent.RunAsync("Search for the latest news on AI agent frameworks."));
```

> **Important:** Server-side Foundry Toolbox tools are executed by the Foundry service. They do NOT appear as `FunctionCallContent` / `FunctionResultContent` in the agent response. Client-side compaction strategies (`CompactionProvider`) have **no effect** on server-side tool execution — the Foundry service manages its own context.

> **Compatibility:** This feature requires `Microsoft.Agents.AI.Foundry 1.3.0` and an Azure AI Foundry project with a configured Toolbox. Local `ChatClientAgent` instances (OpenAI, Azure OpenAI direct) do not support Foundry Toolboxes.

---

## 18. Session Serialization & Replay

### Serialize for Persistence

```csharp
AgentSession session = await agent.CreateSessionAsync();
await agent.RunAsync("My name is Alice.", session);

// Serialize to JsonElement (for database/file storage)
JsonElement serialized = await agent.SerializeSessionAsync(session);

// Store serialized payload in durable storage...
```

### Restore and Continue

```csharp
// Later — restore from serialized JsonElement
AgentSession restored = await agent.DeserializeSessionAsync(serialized);

// Continue the conversation
var response = await agent.RunAsync("What is my name?", restored);
Console.WriteLine(response.Text);  // "Your name is Alice."
```

> **Important:** Treat `AgentSession` as an opaque state object. Restore it with the same agent/provider configuration that created it.

---

## 19. API Patterns Summary

> The tables below summarize the correct 1.3.0 API patterns. Each row links to the detailed section above. Use these as a quick cross-reference, not a migration diff.

### Core Agent Patterns

| Area | 1.3.0 Pattern | Section |
|------|---------------|---------|
| **Agent creation** | `.AsAIAgent(name:, instructions:, tools:)` extension on SDK clients | [§3](#3-creating-agents--the-asaiagent-pattern) |
| **Agent creation (explicit)** | `new ChatClientAgent(chatClient, new ChatClientAgentOptions { ChatOptions = new() { Instructions = "...", Tools = [...] } })` | [§3](#3-creating-agents--the-asaiagent-pattern) |
| **Agent options** | `ChatClientAgentOptions` with nested `ChatOptions` for instructions and tools | [§3](#3-creating-agents--the-asaiagent-pattern) |
| **Running agents** | `agent.RunAsync(input, session?, options?, ct)` → `AgentResponse` | [§4](#4-running-agents--runasync--runstreamingasync) |
| **Streaming** | `agent.RunStreamingAsync(input, session?, options?, ct)` → `IAsyncEnumerable<AgentResponseUpdate>` | [§4](#4-running-agents--runasync--runstreamingasync) |
| **Function tools** | `AIFunctionFactory.Create(method)` + pass in `tools:` or `ChatOptions.Tools` | [§5](#5-function-tools) |
| **Agent-as-tool** | `specialistAgent.AsAIFunction()` → pass as tool to orchestrator | [§6](#6-agent-as-a-tool-composition) |
| **Sessions** | `await agent.CreateSessionAsync()` → `AgentSession` | [§7](#7-sessions--multi-turn-conversations) |
| **Session persistence** | `await agent.SerializeSessionAsync(session)` → `JsonElement` | [§18](#18-session-serialization--replay) |
| **Session restore** | `await agent.DeserializeSessionAsync(jsonElement)` → `AgentSession` | [§18](#18-session-serialization--replay) |

### Memory & Context Patterns

| Area | 1.3.0 Pattern | Section |
|------|---------------|---------|
| **Context providers** | Subclass `AIContextProvider`, override `ProvideAIContextAsync` + `StoreAIContextAsync` | [§8](#8-context-providers--agent-memory-aicontextprovider) |
| **Provider state** | `ProviderSessionState<T>` — never store session state in instance fields | [§8](#8-context-providers--agent-memory-aicontextprovider) |
| **Chat history** | `ChatHistoryProvider` or built-in `InMemoryChatHistoryProvider` | [§9](#9-chat-history-management-chathistoryprovider) |
| **Compaction** | `CompactionProvider` + `PipelineCompactionStrategy` (experimental, `#pragma warning disable MAAI001`) | [§17.5](#175-compaction-strategies-experimental) |

### Middleware & Pipeline Patterns

| Area | 1.3.0 Pattern | Section |
|------|---------------|---------|
| **Agent middleware** | `agent.AsBuilder().Use(runFunc:, runStreamingFunc:).Build()` | [§11](#11-middleware) |
| **Function middleware** | `agent.AsBuilder().Use(functionMiddleware).Build()` | [§11](#11-middleware) |
| **IChatClient middleware** | `chatClient.AsBuilder().Use(getResponseFunc:, getStreamingResponseFunc:).Build()` | [§11](#11-middleware) |
| **Observability** | `.AsBuilder().UseOpenTelemetry(sourceName:).Build()` on both `IChatClient` and `AIAgent` | [§17.8](#178-observability--opentelemetry) |

### Workflow Patterns

| Area | 1.3.0 Pattern | Section |
|------|---------------|---------|
| **Executors** | `partial class : Executor` + `[MessageHandler]` (source-generated) | [§13](#13-source-generated-executors-messagehandler) |
| **Agent in workflow** | `agent.BindAsExecutor(emitEvents: true)` | [§14](#14-agents-in-workflows) |
| **Workflow execution** | `InProcessExecution.RunStreamingAsync(workflow, input)` → `StreamingRun` | [§15](#15-workflow-execution--events) |

### Output & Safety Patterns

| Area | 1.3.0 Pattern | Section |
|------|---------------|---------|
| **Structured output** | `agent.RunAsync<T>(input)` → `AgentResponse<T>` | [§17.6](#176-structured-output) |
| **Tool approval** | `new ApprovalRequiredAIFunction(wrappedFunction)` | [§17.7](#177-tool-approval--human-in-the-loop) |
| **Multimodal** | `DataContent` / `UriContent` in `ChatMessage` content list | [§17.9](#179-multimodal--images) |

### A2A Protocol Patterns

| Area | 1.3.0 Pattern | Section |
|------|---------------|---------|
| **A2A client type** | `IA2AClient` interface via `GetService(typeof(IA2AClient))` | [§17.10](#1710-a2a-agent--remote-protocol-proxies) |
| **A2A client creation** | `A2AClientFactory.Create()` | [§17.10](#1710-a2a-agent--remote-protocol-proxies) |
| **A2A hosting DI** | `services.AddA2AServer(agent, new A2AServerRegistrationOptions { ... })` | [§17.10](#1710-a2a-agent--remote-protocol-proxies) |
| **A2A endpoint mapping** | `app.MapA2AHttpJson(path)` or `app.MapA2AJsonRpc(path)` | [§17.10](#1710-a2a-agent--remote-protocol-proxies) |
| **A2A stream reconnection** | Automatic via continuation tokens — no manual handling | [§17.10](#1710-a2a-agent--remote-protocol-proxies) |

### New in 1.3.0

| Area | 1.3.0 Pattern | Section |
|------|---------------|---------|
| **Dynamic tool expansion** | `FunctionInvokingChatClient.CurrentContext.Options.Tools.Add(tool)` (per-turn only) | [§17.11](#1711-dynamic-tool-expansion-new-in-130) |
| **Foundry Toolbox** | Server-side tools via `Microsoft.Agents.AI.Foundry` — no local function implementations | [§17.12](#1712-server-side-foundry-toolbox-new-in-130) |

### Package Version Requirements

| Package | Version |
|---------|---------|
| `Microsoft.Agents.AI` | 1.3.0 |
| `Microsoft.Agents.AI.Abstractions` | 1.3.0 (transitive) |
| `Microsoft.Agents.AI.OpenAI` | 1.3.0 |
| `Microsoft.Agents.AI.Workflows` | 1.3.0 |
| `Microsoft.Agents.AI.Workflows.Generators` | 1.3.0 |
| `Microsoft.Agents.AI.Foundry` | 1.3.0 |
| `Microsoft.Agents.AI.A2A` | 1.3.0 (if using A2A) |
| `Microsoft.Agents.AI.Hosting.A2A` | 1.3.0 (if using A2A hosting) |
| `Microsoft.Agents.AI.Hosting.A2A.AspNetCore` | 1.3.0 (if using A2A hosting) |
| `Microsoft.Extensions.AI` | ≥ 10.5.0 |
| `Microsoft.Extensions.AI.OpenAI` | ≥ 10.5.0 |

---

## 20. Migration Checklist

Use this checklist when upgrading your project:

### Phase 1: Package Updates
- [ ] Update all `Microsoft.Agents.AI*` packages to `1.3.0`
- [ ] Update `Microsoft.Extensions.AI*` packages to `≥ 10.5.0`
- [ ] Pin `System.Numerics.Tensors` to `≥ 10.0.6` if transitive conflicts arise (`NU1605` downgrade error; MEAI `10.5.0` requires this minimum)
- [ ] **Sweep BOTH `<PackageVersion ... Version="...">` (CPM-managed) AND `<PackageReference ... Version="...">` (CPM-disabled) declarations.** Solutions often have one or two CPM-opt-out projects (NuGet consumer demos, isolated samples) that pin versions explicitly — a `Directory.Packages.props`-only update will silently miss them.
- [ ] Run `dotnet restore --force && dotnet build`
- [ ] Run full test suite

### Phase 2: Agent Creation
- [ ] Adopt `.AsAIAgent()` extensions where possible
- [ ] Set `Instructions` and `Tools` inside `ChatClientAgentOptions.ChatOptions` where `ChatClientAgent` is used directly
- [ ] Use the `tools: [...]` parameter for providing tools
- [ ] Use `.AsAIAgent(name:, instructions:, tools:)` extensions on SDK clients

### Phase 2.5: A2A Agent — SDK v1 (only if using A2A)
- [ ] Update `Microsoft.Agents.AI.A2A` / `.Hosting.A2A` / `.Hosting.A2A.AspNetCore` to `1.3.0`
- [ ] Remove `AIAgentExtensions` DI registration; replace with `services.AddA2AServer(agent, new A2AServerRegistrationOptions { AgentCard = ... })`
- [ ] Replace `app.MapA2A(...)` with `app.MapA2AHttpJson(path)` or `app.MapA2AJsonRpc(path)` (or both)
- [ ] Use `GetService(typeof(IA2AClient))` to resolve the A2A client (concrete `A2AClient` type is not resolvable)
- [ ] Replace direct `new A2AClient(...)` construction with `A2AClientFactory.Create(...)` 
- [ ] Update `A2AAgentCardExtensions` and `A2ACardResolverExtensions` calls to accept optional `A2AClientOptions` (for protocol binding preference)
- [ ] Verify SSE stream reconnection works — it is now automatic via continuation tokens
- [ ] Verify streaming agent responses from `A2AAgentHandler` work end-to-end
- [ ] Update any sample path references from `samples/04-hosting/A2A/` → `samples/02-agents/A2A/`

### Phase 3: Sessions
- [ ] Use `AgentSession` via `await agent.CreateSessionAsync()`
- [ ] Pass sessions to `RunAsync(input, session)` for multi-turn
- [ ] Implement `SerializeSessionAsync`/`DeserializeSessionAsync` where persistence is needed

### Phase 4: Memory & Context
- [ ] Use `ChatHistoryProvider` (or `InMemoryChatHistoryProvider`) for conversation history
- [ ] Use `AIContextProvider` for memory, RAG, and dynamic context injection
- [ ] Implement `ProvideAIContextAsync` (before-LLM) and `StoreAIContextAsync` (after-LLM) — do NOT store session-specific data in instance fields
- [ ] Use `ProviderSessionState<T>` for all per-session provider state (both `AIContextProvider` and `ChatHistoryProvider`)
- [ ] If using `InvokingCoreAsync`/`InvokedCoreAsync` overrides, stamp messages with `WithAgentRequestMessageSource` and filter by `GetAgentRequestMessageSourceType()` to avoid feedback loops
- [ ] Verify that long-term memory survives session resets (as designed — `AIContextProvider` persists, `ChatHistoryProvider` resets)

### Phase 5: Middleware
- [ ] Use agent middleware (`.AsBuilder().Use(...)`) for cross-cutting concerns
- [ ] Use function calling middleware for tool call interception
- [ ] Provide both streaming and non-streaming middleware callbacks

### Phase 5.5: Compaction (Experimental)
- [ ] Evaluate whether long conversations exceed token limits
- [ ] If yes, add `#pragma warning disable MAAI001` and configure compaction strategies
- [ ] Register `CompactionProvider` via `chatClient.AsBuilder().UseAIContextProviders(...).BuildAIAgent(...)`
- [ ] Consider using a smaller model for `SummarizationCompactionStrategy`

### Phase 6: Workflows
- [ ] Use `[MessageHandler]` + `partial class : Executor` (source-generated) for executors
- [ ] Add `Microsoft.Agents.AI.Workflows.Generators 1.3.0` package reference
- [ ] Ensure all executor classes are `partial` and `sealed`
- [ ] **Remove `[StreamsMessage]` and `[YieldsMessage]` attributes** — both were removed in 1.3.0 (build error `CS0246` if still present); replace with `ValueTask<T>` return types or explicit `context.YieldOutputAsync()` calls
- [ ] Executor `[MessageHandler]` methods: void / `ValueTask` (fire-and-forget), `ValueTask<T>` (auto-send result), or use `context.SendMessageAsync`/`context.YieldOutputAsync` explicitly
- [ ] Use `.BindAsExecutor(emitEvents: true)` for agents in workflows
- [ ] Update event processing for `AgentResponseEvent`

### Phase 7: Response Processing
- [ ] Update response handling to use `AgentResponse.Text` and `.Messages`
- [ ] Update streaming to use `AgentResponseUpdate.Text` and `.Contents`
- [ ] Process `FunctionCallContent`/`FunctionResultContent` for tool tracking
- [ ] Check `UsageContent` for token usage in streaming responses

### Phase 7.5: Structured Output & Type Safety
- [ ] Replace manual JSON parsing with `RunAsync<T>()` where applicable
- [ ] Set `ResponseFormat = ChatResponseFormat.ForJsonSchema<T>()` for inter-agent structured output
- [ ] Use `ToAgentResponseAsync()` for deserializing streaming structured output

### Phase 7.6: Tool Approval
- [ ] Wrap sensitive tools with `ApprovalRequiredAIFunction`
- [ ] Handle `FunctionApprovalRequestContent` in response processing loops
- [ ] Implement user approval UI / confirmation flow

### Phase 7.7: Observability
- [ ] Add `UseOpenTelemetry()` to chat client builder
- [ ] Wrap agent with `agent.AsBuilder().UseOpenTelemetry(sourceName, cfg).Build()`
- [ ] Configure OpenTelemetry exporters (OTLP, Azure Monitor, or Aspire Dashboard)
- [ ] Only enable `EnableSensitiveData` in non-production environments

### Phase 7.8: Dynamic Tool Expansion (optional, new in 1.3.0)
- [ ] Evaluate whether progressive tool loading would reduce per-request token overhead
- [ ] If yes, add a `RequestTools` gatekeeper function using `FunctionInvokingChatClient.CurrentContext.Options.Tools`
- [ ] Remember: `CurrentContext` tool additions are **per-turn only** — implement `DynamicToolContextProvider` (`AIContextProvider`) to persist loaded tools across session turns

### Phase 7.9: Server-Side Foundry Toolbox (optional, new in 1.3.0)
- [ ] If using Azure AI Foundry toolboxes, update to `Microsoft.Agents.AI.Foundry 1.3.0`
- [ ] Configure toolboxes in the Azure AI Foundry portal; agent code requires no local tool function implementations
- [ ] Note that client-side `CompactionProvider` has no effect on server-side Foundry Toolbox tool execution

### Phase 8: Verification
- [ ] Full build: `dotnet build`
- [ ] Full test suite: `dotnet test`
- [ ] Verify multi-turn conversations work with sessions
- [ ] Verify tool calls execute correctly
- [ ] Verify streaming output matches non-streaming
- [ ] Verify workflows execute in correct order
- [ ] **If shipping a NuGet package:** Run `dotnet pack -c Release` and grep `obj/Release/*.nuspec` for the new `<version>` and `<releaseNotes>` to confirm metadata propagated from the `.csproj` to the nupkg manifest. Stale release notes are a common silent regression.
- [ ] **If maintaining a `CHANGELOG.md` with comparison links** (Keep-a-Changelog format): update the bottom `[Unreleased]: ...compare/<old>...HEAD` link to point from the new tag, and add a new `[<version>]: ...compare/<previous>...<new>` reference. Easy to miss and breaks GitHub's diff links once the tag exists.

---

## Quick Reference Card

```csharp
// ═══════════════════════════════════════════════════════
// MAF 1.3.0 — Patterns at a Glance
// ═══════════════════════════════════════════════════════

// --- Create an agent ---
AIAgent agent = chatClient.AsAIAgent(
    name: "MyAgent",
    instructions: "You are helpful.",
    tools: [AIFunctionFactory.Create(MyTool)]);

// --- Run (non-streaming) ---
Console.WriteLine(await agent.RunAsync("Hello!"));

// --- Run (streaming) ---
await foreach (var update in agent.RunStreamingAsync("Hello!"))
    Console.Write(update.Text);

// --- Multi-turn with session ---
var session = await agent.CreateSessionAsync();
await agent.RunAsync("I'm Alice.", session);
await agent.RunAsync("What's my name?", session);

// --- Workflow ---
var a = new StepA();
var b = new StepB();
var workflow = new WorkflowBuilder(a).AddEdge(a, b).Build();
var run = await InProcessExecution.RunStreamingAsync(workflow, "input");
await foreach (var evt in run.WatchStreamAsync()) { /* process events */ }

// --- Middleware ---
var enhanced = agent.AsBuilder()
    .Use(runFunc: MyMiddleware, runStreamingFunc: MyStreamMiddleware)
    .Build();

// --- Memory (AIContextProvider) ---
var agentWithMemory = chatClient.AsAIAgent(new ChatClientAgentOptions
{
    ChatOptions = new() { Instructions = "..." },
    AIContextProviders = [new MyMemoryProvider()],
});

// --- Structured output ---
AgentResponse<MyType> typed = await agent.RunAsync<MyType>("query");
Console.WriteLine(typed.Result.Property);

// --- Tool approval ---
var safe = new ApprovalRequiredAIFunction(AIFunctionFactory.Create(DangerousTool));

// --- Observability ---
var traced = agent.AsBuilder().UseOpenTelemetry(sourceName: "MyApp").Build();

// --- Serialize session ---
JsonElement state = await agent.SerializeSessionAsync(session);
AgentSession restored = await agent.DeserializeSessionAsync(state);

// --- A2A remote agent ---
// Server DI: services.AddA2AServer(agent, options); app.MapA2AHttpJson(path);
// Client:  IA2AClient client = (IA2AClient)a2aAgent.GetService(typeof(IA2AClient));

// --- Dynamic tool expansion ---
// Use FunctionInvokingChatClient.CurrentContext.Options.Tools.Add(tool)
// inside a RequestTools gatekeeper function.
```

---

## Further Reading

- [Official MAF Documentation](https://learn.microsoft.com/en-us/agent-framework/)
- [MAF 1.3.0 Release Notes](https://github.com/microsoft/agent-framework/releases/tag/dotnet-1.3.0)
- [Changelog 1.2.0 → 1.3.0](https://github.com/microsoft/agent-framework/compare/dotnet-1.2.0...dotnet-1.3.0)
- [Agents overview](https://learn.microsoft.com/en-us/agent-framework/agents/) — creation, options, SDK extensions
- [Running Agents](https://learn.microsoft.com/en-us/agent-framework/agents/running-agents) — RunAsync, streaming, response types
- [Agent Pipeline](https://learn.microsoft.com/en-us/agent-framework/agents/agent-pipeline) — layered architecture, execution flow
- [Tools / Function Tools](https://learn.microsoft.com/en-us/agent-framework/agents/tools/) — tool types, function tools
- [Tool Approval](https://learn.microsoft.com/en-us/agent-framework/agents/tools/tool-approval) — human-in-the-loop function approvals
- [Sessions](https://learn.microsoft.com/en-us/agent-framework/agents/conversations/session) — sessions, StateBag, serialization
- [Context Providers](https://learn.microsoft.com/en-us/agent-framework/agents/conversations/context-providers) — AIContextProvider, ProviderSessionState
- [Chat History Storage](https://learn.microsoft.com/en-us/agent-framework/agents/conversations/storage) — ChatHistoryProvider, InMemoryChatHistoryProvider, service-managed storage
- [Compaction](https://learn.microsoft.com/en-us/agent-framework/agents/conversations/compaction) — compaction strategies (experimental)
- [Middleware](https://learn.microsoft.com/en-us/agent-framework/agents/middleware/) — agent, streaming, function calling, IChatClient
- [Structured Output](https://learn.microsoft.com/en-us/agent-framework/agents/structured-output) — RunAsync\<T\>, ResponseFormat, JSON schemas
- [Multimodal](https://learn.microsoft.com/en-us/agent-framework/agents/multimodal) — images, vision
- [Observability](https://learn.microsoft.com/en-us/agent-framework/agents/observability) — OpenTelemetry, traces, metrics, logs
- [Background Responses](https://learn.microsoft.com/en-us/agent-framework/agents/background-responses) — long-running operations, continuation tokens
- [Workflows](https://learn.microsoft.com/en-us/agent-framework/workflows/workflows) — multi-agent orchestration
- [Executors](https://learn.microsoft.com/en-us/agent-framework/workflows/executors) — MessageHandler, partial classes, IResettableExecutor
- [Integrations](https://learn.microsoft.com/en-us/agent-framework/integrations/) — A2A, AG-UI, Azure Functions, M365, pre-built context providers
- [Your First Agent Tutorial](https://learn.microsoft.com/en-us/agent-framework/get-started/your-first-agent)
- [MAF GitHub Repository](https://github.com/microsoft/agent-framework)
- [.NET Samples](https://github.com/microsoft/agent-framework/tree/main/dotnet/samples)
- [Agent_Step20_DynamicFunctionTools sample](https://github.com/microsoft/agent-framework/tree/main/dotnet/samples/02-agents/Agents/Agent_Step20_DynamicFunctionTools) — new in 1.3.0
- [A2A Samples](https://github.com/microsoft/agent-framework/tree/main/dotnet/samples/02-agents/A2A) — updated for A2A SDK v1 in 1.3.0
- [MAF Discord Community](https://discord.gg/b5zjErwbQM)

---

## Misalignments — Document vs. Code Reality

> **Last audited:** April 2026 against `MAFVnext/dotnet/src/` source code, official samples, and MAF 1.3.0 release.

The following items represent known discrepancies between this guide (or official documentation) and the actual MAFVnext code. These are documented here for transparency and to assist future corrections.

### 1. `SerializeSessionAsync` (Async) vs `SerializeSession` (Sync) — Still Unresolved

| Item | This Guide (Corrected) | Official Sessions Docs | Official Storage Docs | MAFVnext Code |
|------|------------------------|------------------------|----------------------|---------------|
| Method | `await agent.SerializeSessionAsync(session)` | `agent.SerializeSession(session)` (sync) | `agent.SerializeSession(session)` (sync) | `SerializeSessionAsync` returning `ValueTask<JsonElement>` |

The [official Sessions page](https://learn.microsoft.com/en-us/agent-framework/agents/conversations/session?pivots=programming-language-csharp) and [Storage page](https://learn.microsoft.com/en-us/agent-framework/agents/conversations/storage) both show `agent.SerializeSession(session)` as a synchronous call without `await`. The actual `AIAgent` base class defines `SerializeSessionAsync(AgentSession, JsonSerializerOptions?, CancellationToken)` returning `ValueTask<JsonElement>`. All MAFVnext samples use the async form with `await`. There may be a sync overload, but **this guide uses the correct async form** which works in all confirmed environments.

### 2. `FunctionApprovalRequestContent` vs `ToolApprovalRequestContent`

The official [Tool Approval docs](https://learn.microsoft.com/en-us/agent-framework/agents/tools/tool-approval?pivots=programming-language-csharp) reference `FunctionApprovalRequestContent`. Some MAFVnext samples reference `ToolApprovalRequestContent`. These may be renamed between versions. Check your actual package version's API to determine the correct type name.

### 3. `ChatClientAgent` Constructor — `instructions:` as Named Parameter

The [official Agents page](https://learn.microsoft.com/en-us/agent-framework/agents/?pivots=programming-language-csharp) shows:
```csharp
var agent = new ChatClientAgent(chatClient, instructions: "You are a helpful assistant");
```
The MAFVnext code confirms this signature exists as a simple constructor with optional named parameters (`instructions`, `name`, `description`, `tools`, `loggerFactory`, `services`). This is **aligned**.

### 4. Anthropic SDK Example — Swapped Parameters (Official Docs Bug)

The [official Agents page](https://learn.microsoft.com/en-us/agent-framework/agents/?pivots=programming-language-csharp#using-the-anthropic-sdk) shows:
```csharp
agent = client.AsAIAgent(model: deploymentName, instructions: "Joker", name: "You are good at telling jokes.");
```
This appears to have `instructions` and `name` swapped — `"Joker"` should be the name and `"You are good at telling jokes."` should be the instructions. **This guide does not replicate this apparent documentation bug.**

### 5. Missing from Official Docs: `ChatClientAgentOptions` — Additional Properties

The following `ChatClientAgentOptions` properties (verified via `dotnet-inspect` against `Microsoft.Agents.AI 1.3.0`) are not documented on the official site:
- `UseProvidedChatClientAsIs` — disables default `FunctionInvokingChatClient` wrapping
- `RequirePerServiceCallChatHistoryPersistence` — enables per-service-call chat history persistence (useful for checkpointing intermediate tool calls during long function-calling loops, demonstrated in sample `Agent_Step19_InFunctionLoopCheckpointing`)
- `ClearOnChatHistoryProviderConflict` — clears `ChatHistoryProvider` if the AI service manages its own chat history
- `ThrowOnChatHistoryProviderConflict` — throws if the AI service manages its own chat history but a `ChatHistoryProvider` is configured
- `WarnOnChatHistoryProviderConflict` — logs a warning if the AI service manages its own chat history but a `ChatHistoryProvider` is configured

### 6. Missing from Official Docs: Declarative Agents (YAML)

MAFVnext includes a `ChatClientPromptAgentFactory` that creates agents from YAML definitions (sample `Agent_Step16_Declarative`). This is not yet documented on the official docs site.

### 7. Missing from Official Docs: `MessageAIContextProvider`

The [Agent Pipeline page](https://learn.microsoft.com/en-us/agent-framework/agents/agent-pipeline?pivots=programming-language-csharp) mentions `MessageAIContextProvider` as agent middleware for injecting messages, but has no dedicated documentation page. MAFVnext samples use it as a simpler alternative to `AIContextProvider` when you only need to inject system messages without per-invocation state logic.

### 8. `AgentResponse` Additional Properties Not in Official Docs

The `AgentResponse` class (verified via `dotnet-inspect` against `Microsoft.Agents.AI.Abstractions 1.3.0`) includes properties not mentioned in official documentation:
- `AgentId` — identifier of the agent that generated this response
- `ResponseId` — unique identifier for this specific response
- `ContinuationToken` — for background response polling (`ResponseContinuationToken?`)
- `FinishReason` — `ChatFinishReason?` (stop, length, content_filter, tool_calls)
- `Usage` — token count details (`UsageDetails?`)
- `CreatedAt` — response timestamp (`DateTimeOffset?`)
- `RawRepresentation` — raw run response from underlying implementation (`object?`)
- `AdditionalProperties` — provider-specific metadata (`AdditionalPropertiesDictionary?`)

### 9. Workflow Patterns — Richer Than Documented

MAFVnext samples demonstrate workflow patterns not covered in official docs:
- **Fan-out/Fan-in** (`.AddFanOut()` / `.AddFanIn()`)
- **Switch/conditional routing** (`.AddSwitch()` with `.AddCase<T>()`)
- **Shared state** (`context.QueueStateUpdateAsync()` / `context.ReadStateAsync<T>()`)
- **Group chat** (`AgentWorkflowBuilder.CreateGroupChatBuilderWith()`)
- **Workflow-as-agent** composition pattern
- **Resettable executors** (`IResettableExecutor`) — required for stateful executors shared across workflow runs

### 10. A2A `GetService(typeof(A2AClient))` Returns Null

`A2AAgent.GetService(typeof(A2AClient))` returns `null`. The client is exposed only as `IA2AClient`. Callers resolving the concrete type will get null. Use `GetService(typeof(IA2AClient))` instead. See PR [#5423](https://github.com/microsoft/agent-framework/pull/5423).

### 11. Dynamic Tool Expansion via `CurrentContext` — Per-Turn Only

The `FunctionInvokingChatClient.CurrentContext` approach for dynamic tool expansion only persists tools for the **current function-calling loop turn**. Tools added this way are NOT retained in subsequent `RunAsync` calls. This is expected behavior — `CurrentContext` is designed to be transient. For cross-turn persistence, combine with `AIContextProvider` session state as shown in section 17.11.
