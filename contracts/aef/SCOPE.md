# Scope (draft, pending counsel)

*This is the scope a specification licence's patent commitment would cover, as GOVERNANCE.md plans for the 1.0
release. Nothing here is in force until counsel confirms and GOVERNANCE.md says so.*

The scope is the **normative AEF specification**: `1/spec/01-introduction.md` to `1/spec/09-conformance.md` and the
writer schemas they make normative. Within it, the commitment covers what an implementation strictly needs to:

- write, read and validate the files AEF defines (the run folder, checkpoint manifests, run plans, runner manifests and
  event streams);
- compute and verify seals, overlay batch chains, effective views and signatures as AEF specifies them;
- evaluate lanes and the decision function, and verify checkpoints and runner streams;
- run the conformance corpus.

It does not cover anything outside the normative specification. That includes:
- any product built on AEF, such as a dashboard, a governance service or a hosting service;
- the informative documents;
- any technique an implementation may use but the specification does not require.
