# Suite definition identity

A case roster identifies which cases were observed. It does not identify the prompts, thresholds,
weights, aggregation or configuration that defined their evaluation. AgentEval's external
[suite manifest](../schemas/suites/1/definition.schema.json) separates these concepts.

The manifest reader, deterministic hashing, native eval and dataset-runner bindings are implemented.
Probe/composite adapter bindings are available; their CLI definition capture and comparison compatibility
are still being implemented. Existing
dataset-only or roster-only identities are not silently reclassified as full definitions.

## Manifest 1.0

A manifest declares `schemaVersion: "1.0"`, `kind` (`dataset`, `probe` or `composite`), `suiteRef`,
`definitionVersion`, `frozen`, `inputs`, `definition` and `caseIds`.

- `inputs` maps artifact names to `sha256:<64 lowercase hex>` digests of exact input bytes.
  Producers must include dataset, prompt/rubric and implementation artifacts relevant to the
  definition. They can retain hashes without copying sensitive text into the manifest.
- `definition` holds the producer's declared configuration: evaluator graph, thresholds,
  weights, aggregation, trials and generation policy. It is not built from observed results.
- `caseIds` is the complete planned roster. Its order does not matter; execution order and
  ordered case content, where significant, belong in the definition or hashed inputs.
- `definitionVersion` describes the definition's release, separately from the producer release.
- `frozen` means these inputs and the planned roster identify fixed suite content. A dynamic
  generation recipe can have a manifest digest, but does not claim an AEF suite content digest.

`ISuiteDefinitionReader` validates and creates an immutable `SuiteDefinitionManifest` in
`AgentEval.Results.Suites`. It performs no file or network access. Sources and normalized bytes
are limited to 4 MiB, with at most 10,000 named inputs and 100,000 distinct case IDs. A frozen
definition requires a nonempty planned roster. Duplicate JSON properties, non-finite numbers,
invalid hashes and ambiguous identifiers are refused.

## Deterministic hashing

Manifest-v1 normalization sorts object keys recursively using ordinal UTF-16 order, sorts the
planned case roster using the same order, and preserves all other array order. It uses the
existing AEF JSON writer's UTF-8, binary64 number spelling, two-space indentation and terminal LF.
SHA-256 covers these normalized bytes, including the manifest version. This is an explicit
versioned algorithm, not a claim of JCS compatibility. Unicode and input artifact line endings
are not implicitly normalized.

Result scores, timestamps, completion order and interruption do not belong in the declared
definition. Changing rubric content, configuration, weights, thresholds or planned roster must
change its digest. Keep the observed roster separate, so a partial run cannot masquerade as a
different frozen definition. Caller-supplied declarations still require producer integration:
the manifest reader cannot infer a hidden prompt or prove that arbitrary configuration describes
what a producer actually executed.

## Dataset producers

Native CLI eval parses and identifies the same captured dataset byte snapshot. Its full definition
includes the planned case IDs, trial policy, optional harness/named-metric judge configuration and
content digests of the implementation assemblies that supply parsing, default criteria, harness
logic and embedded prompts. Hashing those assemblies is conservative: rebuilding or changing a
framework-specific assembly can change identity even if an unrelated method changed. No raw dataset
or prompt text is copied into the definition. Subject model identity is not the evaluator identity;
changing the subject alone does not change the suite, while changing an LLM metric's judge does.

Native eval and dataset jobs persist the normalized manifest at
`ext/agenteval/suite-definition.json`, covered by the run seal, with a compact
`agenteval.suiteIdentity` extension pointing to it. The complete planned definition is present even
before cases finish. An interrupted run retains it; eval summaries record the observed roster separately.
Adding a case outside a frozen planned roster is refused.

Adapter callers can supply `AefEvalRunOptions.Definition`. Its suite reference and exact dataset
digest must match the supplied snapshot. Callers omitting it keep the earlier dataset-only digest,
explicitly labelled `scope: dataset`. This compatibility path does not claim full definition identity.

Dataset-job dry runs report resolved suite digests for pinning in the plan. A supplied digest must
match the full definition; old dataset-byte digests are refused. Limits that control evaluation
(case deadline and role input/output/call caps) are included. Job interruption, observed costs and
which planned cases finished are not used to build a new definition. Runner validation checks that
the declared definition, suite reference/version, content digest and actual planned roster agree.

## Probe and composite adapters

`AefProbeRunOptions.Definition` and `AefCompositeRunOptions.Definition` accept a manifest captured
by the caller before execution. Writers check its kind, suite reference and frozen planned roster,
bind its version/content digest, and store the manifest under the run seal. A completed run cannot
omit planned cases; explicitly interrupted runs can contain a subset. Composite rosters include
nested component IDs. `run.json` records the observed roster separately in `agenteval.observedRoster`.

Without a manifest, probe adapters label their identity `observed-roster` and composite adapters
label theirs `legacy-roster`. Existing composite roster digests remain unchanged, including empty
interrupted exports. These scopes do not prove evaluation-definition equivalence. Generated probes
have no suite content digest and report `frozen: false`; a supplied dynamic manifest identifies
the recipe without claiming fixed generated stimuli. A probe writer rejects a frozen declaration
when its stimulus mode is `generated`, before creating the run. Producers that materialize all
stimuli before execution must declare that fixed suite using the `suite` stimulus mode.
Cancellation before the first result still preserves the declared manifest and planned roster;
the separately recorded observed roster is empty. This applies to frozen suites and dynamic recipes.

