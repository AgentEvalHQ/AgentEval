#!/usr/bin/env python3
"""Compares two versions of the AEF writer schemas and fails on any change a minor version may not make (spec 07,
[VER-5]; spec 09, [CONF-5]).

A minor version only adds: optional fields, enum values, union kinds (a new 'kind' in a discriminated oneOf), $defs,
and annotations (description, title, examples, $comment). It may widen a bound or drop one. Everything else fails:
a removed or renamed field, a newly required field, a removed enum value, a narrowed bound, a new or changed pattern,
const or $ref, a changed type, and any change to a conditional rule (if/then/else, not, allOf) other than its
annotations. The check is deliberately conservative: a change it cannot prove additive (a pattern rewritten to an
equivalent one) fails, for a person to review. The schemaVersion const may move to a later minor of the same major.

Usage: python schema_diff.py OLD_WRITER_DIR NEW_WRITER_DIR
       python schema_diff.py --self-test
"""
import json
import re
import sys
from pathlib import Path

ANNOTATIONS = {"description", "title", "examples", "$comment", "default", "deprecated", "readOnly", "writeOnly", "format"}
UPPER = {"maxLength", "maxItems", "maximum", "exclusiveMaximum", "maxProperties", "maxContains"}
LOWER = {"minLength", "minItems", "minimum", "exclusiveMinimum", "minProperties", "minContains"}
WIDEN_WHEN_DROPPED = UPPER | LOWER | {"pattern", "uniqueItems"}
VERSION = re.compile(r"^1\.([0-9]+)$")


def kind_of(branch):
    if isinstance(branch, dict):
        k = branch.get("properties", {}).get("kind", {})
        if isinstance(k, dict) and isinstance(k.get("const"), str):
            return k["const"]
    return None


def diff(old, new, where, out):
    if isinstance(old, dict) and isinstance(new, dict):
        for key in old:
            if key not in new:
                if key in ANNOTATIONS or key in WIDEN_WHEN_DROPPED:
                    continue
                out.append(f"{where}: '{key}' removed")
        for key in new:
            if key in old or key in ANNOTATIONS or key == "$defs":
                continue
            if key == "properties":
                continue  # a properties object where there was none: its members are checked below as additions
            out.append(f"{where}: '{key}' added (narrows what a writer may write)")
        for key in old:
            if key not in new or key in ANNOTATIONS:
                continue
            a, b, at = old[key], new[key], f"{where}/{key}"
            if key == "properties":
                for name, sub in a.items():
                    if name not in b:
                        out.append(f"{at}/{name}: field removed")
                    else:
                        diff(sub, b[name], f"{at}/{name}", out)
            elif key == "required":
                for name in sorted(set(b) - set(a)):
                    out.append(f"{at}: '{name}' newly required")
            elif key == "enum":
                for value in a:
                    if value not in b:
                        out.append(f"{at}: value {json.dumps(value)} removed")
            elif key == "type":
                ta, tb = (a if isinstance(a, list) else [a]), (b if isinstance(b, list) else [b])
                if not set(ta) <= set(tb):
                    out.append(f"{at}: type {json.dumps(a)} became {json.dumps(b)}")
            elif key in UPPER:
                if b < a:
                    out.append(f"{at}: lowered from {a} to {b}")
            elif key in LOWER:
                if b > a:
                    out.append(f"{at}: raised from {a} to {b}")
            elif key == "uniqueItems":
                if b and not a:
                    out.append(f"{at}: now required")
            elif key == "const":
                if a != b and not (where.endswith("/schemaVersion") and isinstance(a, str) and isinstance(b, str)
                                   and VERSION.match(a) and VERSION.match(b)
                                   and int(VERSION.match(b).group(1)) > int(VERSION.match(a).group(1))):
                    out.append(f"{at}: {json.dumps(a)} became {json.dumps(b)}")
            elif key in ("oneOf", "anyOf") and isinstance(a, list) and a and all(kind_of(x) for x in a):
                kinds_b = {kind_of(x): x for x in b if kind_of(x)}
                for x in a:
                    if kind_of(x) not in kinds_b:
                        out.append(f"{at}: kind '{kind_of(x)}' removed")
                    else:
                        diff(x, kinds_b[kind_of(x)], f"{at}[kind={kind_of(x)}]", out)
                if len(b) != len(kinds_b):
                    out.append(f"{at}: a branch without a kind added")
            elif key == "$defs":
                for name, sub in a.items():
                    if name not in b:
                        out.append(f"{at}/{name}: definition removed")
                    else:
                        diff(sub, b[name], f"{at}/{name}", out)
            else:
                diff(a, b, at, out)
    elif isinstance(old, list) and isinstance(new, list):
        if len(old) != len(new):
            out.append(f"{where}: {len(old)} entries became {len(new)}")
        for i, (a, b) in enumerate(zip(old, new)):
            diff(a, b, f"{where}/{i}", out)
    elif old != new or type(old) is not type(new):
        out.append(f"{where}: {json.dumps(old)} became {json.dumps(new)}")


