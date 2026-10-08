# Test keys: never trust them

The private keys are derived from public strings (see `tools/signature_vectors.py`): anyone can sign with them.
`rsa.pub.pem` is an RSA key AEF does not support; its modulus is arbitrary.

| Key | Key id |
|---|---|
| `ecdsa-a` | `sha256:b5465da5f72069887e490258fc60e5319c05344df17f2c9d91ad2c779d07734e` |
| `ecdsa-b` | `sha256:73af97a27d5d09ef4bf48e650748da6cab2b7e75b9345d129ebcef17449d7e34` |
| `ed25519-a` | `sha256:2986cef07918485086a97ba70ef87ea966b8c3634fb59bcafc4e8b6a9ea5d787` |
| `rsa` | `sha256:53b4e942b234837e1786f28805d07b4eb68d1efca307d2eaff65f0e06f714c5f` |

## Private keys: test keys, never trust them

`<key>.key.pem` is the private key of `<key>.pub.pem`, an unencrypted PKCS#8 `PRIVATE KEY` block (RFC 5208,
RFC 5958): for P-256 an RFC 5915 ECPrivateKey with its public key, for Ed25519 the 32-byte seed (RFC 8410).
The write-side `sign` vectors (spec 09, `write-vectors/sign/`) sign with them. They are published, so anyone
can sign with them: never put them, or their public keys, in a real trust policy.
