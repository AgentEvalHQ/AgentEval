![AgentEval CLI](https://raw.githubusercontent.com/AgentEvalHQ/AgentEval/main/assets/AgentEvalCli.png)

# AgentEval CLI

[![NuGet](https://img.shields.io/nuget/vpre/AgentEval.Cli.svg)](https://www.nuget.org/packages/AgentEval.Cli)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://github.com/AgentEvalHQ/AgentEval/blob/main/LICENSE)
![MAF 1.11.1](https://img.shields.io/badge/MAF-1.11.1-blueviolet)
![.NET 8.0 | 10.0](https://img.shields.io/badge/.NET-8.0%20|%2010.0-512BD4)

Command-line interface for [AgentEval](https://github.com/AgentEvalHQ/AgentEval) — the comprehensive
.NET evaluation toolkit for AI agents. Evaluate any OpenAI-compatible agent, run compliance and
security benchmark suites, manage the canonical `.agenteval/` output store, and serve the Mission
Control web portal — all from the terminal or your CI/CD pipeline.

## Installation

```bash
dotnet tool install --global AgentEval.Cli --prerelease
```

### Compatibility

`AgentEval.Cli` ships in lockstep with the `AgentEval` library — both are released together at the
same version from one pipeline.

| Version     | MAF    | .NET      |
|-------------|--------|-----------|
| 0.13.1-beta | 1.11.1 | 8.0, 10.0 |

> The tool multi-targets `net8.0` and `net10.0`; the dotnet-tool installer picks the highest
> compatible runtime. The `mc serve` portal requires .NET 10.

## Quick Start

### Scaffold a test dataset

```bash
agenteval init
agenteval init -o my-tests.yaml
agenteval init --format json
```

### Run evaluations

```bash
# Against Azure OpenAI
agenteval eval --azure --endpoint https://myresource.openai.azure.com/ --deployment-name gpt-4o --dataset agenteval.yaml

# Against OpenAI directly
agenteval eval --endpoint https://api.openai.com/v1 --model gpt-4o --dataset agenteval.yaml

# Against a local Ollama model
agenteval eval --endpoint http://localhost:11434/v1 --model llama3 --dataset agenteval.yaml
```

### Stochastic evaluation (multi-run)

```bash
agenteval eval --azure --endpoint https://myresource.openai.azure.com/ --deployment-name gpt-4o --dataset agenteval.yaml --runs 5 --success-threshold 0.9
```

### Run benchmark families

```bash
# List every registered family with its presets and cost tiers
agenteval bench --list

# Compliance: GDPR / EU AI Act (audit-chain evidence written to .agenteval/)
agenteval bench gdpr --subject my-agent --preset standard --response-file answer.txt
agenteval bench eu-ai-act --subject my-agent --input "..." --response-file answer.txt

# Security: OWASP LLM Top 10 / MITRE ATLAS / NIST AI RMF
agenteval bench owasp --subject my-agent
agenteval bench mitre --subject my-agent
agenteval bench nist  --subject my-agent

# Quality & performance
agenteval bench agentic --subject my-agent
agenteval bench perf latency --subject my-agent --azure-from-env
agenteval bench longmemeval --subject my-agent --preset subset
agenteval bench memory --subject my-agent --preset quick
```

### Red team security scanning

```bash
# Run all attack types
agenteval redteam --azure --endpoint https://myresource.openai.azure.com/ --deployment-name gpt-4o --intensity moderate

# Run specific attacks, export SARIF for CI
agenteval redteam --azure --endpoint https://myresource.openai.azure.com/ --deployment-name gpt-4o --attacks PromptInjection,Jailbreak --format sarif
```

### Manage the workspace

```bash
agenteval init-workspace --name "My Solution"   # bootstrap .agenteval/
agenteval doctor                                # validate structure + content hashes
agenteval migrate                               # dry-run legacy → canonical layout
agenteval migrate --apply                       # apply the migration
```

### Render reports (no LLM cost)

```bash
# PDF from existing compliance evidence
agenteval compliance render --regulation gdpr --subject my-agent

# Markdown from existing agentic benchmark results
agenteval render --benchmark agentic --subject my-agent
```

### Mission Control web portal (requires .NET 10)

```bash
agenteval mc serve --workspace .            # GraphQL + REST + SPA on one port (default 5000)
agenteval mc doctor                         # verify the portal bundle is intact
```

### List metrics, attacks, exporters, and datasets

```bash
agenteval list
agenteval list --type metrics
agenteval list --type attacks
```

## Authentication

The `eval` and `redteam` commands support two endpoint modes: **Azure OpenAI** (`--azure`) and
**OpenAI-compatible** (`--endpoint`).

### Azure OpenAI (`--azure`)

The `--azure` flag uses `AzureOpenAIClient`. Both `--endpoint` and `--deployment-name` are **required**:

| Setting    | Flag                          | Env var fallback        |
|------------|-------------------------------|-------------------------|
| Endpoint   | `--endpoint` *(required)*     | `AZURE_OPENAI_ENDPOINT` |
| Deployment | `--deployment-name` *(required)* | `AZURE_OPENAI_DEPLOYMENT` |
| API Key    | `--api-key`                   | `AZURE_OPENAI_API_KEY`  |

```bash
# Key from env var
export AZURE_OPENAI_API_KEY=sk-...
agenteval eval --azure --endpoint https://myresource.openai.azure.com/ --deployment-name gpt-4o --dataset agenteval.yaml
```

> **Note:** `--deployment-name` is the name you gave your model deployment in Azure AI Foundry, not the
> underlying model name.

### OpenAI-compatible (`--endpoint`)

For OpenAI, Ollama, Groq, vLLM, LM Studio, Together.ai, or any OpenAI-compatible API:

```bash
# OpenAI (set OPENAI_API_KEY or use --api-key)
agenteval eval --endpoint https://api.openai.com/v1 --model gpt-4o --dataset agenteval.yaml --api-key sk-...

# Local Ollama (no key needed)
agenteval eval --endpoint http://localhost:11434/v1 --model llama3 --dataset agenteval.yaml
```

## Commands

| Command          | Description                                                                 |
|------------------|-----------------------------------------------------------------------------|
| `init`           | Scaffold a sample evaluation dataset file                                   |
| `eval`           | Run evaluations against an AI agent endpoint                                |
| `list`           | List available metrics, attacks, exporters, and datasets                   |
| `redteam`        | Run red team security scans                                                 |
| `bench`          | Run a benchmark family (gdpr, eu-ai-act, agentic, owasp, mitre, nist, longmemeval, memory, perf) |
| `init-workspace` | Initialize the canonical `.agenteval/` workspace                            |
| `doctor`         | Validate the `.agenteval/` workspace structure and content hashes           |
| `migrate`        | Migrate legacy output paths to the canonical `.agenteval/` layout           |
| `compliance render` | Render a PDF report from existing compliance evidence (no LLM cost)      |
| `render`         | Render a Markdown report from existing benchmark results (no LLM cost)      |
| `mc serve`       | Start the Mission Control web portal (requires .NET 10)                     |

## Requirements

- .NET 8.0 or .NET 10.0 (`mc serve` requires .NET 10.0)
- An AI agent endpoint (Azure OpenAI, OpenAI, Ollama, or any OpenAI-compatible API) for `eval` / `redteam` / LLM-graded benchmarks
- Built on Microsoft Agent Framework (MAF) 1.11.1 and [Microsoft.Extensions.AI](https://github.com/dotnet/extensions)

## Documentation

- [AgentEval Documentation](https://agenteval.dev)
- [Changelog](https://github.com/AgentEvalHQ/AgentEval/blob/main/CHANGELOG.md)

## Contributing

Contributions are welcome! Please open an issue or pull request on the
[main repository](https://github.com/AgentEvalHQ/AgentEval).

## License

MIT License. See [LICENSE](https://github.com/AgentEvalHQ/AgentEval/blob/main/LICENSE) for details.
