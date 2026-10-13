# OTLP observations

The OTLP observation receiver exposes opt-in `POST /v1/traces` in Mission Control.
It is disabled by default. Delivered components in `AgentEval.Results.Observations` are the bounded
`IOtlpPayloadReader`, validated `OtlpIngestionLimits`, an immutable trace model
and both `OtlpJsonTraceDecoder` and `OtlpProtobufTraceDecoder`, normalization,
content policy, session grouping and the separate `IOtlpObservationStore`.
Payload reading and decoding alone do not persist observations or establish evaluation results.

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
Versioned structural normalization is implemented; see
[the dialect mappings](otlp-dialects.md). Grouping, retention and the content
policy remain the next implementation slices. Tests prove the payload
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

## Content policy before persistence

`IOtlpContentPolicy` consumes a batch already validated by a bounded decoder.
The default `Off` mode retains only the resource and span attribute allowlists
shared with normalization. It removes span names, trace state, status messages,
events, scope/link attributes and resource entity references. Link topology,
numeric status, timestamps, flags and producer loss counters remain intact.
Recognized attributes with invalid values become empty typed placeholders;
normalization still reports them as invalid instead of treating them as missing
or silently accepting another alias. The original invalid payload is discarded.

The returned snapshot records `otlp-content/1`, the capture mode and numeric
removal counts. Receiver removals never inflate producer-reported dropped
counts. Removed events and entity references count as whole objects, without
counting their nested values again. Reports contain no removed keys or values.
`On` is explicit opt-in and retains the immutable decoded batch; neither mode
persists the original request bytes. These are per-application reports, not
cumulative reports if a caller filters an already filtered batch again.

This boundary is an allowlist, not secret detection: retained service/session
identifiers, model/tool names, scope metadata and schema URLs are producer-controlled
strings and can contain sensitive information. Producers must configure these
metadata fields appropriately. The policy service does not replace decoder
validation or budgets. The observation store applies this policy before writing,
including requests received through the HTTP endpoint.

## Observed-session grouping

`IOtlpSessionGrouper` groups filtered batches using a receiver-configured source
namespace, resource `service.namespace` / `service.name` / `service.instance.id`,
and a valid consensus session ID. Service name is required for grouping across
traces; optional service fields may be absent but must not be invalid or conflict.
Different resource instances remain separate, even when they report the same
session. Instrumentation scopes do not split a session.

Missing, invalid or conflicting session fields fall back to trace ID with an
explicit reason per member. Missing or invalid service identity also prevents
grouping across traces. Trace fallback can only distinguish the metadata actually
reported: producers sharing a source and identical incomplete resource identity
cannot be distinguished within the same trace. A later session-bearing report
does not retroactively assign earlier sessionless reports to that session.

Group IDs use a versioned, unambiguous JSON identity tuple hashed with SHA-256
and an `obs-` prefix. They are observation keys, not AEF run hashes, signatures,
tenant authentication or evaluation results. Source must come from receiver
configuration, not an untrusted request attribute. Exact string values and
presence states matter; no Unicode normalization or heuristic identity repair
is applied. Scope and source evidence remain available through normalized members.

Input ordering does not affect group IDs. Members sort by start time, trace and
span ID, with original positions retained for joining to the filtered batch.
Duplicate and conflicting reports remain separate members; the grouper never
chooses an arrival-order winner or infers telemetry completeness. The observation
store handles record-level retries; cross-batch query reconciliation remains an
index/query responsibility.

## Durable observation records

`OtlpObservationStore` owns an explicitly configured absolute directory with an
exclusive process lease and version marker. It refuses nonempty unrecognized
roots, unsupported markers, unexpected layout entries and linked/special files.
Unix requires the existing native regular-file classifier. The operating-system
owner must prevent other processes from replacing paths while the store is open.
Host integration enforces separation from viewer and configured AEF roots before
opening the store; the standalone service cannot discover other services' configured paths.

