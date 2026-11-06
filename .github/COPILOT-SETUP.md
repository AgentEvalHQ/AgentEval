# GitHub Copilot Configuration

This folder contains AI agent instructions for GitHub Copilot in VS Code.

## 📁 Structure

```
.github/
├── copilot-instructions.md    # Global instructions (always active)
├── agents/                    # Custom Copilot agents
│   ├── agenteval-dev.agent.md        # Development & implementation
│   ├── agenteval-planner.agent.md    # Feature planning
│   ├── agenteval-docwriter.agent.md  # Documentation
│   ├── agenteval-samples.agent.md    # Samples & demos
│   └── agenteval-m365.agent.md       # Evaluating Microsoft 365 agents (Work IQ A2A)
├── skills/                    # Procedural skills the agents load
│   ├── dotnet-inspect/SKILL.md       # Query .NET APIs across NuGet packages
│   └── m365-eval/SKILL.md            # Evaluate a Microsoft 365 agent: doctor → benchmark → evidence
├── instructions/              # File-scoped instructions (auto-apply)
│   ├── testing.instructions.md     # → tests/**/*.cs
│   ├── metrics.instructions.md     # → src/AgentEval/Metrics/**/*.cs
│   ├── assertions.instructions.md  # → src/AgentEval/Assertions/**/*.cs
│   ├── samples.instructions.md     # → samples/**/*.cs
│   ├── tracing.instructions.md     # → src/AgentEval/Tracing/**/*.cs
│   └── documentation.instructions.md # → **/*.md
└── COPILOT-SETUP.md          # This file
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

| Agent | Use it for |
|---|---|
| `@agenteval-planner` (AgentEval Planner) | Planning a feature without changing code |
| `@agenteval-dev` (AgentEval Dev) | Implementing, reviewing and debugging code |
| `@agenteval-docwriter` (AgentEval DocWriter) | Writing and reviewing documentation |
| `@agenteval-samples` (AgentEval Samples) | Reviewing and improving samples and demos |
| `@agenteval-m365` (AgentEval M365) | Evaluating a Microsoft 365 agent over Work IQ A2A with `agenteval m365 doctor` and `--sut microsoft365`. It follows the `m365-eval` skill. |

```
@agenteval-m365 Check that we can reach our test tenant, then write a smoke benchmark for our HR Copilot
```

#### **Skills**

`.github/skills/<name>/SKILL.md` holds a step-by-step procedure (frontmatter `name`, `version`, `description`) that an agent loads
when a task matches its description:

- `dotnet-inspect`: query .NET library APIs (types, members, diffs) instead of guessing them.
- `m365-eval`: evaluate a Microsoft 365 agent, from the config file to the evidence. It also holds the rules for live
  steps: a non-production identity, the user's consent, and a yes before a judge spends.

#### **A sample with its own Copilot setup: the Microsoft 365 demo**

`samples/AgentEval.Microsoft365Demo/.github/` is a second Copilot setup, for an external tester who opens only that
folder in VS Code. Copilot loads a folder's `.github/` only when that folder is the workspace root. So these files are
not loaded when the repository root is open.

```
samples/AgentEval.Microsoft365Demo/.github/
├── copilot-instructions.md                  # the demo's rules (always loaded)
├── agents/m365-demo.agent.md                # the m365-demo agent: routes to the three skills
├── skills/
│   ├── m365-demo-setup/SKILL.md             # from zero to a first zipped run
│   ├── m365-demo-customize/SKILL.md         # another agent, prompt, cases, a check, the profile or the limits
│   └── m365-demo-triage/SKILL.md            # read a run folder: where, why, who can fix it
├── instructions/m365-demo.instructions.md   # code rules for **/*.cs
└── prompts/                                 # /run-demo, /triage-run, /add-case
```

When the demo's options, run folder or exit codes change, update its README and these files in the same change.

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

### **To add a new skill:**
1. Create `.github/skills/{name}/SKILL.md`
2. Start it with frontmatter `name`, `version` and `description` (when to use it), then a decision tree and exact commands

---

## 🔗 Related Documentation

- [AGENTS.md](../AGENTS.md) - Root-level quick reference for AI agents
- [docs/architecture.md](../docs/architecture.md) - Codebase architecture
- [docs/adr/](../docs/adr/) - Architecture Decision Records
- [CONTRIBUTING.md](../CONTRIBUTING.md) - Contribution guidelines
