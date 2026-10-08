# Reporting a flaw in AEF

A security flaw in the format is anything that lets evidence deceive a conforming verifier. Examples:
- a run changed after sealing that still verifies;
- a signature accepted for the wrong key;
- a checkpoint that approves without the evidence its rules require;
- a rule two conforming verifiers read differently in a way an attacker can choose.

**Report it privately, never in a public issue.** Use the private channel in the security policy of the repository
that hosts AEF (for the AgentEval repository, `SECURITY.md` at its root). Include:
- the rule ids involved;
- a minimal example (files, or a script that builds them);
- what a verifier reports, against what it should report.

The editors acknowledge a report within 48 hours and assess it within 7 days. They publish the fix as an erratum or a
minor version, with a corpus vector that would have caught the flaw. Disclosure follows the hosting repository's
policy (90 days by default), and the changelog credits the reporter unless they ask otherwise.
