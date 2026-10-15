# Requirement catalogues

Requirement catalogues describe your acceptance policy separately from sealed AEF evidence.
They do not alter results, approve releases or grant a reviewer authority.

Validate a catalogue without running an agent or changing any evidence:

```bash
agenteval requirements validate samples/requirements/catalogue.yaml --json
```

The command accepts `.json`, `.yaml` and `.yml`. Exit 0 means valid; exit 2 means invalid input
or an unreadable source. JSON output always reports `valid` and `diagnostics`; valid output also
includes the catalogue version, requirement IDs and `catalogueDigest`, the SHA-256 of the exact
source bytes. Formatting changes therefore change source identity, even when meaning is unchanged.

## Catalogue 1.0

```yaml
schemaVersion: "1.0"
requirements:
  - id: example:quality
    selector:
      lane: quality
      metric: accuracy
      path: accuracy
    acceptance:
      kind: threshold
      operator: ">="
      value: 0.8
    aggregation:
      kind: all
      missing: not_measured
    freshnessSeconds: 86400
```

The threshold and freshness above are illustrative, not recommended acceptance criteria.
Use the exact lane, metric ID and result path declared in your evidence. Paths are literal AEF
path strings, not filesystem paths, glob expressions or inferred metric names. A selector may
also restrict `suiteRef`, `suiteDigest` (the exact AEF `sha256:<64 lowercase hex>` value;
requires `suiteRef`) and `deployment`.

- IDs are unique, case-sensitive `namespace:name` values, at most 128 characters and compatible with AEF requirement targets. Slash-separated IDs are rejected.
- Acceptance is either `threshold` with a numeric `value` and `operator` (`>`, `>=`, `<`, `<=`,
  `==`, `!=`), or `state` with a nonempty list of measured `states` (`passed`, `failed`, `warn`, `scored`).
- Aggregation must explicitly name `all`, `any`, or `fraction`; a fraction also requires `minimum`
  greater than zero and at most one. Every rule declares `missing: not_measured`.
- `freshnessSeconds` is an explicit positive integer, at most 3,155,760,000 seconds.
- `allowEffectiveOverrides` defaults to false and is permitted only for state rules. It cannot
  change numeric observations or recompute sealed checkpoint outcomes.
- Optional `description` is for humans; it does not affect the rule.

The [catalogue schema](../schemas/requirements/1/catalogue.schema.json) is independent of AEF 1.0.
Validation rejects unknown properties, duplicate IDs, padded/blank selectors and ambiguous rules.
It does not certify that a selected metric exists in a future run or that a requirement is satisfied.

## Bounded loading

A source and its normalized JSON are each limited to 1 MiB, with at most 1,000 requirements.
JSON is strict UTF-8 with unique object properties. YAML accepts a single document, at most
32 nested levels and 20,000 value nodes, and rejects aliases, anchors, merge keys and explicit tags.
Plain YAML scalars use JSON-style numeric/boolean spelling; quote version strings such as `"1.0"`.
No remote schema or catalogue reference is fetched.

The core reader implements `IRequirementCatalogueReader` in `AgentEval.Results.Requirements`
without YAML or DI-container dependencies. The CLI's file adapter handles YAML and preserves exact
source identity. These APIs are currently part of the unreleased catalogue implementation.

## Compute status from selected evidence

```shell
agenteval requirements status catalogue.yaml --subject agent:assistant --version v1 \
  --at 2026-10-13T12:00:00Z --evidence HASH=DIRECTORY --json
```

Replace `HASH` with the exact lowercase 64-character sealed run hash. Repeat `--evidence`
for additional runs, up to 256. No evidence selection returns `not_measured`. The command
never discovers a newest or best run. A missing directory, unsealed or invalid run, hash
mismatch, invalid policy or invalid catalogue is an input error (exit 2), not a status.
Exit 0 means every requirement is `met` or explicitly `waived`; exit 1 means at least one
is `not_met`, `not_measured` or `stale`.

Eligible evidence is intact, completed, live, for the exact subject/version and any declared
deployment/suite restrictions, and ended no later than the view time. Exclusions are explained.
Each eligible selected run must contain the exact summary lane/metric/path binding and matching
non-trial result observations. Duplicate hashes count once; conflicting overlay snapshots refuse.
Thresholds apply to each observation's AEF metric value, not to the producer's aggregate mean.
`all`, `any` and `fraction` combine those individual acceptance outcomes. `not_applicable`
observations are excluded; a run with none remaining is unmeasured. A missing required observation
or value makes the requirement `not_measured` even if another observation passed. Otherwise any
expired eligible run makes it `stale`. Evidence is fresh through `endedAt + freshnessSeconds`,
with nanosecond precision.

Output retains `baseStatus` from sealed evidence, `effectiveStatus` after explicitly opted-in
state overrides, and the displayed `status`. Reviews never change either measured status.
Numeric values, parents, summaries, gates and checkpoints are never recomputed by overlays.
As AEF specifies, verified overrides use file order; `--at` controls freshness and waiver activity,
not historical cutoff of the overlay log. Archive the chosen overlay snapshot for reproduction.

## Scoped waiver authority

Pass `--policy trust.json` to use an AEF trust policy. An exact permission such as
`requirements:waive:example:quality` in an identity's `may` list authorizes requirement waivers
for `example:quality`. A valid trusted signature for that identity is also mandatory.
Neither a self-attested `--by` identity nor a signature alone grants authority. No wildcard
or default exception authority is supplied.

Every eligible selected run needs its own active, authorized waiver for the exact requirement
before a non-met requirement displays `waived`. Result-only waivers and other namespaces have
no inferred effect. Waivers are active when `at <= viewTime < expires`; at expiry the underlying
status returns. History includes inactive and unauthorized verified requirement waivers.

The report records the exact catalogue source digest, normalized trust-policy digest, explicit
view time, subject/version, selected run hashes, and a separate digest of each verified overlay
view (normalized verified events in file order plus shown assurance). These inputs identify
the evidence and policy used. `IRequirementEvidenceReader` performs verification and detaches
snapshots; `IRequirementStatusResolver` computes statuses without I/O. Neither writes to evidence.
