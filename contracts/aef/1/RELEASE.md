# AEF 1.0 release identity

Status: prepared for the stable release; publication requires the `aef-1.0` tag. An untagged branch is a candidate, not a published specification.

The release is identified by both:

- Git tag: `aef-1.0`, on the reviewed AgentEval 1.0 release source.
- Corpus index: `contracts/aef/1/conformance/index.json`, SHA-256 `1bd7414e9517eba0add126702ca85b914c6bf34e7f4100a29666288603107445`.

The tag freezes the specification, schemas, corpus and reference tools together. Verify the index bytes from that tag; a matching version label alone does not establish corpus identity. This release preparation changes no schema or corpus bytes.

The [governance release criteria](../GOVERNANCE.md#versions-and-releases) require a passing whole-corpus Python reference run and a second implementation for every claimed conformance class. The existing AEF workflow checks the reference, mutation controls, byte-for-byte regeneration, tools and the .NET command-line driver. Those checks must pass on the release candidate before the tag is published. See [implementations](../IMPLEMENTATIONS.md) for class and signature-algorithm claims; optional Ed25519 signing is not claimed by the .NET implementation.

Publish this format tag alongside AgentEval `v1.0.0` after the normal review and CI gates. The library and CLI retain their existing experimental maturity exceptions; freezing AEF does not promote them.
