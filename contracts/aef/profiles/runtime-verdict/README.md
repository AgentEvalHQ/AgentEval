# AEF runtime-verdict profile

The evidence an interceptor (a Gatekeeper gate, an AGENT-HOOKS interceptor) attaches to a runtime verdict: whether
the call was evaluated at all, what kind of evidence backs the verdict, what the interception point could physically
have done, and how far the judge behind it was calibrated.

Its current published form is **AEVP 0.1**, unchanged:

- Specification: [`docs/aevp/AEVP-0.1.md`](../../../../docs/aevp/AEVP-0.1.md)
- Schema: [`aevp-0.1.schema.json`](aevp-0.1.schema.json), a byte-for-byte copy of the schema the library ships
  (`src/AgentEval.MAF.AgentHooks/Aevp/aevp-0.1.schema.json`); a test keeps the two identical.

AEVP 0.1 keeps its name and `$id` as its published lineage. It is a draft profile published in this repository, not
a standard; see its §7.
