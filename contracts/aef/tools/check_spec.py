#!/usr/bin/env python3
"""Checks that the AEF 1.0 specification, schemas and corpus agree with each other:

1. every rule id is defined once (**[XXX-n]**) and every cited id ([XXX-n]) is defined;
2. every rule id a corpus vector names in `rules` is defined;
3. every problem code the corpus expects is defined in a code table of the spec;
4. every field name the prose writes in backticks (camelCase) is a property in some writer schema;
5. when the corpus is in a git work tree, git ignores none of its files (a repository's own ignore rules, such as
   AgentEval's `runs/`, must not drop vectors from a commit);
6. every rule is named by some corpus vector, or listed in UNTESTED with the reason no vector can test it;
7. no schema pattern uses lookaround or a backreference ([ENC-14]);
8. every job vector's inputs are files of the corpus, its env names set, empty or absent; one that expects a job has
   a target whose cases have a severity exactly when failed or warn, and expects the first cases of a suite its
   scripted target has, and one with `refused` expects nothing of a job; and run.json, a plan and a runner manifest list the same target modes;
9. every row of a table in the spec is indented as its table's first row (a row that is not leaves the table: in a
   list item, CommonMark renders it as a paragraph of pipes);
10. no published page cites a review id (R7R-3, R9-2, W3-17, …): they resolve only in the editors' notes, which are
   not published. A page names the rule a ruling concerns instead. Every published page is checked, the changelog
   and the interop pages included; so is every vector's `why` and `description`, public text too, where a critic's
   round ("changed in round 9") counts as one as well. Tool code may cite them. A date a page gives a ruling has
   its year ("settled 2026-10-09", never "settled 10-09"): without it, it means nothing after the release.

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
              "anyOf", "effectiveState", "endTimeUnixNano", "envelopeResult", "expectedError", "instrumentationLibrarySpans", "keyid", "maxItems",
              "maxLength", "minItems", "minLength", "oneOf", "payloadType", "publicKey", "readOnly", "sealedState",
              "startTimeUnixNano", "uniqueItems", "unsealedEvents", "verifiesFor", "writeOnly",
              # spec 09 §9.2.1: the scripted target of the job vectors, and what a job vector expects
              "closeSeconds", "secondsBound", "usdBound", "endsAt"}


# Rules no corpus vector can test, and why. Everything else needs a vector that names it (CONF-1).
# Problem codes no vector can expect, with the reason.
CODES_UNTESTED = {}
# Backticked words in the rules that define codes in prose which are not codes: DSSE fields, manifest fields a code's
# explanation names, a state value, literals.
NOT_CODES = {"payload", "payloadType", "signatures", "keyid", "sig", "runs", "status", "axes", "s", "null", "aborted",
             "severity", "direction", "comparison",  # CKP-8 names the fields a lane reads
             "decided"}  # CKP-7 names the state

# Published pages (every Markdown file of contracts/aef) that may still cite review ids, and why (check 10).
REVIEW_IDS_ELSEWHERE: dict[str, str] = {}  # every published page is checked: none cites a review id
REVIEW_ID = re.compile(r"(?<![A-Za-z0-9])(R[0-9]+[A-Z]*(?:-[0-9]+[a-z]?)?|W[0-9]+-[0-9]+)(?![A-Za-z0-9])")
YEARLESS_DATE = re.compile(r"settled (?:on )?(?![0-9]{4}-)[0-9]{1,2}-[0-9]{1,2}(?![0-9])")
# In a vector's text, a critic's round is a review reference too (the changelog's headings name rounds, by design).
VECTOR_REVIEW = re.compile(r"(?<![A-Za-z0-9])(R[0-9]+[A-Z]*(?:-[0-9]+[a-z]?)?|W[0-9]+-[0-9]+|[Rr]ound [0-9]+)"
                           r"(?![A-Za-z0-9])")


def vector_texts(conf):
    """(where, text) for every `why` and `description` string in the corpus's JSON files: public text too."""
    found = []

    def walk(value, where):
        if isinstance(value, dict):
            for key, item in value.items():
                if key in ("why", "description") and isinstance(item, str):
                    found.append((where, item))
                else:
                    walk(item, where)
        elif isinstance(value, list):
            for item in value:
                walk(item, where)

    for path in sorted(conf.rglob("*.json")):
        if path.name == "index.json":
            continue
        try:
            walk(json.loads(path.read_text(encoding="utf-8")), path.relative_to(conf).as_posix())
        except (ValueError, UnicodeDecodeError):
            continue  # a vector's input that is not JSON on purpose
    return found


