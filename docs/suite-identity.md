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
without a frozen declaration have no suite content digest and report `frozen: false`; a supplied
dynamic manifest identifies the recipe without claiming fixed generated stimuli.

CLI probe/composite producers do not yet supply these manifests. Their existing evidence remains
explicitly roster-scoped until producer capture is integrated.
