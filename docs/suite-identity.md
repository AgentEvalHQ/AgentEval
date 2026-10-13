# Suite definition identity

A case roster identifies which cases were observed. It does not identify the prompts, thresholds,
weights, aggregation or configuration that defined their evaluation. AgentEval's external
[suite manifest](../schemas/suites/1/definition.schema.json) separates these concepts.

The manifest reader and deterministic hashing contract are implemented. Native producer and
dataset-runner bindings are still being implemented; existing run digests are not silently
reclassified as full-definition identities.

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
