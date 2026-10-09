# AEF 1.0: a trust policy

*Generated from [`schemas/writer/trust-policy.schema.json`](../schemas/writer/trust-policy.schema.json) by `tools/gen_reference.py`. Do not edit: edit the schema.*

SIG-4: the public keys a verifier trusts, each with the identity it speaks for and what that identity may do beyond signing. An input to verification, never read from a run. Closed for readers too (spec 07, VER-9): a verifier cannot honour a restriction it does not know, so it refuses a policy with a member it does not know. A 'may' value it does not know grants nothing.

| Field | Type | Required | Bounds | Description |
|---|---|---|---|---|
| `keys` | array of [key](#key) | yes | ≤ 10000 items |  |

## Definitions

### key

Type: object

| Field | Type | Required | Bounds | Description |
|---|---|---|---|---|
| `identity` | string | yes | ≥ 1 chars; ≤ 1024 chars | Who the key speaks for, for example git:alice@example.com, spiffe://..., oidc:issuer/subject. |
| `publicKey` | string | yes | ≤ 65536 chars; pattern `^-----BEGIN PUBLIC KEY-----\r?\n[A-Za-z0-9+/=\r\n]+-----END PUBLIC KEY-----(\r?\n)?$` | The SubjectPublicKeyInfo as PEM, in RFC 7468's strict form (SIG-3 checks the rest). |
| `may` | array of string |  | ≤ 64 items; unique | What the identity may do beyond signing: 'redact' authorizes redactions (OVL-10). Values a verifier does not know grant nothing. |
