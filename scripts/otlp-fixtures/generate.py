#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Regenerate independent OTLP fixtures with protoc and Google's Python protobuf runtime.

Usage: python scripts/otlp-fixtures/generate.py --protoc /path/to/protoc
No network or provider calls. Schemas are pinned upstream v1.9.0 copies.
"""
import argparse
import base64
import hashlib
import json
from pathlib import Path
import subprocess
import tempfile

from google.protobuf import descriptor_pb2, descriptor_pool, json_format, message_factory


def varint(value):
    value &= (1 << 64) - 1
    out = bytearray()
    while value > 127:
        out.append((value & 127) | 128)
        value >>= 7
    out.append(value)
    return bytes(out)


def block(field, payload):
    return varint(field * 8 + 2) + varint(len(payload)) + payload


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--protoc", required=True)
    args = parser.parse_args()
    root = Path(__file__).resolve().parent
    repo = root.parent.parent
    schema = root / "proto"
    for relative, digest in json.loads((root / "schema-sha256.json").read_text()).items():
        if hashlib.sha256((schema / relative).read_bytes()).hexdigest() != digest:
            raise ValueError(f"Pinned schema bytes changed: {relative}")
    target = repo / "tests/AgentEval.Results.Tests/Observations/Fixtures"
    target.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="ae-otlp-protoc-") as temporary:
        descriptors = Path(temporary) / "trace.desc"
        subprocess.run([args.protoc, "-I", str(schema), "--include_imports", f"--descriptor_set_out={descriptors}",
                        "opentelemetry/proto/collector/trace/v1/trace_service.proto"], check=True)
        files = descriptor_pb2.FileDescriptorSet.FromString(descriptors.read_bytes())
    pool = descriptor_pool.DescriptorPool()
    for file in files.file:
        pool.Add(file)
    request_type = message_factory.GetMessageClass(pool.FindMessageTypeByName(
        "opentelemetry.proto.collector.trace.v1.ExportTraceServiceRequest"))
    request = request_type()
    resource = request.resource_spans.add()
    resource.schema_url = "resource-schema/1"
    resource.resource.attributes.add(key="service.name").value.string_value = "independent-fixture"
    resource.resource.dropped_attributes_count = 3
    entity = resource.resource.entity_refs.add(schema_url="entity/1", type="service")
    entity.id_keys.append("service.name")
    entity.description_keys.append("service.name")
    scope = resource.scope_spans.add(schema_url="scope-schema/2")
    scope.scope.name = "fixture-sdk"
    scope.scope.version = "1.2.3"
    scope.scope.dropped_attributes_count = 4
    scope.scope.attributes.add(key="scope.bool").value.bool_value = True
    span = scope.spans.add(trace_id=bytes.fromhex("0123456789abcdef" * 2), span_id=bytes.fromhex("0123456789abcdef"),
                           parent_span_id=bytes.fromhex("fedcba9876543210"), trace_state="vendor=value", name="op", kind=3,
                           start_time_unix_nano=18446744073709551614, end_time_unix_nano=18446744073709551615,
                           flags=4294967295, dropped_attributes_count=5, dropped_events_count=6, dropped_links_count=7)
    span.status.code = 2
    span.status.message = "reported status"
    values = span.attributes.add(key="typed.values").value.array_value.values
    values.add().string_value = "caf\u00e9"
    values.add().bool_value = True
    values.add().int_value = -9223372036854775808
    values.add().int_value = 9223372036854775807
    values.add().double_value = 1.25
    values.add().double_value = float("inf")
    values.add().bytes_value = bytes([0, 251, 255])
    values.add().kvlist_value.values.add(key="nested").value.string_value = "kept"
    values.add()  # explicit empty AnyValue
    event = span.events.add(time_unix_nano=9007199254740993, name="event", dropped_attributes_count=8)
    event.attributes.add(key="event.bytes").value.bytes_value = b"event"
    link = span.links.add(trace_id=bytes.fromhex("fedcba9876543210" * 2), span_id=bytes.fromhex("fedcba9876543210"),
                          trace_state="other=state", flags=0xffffffff, dropped_attributes_count=9)
    link.attributes.add(key="link.int").value.int_value = 123

    def otlp_json(value):
        # Google's generic JSON uses base64 for bytes; OTLP specifically uses hex for trace/span IDs.
        if isinstance(value, dict):
            return {key: (base64.b64decode(item).hex() if key in {"traceId", "spanId", "parentSpanId"}
                          else otlp_json(item)) for key, item in value.items()}
        if isinstance(value, list):
            return [otlp_json(item) for item in value]
        return value

    def save(name, wire):
        decoded = request_type.FromString(wire)  # Independent parse/merge semantics, not just serialization.
        result = otlp_json(json_format.MessageToDict(decoded, use_integers_for_enums=True))
        (target / f"{name}.pb").write_bytes(wire)
        (target / f"{name}.json").write_bytes((json.dumps(result, indent=2, ensure_ascii=True) + "\n").encode())

    save("complete", request.SerializeToString(deterministic=True))
    # Two singular scope fragments merge; scalar fields replace; two same-message oneof fragments merge,
    # a different oneof alternative clears the preceding collection, then the next repeated value survives.
    first_scope = block(1, b"first")
    second_scope = block(2, b"merged-version")
    any_array_a = block(5, block(1, block(1, b"old")))
    any_array_b = block(5, block(1, block(1, b"append")))
    any_replace = block(1, b"replace") + block(5, block(1, block(1, b"final")))
    attribute = block(1, b"merge") + block(2, any_array_a + any_array_b + any_replace)
    merge_attribute = block(1, b"array-merge") + block(2, any_array_a + any_array_b)
    span_wire = block(1, bytes.fromhex("0123456789abcdef" * 2)) + block(2, bytes.fromhex("0123456789abcdef"))
    span_wire += block(5, b"old-name") + block(5, b"final-name") + block(9, attribute) + block(9, merge_attribute)
    span_wire += block(15, block(2, b"merged-status")) + block(15, varint(3 * 8) + varint(2))
    scope_wire = block(1, first_scope) + block(1, second_scope) + block(2, span_wire)
    # Unknown fields: varint, fixed64, bytes, group and fixed32.
    unknown = varint(100 * 8) + varint(99) + varint(101 * 8 + 1) + bytes(8)
    unknown += block(102, b"unknown") + varint(103 * 8 + 3) + varint(1 * 8) + varint(1) + varint(103 * 8 + 4)
    unknown += varint(104 * 8 + 5) + bytes(4)
    save("merge-and-unknown", block(1, block(2, scope_wire)) + unknown)
    # Generated parsers treat nonzero varint booleans as true and retain the low 32 bits of uint32/int32.
    scalar_span = block(1, bytes.fromhex("0123456789abcdef" * 2)) + block(2, bytes.fromhex("0123456789abcdef"))
    scalar_span += varint(6 * 8) + varint(-1) + varint(10 * 8) + varint((1 << 32) + 5)
    scalar_span += block(9, block(1, b"bool") + block(2, varint(2 * 8) + varint(2)))
    scalar_span += block(9, block(1, b"integer") + block(2, varint(3 * 8) + varint(-1)))
    scalar_span += block(9, block(1, b"double") + block(2, varint(4 * 8 + 1) + bytes.fromhex("000000000000f87f")))
    save("wire-scalars", block(1, block(2, block(2, scalar_span))))
    print("Generated three JSON/protobuf fixture pairs using the upstream schemas and Google protobuf.")


if __name__ == "__main__":
    main()
