# Sample Datasets

This directory contains example datasets for use with AgentEval CLI and library.

## Formats Supported

AgentEval supports multiple dataset formats:
- **YAML** (`.yaml`, `.yml`) - Human-readable, supports comments
- **JSON** (`.json`) - Standard JSON arrays
- **JSONL** (`.jsonl`) - JSON Lines, one object per line
- **CSV** (`.csv`) - Comma-separated values

## Sample Files

### travel-agent.yaml
Agentic evaluation dataset for a travel booking agent. Demonstrates:
- Ground truth tool calls (`ground_truth` with `name` and `arguments`)
- Alternative field format (`function` + `arguments`)
- Expected tool lists
- Different input field aliases (`input`, `question`, `prompt`)
- Metadata for test categorization

### rag-qa.yaml
RAG (Retrieval-Augmented Generation) evaluation dataset. Demonstrates:
- Context documents for grounding
- Q&A format with expected answers
- Category-based organization
- Metadata for filtering

## Field Aliases

AgentEval supports flexible field naming:

| Canonical Field | Aliases |
|----------------|---------|
| `input` | `question`, `prompt`, `query` |
| `expected` | `expected_output`, `answer`, `response` |
| `context` | `contexts`, `documents` |
| `expected_tools` | `tools` |

## Root Element Aliases

YAML/JSON files can use different root elements:
- `data` - Generic data array
- `examples` - Example test cases
- `samples` - Sample test cases
- `testCases` / `test_cases` - Explicit test cases

## Usage

```bash
# Validate a dataset
agenteval eval --dataset travel-agent.yaml --format markdown

# Run with specific threshold
agenteval eval --dataset rag-qa.yaml --pass-threshold 80

# Export results to file
agenteval eval --dataset travel-agent.yaml --output results.json --format json
```

## Creating Custom Datasets

Minimal test case:
```yaml
- id: my_test
  input: What is 2+2?
  expected: 4
```

Full-featured test case:
```yaml
- id: complete_example
  category: Agentic
  input: Book a flight to Paris
  expected: I'll book that flight for you.
  ground_truth:
    name: book_flight
    arguments:
      destination: Paris
  expected_tools:
    - book_flight
    - check_availability
  context:
    - User prefers morning flights
    - Budget is $500
  metadata:
    priority: high
    author: test-team
```
