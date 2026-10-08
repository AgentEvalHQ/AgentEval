#!/usr/bin/env python3
"""Derives the AEF v2 reader schemas (tolerant) from the writer schemas (strict).

A reader accepts what a later minor version may add: unknown fields, enum values it does not know (it treats them as
'other'), and any 2.x schemaVersion. Everything else stays: required fields, types, patterns, consts and the
conditional rules. Conditions (the 'if' of an if/then) are copied unchanged, since they select a rule rather than
restrict a value. The .NET conformance tests derive the same schemas independently and compare.

Usage: python contracts/aef/tools/derive_reader.py
"""
import json
from pathlib import Path

V2 = Path(__file__).resolve().parents[1] / "v2" / "schemas"


def derive(node, in_condition=False):
    if isinstance(node, list):
        return [derive(item, in_condition) for item in node]
    if not isinstance(node, dict):
        return node
    out = {}
    for key, value in node.items():
        if key == "$id":
            out[key] = value.replace("/aef/v2/writer/", "/aef/v2/reader/")
        elif key == "additionalProperties" and value is False and not in_condition:
            continue  # unknown fields are allowed
        elif key == "enum" and not in_condition:
            out["type"] = "string"  # open: an unknown value reads as 'other'
        elif key == "const" and value == "2.0" and not in_condition:
            out["type"] = "string"
            out["pattern"] = "^2\\.[0-9]+$"  # any minor of the known major
        elif key == "if":
            out[key] = derive(value, in_condition=True)
        else:
            out[key] = derive(value, in_condition)
    return out


def main():
    (V2 / "reader").mkdir(exist_ok=True)
    for writer in sorted((V2 / "writer").glob("*.schema.json")):
        schema = json.loads(writer.read_text(encoding="utf-8"))
        reader = derive(schema)
        reader["description"] = "READER (tolerant), derived from the writer schema by tools/derive_reader.py. " + schema.get("description", "")
        (V2 / "reader" / writer.name).write_bytes((json.dumps(reader, indent=2, ensure_ascii=False) + "\n").encode("utf-8"))


if __name__ == "__main__":
    main()
