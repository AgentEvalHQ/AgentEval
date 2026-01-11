---
applyTo: "**/*.md"
description: Guidelines for writing AgentEval documentation
---

# Documentation Guidelines

## Documentation Structure

- `docs/` - Main documentation folder
- `docs/adr/` - Architecture Decision Records
- `README.md` - Project overview
- `CONTRIBUTING.md` - Contribution guidelines

## ADR Format

Architecture Decision Records follow this structure:

```markdown
# ADR-XXX: Title

> **Status:** Proposed | Accepted | Deprecated | Superseded
> **Date:** YYYY-MM-DD
> **Decision Makers:** Team members involved

---

## Context
The situation that led to this decision.

## Decision
What we decided to do.

## Consequences
### Positive
### Negative
### Neutral

## Alternatives Considered
Other options evaluated.
```

## Documentation Best Practices

### Code Examples
Always include working code examples:
```csharp
// Good - complete, runnable example
var harness = new MAFTestHarness();
var result = await harness.RunTestAsync(adapter, testCase);
result.ToolUsage!.Should().HaveCalledTool("SearchTool");
```

### API Reference Pattern
Document methods with:
- Purpose
- Parameters table
- Return value
- Example

### Markdown Formatting
- Use fenced code blocks with language hints (```csharp)
- Use tables for comparisons and options
- Use > blockquotes for important notes
- Use horizontal rules (---) to separate sections

## File Headers

Each major doc file should have:
```markdown
# Title

> **Short description of what this document covers**

---

## Overview

Brief introduction paragraph.
```

## Cross-References

Link to related docs:
```markdown
See [Architecture Guide](architecture.md) for component details.
```

## Version Notes

For breaking changes or new features:
```markdown
> **New in v1.1**: This feature was added in version 1.1.
```
