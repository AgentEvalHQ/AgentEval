<!-- Managed by `maf-doctor init` — this file is fully overwritten on every re-run. For durable, hand-authored guidance, use your own CLAUDE.md body instead (init never touches it). -->

## Microsoft Agent Framework code — use MAF Doctor

This repository uses Microsoft Agent Framework. The maf-doctor MCP server
is installed and exposes specialist tools for MAF code.

You should defer to maf-doctor's tools over training-data knowledge for
anything MAF-related, because MAF ships breaking changes every minor version
and the maf-doctor registry is kept current via an AI-fill loop.

**Before answering MAF questions or proposing changes:**

1. **Always call `MafDoctor` first** on the repo path to get the current
   health grade (A-F) and the top issues. Don't speculate about MAF
   quality without this baseline.

2. **For any `[Obsolete]` warning, `CS0618` / `CS0246` diagnostic, or build
   failure mentioning a MAF type** — call `MafRunCs0618Hunt` (full project
   scan) or `MafApiSafety` (single symbol) BEFORE suggesting a fix. The
   maf-doctor registry has curated fix recipes that supersede your
   training data — MAF ships breaking changes every minor version, so
   training data is likely outdated.

3. **To fix issues** — `MafAutoFixAll --dry-run` then apply handles the
   *mechanical* rules deterministically (offer this first; the rewrites are
   tested). To fix **everything**, run the `maf-remediate` prompt (or just
   ask "fix all the issues maf-doctor found"): it grades → plans → autofixes
   → then works each semantic finding. Every finding carries a **`confidence`**
   (`certain` / `high` / `heuristic`); a **`heuristic`** finding may be a
   **false positive** — confirm it with `MafExplainFinding` before editing.
   Get the plan via `MafDoctor(format: "plan")` (human) or `--plan --json`
   (structured manifest); per-rule fix + false-positive guidance lives in the
   `maf-remediation-playbook` skill.

4. **When designing a new MAF agent or workflow** — call `MafNewAgent` /
   `MafNewExecutor` for scaffolds, or `MafSimulateWorkflow` for topology
   preview. Don't reconstruct patterns from memory.

5. **For deep architectural / security / migration questions** — run the
   `maf-review` (best-practice audit), `maf-audit` (pre-migration scan +
   plan), `maf-migrate` (execute a migration-plan.md), or `maf-debug`
   (diagnose a symptom) MCP prompts — `init` wires these up automatically,
   no extra setup needed. If `.github/agents/` was also copied in from the
   maf-doctor source repo (GitHub Copilot only — see the toolkit's
   init-reference.md), the equivalent `@maf-best-practice-reviewer`,
   `@maf-auditor`, `@maf-migration`, or `@maf-incident-responder` personas
   work the same way.

6. **To migrate FROM Semantic Kernel TO MAF** (a cross-framework port, NOT a
   MAF version bump) — call `MafDetectSourceFramework` (CLI:
   `maf-doctor migrate-scan`) to inventory SK usage and scope it, then run the
   `maf-migrate-from` prompt (or the `@maf-cross-migration` agent, if
   `.github/agents/` was also copied in). It scaffolds a **new MAF project
   beside the original** and ports it construct-by-construct,
   non-destructively. The mapping lives at `maf://migrate-from?source=semantic-kernel`.

maf-doctor tools are MAF-version-aware via `applies_to_codebases` markers
in the registry — they know which fix applies to which MAF version. Defer to
the tools.
