#!/usr/bin/env python3
"""Checks that the AEF 1.0 specification, schemas and corpus agree with each other:

1. every rule id is defined once (**[XXX-n]**) and every cited id ([XXX-n]) is defined;
2. every rule id a corpus vector names in `rules` is defined;
3. every problem code the corpus expects is defined in a code table of the spec;
4. every field name the prose writes in backticks (camelCase) is a property in some writer schema;
5. when the corpus is in a git work tree, git ignores none of its files (a repository's own ignore rules, such as
   AgentEval's `runs/`, must not drop vectors from a commit);
6. every rule is named by some corpus vector, or listed in UNTESTED with the reason no vector can test it;
7. no schema pattern uses lookaround or a backreference ([ENC-14]).

Usage: python contracts/aef/tools/check_spec.py
"""
import json
import re
import subprocess
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


# Rules no corpus vector can test, and why. Everything else needs a vector that names it (CONF-1).
UNTESTED = {
    "ENC-12": "a reader must not fetch the $id names: behaviour, not a file property",
    "ENC-14": "a rule on the schemas themselves: checked here (no lookaround, no backreference)",
    "CKP-6": "how a reader shows an approver's claimed identity: presentation",
    "VER-5": "what a minor version may change: checked by tools/schema_diff.py --self-test against the next version",
    "VER-7": "deprecation: a process rule for later minors",
    "SEC-2": "a tool must not seal a run holding a secret: a process rule",
    "SEC-4": "a SHOULD about the identities people choose",
    "CONF-1": "the index itself: checked by aef_conformance.py and the .NET index test on every file",
    "CONF-3": "what a conformance runner does: tools/aef_conformance.py is one",
    "CONF-4": "what a conformance claim says",
    "CONF-5": "how a new minor is published: tools/schema_diff.py",
}


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

    try:
        ignored = subprocess.run(["git", "ls-files", "--others", "--ignored", "--exclude-standard", "--", "."],
                                 cwd=conf, capture_output=True, text=True, timeout=60)
        if ignored.returncode == 0:
            for line in ignored.stdout.splitlines():
                problems.append(f"git ignores the corpus file {line}: it would be left out of a commit")
    except (OSError, subprocess.SubprocessError):
        pass  # no git: nothing to check

    for p in sorted((conf / "decision-vectors").glob("*.json")):
        used_rules.update(json.loads(p.read_text(encoding="utf-8")).get("rules", []))
    for rid in sorted(set(defined) - used_rules - set(UNTESTED)):
        problems.append(f"rule {rid} is named by no corpus vector (add one, or list it in UNTESTED with the reason)")
    for rid in sorted(set(UNTESTED) - set(defined)):
        problems.append(f"UNTESTED lists {rid}, which the spec does not define")
    for rid in sorted(set(UNTESTED) & used_rules):
        problems.append(f"UNTESTED lists {rid}, but a vector names it: take it off the list")

    for kind in ("writer", "reader"):
        for f in sorted((AEF / "schemas" / kind).glob("*.schema.json")):
            for pat in re.findall(r'"pattern": "((?:[^"\\]|\\.)*)"', f.read_text(encoding="utf-8")):
                raw = json.loads('"' + pat + '"')
                if any(s in raw for s in ("(?=", "(?!", "(?<=", "(?<!")) or re.search(r"\\[1-9]", raw):
                    problems.append(f"{kind}/{f.name}: pattern {raw!r} uses lookaround or a backreference (ENC-14)")

    print(f"{len(defined)} rules defined, {len(cited)} cited; {len(used_rules)} named by the corpus; "
          f"{len(used_codes)} problem codes used, {len(codes)} defined")
    for p in problems:
        print("  " + p)
    print("spec, schemas and corpus agree" if not problems else f"{len(problems)} problem(s)")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
