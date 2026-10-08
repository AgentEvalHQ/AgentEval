#!/usr/bin/env python3
"""Derives the AEF v2 reader schemas (tolerant) from the writer schemas (strict).

A reader accepts what a later minor version may add: unknown fields, enum values it does not know (it treats them as
'other'), any 2.x schemaVersion, and a kind it does not know in a union discriminated by 'kind'. Everything else
stays: required fields, types, patterns, consts and the conditional rules. The subtrees of 'if' and 'not' are copied
unchanged: an 'if' selects a rule and a 'not' prohibits, so relaxing either would change what the rule means. The
.NET conformance tests derive the same schemas independently and compare.

Usage: python contracts/aef/tools/derive_reader.py
"""
import json
from pathlib import Path

V2 = Path(__file__).resolve().parents[1] / "v2" / "schemas"


def kind_of(branch):
    """The const 'kind' of a branch of a discriminated union, or None."""
    if not isinstance(branch, dict):
        return None
    kind = branch.get("properties", {}).get("kind", {})
    return kind.get("const") if isinstance(kind, dict) and isinstance(kind.get("const"), str) else None


def derive(node, strict=False):
    if isinstance(node, list):
        return [derive(item, strict) for item in node]
    if not isinstance(node, dict):
        return node
    out = {}
    for key, value in node.items():
        if key == "$id":
            out[key] = value.replace("/aef/v2/writer/", "/aef/v2/reader/")
        elif strict:
            out[key] = derive(value, True)
        elif key == "additionalProperties" and value is False:
            continue  # unknown fields are allowed
        elif key == "enum":
            # Open: an unknown value reads as 'other'. A nullable enum stays nullable.
            out["type"] = node["type"] if isinstance(node.get("type"), list) else "string"
        elif key == "const" and value == "2.0":
            out["type"] = "string"
            out["pattern"] = "^2\\.[0-9]+(?!\\n)$"  # any minor of the known major
        elif key in ("if", "not"):
            out[key] = derive(value, True)
        elif key == "oneOf" and isinstance(value, list) and value and all(kind_of(b) for b in value):
            # A union discriminated by kind: known kinds keep their rules; an unknown kind is accepted as 'other'.
            known = [kind_of(b) for b in value]
            out["anyOf"] = [derive(b) for b in value] + [
                {"type": "object", "required": ["kind"], "properties": {"kind": {"type": "string", "not": {"enum": known}}}}]
        else:
            out[key] = derive(value)
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
