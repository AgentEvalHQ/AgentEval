#!/usr/bin/env python3
"""Checks that the AEF 1.0 specification, schemas and corpus agree with each other:

1. every rule id is defined once (**[XXX-n]**) and every cited id ([XXX-n]) is defined;
2. every rule id a corpus vector names in `rules` is defined;
3. every problem code the corpus expects is defined in a code table of the spec;
4. every field name the prose writes in backticks (camelCase) is a property in some writer schema.

Usage: python contracts/aef/tools/check_spec.py
"""
import json
import re
import sys
from pathlib import Path

AEF = Path(__file__).resolve().parents[1] / "1"
SPEC = sorted((AEF / "spec").glob("*.md"))
PREFIXES = "(?:ENC|RUN|RES|SUM|EVD|GATE|SEAL|OVL|SIG|CKP|LANE|DEC|PLAN|STRM|VER|SEC|CONF)"

# Backticked camelCase words in the prose that are not schema fields: placeholders, JSON Schema keywords, code.
NOT_FIELDS = {"ExportTraceServiceRequest", "signedBy", "LaneResult", "MeasurementState", "additionalProperties", "allOf",
              "anyOf", "effectiveState", "endTimeUnixNano", "envelopeResult", "expectedError", "keyid", "maxItems",
              "maxLength", "minItems", "oneOf", "payloadType", "publicKey", "readOnly", "sealedState",
              "startTimeUnixNano", "uniqueItems", "unsealedEvents", "verifiesFor", "writeOnly"}


def schema_properties():
    names = set()

    def walk(n):
        if isinstance(n, dict):
            for k, v in (n.get("properties") or {}).items():
                names.add(k)
            for v in n.values():
                walk(v)
        elif isinstance(n, list):
            for v in n:
                walk(v)
    for p in (AEF / "schemas" / "writer").glob("*.schema.json"):
        walk(json.loads(p.read_text(encoding="utf-8")))
    return names


def main():
    problems = []
    text = {p.name: p.read_text(encoding="utf-8") for p in SPEC}
    defined = {}
    for name, t in text.items():
        for m in re.finditer(r"\*\*\[(" + PREFIXES + r"-[0-9]+)\]", t):
            defined.setdefault(m.group(1), []).append(name)
    for rid, where in defined.items():
        if len(where) > 1:
            problems.append(f"rule {rid} defined {len(where)} times: {where}")
    cited = {m.group(1) for t in text.values() for m in re.finditer(r"\[(" + PREFIXES + r"-[0-9]+)\]", t)}
    for rid in sorted(cited - set(defined)):
        problems.append(f"rule {rid} cited but never defined")

    # Problem codes: the first column of tables whose header names a code.
    codes = set()
    for t in text.values():
        for table in re.findall(r"( *\| Code \|[^\n]*\n(?: *\|[^\n]*\n)+)", t):
            codes.update(re.findall(r"^ *\| `([a-z-]+)` \|", table, flags=re.M))
    # Codes defined in running prose: CKP-7 / CKP-8 / SIG-5 name theirs in backticks inside the rule.
    for rid in ("CKP-7", "CKP-8", "SIG-5", "SIG-2", "SIG-1"):
        for name, t in text.items():
            m = re.search(r"\*\*\[" + rid + r"\][\s\S]*?(?=\n- \*\*\[|\n## |\Z)", t)
            if m:
                codes.update(re.findall(r"`([a-z]+(?:-[a-z]+)+|[a-z]+)`", m.group(0)))

    used_rules, used_codes = set(), set()
    conf = AEF / "conformance"
    for exp in conf.rglob("expected.json"):
        e = json.loads(exp.read_text(encoding="utf-8"))
        used_rules.update(e.get("rules", []))
        for p in e.get("problems", []) or []:
            used_codes.add(p[1] if isinstance(p, list) else p["problem"] if isinstance(p, dict) else p)
        for s in e.get("signatures", []) or []:
            used_codes.add(s["result"])
        if e.get("envelopeResult"):
            used_codes.add(e["envelopeResult"])
    for name in ("paths.json",):
        for item in json.loads((conf / name).read_text(encoding="utf-8")):
            used_rules.update(item.get("rules", []))
            used_codes.update(p[1] for p in item["problems"])
    for rid in sorted(used_rules - set(defined)):
        problems.append(f"the corpus names rule {rid}, which the spec does not define")
    for code in sorted(used_codes - codes):
        problems.append(f"the corpus expects problem code '{code}', which no code table of the spec defines")

    props = schema_properties()
    for name, t in text.items():
        for word in sorted(set(re.findall(r"`([a-z]+[A-Z][A-Za-z0-9]*)`", t))):
            if word not in props and word not in NOT_FIELDS:
                problems.append(f"{name}: `{word}` looks like a field but no writer schema has it")

    print(f"{len(defined)} rules defined, {len(cited)} cited; {len(used_rules)} named by the corpus; "
          f"{len(used_codes)} problem codes used, {len(codes)} defined")
    for p in problems:
        print("  " + p)
    print("spec, schemas and corpus agree" if not problems else f"{len(problems)} problem(s)")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
