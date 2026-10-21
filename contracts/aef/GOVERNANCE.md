# AEF governance

## Editors

- **Lead editor:** [@joslat](https://github.com/joslat), for the AgentEval project.
- **Second editor:** an open seat, offered to a maintainer of the first independent implementation that passes the
  corpus for at least one conformance class. Further editors join the same way. Implementations and their claims are listed in
  [IMPLEMENTATIONS.md](IMPLEMENTATIONS.md).
- Editors decide by consensus. When they do not reach it, the lead editor decides and records why in the changelog.
- **Succession:** if the lead editor is unavailable for 90 days, the AgentEval maintainers appoint an acting lead
  editor, so the specification cannot be left without one.

## Proposing a change

1. Open an issue labelled `aef-proposal` in the AgentEval repository: what is missing or wrong, an example, and the
   rule ids it touches.
2. A change is a pull request that updates, together, the specification text, the writer schemas (the reader schemas
   are derived), the corpus (a vector for every new or changed rule, written independently of any implementation), the
   reference tools, and the changelog.
3. A change merges when:
   - the reference implementation passes the whole corpus (`tools/aef_conformance.py`);
   - AgentEval's implementation passes the vectors of every class it claims;
   - `tools/check_spec.py` finds the text, schemas and corpus in agreement, and every rule covered;
   - `tools/schema_diff.py` accepts the change for the version it is released in.

   A CI job (`.github/workflows/aef.yml` in the AgentEval repository) runs these checks, the corpus regeneration
   (byte for byte), the tools' self-tests and the DCO check on every change to `contracts/aef/`; on pull requests it
   also runs AgentEval's implementation through the conformance runner.

   Every decision on a proposal, including a refusal, is recorded with its reason on the issue.

## Contributions

Every commit to `contracts/aef/` carries a **Developer Certificate of Origin** sign-off (`Signed-off-by: Name
<email>`, as `git commit -s` writes it): the contributor certifies they may contribute it under the terms below
(<https://developercertificate.org/>). There is no separate contributor licence agreement.

## Licence and patents

- Everything in this folder (the specification text, the primer, rationale, interop and reference pages, the schemas,
  the corpus and the tools) is licensed under the **Apache License 2.0** (`LICENSE`), with the attributions in
  `NOTICE`. This is the licence of AEF 1.0, decided by the lead editor on 2026-10-20 and recorded in the changelog.
- Its patent licence (section 3) comes from each contributor and covers their contributions and works based on them.
  AEF makes no separate patent commitment to implementations written from the text alone.
- A change to these terms is a governance change: it follows the proposal process above, and the changelog records it.

## Versions and releases

- AEF follows the rules of [§7](1/spec/07-versioning.md): a minor version only adds; anything else is a new major
  version, in a new folder (`2/`).
- A version is released as a git tag `aef-<major>.<minor>` and a corpus digest (the SHA-256 of
  `conformance/index.json`).
- **Release criteria for 1.0:** the reference implementation passes the whole corpus, and every conformance class
  ([§9.1](1/spec/09-conformance.md#91-classes)) has a second implementation passing its vectors (AgentEval's,
  or an independent one). A class without one is released marked **at risk** in the release notes, and may change in
  1.1 more than a minor normally allows.
- Deprecation: [VER-7]. A deprecated field stays valid for at least one minor version and is removed only by a major.
- Errata (a rule whose text contradicts its vectors or schema) are fixed in the next minor and listed in its
  changelog; the corpus vector is the tie-breaker until then.

## Identifiers

The schema `$id`s and predicate types under `https://agenteval.dev/aef/` are names ([ENC-12]), not locations: no
reader fetches them, so nothing breaks if the domain ever lapses. The lead editor holds the `agenteval.dev` domain for
the project. If AEF moves to a neutral home, version 1 keeps its names and a later major may take new ones.

## Profiles

A profile builds on AEF for a narrower use, with its own `$id` (outside `/aef/1/`) and its own versions. Profiles are
not part of AEF 1.0 and no AEF conformance class requires one. The runtime-verdict profile (AEVP 0.1, in
`profiles/runtime-verdict/`) is the first. The artifact-evidence profile (in `profiles/artifact-evidence/`) binds an
evaluated artifact, by digest, to a sealed run.

## Security flaws in the format

A weakness in AEF itself (a way to make a tampered run verify, an ambiguity two verifiers resolve differently) is
reported privately, as [SECURITY.md](SECURITY.md) describes, never in a public issue. The fix ships as an erratum or a
new minor version with a corpus vector that would have caught it, and the changelog credits the reporter unless they
ask otherwise.

## Conduct

Discussions follow the code of conduct of the repository that hosts AEF (for AgentEval, `CODE_OF_CONDUCT.md` at its
root, the Contributor Covenant).
