# ASSERT Interoperability

> **Status: in development. Nothing on this page is available in any AgentEval release yet.** This page describes
> what is being built. It will become the user guide when the feature ships, and until then it makes no
> compatibility claim.

[ASSERT](https://github.com/responsibleai/ASSERT) (`assert-ai` on PyPI) is Microsoft Responsible AI's Python harness.
It turns a written behavior description into test cases, sends them to a target, and has an LLM judge flag
violations. It reports two headline numbers: how often the target did something it should not have (harm), and how
often it refused something it was allowed to do (over-refusal).

ASSERT and AgentEval do different jobs. ASSERT generates cases and judges any agent from the outside. AgentEval runs
inside .NET, combines deterministic, decision-model and LLM checks, and calibrates judges against labelled cases.
The goal of this work is that the two can be used together without either one changing.

## What is planned

| Surface | What it will do | Status |
|---|---|---|
| **1. A .NET agent as an ASSERT target** | Serve an `IChatClient` or MAF agent at the HTTP endpoint ASSERT calls, including tool calls and their results | Not built |
| **2. Import ASSERT results** | Read an ASSERT run's `scores.jsonl` into AgentEval results, so they can sit in a composite next to AgentEval's own checks | Not built |
| **3. ASSERT's headline numbers** | Report harm and over-refusal the way ASSERT computes them, with the ASSERT version and judge model they came from | Not built |
| **4. Write ASSERT's inference file** | Write AgentEval transcripts in ASSERT's format, so ASSERT's judge can grade them without calling the agent again | Not built |
| **5. Calibrate ASSERT's judge** | Run ASSERT's judge over AgentEval's labelled cases and report agreement (κ, accuracy), so an imported number says how far its judge can be trusted | Not built |

## Rules the import will follow

ASSERT's files have no versioned schema, so compatibility will be pinned to named ASSERT versions and checked by a
contract test against real ASSERT output on every AgentEval release. Some ASSERT result shapes read as good news when
they are not, and the importer will treat them as follows:

- A failed judge call writes scores of 0.0. The importer will report these cases as errors, never as "no violation".
- A judge call stopped by a content filter writes no score row. The importer will count each missing case as not
  measured and name it in the report.
- ASSERT's judge is not calibrated. Imported verdicts will be labelled as coming from an uncalibrated judge until
  surface 5 has been run for that judge.

## Not planned

- **Running ASSERT from .NET.** ASSERT has no public Python API and no judge service. It runs as its own Python
  program; AgentEval provides the target and reads the results.
- **Generating test cases.** ASSERT's behavior-to-test-case stages stay in ASSERT.
