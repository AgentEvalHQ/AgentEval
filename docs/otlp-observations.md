# OTLP observations

The OTLP observation receiver is under implementation. No `/v1/traces`
endpoint is enabled by this change. Delivered components in `AgentEval.Results.Observations` are the bounded
`IOtlpPayloadReader`, validated `OtlpIngestionLimits`, an immutable trace model
and both `OtlpJsonTraceDecoder` and `OtlpProtobufTraceDecoder`. Payload reading
and decoding do not persist observations or establish evaluation results.

## Transport contract

The receiver's scope is OTLP/HTTP `POST /v1/traces`, with
`application/json` and `application/x-protobuf`. Requests may be uncompressed
or gzip-compressed. Responses will use the request's serialization format.
gRPC, metric and log collection are separate capabilities, outside this trace
receiver. The protocol reference is the
[OTLP specification](https://opentelemetry.io/docs/specs/otlp/), checked at
version 1.11.0 on 2026-10-13.

The implementation must keep the receiver opt-in, validate limits at startup,
and apply the same owner-token, browser-origin and direct-peer transport policy
as AEF ingestion. Observation storage must be explicitly configured, separately
owned, and disjoint from both accepted AEF runs and the viewer store. There is
no inferred writable directory. A successful export response must follow durable
observation acceptance; reading or decoding a payload is insufficient.

## Resource budgets

`OtlpIngestionLimits` defines these defaults and allowed ranges:

| Budget | Default | Allowed range | Enforcement stage |
| --- | --- | --- | --- |
| Transport bytes | 4 MiB | 1 byte–64 MiB | Delivered payload reader |
| Decoded bytes | 4 MiB | 1 byte–64 MiB | Delivered payload reader |
| Spans/request | 4,096 | 1–100,000 | Delivered JSON and protobuf decoders |
| Attributes/owner | 128 | 1–1,024 | Delivered JSON and protobuf decoders |
| Total collection items/request | 100,000 | 1–1,000,000 | Delivered JSON and protobuf decoders |
| Nested message/value depth | 32 | 8–64 | Delivered JSON and protobuf decoders |
| UTF-8 string or bytes value | 16 KiB | 1 byte–1 MiB | Delivered JSON and protobuf decoders |
| Active receiver requests | 2 | 1–16 | Receiver lifetime, pending |
| Timeout | 30 seconds | 1–600 seconds | Receiver lifetime, pending |

The 4 MiB application default targets development/evaluation telemetry; it is
configurable up to 64 MiB. These budgets are independent: compression cannot
increase the decoded budget. The receiver must retain its concurrency slot
through reading, decoding and acceptance, and pass its linked timeout/cancellation
token through each phase. The payload reader itself is stateless across calls
and does not enforce receiver concurrency or wall-clock time.

`OtlpPayloadReader` uses a 64 KiB copy buffer and a bounded output stream. It
requests at most the remaining transport/decoded allowance plus one lookahead
byte. Gzip members share one aggregate decoded budget. Trailing transport bytes
still count even when the decompressor stops reading. Compression decoding uses
the platform `GZipStream`; the message decoder must separately validate OTLP
structure. Returned arrays are caller-owned; the input remains open on success,
refusal and cancellation. Output accumulation and the returned copy can coexist,
so the decoded byte budget is not an exact process-memory ceiling.

## Evidence and content boundary

Observed traces describe what telemetry reported. They do not establish an
evaluation verdict, benchmark completeness, suite compatibility, or a sealed AEF
run hash. The existing `AefOtelImporter` converts explicit evaluation log events;
it is not this live trace receiver. AEF evidence and observation storage remain
separate. A later SQLite index is derived from those authoritative files.

Content capture will default to off. Structural metadata must be allowlisted;
prompt, completion and tool payload content cannot be persisted by default.
Versioned dialect normalization, grouping, retention and this
content policy remain the next implementation slices. Tests prove the payload
boundary and both decoders, not a completed receiver.

## JSON trace decoding

`OtlpJsonTraceDecoder` implements `IOtlpTraceDecoder` and returns an immutable
`OtlpTraceBatch`. The message fields follow the pinned upstream
[trace definitions](https://github.com/open-telemetry/opentelemetry-proto/blob/v1.9.0/opentelemetry/proto/trace/v1/trace.proto),
[common values](https://github.com/open-telemetry/opentelemetry-proto/blob/v1.9.0/opentelemetry/proto/common/v1/common.proto)
and [resource definitions](https://github.com/open-telemetry/opentelemetry-proto/blob/v1.9.0/opentelemetry/proto/resource/v1/resource.proto).
Resource/scope schema URLs, scope version, all flag bits, events, links and
producer-reported dropped counts remain available to normalization. Entity
references are retained as reported, not verified identities.

Integers are parsed exactly, including integral exponent notation; timestamps
remain unsigned nanoseconds. Integer and quoted double representations have an
additional 128-character budget. Numeric enum values survive even when unknown;
enum names are refused. Typed AnyValue data retains signed integers, binary64
values (including explicit non-finite values), bytes, arrays and key-value lists.
IDs must have the prescribed nonzero hexadecimal shape and are normalized to
lower case. Empty parent IDs are allowed. Duplicate attribute keys and ambiguous
AnyValue alternatives are refused.

Unknown message fields are ignored semantically but consume structural budgets.
JSON must be valid UTF-8. Null fields act as absent fields; null repeated entries
are refused. General duplicate message fields use the final JSON value; AnyValue
rejects multiple non-null alternatives. All JSON nodes, including unknown fields,
count against the aggregate item budget. String limits count UTF-8 bytes. The
payload is parsed into a bounded JsonDocument before model construction; parsing
is synchronous, with cancellation checked before parsing and during traversal.
The returned model owns its strings and immutable collections after that document
is disposed. No raw JSON is retained by the decoder.

## Protobuf trace decoding

`OtlpProtobufTraceDecoder` uses the same immutable model and limits, without an
intermediate JSON representation. It implements the pinned schema's complete
trace field mapping and keeps unknown fields out of the model. Singular messages
merge, repeated messages append, scalar values replace earlier values, and
AnyValue alternatives follow protobuf oneof replacement/merge semantics.
Unknown fields are skipped for all wire types, including matched, depth-bounded
groups. Known field numbers with incompatible wire types are skipped as unknown,
matching generated protobuf parsers.

Every wire field consumes the aggregate item budget, including unknown and
superseded fields. Nested messages/groups consume the depth budget. Byte lengths,
varint termination/overflow, tags, fixed-width reads and UTF-8 strings are checked
before conversion. The typed model applies ID and duplicate-attribute checks too.
Unsigned 32-bit and enum fields use protobuf's low-32-bit conversion semantics;
nonzero varint booleans become true. These wire conversion rules are verified
against Google's parser, rather than inferred from JSON numeric rules.

The fixtures under `tests/AgentEval.Results.Tests/Observations/Fixtures` are
produced with Google's protobuf runtime from the vendored upstream schemas.
They cover full typed traces, message/oneof merging, all unknown wire types and
scalar edge cases. Each binary fixture has an independently decoded JSON pair.
See [fixture provenance and regeneration](../scripts/otlp-fixtures/README.md).
Tests compare the complete models from both encodings and exercise malformed
wire data, resource budgets, ownership and deterministic mutations. The decoder
is a bounded schema implementation, not a claim of general protobuf conformance.
