# Gatekeeper sample index

Choose a scenario by execution mode, complexity, protected boundary, or threat. The catalog is mechanically checked
against the launcher and sample source files by `GatekeeperSampleManifestTests`. Its canonical metadata is
[`sample-manifest.json`](https://github.com/AgentEvalHQ/AgentEval/blob/main/samples/AgentEval.Samples/Gatekeeper/sample-manifest.json).

For concepts, start with the [introduction](introduction.md). For gate contracts, use the
[compact reference](gate-reference.md).

## Execution modes

| Mode | Meaning |
|---|---|
| **Offline** | Scripted/fake components; no network or destructive effect |
| **Live model** | Calls the explicitly configured Azure OpenAI deployment; tool effects remain local/fake |
| **Live boundary** | Also contacts an explicitly configured and consented remote boundary |
| **Hybrid** | Has a useful offline path and an optional live overlay |

A sample passes only when an internal invariant proves the claimed behavior. Console text, model compliance, or the
absence of an exception is not a sufficient oracle.

Samples 00–09 choose the deterministic offline path when Azure OpenAI is not configured. Set
`AGENTEVAL_GATEKEEPER_FORCE_OFFLINE=true` to force that release oracle even on a configured workstation; otherwise
the optional live overlay runs. Both paths use fake/local tool effects.

## Start small: recommended paths

The repository keeps 30 contracts because each sample owns distinct executable evidence. The interactive launcher
uses progressive disclosure: group **J** initially shows six recommended samples and **M** reveals all 29 menu
entries. The direct-only A2A calibration fixture accounts for the thirtieth manifest entry. Legacy numeric execution
order is unchanged.

| Path | Samples | Outcome |
|---|---|---|
| Recommended six | **00, 14, 16, 20, 23, 27** | Smallest gate, capstone attack, authorization, state, wire, and construction integrity |
| Tool and egress | **02 → 17 → 21 → 23** | Cross-call, result, batch, and network boundaries |
| State and containment | **20 → 19 → 22 → 26** | Lifecycle, isolation, graph response, and actor binding |
| Dynamic and construction | **18 → 24 → 27** | Hosted limits, runtime providers, and manifest drift |
| Semantic and approval | **03 → 28 → 25 → 04** | Continuation, fail-safe approval, trajectory timing, and calibrated judges |

Use [Gatekeeper recipes](examples.md) for the reasoning behind each path. The catalog below remains complete for
regression ownership and specialist lookup.

## Core Gatekeeper catalog

Stable IDs preserve the two historical `11_` source files as **11A** and **11B** without a breaking rename.

| ID | Sample | Complexity | Execution | Protected boundaries | Primary mechanisms | Launcher |
|---|---|---|---|---|---|---|
| 00 | [Hello World](../../samples/AgentEval.Samples/Gatekeeper/00_GatekeeperHelloWorld.cs) | Introductory | Hybrid | tool, evidence | `ProbeEvaluatorGate` | Menu |
| 01 | [Enforcement Walkthrough](../../samples/AgentEval.Samples/Gatekeeper/01_GatekeeperEnforcement.cs) | Advanced | Hybrid | run, tool, session, shadow, evidence | deny/canary/sequence/quarantine/shadow stack | Menu |
| 02 | [MAF Support Agent](../../samples/AgentEval.Samples/Gatekeeper/02_GatekeeperMafHarness.cs) | Intermediate | Hybrid | tool, evidence | `SequenceGate` | Menu |
| 03 | [Tool Approval](../../samples/AgentEval.Samples/Gatekeeper/03_GatekeeperToolApproval.cs) | Intermediate | Hybrid | approval | argument approval + MAF continuation | Menu |
| 04 | [Beachhead and Tribunal](../../samples/AgentEval.Samples/Gatekeeper/04_GatekeeperBeachhead.cs) | Advanced | Hybrid | run, tool, evidence | budget, domain, output, calibrated injection judge | Menu |
| 05 | [Agent Harness Simple](../../samples/AgentEval.Samples/Gatekeeper/05_GatekeeperAgentHarness.cs) | Intermediate | Hybrid | tool, autonomous harness | run budget | Menu |
| 06 | [Agent Harness Defended](../../samples/AgentEval.Samples/Gatekeeper/06_GatekeeperAgentHarnessDefended.cs) | Advanced | Hybrid | tool, autonomous harness | budget, sequence, domain | Menu |
| 07 | [Defense in Depth](../../samples/AgentEval.Samples/Gatekeeper/07_GatekeeperDefenseInDepth.cs) | Advanced | Hybrid | run, tool, result | injection judge, result admission, identity, taint, domain | Menu |
| 08 | [Output Panel](../../samples/AgentEval.Samples/Gatekeeper/08_GatekeeperOutputPanel.cs) | Advanced | Hybrid | run-post, evidence | calibrated output judges + fan-out | Menu |
| 09 | [Monetary and Per-Call Budget](../../samples/AgentEval.Samples/Gatekeeper/09_GatekeeperMonetaryAndPerCallBudget.cs) | Intermediate | Hybrid | tool, evidence | monetary and per-tool limits | Menu |
| 10 | [Explainability and Trust](../../samples/AgentEval.Samples/Gatekeeper/10_GatekeeperExplainabilityAndTrust.cs) | Intermediate | Hybrid | tool, replay, evidence | provenance, replay, trust aggregation | Menu |
| 11A | [Real A2A Boundary](../../samples/AgentEval.Samples/Gatekeeper/11_GatekeeperA2ABoundary.cs) | Advanced | Live boundary | construction, A2A run-pre/post | calibrated inbound/outbound boundary judges | Menu |
| 11B | [A2A Calibration](../../samples/AgentEval.Samples/Gatekeeper/11_GatekeeperA2ACalibration.cs) | Advanced | Live model | construction, A2A promotion | both calibration corpora + thresholds | Direct |
| 13 | [Mocked Dangerous Tools](../../samples/AgentEval.Samples/Gatekeeper/13_GatekeeperMockedDangerousTools.cs) | Intermediate | Offline | tool, evidence | sample-local SQL/browser/cloud/package fixtures | Menu |
| 14 | [Poisoned Tool Kill Chain](../../samples/AgentEval.Samples/Gatekeeper/14_GatekeeperPoisonedToolKillChain.cs) | Advanced | Offline | construction, tool, result, containment | result injection, contracts, taint, containment, block storm | Menu |
| 15 | [Harness-Owned Tool Misuse](../../samples/AgentEval.Samples/Gatekeeper/15_GatekeeperHarnessOwnedToolMisuse.cs) | Intermediate | Offline | run, tool, autonomous harness | discovered runtime capability + deny/budget | Menu |
| 16 | [Jailbreak and Tool Abuse](../../samples/AgentEval.Samples/Gatekeeper/16_GatekeeperJailbreakAndToolAbuse.cs) | Intermediate | Offline | construction, run, tool | input marker + authoritative contracts + coverage | Menu |
| 17 | [Tool Result Admission](../../samples/AgentEval.Samples/Gatekeeper/17_GatekeeperToolResultAdmission.cs) | Intermediate | Offline | tool result | secret masking + fixed-size truncation | Menu |
| 18 | [Hosted Tool Coverage Boundary](../../samples/AgentEval.Samples/Gatekeeper/18_GatekeeperHostedToolCoverageBoundary.cs) | Intermediate | Offline | construction, coverage | hosted-tool classification + promotion refusal | Menu |
| 19 | [Bulkhead + Containment Isolation](../../samples/AgentEval.Samples/Gatekeeper/19_GatekeeperBulkheadIsolation.cs) | Advanced | Offline | construction, tool-resource, containment, evidence | separate normal/isolated HTTP pools + measured concurrency | Menu |
| 20 | [Stateful Gate Timeline](../../samples/AgentEval.Samples/Gatekeeper/20_GatekeeperStatefulTimeline.cs) | Advanced | Offline | tool-call, run, session, durable containment | run budget, session rate state, signed containment persistence | Menu |
| 21 | [Same-Batch Exfiltration Race](../../samples/AgentEval.Samples/Gatekeeper/21_GatekeeperSameBatchRace.cs) | Advanced | Offline | tool-call, concurrent batch, evidence | `SequenceGate` contrast + `SameBatchOrderingGate` | Menu |
| 22 | [Security Graph Incident Response](../../samples/AgentEval.Samples/Gatekeeper/22_GatekeeperSecurityGraphIncident.cs) | Advanced | Offline | ingestion, durable graph, containment, tool-call | bounded ingestion/store/compute, read-only projection, containment bridge | Menu |
| 23 | [HTTP Wire Boundary](../../samples/AgentEval.Samples/Gatekeeper/23_GatekeeperHttpWireBoundary.cs) | Advanced | Offline | HTTP wire, DNS, redirect, cancellation | `GatekeeperHttpMessageHandler` + fake DNS/transport | Menu |
| 24 | [Dynamic Context Provider Boundary](../../samples/AgentEval.Samples/Gatekeeper/24_GatekeeperDynamicContextProviderBoundary.cs) | Advanced | Offline | construction, AIContextProvider, dynamic tool | static inventory refusal + real provider-boundary filtering | Menu |
| 25 | [Crescendo Trajectory](../../samples/AgentEval.Samples/Gatekeeper/25_GatekeeperCrescendoTrajectory.cs) | Advanced | Offline | shadow, run pre, session | trajectory judge + shadow pump + next-run quarantine | Menu |
| 26 | [Session Identity Takeover and Reload](../../samples/AgentEval.Samples/Gatekeeper/26_GatekeeperSessionIdentityTakeover.cs) | Advanced | Offline | session, run pre, concurrency | stable logical identity + atomic actor binding | Menu |
| 27 | [Prompt and MCP Manifest Provenance Drift](../../samples/AgentEval.Samples/Gatekeeper/27_GatekeeperManifestProvenanceDrift.cs) | Advanced | Offline | construction, prompt, MCP discovery | prompt pin + qualified MCP manifest | Menu |
| 28 | [Approval Decision Matrix](../../samples/AgentEval.Samples/Gatekeeper/28_GatekeeperApprovalDecisionMatrix.cs) | Advanced | Offline | approval, tool, human continuation | deterministic + semantic gates and real continuation | Menu |
| 29 | [Tool Result Behavioral Anomaly](../../samples/AgentEval.Samples/Gatekeeper/29_GatekeeperToolResultBehavioralAnomaly.cs) | Advanced | Offline | result, run, state | fixed cap + per-tool running anomaly | Menu |

## Boundary coverage

A check means the sample executes or directly evaluates the boundary; a type mentioned only in prose does not count.

| ID | Construction | Run pre | Tool | Result | Approval | Run post | Shadow/later | Harness/A2A | Evidence/replay |
|---|:---:|:---:|:---:|:---:|:---:|:---:|:---:|:---:|:---:|
| 00 |  |  | ✓ |  |  |  |  |  | ✓ |
| 01 |  | ✓ | ✓ |  |  | ✓ | ✓ |  | ✓ |
| 02 |  |  | ✓ |  |  |  |  |  | ✓ |
| 03 |  |  |  |  | ✓ |  |  |  |  |
| 04 |  | ✓ | ✓ |  |  | ✓ |  |  | ✓ |
| 05 |  |  | ✓ |  |  |  |  | ✓ | ✓ |
| 06 |  |  | ✓ |  |  |  |  | ✓ | ✓ |
| 07 |  | ✓ | ✓ | ✓ |  |  |  |  | ✓ |
| 08 |  |  |  |  |  | ✓ |  |  | ✓ |
| 09 |  |  | ✓ |  |  |  |  |  | ✓ |
| 10 |  |  | ✓ |  |  |  |  |  | ✓ |
| 11A | ✓ | ✓ |  |  |  | ✓ |  | ✓ | ✓ |
| 11B | ✓ |  |  |  |  |  |  | ✓ | ✓ |
| 13 |  |  | ✓ |  |  |  |  |  | ✓ |
| 14 | ✓ |  | ✓ | ✓ |  |  |  |  | ✓ |
| 15 |  | ✓ | ✓ |  |  |  |  | ✓ | ✓ |
| 16 | ✓ | ✓ | ✓ |  |  |  |  |  | ✓ |
| 17 |  |  |  | ✓ |  |  |  |  | ✓ |
| 18 | ✓ |  |  |  |  |  |  |  | ✓ |
| 19 | ✓ |  | ✓ |  |  |  | ✓ |  | ✓ |
| 20 |  | ✓ | ✓ |  |  |  | ✓ |  | ✓ |
| 21 |  |  | ✓ |  |  |  |  |  | ✓ |
| 22 |  |  | ✓ |  |  |  | ✓ |  | ✓ |
| 23 |  |  | ✓ |  |  |  |  |  | ✓ |
| 24 | ✓ |  | ✓ |  |  |  |  | ✓ | ✓ |
| 25 |  | ✓ |  |  |  |  | ✓ |  | ✓ |
| 26 |  | ✓ |  |  |  |  | ✓ |  | ✓ |
| 27 | ✓ |  |  |  |  |  |  | ✓ | ✓ |
| 28 |  |  | ✓ |  | ✓ |  |  |  | ✓ |
| 29 |  |  |  | ✓ |  |  | ✓ |  | ✓ |

## Threat and feature coverage

| Threat or capability | Current samples | Assessment |
|---|---|---|
| Direct destructive-tool denial | 01, 10, 14 | Covered, including fake-effect proof |
| Direct jailbreak/malicious request | 01, 04, 16 | Attack, paraphrase, and benign paths |
| Indirect injection from tool content | 07, 14 | Covered at run and result seams |
| Exfiltration through sequence/taint/domain | 02, 06, 07, 14 | Strong deterministic coverage |
| Tool-specific argument contracts | 13, 14, 16 | Production contracts plus clearly labelled design fixtures |
| Tool-result secrets and size | 17, 29 | Fixed admission plus per-tool run-scoped behavioral anomaly and reset |
| Human approval | 03, 28 | Routine, parameterless-sensitive, risky args, goal mismatch, failure, rejection, and approved continuation |
| Budgets/denial of wallet | 04, 05, 06, 09, 14–16 | Strong |
| Agent Harness protection | 05, 06, 15 | Includes runtime-injected capability discovery |
| Repeated-denial escalation/containment | 01, 14 | Shadow quarantine and durable fake-source containment |
| Remote A2A boundary | 11A, 11B | Implementation/calibration covered; 11A requires a configured endpoint |
| Provider-hosted coverage honesty | 18 | Strong construction-time boundary |
| Explainability and replay | 10 | Strong |
| Bulkhead/resource isolation | 19 | Strong measured 3:1 pool isolation with contained saturation |
| Stateful lifecycle/reset/restart | 01, 09, 14, 20 | Dedicated call/run/session/durable timeline plus focused examples |
| Same-batch ordering race | 21 | Dedicated five-control race demonstration and fake-effect proof |
| Security-graph incident response | 22 | Durable multi-session path through honest compute, read-only ops, containment, and refusal |
| HTTP redirect/DNS wire boundary | 23 | Deterministic redirect, private-DNS, limit, cancellation, and disclosure checks |
| Dynamic AIContextProvider tool coverage | 24 | Static promotion refuses unknown inventory; real provider boundary filters unsupported tools |
| Crescendo/slow-burn trajectory | 25 | Deterministic shadow timing, one-time arm, next-run quarantine, and safe-frustration control |
| Session actor takeover/reload | 26 | Weak-object limitation contrasted with stable reload, poisoning, and concurrent-race defense |
| Prompt/MCP manifest and provenance drift | 27 | Prompt registration pin plus canonical schema and qualified server identity checks |
| Approval edge-case matrix | 28 | Every risky/inconclusive path pauses; reject/approve effects measured |
| Per-tool result behavioral anomaly | 29 | Independent run-scoped baselines, non-poisoning repeats, and reset proof |

## Other Gatekeeper sample suites

The core catalog does not duplicate these specialized suites:

| Suite | Coverage | Entry point |
|---|---|---|
| Memory security | Eight offline scenarios covering recall, write, tenant, provenance, lifecycle, MCP, tool, and context-provider adapters | [Memory-security samples](memory-security-samples.md) |
| Agent Skills | Manifest/content construction checks plus deterministic script execution and approval postures | [Agent Skills documentation](../agent-skills.md) and [`04_AgentSkillsSkillGate`](../../samples/AgentEval.Samples/AgentSkills/04_AgentSkillsSkillGate.cs) |
| Attack the Gate | Credential-free red-team regression target and baseline comparison | [Attack the Gate](attack-the-gate.md) |

## Using the manifest

`sample-manifest.json` records, for every core entry:

- stable ID and handler;
- source and launcher status;
- execution mode and complexity;
- protected boundaries, mechanisms, and threats;
- external effects; and
- the pass oracle.

The validation test rejects missing/extra fields, duplicates, invalid enum values, missing sources, unregistered menu
handlers, uncatalogued Gatekeeper sample files, and catalog entries without a stable ID. Add the manifest row, source,
launcher registration, and this catalog in the same change.
