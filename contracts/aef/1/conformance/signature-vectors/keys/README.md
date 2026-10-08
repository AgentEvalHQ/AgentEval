# Test keys: never trust them

The private keys are derived from public strings (see `tools/signature_vectors.py`): anyone can sign with them.
`rsa.pub.pem` is an RSA key AEF does not support; its modulus is arbitrary.

| Key | Key id |
|---|---|
| `ecdsa-a` | `sha256:b5465da5f72069887e490258fc60e5319c05344df17f2c9d91ad2c779d07734e` |
| `ecdsa-b` | `sha256:73af97a27d5d09ef4bf48e650748da6cab2b7e75b9345d129ebcef17449d7e34` |
| `ed25519-a` | `sha256:2986cef07918485086a97ba70ef87ea966b8c3634fb59bcafc4e8b6a9ea5d787` |
| `rsa` | `sha256:53b4e942b234837e1786f28805d07b4eb68d1efca307d2eaff65f0e06f714c5f` |
