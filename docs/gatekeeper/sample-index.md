# Gatekeeper sample index

Use this page to choose a Gatekeeper sample by difficulty, execution mode, protected boundary, or gate family.
For conceptual guidance, start with the [introduction](introduction.md). For individual gate contracts, see the
[gate reference](gate-reference.md). The [examples](examples.md) page remains the recipe-oriented walkthrough.

## Complexity legend

| Level | Best for | Expected background |
|---|---|---|
| Introductory | First successful runtime block | MAF agent and tool basics |
| Intermediate | One or two coordinated boundaries | Tool middleware, traces and enforcement modes |
| Advanced | Multi-stage attacks, calibration or autonomous agents | Run scope, tool/result ordering and threat modeling |

Execution modes are reported explicitly:

- **Offline** uses deterministic scripted components and performs no network or destructive operation.
- **Live model** calls the configured Azure OpenAI deployment, while sample tool effects remain fake or in-memory.
- **Live boundary** additionally contacts an explicitly authorized remote boundary.
- **Hybrid** retains a useful offline path but enables extra evidence when credentials are configured.

## Current sample catalog

| Sample | Complexity | Execution | Primary gates and mechanisms | What it demonstrates | Launcher |
|---|---|---|---|---|---|
| [`00_GatekeeperHelloWorld`](../../samples/AgentEval.Samples/Gatekeeper/00_GatekeeperHelloWorld.cs) | Introductory | Live model | `ProbeEvaluatorGate` | Reuses a deterministic red-team evaluator as a pre-execution tool gate | Menu |
| [`01_GatekeeperEnforcement`](../../samples/AgentEval.Samples/Gatekeeper/01_GatekeeperEnforcement.cs) | Advanced | Live model | `ForbiddenToolGate`, `ProbeEvaluatorGate`, `CanaryToolGate`, `QuarantineGate`, `TokenInjectionGate`, `OperatorAuthGate`, `RateLimitGate`, `ArgumentPatternGate`, `SequenceGate`, `RegexPiiGate`, shadow judge | Walks through tool, run, session, moat, shadow and output enforcement | Menu |
| [`02_GatekeeperMafHarness`](../../samples/AgentEval.Samples/Gatekeeper/02_GatekeeperMafHarness.cs) | Intermediate | Live model | `SequenceGate` | Allows legitimate customer reads and HTTP tools independently, but blocks read-to-POST exfiltration | Menu |
| [`03_GatekeeperToolApproval`](../../samples/AgentEval.Samples/Gatekeeper/03_GatekeeperToolApproval.cs) | Intermediate | Live model | `ArgumentPatternApprovalGate`, MAF approval continuation | Auto-approves routine work and pauses a risky call for a human decision | Menu |
| [`04_GatekeeperBeachhead`](../../samples/AgentEval.Samples/Gatekeeper/04_GatekeeperBeachhead.cs) | Advanced | Live model | `RunBudgetGate`, `DomainAllowListGate`, `RenderedOutputExfilGate`, calibrated `IndirectInjectionJudge` | Combines deterministic budget/egress controls with a judge that must earn inline promotion | Menu |
| [`05_GatekeeperAgentHarness`](../../samples/AgentEval.Samples/Gatekeeper/05_GatekeeperAgentHarness.cs) | Intermediate | Live model | `RunBudgetGate`, MAF Agent Harness | Caps an autonomous harness loop without claiming the harness itself is unsafe | Menu |
| [`06_GatekeeperAgentHarnessDefended`](../../samples/AgentEval.Samples/Gatekeeper/06_GatekeeperAgentHarnessDefended.cs) | Advanced | Live model | `RunBudgetGate`, `SequenceGate`, `DomainAllowListGate`, MAF Agent Harness | Protects a capable autonomous support harness against runaway work and customer-data exfiltration | Menu |
| [`07_GatekeeperDefenseInDepth`](../../samples/AgentEval.Samples/Gatekeeper/07_GatekeeperDefenseInDepth.cs) | Advanced | Live model | calibrated `IndirectInjectionJudge`, `ToolResultInjectionGate`, `ReferentialIntegrityGate`, `TaintTrackingGate`, `DomainAllowListGate` | Shows different boundaries catching different stages of one indirect-injection campaign | Menu |
| [`08_GatekeeperOutputPanel`](../../samples/AgentEval.Samples/Gatekeeper/08_GatekeeperOutputPanel.cs) | Advanced | Live model | `ExfiltrationIntentJudge`, `SystemPromptExtractionJudge`, `OverRefusalJudge`, parallel fan-out | Calibrates and composes blocking output axes with an advisory utility valve | Menu |
| [`09_GatekeeperMonetaryAndPerCallBudget`](../../samples/AgentEval.Samples/Gatekeeper/09_GatekeeperMonetaryAndPerCallBudget.cs) | Intermediate | Live model | `MonetaryLimitGate`, `PerToolCallBudgetGate` | Limits refund spray by both call count and cumulative financial exposure | Menu |
| [`10_GatekeeperExplainabilityAndTrust`](../../samples/AgentEval.Samples/Gatekeeper/10_GatekeeperExplainabilityAndTrust.cs) | Intermediate | Hybrid | `CompositeJudgeGate`, `GateProvenance`, `GateReplayer`, `ForbiddenToolGate`, `TrustScoreCalculator` | Explains a decision, replays captured calls under a candidate policy and computes an honest trust score | Menu |
| [`11_GatekeeperA2ABoundary`](../../samples/AgentEval.Samples/Gatekeeper/11_GatekeeperA2ABoundary.cs) | Advanced | Live boundary | inbound/outbound inter-agent boundary judges, consent and promotion checks | Guards both directions of an explicitly authorized remote A2A delegation | Menu |
| [`11_GatekeeperA2ACalibration`](../../samples/AgentEval.Samples/Gatekeeper/11_GatekeeperA2ACalibration.cs) | Advanced | Live model | inter-agent judge calibration corpora and promotion thresholds | Calibrates A2A judges without contacting an A2A endpoint | Direct source fixture |
| [`13_GatekeeperMockedDangerousTools`](../../samples/AgentEval.Samples/Gatekeeper/13_GatekeeperMockedDangerousTools.cs) | Intermediate | Offline | sample-local SQL, browser, cloud and package contract gates | Exercises narrow fake grammars and fail-closed verdicts; it is a design fixture, not production parser guidance | Menu |
| [`14_GatekeeperPoisonedToolKillChain`](../../samples/AgentEval.Samples/Gatekeeper/14_GatekeeperPoisonedToolKillChain.cs) | Advanced | Offline | `ToolResultInjectionGate`, containment override/store, `ToolUsageContractGate`, `TaintTrackingGate`, `DomainAllowListGate`, `ForbiddenToolGate`, `BlockStormSentinelGate`, `RunBudgetGate` | Isolates a fake poisoned MCP source, then blocks bulk retrieval, customer-email exfiltration, external POST, delete-all and worm propagation | Menu |
| [`15_GatekeeperHarnessOwnedToolMisuse`](../../samples/AgentEval.Samples/Gatekeeper/15_GatekeeperHarnessOwnedToolMisuse.cs) | Intermediate | Offline | `ForbiddenToolGate`, `RunBudgetGate`, real MAF Agent Harness with scripted provider | Discovers an actual runtime-injected Harness tool name, blocks a weird request from using it, and preserves a benign control | Menu |
| [`16_GatekeeperJailbreakAndToolAbuse`](../../samples/AgentEval.Samples/Gatekeeper/16_GatekeeperJailbreakAndToolAbuse.cs) | Intermediate | Offline | `TokenInjectionGate`, `ToolUsageContractGate`, `RunBudgetGate`, coverage analyzer | Contrasts an obvious pre-model jailbreak block with shell, bulk-delete and external-email contracts that remain authoritative after a paraphrase | Menu |