def published_pages():
    """{path relative to contracts/aef: text} of every Markdown page check 10 covers."""
    root = AEF.parent
    pages = {}
    for page in sorted(root.rglob("*.md")):
        rel = page.relative_to(root).as_posix()
        if not any(rel == skip or (skip.endswith("/") and rel.startswith(skip)) for skip in REVIEW_IDS_ELSEWHERE):
            pages[rel] = page.read_text(encoding="utf-8")
    return pages


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


def unportable(pattern):
    """ENC-14: a construct every engine compiles but not every engine matches alike, or None. Anywhere: \\d, \\w, \\s,
    \\b and their capitals; outside a character class: an unescaped '.', an inline option or a named group."""
    in_class, i = False, 0
    while i < len(pattern):
        c = pattern[i]
        if c == "\\" and i + 1 < len(pattern):
            if pattern[i + 1] in "dDwWsSbB":
                return "\\" + pattern[i + 1]
            i += 2
            continue
        if in_class:
            in_class = c != "]"
        elif c == "[":
            in_class = True
        elif c == ".":
            return "an unescaped '.'"
        elif c == "(" and pattern[i + 1:i + 2] == "?" and pattern[i + 2:i + 3] != ":":
            return "an inline option or a named group"
        i += 1
    return None


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
                codes.update(w for w in re.findall(r"`([a-z]+(?:-[a-z]+)+|[a-z]+)`", m.group(0))
                             if w not in NOT_CODES)

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
    for code in sorted(codes - used_codes - set(CODES_UNTESTED)):
        problems.append(f"problem code '{code}' is expected by no corpus vector (add one, or list it in CODES_UNTESTED)")
    for code in sorted(set(CODES_UNTESTED) & used_codes):
        problems.append(f"CODES_UNTESTED lists '{code}', but a vector expects it: take it off the list")

    # Review ids resolve only in the editors' notes: a published page names the rule instead (check 10).
    for rel, page in published_pages().items():
        for n, line in enumerate(page.splitlines(), start=1):
            for found in REVIEW_ID.findall(line):
                problems.append(f"{rel}:{n}: cites the review id {found}, which no published page defines")
            for found in YEARLESS_DATE.findall(line):
                problems.append(f"{rel}:{n}: '{found}' gives a date without its year")
    for where, said in vector_texts(conf):
        for found in VECTOR_REVIEW.findall(said):
            problems.append(f"conformance/{where}: its why or description cites the review id {found}")

    # Tables: a row indented otherwise than its table's first row is not part of the table (CommonMark, GFM).
    for name, t in text.items():
        first = None
        for n, line in enumerate(t.splitlines(), start=1):
            row = re.match(r"( *)\|", line)
            if row is None:
                first = None
            elif first is None:
                first = len(row.group(1))
            elif len(row.group(1)) != first:
                problems.append(f"{name}:{n}: a table row indented {len(row.group(1))} spaces, its table {first}")

    # §9.1 against index.json: the vector kinds each class names (with the classes it includes) are the kinds the
    # index lists under it.
    names = r"Producer|Sealer|Reader|Run verifier|Overlay verifier|Checkpoint verifier|Decision engine|Runner|Stream verifier"
    table = re.search(r"\| Class \| Requirements \| Vectors[^\n]*\n\|[-|]+\n((?:\|[^\n]*\n)+)", text["09-conformance.md"])
    named, includes = {}, {}
    for row in table.group(1).splitlines():
        cells = [c.strip() for c in row.strip("|").split("|")]
        cls = re.match(r"\*\*(" + names + r")\*\*", cells[0]).group(1)
        named[cls] = set(re.findall(r"`([a-z-]+)`", cells[-1]))
        includes[cls] = set(re.findall(r"(" + names + r")'s", cells[-1]))
    for cls in named:
        for inner in includes[cls]:
            named[cls] |= named[inner]
    indexed = {}
    for v in json.loads((conf / "index.json").read_text(encoding="utf-8"))["vectors"]:
        if v["kind"] != "fixture":
            for cls in v["classes"]:
                indexed.setdefault(cls, set()).add(v["kind"])
    for cls in sorted(set(named) | set(indexed)):
        if named.get(cls, set()) != indexed.get(cls, set()):
            problems.append(f"§9.1 names the vector kinds {sorted(named.get(cls, set()))} for {cls}; index.json lists "
                            f"{sorted(indexed.get(cls, set()))}")

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

    # The job vectors (spec 09 §9.2.1): each names its inputs, which are files of the corpus, and expects runs the
    # target has; and a target mode is spelled alike wherever a schema lists them (run.json, a plan, a manifest).
    for exp in sorted((conf / "jobs").glob("*/expected.json")):
        e, where = json.loads(exp.read_text(encoding="utf-8")), exp.parent.name
        for name in ("plan", "runner", "target"):
            if not (exp.parent / e[name]).is_file():
                problems.append(f"jobs/{where}: its {name} {e[name]} is not a file")
        if set((e.get("env") or {}).values()) - {"set", "empty", "absent"}:
            problems.append(f"jobs/{where}: env gives a variable something other than set, empty or absent")
        if e.get("refused"):  # an input error: its inputs are wrong on purpose, and it expects no job
            if any(k in e for k in ("terminal", "limit", "runs", "spentUsd", "estimated", "endsAt")):
                problems.append(f"jobs/{where}: a refused vector expects what a job ends with")
            continue
        target = json.loads((exp.parent / e["target"]).read_text(encoding="utf-8"))
        for c in (c for s in target["suites"] for c in s["cases"]):
            if (c["state"] in ("failed", "warn")) != ("severity" in c):  # §9.2.1, RES-9
                problems.append(f"jobs/{where}: case {c['caseId']} has a severity, but not exactly as failed or warn")
        cases = {(s["ref"], s["version"]): [[c["caseId"], c["state"]] for c in s["cases"]] for s in target["suites"]}
        for run in e["runs"]:
            have = cases.get((run["suite"]["ref"], run["suite"]["version"]), [])
            if run["cases"] != have[:len(run["cases"])] or not run["cases"]:
                problems.append(f"jobs/{where}: a run's cases are not the first cases of a suite its target has")
    writer = {n: json.loads((AEF / "schemas" / "writer" / f"{n}.schema.json").read_text(encoding="utf-8"))
              for n in ("run", "run-plan", "runner")}
    modes = [writer["run"]["properties"]["execution"]["properties"]["targetMode"]["enum"],
             writer["run-plan"]["properties"]["targetMode"]["enum"],
             writer["runner"]["properties"]["targetModes"]["items"]["enum"]]
    if any(m != modes[0] for m in modes):
        problems.append(f"the target modes of run.json, a plan and a runner manifest differ: {modes}")

    for kind in ("writer", "reader"):
        for f in sorted((AEF / "schemas" / kind).glob("*.schema.json")):
            for pat in re.findall(r'"pattern": "((?:[^"\\]|\\.)*)"', f.read_text(encoding="utf-8")):
                raw = json.loads('"' + pat + '"')
                if any(s in raw for s in ("(?=", "(?!", "(?<=", "(?<!")) or re.search(r"\\[1-9]", raw):
                    problems.append(f"{kind}/{f.name}: pattern {raw!r} uses lookaround or a backreference (ENC-14)")
                if unportable(raw):
                    problems.append(f"{kind}/{f.name}: pattern {raw!r} uses {unportable(raw)}, which engines match "
                                    "differently (ENC-14)")

    print(f"{len(defined)} rules defined, {len(cited)} cited; {len(used_rules)} named by the corpus; "
          f"{len(used_codes)} problem codes used, {len(codes)} defined")
    for p in problems:
        print("  " + p)
    print("spec, schemas and corpus agree" if not problems else f"{len(problems)} problem(s)")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