GDPR and EU AI Act commands capture supported composite graphs before execution and pass that same
manifest to successful and interrupted exports. Capture covers the complete nested roster, component
weights/required/failure effects, aggregation, thresholds, minimum measured share, severity policy,
judge identity, hashed prompts/criteria/scenario inputs, query/context and requested trials. Implementation
assemblies are hashed once per capture. Subject responses and identities are kept separate.

`AtomicLlmEval.CaptureDefinition()` exposes an immutable, content-free leaf snapshot, including whether
the evaluator identifies its prompt. `AgentScenarioEval` exposes its inner judge and scenario input digest.
Unknown evaluator kinds, unidentified prompts and custom aggregation strategies keep legacy identity
scope; they are not guessed from result trees. Agentic and other composite producer capture is
still in progress. Explicit mock benchmark paths also retain their existing identity scope.

Agentic wrappers can expose their configured inner instrument through `IEvalDefinitionWrapper`.
The wrapper implementation is still hashed, including its input framing and fixed decision logic.
The contract is only suitable when all configurable settings reside in the inner instrument and
the result key/tree structure is preserved. Leaf evaluators can provide a detached, content-free
configuration through `IEvalConfigurationSource`; F1 includes its threshold and hashed fallback
reference, and path-distance includes its threshold. Capture and evidence shape traversal are bounded.

The agentic CLI captures supported graphs before evaluation, including a hash of the supplied
ground-truth reference and declared system message, expected actions and tool definitions. Observed
responses/tool calls remain subject evidence. Current coverage includes agentic execution, tool-call accuracy, RAG quality,
conversational, reasoning, user-experience, telemetry, stochastic-stability, judge-quality, glass-box-diagnostics, safety and adversarial-direct presets. Operational
checks declare thresholds and budgets; constructor-supplied telemetry and prior run results remain
subject observations and do not change suite identity. Judge-quality captures its thresholds and hashes
ordered expected calibration labels; actual verdicts remain observations. Producers use the input-aware
`IEvalConfigurationSource.CaptureConfiguration(EvalInput)` overload. Calibration capture accepts stable
lists or JSON and returns no declaration for potentially one-shot enumerables, preserving execution inputs.
Diagnostic capture hashes the trusted system-prompt baseline and regex pattern/options/timeout, and binds
optional judges through `IEvalInstrumentSource`. Nested instruments contribute identity without adding
emitted result cases. Culture-dependent or custom regex implementations remain unclassified because
construction culture and subclass behavior cannot be reconstructed safely.
Safety and adversarial capture hash applied static policy, compiled custom patterns and ordered library
entries, effective regex settings, scan limits and thresholds, with nested judge declarations. Dynamic
policy resolvers, external content classifiers and overridden matcher delegates remain unclassified;
capture never calls those dependencies. All twelve built-in agentic CLI presets now have capture support
when their judge is identified. Explicit composite mock exports also capture their declared graph before
execution and retain it on cancellation. Their built-in judge is identified as `fixed-placeholder` by
its exact implementation type, with no claimed LLM prompt. They retain `mocked` target mode and one
actual trial regardless of an ignored mock `--runs` option. Interrupted snapshots expand configured
wrappers with bounded traversal so missing nested checks retain their planned shape. Other producer capture remains in progress;
unsupported graphs keep legacy scope. These changes do not alter scoring or judge prompts.

## Red-team scan capture

`ScanOptions.OnPlanPrepared` observes the runner's ordered, materialized attack arms after transforms
and probe limits, including enabled benign controls. It runs before subject/judge calls. Capture reuses
the probes selected for execution and does not call generators again. Callbacks must not mutate the plan.

The `redteam` CLI hashes those probe declarations (including prompt, expected tokens and metadata),
implementation assemblies and applied scan policy. Only hashes of probe content enter the manifest;
endpoints, credentials and observed responses are excluded. The full planned roster survives fail-fast
and cancellation, with missing probes reported separately, including benign controls that did not run.
Attacker-driven scans retain a recipe manifest with `frozen: false` and no suite content digest.
Capture supports the CLI's shipped attack factory; arbitrary external attack instances remain
unclassified. `AttackPipeline.OnPlanPrepared` forwards the same callback for pipeline consumers.
OWASP, MITRE and NIST commands bind the prepared plan together with the resolved preset and verdict
implementation. Their fixed verdict rules are covered by the implementation assembly digest.
Completed, interrupted and explicitly requested mock scan exports retain this declaration.

## Trace reconciliation producers

Trace-fidelity commands capture their policy and planned checks before reconciliation. Standard trace
fidelity declares six discrepancy checks. Workflow trace fidelity groups executor IDs case-insensitively,
retaining the first spelling as execution does; an empty workflow declares the existing root-only check.
The manifest binds the resolved preset, token tolerance and implementation assemblies. Trace contents,
reported token counts and finish reasons are subject observations, so changing them does not redefine
the suite. Completed and interrupted exports seal the same pre-reconciliation definition.