def compare(old_dir, new_dir):
    out = []
    olds = {p.name: p for p in Path(old_dir).glob("*.schema.json")}
    news = {p.name: p for p in Path(new_dir).glob("*.schema.json")}
    for name in sorted(olds):
        if name not in news:
            out.append(f"{name}: schema removed")
            continue
        diff(json.loads(olds[name].read_text(encoding="utf-8")), json.loads(news[name].read_text(encoding="utf-8")), name, out)
    return out


def self_test():
    base = {"$id": "x", "type": "object", "additionalProperties": False, "required": ["a"],
            "properties": {"schemaVersion": {"const": "1.0"}, "a": {"type": "string", "maxLength": 10, "pattern": "^[a-z]+$"},
                           "s": {"enum": ["x", "y"]},
                           "u": {"oneOf": [{"properties": {"kind": {"const": "k1"}, "v": {"type": "integer", "minimum": 0}}}]}}}

    def change(fn):
        new = json.loads(json.dumps(base))
        fn(new)
        out = []
        diff(base, new, "t", out)
        return out

    cases = [
        ("an optional field added", lambda s: s["properties"].update(b={"type": "string", "maxLength": 3}), True),
        ("an enum value added", lambda s: s["properties"]["s"]["enum"].append("z"), True),
        ("a union kind added", lambda s: s["properties"]["u"]["oneOf"].append({"properties": {"kind": {"const": "k2"}}}), True),
        ("a description added", lambda s: s["properties"]["a"].update(description="text"), True),
        ("a bound widened", lambda s: s["properties"]["a"].update(maxLength=20), True),
        ("a bound dropped", lambda s: s["properties"]["a"].pop("maxLength"), True),
        ("the minor version moved on", lambda s: s["properties"]["schemaVersion"].update(const="1.1"), True),
        ("a definition added", lambda s: s.update({"$defs": {"d": {"type": "string"}}}), True),
        ("a field removed", lambda s: s["properties"].pop("s"), False),
        ("a field newly required", lambda s: s["required"].append("s"), False),
        ("an enum value removed", lambda s: s["properties"]["s"]["enum"].remove("y"), False),
        ("a bound narrowed", lambda s: s["properties"]["a"].update(maxLength=5), False),
        ("a minimum raised", lambda s: s["properties"]["u"]["oneOf"][0]["properties"]["v"].update(minimum=1), False),
        ("a pattern changed", lambda s: s["properties"]["a"].update(pattern="^[a-z0-9]+$"), False),
        ("a pattern added", lambda s: s["properties"]["s"].update(pattern="^x$"), False),
        ("a union kind removed", lambda s: s["properties"]["u"].update(oneOf=[{"properties": {"kind": {"const": "k9"}}}]), False),
        ("a type changed", lambda s: s["properties"]["a"].update(type="integer"), False),
        ("a major version", lambda s: s["properties"]["schemaVersion"].update(const="2.0"), False),
        ("an older minor", lambda s: s["properties"]["schemaVersion"].update(const="0.9"), False),
        ("a conditional rule added", lambda s: s.update(allOf=[{"if": {"required": ["s"]}, "then": {"required": ["a"]}}]), False),
        ("additionalProperties opened", lambda s: s.update(additionalProperties=True), False),
    ]
    failures = 0
    for name, fn, allowed in cases:
        out = change(fn)
        ok = (not out) == allowed
        failures += not ok
        print(f"{'pass' if ok else 'FAIL'}  {name}: {'allowed' if not out else out[0]}")
    print(f"{len(cases) - failures} of {len(cases)} checks pass")
    return 1 if failures else 0


def main(argv):
    if argv[1:] == ["--self-test"]:
        return self_test()
    if len(argv) != 3:
        print(__doc__)
        return 2
    out = compare(argv[1], argv[2])
    for line in out:
        print(line)
    print("no change a minor version may not make" if not out else f"{len(out)} change(s) a minor version may not make")
    return 1 if out else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