`Accept` takes decoder-validated input, applies the configured content policy and
groups the filtered batch. A versioned record stores the filtered snapshot,
removal report, source and normalized grouping provenance. No raw request bytes
are written. Type discriminators preserve all AnyValue types, including exact
integers, bytes and non-finite doubles when capture is explicitly enabled.

Serialization writes through a byte-limited stream into one scratch file. The
file is flushed to disk before a same-directory-filesystem move publishes it
under a SHA-256 `batch-` identifier. Publication is the commit point; cancellation
after publication does not turn accepted evidence into a reported cancellation.
This supports process-restart recovery; directory-entry survival across power
loss depends on the filesystem and is not a portable transactional guarantee.

An identical serialized **filtered record** is a retry. The store verifies the
existing record's bytes before accepting that retry, including after restart and
at full capacity. Sorted normalized-field serialization avoids process-randomized
dictionary ordering. Differences only in omitted content can produce identical
filtered evidence; request-byte identity is neither retained nor promised.
Different ordering, batching or retained metadata may produce different records.
Duplicate spans and conflicting variants in those records remain available for
explicit query reconciliation rather than being overwritten.

Defaults are 4096 records (maximum 100000), 16 MiB per record (maximum 64 MiB),
and 512 MiB accepted bytes (maximum 64 GiB). One extra bounded scratch file may
exist. Capacity exhaustion refuses new records and never evicts accepted data.
Concurrent operations are refused as busy rather than queued. Interrupted known
scratch is removed under the exclusive lease at startup; unknown or oversized
scratch causes refusal. Startup inventories bounded names and sizes; `Read`
verifies the selected record's digest before deserializing. These digests detect
changed bytes, not malicious replacement by the filesystem owner or authenticity.

Cancellation is checked between bounded operations and serialized writes.
Synchronous serialization tokens, reads, deserialization and the final flush/move
are not individually preemptible. Decoder limits, receiver concurrency and timeout
limits must still apply; the record byte cap is not an exact process-memory cap.
Changing capture from On to Off affects new records and does not redact history.

The independent fixture `scripts/verify-observation-store.ps1 -AssemblyPath
<AgentEval.Results.dll>` uses separate native writer and retry processes to
verify publication, cross-process identity, default filtering, integer precision
and scratch recovery. HTTP test-host checks cover both formats, compression,
access refusal, limits, default-off persistence and unavailable storage. Actual
Kestrel transport failure fixtures remain the next validation step.

## Mission Control configuration

Set these configuration values explicitly (environment-variable equivalents use
double underscores, for example `AgentEval__Otlp__Enabled`):

| Setting under `AgentEval:Otlp` | Value / default |
| --- | --- |
| `Enabled` | `false`; set `true` to register the receiver |
| `Root` | Required absolute dedicated observation directory |
| `Source` | Required stable receiver namespace, at most 256 UTF-8 bytes |
| `ApiKey` | Required secret: 32–256 printable ASCII characters, no spaces |
| `Capture` | `Off`; only explicit `On` retains content |
| Request limits | Names and defaults from the resource-budget table above |
| `MaxRecords`, `MaxRecordBytes`, `MaxStoredBytes` | Store limits described above |

Enabling OTLP does not require enabling AEF uploads. Both receivers share the
same access-policy implementation, with independently configured keys. Send
`Authorization: Bearer <key>`. Browser-origin/fetch-site headers are refused.
Direct non-loopback connections require HTTPS; forwarded headers do not bypass
that requirement. Authentication is checked before reading the request body.

The receiver accepts `application/json` and `application/x-protobuf`, with optional
gzip compression. It holds a bounded concurrency slot through payload reading,
decoding and durable acceptance. JSON success is `{}`; protobuf success is the
empty `ExportTraceServiceResponse`. Errors carry a path-free `Status.message`
in the matching encoding. Malformed payloads return 400, access failures 401/403,
byte/structure limits 413, unsupported media/compression 415, overload 429 and
timeout/storage failures 503. Retryable refusals include `Retry-After: 1`.
No successful response is sent before the store commits or verifies a retry.