## Performance producer

Performance evaluations prepare an immutable workload, threshold, iteration/concurrency policy and
pricing snapshot before agent calls. The CLI hashes ordered prompts, the initial pricing key and the
captured rates; it declares latency, throughput and cost checks for every preset. Observed timings,
token counts, responses and reported model IDs do not redefine the suite. Reported model IDs remain
inputs to the declared pricing fallback policy. Missing pricing still leaves cost unmeasured.

Execution uses the captured options and rates even if callers change the original options or global
pricing during a run. Pricing uses exact matches, then the longest substring; equal-length matches
use ordinal, case-insensitive key order so process/dictionary order cannot change the result. The
shared judge-cost lookup uses the same tie rule. Completed, interrupted and explicit mock exports
retain the prepared definition; custom registry adapters that do not prepare a plan retain legacy scope.

## Auto-audit producer

Auto-audit declares its fixed fidelity and safety checks before starting any endpoint. The real support
task binds the instruction/request hashes, tool-loop limit, gate policy, trace tolerance and implementation
assemblies. The scripted showcase declares a distinct instrument and retains mocked target mode.
Endpoint names and measured behavior are observations, so all endpoints within the same instrument
share a definition. Completion, client-factory failures, cancellation and report-write failures retain it.

## Built-in memory producer

The built-in memory command declares a generated recipe before execution: the selected benchmark,
resolved preset key, ordered category weights/types, context-pressure overrides, judge identity,
embedded scenario/corpus hashes and implementation hashes. It emits one benchmark case with weighted
category children. Completed and interrupted exports retain the same recipe and separately record
the observed results.

This producer uses runtime UTC values for unanchored temporal inputs and code-generated fallback
scenarios. Its declaration is therefore `frozen: false`, with a recipe digest in the extension and no
suite content digest. It must not be treated as identical frozen evaluation content in comparisons.
Changing the recipe's configuration still changes its digest; no prompts or corpus text are exported.

## External memory producers

LongMemEval and TypedMemEval capture the actual selected entries and applied configuration after loading
and sampling, before agent or judge calls. `ExternalBenchmarkPlan` exposes an immutable, content-free
snapshot: ordered question IDs, a hash of selected questions/history/expected answers, and detached
configuration without the local dataset path. TypedMemEval additionally binds the source corpus,
including judge extension data that is deliberately absent from the agent-facing entries.

The CLI seals a frozen definition for this materialized selection, even when the selection originally
used an unspecified random seed. It does not sample a second time or re-read a changed dataset to derive
identity. Full planned rosters survive interruption; observations remain separate. Empty selections
declare the existing root-only case. Existing runner overloads retain their behavior; new progress
overloads accept an optional preparation callback. [Paired exports](benchmarks/longmemeval/paired-evidence.md)
retain separate normal and oracle definitions and link the oracle to the normal seal.

The paired runner captures a single `LongMemEvalPairedPlan` before calls, then reports preparation
and observed progress separately for normal and oracle arms. Its source input digest binds the
selected workload; its unique pair ID groups one execution and does not enter content identity.
Normal and oracle suite definitions have distinct role references and retain their own materialized
input digests, the shared source digest, matching declared answer-model settings and gold-only
projection policy. Oracle preparation happens only after normal completion. These definitions
are used by paired export; sharing source questions does not make the two arms equivalent under the
strict full-definition comparison policy.

## Comparing suite identities

Use `agenteval aef compare-suites <baseline-dir> <candidate-dir> --json` before comparing scores.
This command applies `agenteval/full-definition-v1`: both runs must be intact and sealed, with
matching full frozen definitions. It verifies the fixed extension file against the seal and checks
its digest, version, reference, frozen flag and planned count against the header metadata.
Reads are bounded; extension metadata cannot redirect the reader to another file.

The report contains both exact seal hashes, identity scopes and run statuses. Optional
`--baseline-hash` and `--candidate-hash` pins refuse evidence other than the selected run.
Exit codes are 0 for matching definitions, 13 for different or insufficient identities, and 2 for
unsealed, invalid, inconsistent or unreadable evidence. The command does not modify the runs.

Equal generation recipes do not establish equal generated content. Dataset-only and roster-only
legacy digests, and missing digests, cannot establish full-definition equivalence either. They are
reported as incomparable instead of being silently upgraded. Frozen definitions survive interruption,
so completed and aborted runs can share an identity; score comparison still needs separate checks for
completeness, subject, execution conditions and statistical validity. Integrity verification alone
does not authenticate the producer or prove its declarations are truthful.

`ISuiteIdentityReader` creates immutable verified snapshots, and `ISuiteCompatibilityEvaluator`
compares them without filesystem access. The BCL-only Results package exposes these services without
requiring a dependency injection framework. AEF checkpoint rule LANE-6 remains unchanged: its
`suite-content` axis compares protocol digest values, including absent values. The stricter AgentEval
policy is an explicit additional check, not a change to AEF conformance semantics.
