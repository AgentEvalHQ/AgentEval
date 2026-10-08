# AEF governance

## Editors

AEF is edited by the AgentEval maintainers ([@joslat](https://github.com/joslat)) until it has independent
implementers; then editors from at least two implementations are invited. Editors decide by consensus; when they do
not reach it, the lead editor decides and records why in the changelog.

## Proposing a change

1. Open an issue labelled `aef-proposal` in the AgentEval repository: what is missing or wrong, an example, and the
   rule ids it touches.
2. A change is a pull request that updates, together, the specification text, the writer schemas (the reader schemas
   are derived), the corpus (a vector for every new or changed rule, written independently of any implementation), the
   reference tools, and the changelog.
3. A change merges when:
   - the reference implementation passes the whole corpus (`tools/aef_conformance.py`);
   - AgentEval's implementation passes the vectors of every class it claims;
   - `tools/check_spec.py` finds the text, schemas and corpus in agreement;
   - `tools/schema_diff.py` accepts the change for the version it is released in.

   Every decision on a proposal, including a refusal, is recorded with its reason on the issue.

## Security flaws in the format

A weakness in AEF itself (a way to make a tampered run verify, an ambiguity two verifiers resolve differently) is
reported privately, as the repository's [security policy](../../SECURITY.md) describes, not in a public issue. The
fix ships as an erratum or a new minor version, with a corpus vector that would have caught it, and the changelog
credits the reporter unless they ask otherwise.

## Conduct

Discussions follow the repository's [code of conduct](../../CODE_OF_CONDUCT.md).

## Versions

- AEF follows the rules of [§7](1/spec/07-versioning.md): a minor version only adds; anything else is a new major
  version, in a new folder (`2/`).
- A version is released as a git tag `aef-<major>.<minor>` and a corpus digest (the SHA-256 of
  `conformance/index.json`).
- Deprecation: [VER-7]. A deprecated field stays valid for at least one minor version and is removed only by a major.
- Errata (a rule whose text contradicts its vectors or schema) are fixed in the next minor and listed in its
  changelog; the corpus vector is the tie-breaker until then.

## Identifiers

The schema `$id`s and predicate types under `https://agenteval.dev/aef/` are names ([ENC-12]). The AgentEval project
keeps the `agenteval.dev` domain so they never become ambiguous; if AEF moves to a neutral home, version 1 keeps its
names and a later major may take new ones.

## Licence and patents

The specification, schemas, corpus and tools are licensed under the Apache License 2.0 (`LICENSE`). Its patent grant
covers contributions to this folder. A commitment covering independent implementations of the specification (as the
Community Specification License or OWFa provide) is under review and will be stated here before 1.0 is released.
