#!/usr/bin/env python3
"""Derives the AEF 1.0 reader schemas (tolerant) from the writer schemas (strict).

A reader accepts what a later minor version may add: unknown fields, enum values it does not know (it treats them as
'other') except in the enums VER-9 closes, any 1.x schemaVersion, and a kind it does not know in a union discriminated by 'kind'. Everything else
stays: required fields, types, patterns, consts and the conditional rules. The subtrees of 'if' and 'not' are copied
unchanged: an 'if' selects a rule and a 'not' prohibits, so relaxing either would change what the rule means. The
.NET conformance tests derive the same schemas independently and compare.

Usage: python contracts/aef/tools/derive_reader.py
"""
import json
from pathlib import Path

SCHEMAS = Path(__file__).resolve().parents[1] / "1" / "schemas"


def kind_of(branch):
    """The const 'kind' of a branch of a discriminated union, or None."""
    if not isinstance(branch, dict):
        return None
    kind = branch.get("properties", {}).get("kind", {})
    return kind.get("const") if isinstance(kind, dict) and isinstance(kind.get("const"), str) else None


# Enums closed for major 1 (spec 07, VER-9): the rules across files compute with them, so a reader keeps them closed.
CLOSED_POINTERS = {
    ("common.schema.json", "/$defs/state"),
    ("run.schema.json", "/properties/status"),
    ("metrics.schema.json", "/properties/metrics/items/properties/kind"),
    # A trust policy is an input to verification (SIG-4): a verifier cannot honour a restriction it does not know.
    ("trust-policy.schema.json", ""),
}


def derive(node, strict=False, file="", pointer=""):
    if isinstance(node, list):
        return [derive(item, strict, file, f"{pointer}/{i}") for i, item in enumerate(node)]
    if not isinstance(node, dict):
        return node
    out = {}
    if not strict and (file, pointer) in CLOSED_POINTERS:
        return derive(node, True, file, pointer)  # closed: copied as the writer has it
    for key, value in node.items():
        if key == "$id":
            out[key] = value.replace("/aef/1/writer/", "/aef/1/reader/")
        elif strict:
            out[key] = derive(value, True, file, f"{pointer}/{key}")
        elif key == "type" and "enum" in node:
            continue  # the enum below decides the type, whatever the key order
        elif key == "additionalProperties" and value is False:
            continue  # unknown fields are allowed
        elif key == "enum":
            # Open: an unknown value reads as 'other'. A nullable enum stays nullable.
            out["type"] = node["type"] if isinstance(node.get("type"), list) else ["string", "null"] if None in value else "string"
        elif key == "const" and value == "1.0":
            out["type"] = "string"
            out["pattern"] = "^1[.][0-9]+$"  # any minor of the known major ($: end of input, ENC-15)
        elif key in ("if", "not"):
            out[key] = derive(value, True, file, f"{pointer}/{key}")
        elif key == "oneOf" and isinstance(value, list) and value and all(kind_of(b) for b in value):
            # A union discriminated by kind: known kinds keep their rules; an unknown kind is accepted as 'other'.
            known = [kind_of(b) for b in value]
            out["anyOf"] = [derive(b, False, file, f"{pointer}/oneOf/{i}") for i, b in enumerate(value)] + [
                {"type": "object", "required": ["kind"], "properties": {"kind": {"type": "string", "not": {"enum": known}}}}]
        else:
            out[key] = derive(value, False, file, f"{pointer}/{key}")
    return out


def main():
    (SCHEMAS / "reader").mkdir(exist_ok=True)
    for writer in sorted((SCHEMAS / "writer").glob("*.schema.json")):
        schema = json.loads(writer.read_text(encoding="utf-8"))
        reader = derive(schema, False, writer.name, "")
        reader["description"] = "READER (tolerant), derived from the writer schema by tools/derive_reader.py. " + schema.get("description", "")
        (SCHEMAS / "reader" / writer.name).write_bytes((json.dumps(reader, indent=2, ensure_ascii=False) + "\n").encode("utf-8"))


if __name__ == "__main__":
    main()
