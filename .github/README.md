# GitHub Copilot Configuration

This folder contains AI agent instructions for GitHub Copilot in VS Code.

## 📁 Structure

```
.github/
├── copilot-instructions.md    # Global instructions (always active)
├── agents/                    # Custom Copilot agents
│   ├── agenteval-dev.agent.md     # Development & implementation
│   └── agenteval-planner.agent.md # Feature planning
├── instructions/              # File-scoped instructions (auto-apply)
│   ├── testing.instructions.md     # → tests/**/*.cs
│   ├── metrics.instructions.md     # → src/AgentEval/Metrics/**/*.cs
│   ├── assertions.instructions.md  # → src/AgentEval/Assertions/**/*.cs
│   ├── samples.instructions.md     # → samples/**/*.cs
│   ├── tracing.instructions.md     # → src/AgentEval/Tracing/**/*.cs
│   └── documentation.instructions.md # → **/*.md
└── README.md                  # This file
```

---

## 🚀 How to Use

### **Default Copilot (Always Available)**

Just use GitHub Copilot normally. The `copilot-instructions.md` file is **automatically loaded** for all interactions.

**Example prompts:**
- "Add a new metric called `llm_coherence`"
- "Create tests for the ToolUsageAssertions class"
- "Explain how stochastic testing works"

### **Scoped Instructions (Automatic)**

When you open/edit files matching patterns, additional instructions load automatically:

| You're editing... | Loads... |
|------------------|----------|
| `tests/**/*.cs` | `testing.instructions.md` |
| `src/AgentEval/Metrics/**/*.cs` | `metrics.instructions.md` |
| `src/AgentEval/Assertions/**/*.cs` | `assertions.instructions.md` |
| `samples/**/*.cs` | `samples.instructions.md` |
| `src/AgentEval/Tracing/**/*.cs` | `tracing.instructions.md` |
| `**/*.md` | `documentation.instructions.md` |

### **Custom Agents (Invoke Manually)**

Invoke custom agents using `@` in Copilot Chat:

```
@agenteval-planner Plan a new metric for measuring response conciseness
```

```
@agenteval-dev Implement the FaithfulnessMetric changes
```

#### **Agent Workflows**

```
┌─────────────────────────┐
│   AgentEval Planner     │   "Plan a new feature"
│   (Research & Design)   │
└──────────┬──────────────┘
           │ Handoff: "Start Implementation"
           ▼
┌─────────────────────────┐
│   AgentEval Dev         │   "Implement the plan"
│   (Code & Test)         │
└──────────┬──────────────┘
           │ Handoff: "Run Tests"
           ▼
┌─────────────────────────┐
│   Terminal              │   "dotnet test"
└─────────────────────────┘
```

---

## 🎯 Common Workflows

### **1. Adding a New Metric**

```
Step 1: @agenteval-planner Plan a new metric for [description]
        → Generates implementation plan

Step 2: Click "Start Implementation" handoff (or manually invoke)
        @agenteval-dev Implement the plan above

Step 3: Click "Run Tests" handoff
        → Runs dotnet test, analyzes failures
```

### **2. Quick Code Changes**

No need for agents - just use Copilot normally:
```
"Fix the null reference in ToolUsageExtractor"
"Add a new assertion HaveCalledToolWithRetry"
```

The scoped instructions will automatically apply based on the files you're editing.

### **3. Feature Planning Only**

```
@agenteval-planner How should I implement multi-model comparison?
```

The Planner agent researches the codebase and generates a detailed plan **without making changes**.

---

## ⚙️ VS Code Settings

These settings in `.vscode/settings.json` enable the instruction files:

```json
{
    "github.copilot.chat.codeGeneration.useInstructionFiles": true,
    "chat.instructionsFilesLocations": [".github/instructions"]
}
```

---

## 📝 Editing Instructions

### **To modify global behavior:**
Edit `copilot-instructions.md`

### **To add new file-scoped instructions:**
1. Create `{name}.instructions.md` in `.github/instructions/`
2. Add frontmatter with `applyTo` pattern:
   ```yaml
   ---
   applyTo: "src/AgentEval/NewFolder/**/*.cs"
   description: Guidelines for new feature
   ---
   ```

### **To add new custom agents:**
1. Create `{name}.agent.md` in `.github/agents/`
2. Follow the chatagent format with tools and handoffs

---

## 🔗 Related Documentation

- [AGENTS.md](../AGENTS.md) - Root-level quick reference for AI agents
- [docs/architecture.md](../docs/architecture.md) - Codebase architecture
- [docs/adr/](../docs/adr/) - Architecture Decision Records
- [CONTRIBUTING.md](../CONTRIBUTING.md) - Contribution guidelines
