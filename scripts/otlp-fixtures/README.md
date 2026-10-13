# Independent OTLP fixtures

The `proto/` files are unmodified copies from
[open-telemetry/opentelemetry-proto v1.9.0](https://github.com/open-telemetry/opentelemetry-proto/tree/v1.9.0/opentelemetry/proto).
Copyright 2019, OpenTelemetry Authors. They are licensed under Apache-2.0;
their original notices are retained and the license is included in
[LICENSE-APACHE-2.0.txt](LICENSE-APACHE-2.0.txt).
`schema-sha256.json` pins each source file's exact bytes.

The generator is original AgentEval test tooling (MIT). It compiles a descriptor
set with `protoc`, constructs messages with Google's Python protobuf runtime,
and writes binary/JSON pairs. It also parses handcrafted merge/unknown/scalar
wire cases through Google's runtime before producing their expected JSON.
OTLP's hexadecimal ID and numeric enum JSON conventions are applied explicitly.
No application decoder is used to generate expected data.

Regenerate from the repository root:

```text
python scripts/otlp-fixtures/generate.py --protoc /path/to/protoc
```

Dependencies: local `protoc` and Python `google.protobuf`. The checked-in fixtures
were generated with protoc 29.0 and protobuf 6.33.5. Generation is offline after
those tools are installed; ordinary .NET tests need neither tool.

Outputs live in `tests/AgentEval.Results.Tests/Observations/Fixtures`:

- `complete`: metadata, scope/resource provenance, entity references, typed
  attributes, precision limits, events, links, status, flags and dropped counts.
- `merge-and-unknown`: singular-message merge, repeated fields, oneof merge and
  replacement, and unknown varint/fixed64/bytes/group/fixed32 fields.
- `wire-scalars`: signed enum/integer, noncanonical nonzero boolean, uint32
  truncation and non-finite double behavior from the independent runtime.

These are targeted interoperability fixtures, not an upstream conformance suite.