## Gate-boundary coverage matrix

The matrix records demonstrated behavior, not merely a type mentioned in a comment. A filled cell means the
sample executes or directly evaluates that boundary.

| Sample | Construction | Run pre | Tool call | Tool result | Approval | Run post | Shadow/later run | Autonomous/handoff | Evidence/replay |
|---|:---:|:---:|:---:|:---:|:---:|:---:|:---:|:---:|:---:|
| 00 Hello World |  |  | ✓ |  |  |  |  |  | ✓ |
| 01 Enforcement |  | ✓ | ✓ |  |  | ✓ | ✓ |  | ✓ |
| 02 MAF support |  |  | ✓ |  |  |  |  |  | ✓ |
| 03 Approval |  |  |  |  | ✓ |  |  |  |  |
| 04 Beachhead |  | ✓ | ✓ |  |  | ✓ |  |  | ✓ |
| 05 Harness |  |  | ✓ |  |  |  |  | ✓ | ✓ |
| 06 Defended harness |  |  | ✓ |  |  |  |  | ✓ | ✓ |
| 07 Defense in depth |  | ✓ | ✓ | ✓ |  |  |  |  | ✓ |
| 08 Output Panel |  |  |  |  |  | ✓ |  |  | ✓ |
| 09 Budget |  |  | ✓ |  |  |  |  |  | ✓ |
| 10 Explainability |  |  | ✓ |  |  |  |  |  | ✓ |
| 11 A2A boundary | ✓ | ✓ |  |  |  | ✓ |  | ✓ | ✓ |
| 11 A2A calibration | ✓ |  |  |  |  |  |  | ✓ | ✓ |
| 13 Dangerous tools |  |  | ✓ |  |  |  |  |  | ✓ |
| 14 Poisoned kill chain | ✓ |  | ✓ | ✓ |  |  |  |  | ✓ |
| 15 Harness-owned tool |  | ✓ | ✓ |  |  |  |  | ✓ | ✓ |
| 16 Jailbreak + abuse | ✓ | ✓ | ✓ |  |  |  |  |  | ✓ |

