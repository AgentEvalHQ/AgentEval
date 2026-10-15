# Pinned EvalPort ResultSet schema

`resultset.schema.json` is copied unchanged from
`adhabnr-ux/evalport` commit `d0e90c48cfa8cabceefb066dcfa757ea3f5c85f3`,
path `schema/resultset.json`, retrieved 2026-10-14.
SHA-256: `c313bc8a726e823db6a95ed7053b2b0985b64cc6b318fa0b7e2c031c8d4206f8`.

Copyright 2026 EvalPort Working Group. Distributed under the accompanying upstream
Apache-2.0 `LICENSE`. No upstream NOTICE file exists at this revision.

The adapter embeds the schema for offline validation. At compilation of the schema
only, the SemVer pattern's JavaScript `\d` is replaced by `[0-9]` to preserve its
ASCII semantics in the existing .NET schema engine. The vendored file is unchanged.
Schema `format` remains an annotation; timestamp conversion must separately validate
dates. Exact Int64 attempt/duration checks and null-score/uniqueness rules supplement
schema validation. This is a pinned interoperability target, not a claim about the
latest upstream release or complete semantic validation of aggregation metadata.