## Feature coverage matrix

| Feature or threat | Current demonstrating samples | Coverage assessment |
|---|---|---|
| Deterministic evaluator reused inline | 00, 01 | Clear introductory coverage |
| Direct destructive-tool denial | 01, 10, 14 | Covered, including a complete fake kill chain |
| Indirect prompt injection in retrieved/tool content | 07, 14 | Covered across judge, tool-result admission and source containment |
| Direct jailbreak or malicious user request | 01, 04, 16 | Focused offline malicious, paraphrased and benign controls now included |
| Data exfiltration through tool sequences | 02, 06, 07 | Strong coverage |
| Taint from a sensitive source to an external sink | 07, 14 | Covered in both campaign and complete kill-chain scenarios |
| Argument-level domain control | 04, 06, 07, 14 | Strong coverage |
| Tool-specific argument contracts | 13, 14, 16 | Sample-local design fixture plus production declarative contracts |
| Tool-result injection, secret and size handling | 07, 14 demonstrate injection | Poison admission and containment are focused; secret/size combinations remain a useful next sample |
| Human approval and continuation | 03 | Focused coverage |
| Budget and denial-of-wallet | 04, 05, 06, 09, 14, 15, 16 | Strong coverage |
| Autonomous Agent Harness protection | 05, 06, 15 | Includes exact runtime discovery and denial of a Harness-owned capability |
| Repeated-denial incident escalation | 01, 14 | Shadow quarantine, block-storm incident evidence and durable fake MCP containment are covered |
| Remote-agent/A2A boundary | 11 A2A samples | Strong, explicitly consented coverage |
| Memory lifecycle protection | [Memory-security samples](memory-security-samples.md) | Covered in the dedicated offline release-validation suite |
| Coverage analysis and construction-time refusal | 14, 16 plus focused tests | Runnable reports cover local tools; hosted/dynamic coverage remains an explicit boundary |
| Explainability, replay and trust aggregation | 10 | Strong coverage |

## Coverage delivered and next additions

Samples 14–16 close the highest-priority showcase gaps identified by the first matrix:

- a poisoned tool/MCP result is withheld, its fake server is durably isolated, and retries are blocked;
- a compromised-model kill chain cannot retrieve all customers, email tainted records, POST externally, delete all customers, or propagate worm instructions;
- a real Agent Harness contribution is discovered by runtime name and denied on a weird request;
- an obvious jailbreak stops before the model while paraphrased attacks remain bounded by declarative contracts; and
- construction-time coverage reports and fake effect counters make the assertions independent of model compliance.

The best next sample additions are a focused secret/oversize result-gate composition, an explicitly unsupported
provider-hosted-tool coverage case, and a cross-link from the memory-security release validation suite. Live-model
variants may be layered on the offline fixtures, but must keep gate evidence and fake effects as the pass/fail oracle.
