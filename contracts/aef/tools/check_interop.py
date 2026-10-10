#!/usr/bin/env python3
"""Runs the checked interop examples (1/interop/examples/*/expected.json) and exits non-zero on any difference.

The examples are informative, like the interop pages: they are not conformance vectors. Each folder holds its input
(an AEF run copied from the corpus, or an OTLP/JSON file), what tools/aef_interop.py writes from it, and an
expected.json naming the direction, the page and its sections, and what the check compares:

  "steps"      [{"args": [command, ...], "expected": path}]: runs aef_interop.py in the folder, "{out}" standing
               for a fresh output path, and compares what it writes with the expected file or folder: JSON and
               NDJSON as JSON values (the pages fix values, not bytes), any other file byte for byte.
  "refusals"   [{"args": [...], "says": id}]: runs aef_interop.py and expects exit status 2, a message naming the
               page's rule (OT-n, IN-n, or a rule of the spec) and nothing written.
  "runs"       {folder: outcome}: every AEF run of the example, input or output, verifies with
               `aef_verify.py run` with that outcome ("unsealed" or "intact") and no problem. A run a step writes is
               verified too, before it is compared.
  "roundTrip"  for AEF -> OpenTelemetry -> AEF or AEF -> Inspect -> AEF: the original run, the run that came back,
               how their lines match ("match": "resultId", for a trip that keeps result ids; otherwise the events
               between, "through", one logs line per result line), and what the page says is kept, lost and added. The check computes, field by field, what the trip kept and
               lost, and fails unless every field is accounted for by exactly the declared entry; every lost entry
               cites a bullet of the page's "What does not carry over" list, and every bullet of that list for this
               direction is cited, exercised by the run or declared absent from it (and then absent).
  "pageBlocks" the page's worked-example blocks, each equal (as JSON values) to data of an example, so a page cannot
               drift from the examples; every block of a section named here must be covered.

  --update rewrites the expected outputs of the steps from what the tool writes now (never the inputs, the pages
  or expected.json); review the difference before keeping it.

Usage: python -X utf8 -I contracts/aef/tools/check_interop.py [--update] [--quiet]
"""
from __future__ import annotations

import json
import re
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

TOOLS = Path(__file__).resolve().parent
if str(TOOLS) not in sys.path:  # python -I leaves the script's own folder out of sys.path
    sys.path.insert(0, str(TOOLS))

import aef_produce  # noqa: E402

INTEROP = TOOLS.parent / "1" / "interop"
EXAMPLES = INTEROP / "examples"
PYTHON = [sys.executable, "-X", "utf8", "-I"]


class Failure(Exception):
    pass


# ---------------------------------------------------------------------------- running the tools

def run_tool(script, args, cwd):
    done = subprocess.run(PYTHON + [str(TOOLS / script)] + [str(a) for a in args], cwd=cwd, capture_output=True,
                          timeout=600)
    return done.returncode, done.stdout.decode("utf-8", "replace"), done.stderr.decode("utf-8", "replace")


def verify(folder):
    code, out, err = run_tool("aef_verify.py", ["run", folder], folder)
    if code != 0:
        raise Failure(f"aef_verify.py run {folder}: exit {code}: {err.strip()}")
    return json.loads(out)


def tree(path):
    """{relative path: bytes} of a folder, or {"": bytes} of a file; None when absent."""
    path = Path(path)
    if path.is_file():
        return {"": path.read_bytes()}
    if path.is_dir():
        return {p.relative_to(path).as_posix(): p.read_bytes() for p in sorted(path.rglob("*")) if p.is_file()}
    return None


JSON_SUFFIXES = (".json", ".ndjson", ".jsonl")


def json_values(data, suffix):
    """The JSON values of a file (one per line for NDJSON), or None when it does not read as JSON."""
    try:
        text = data.decode("utf-8")
        if suffix == ".json":
            return json.loads(text)  # Inspect's NaN is read as Python reads it
        return [json.loads(line) for line in text.split("\n") if line]
    except ValueError:
        return None


def differences(expected, actual, suffix=""):
    """What differs between two outputs. The pages fix values, not bytes: a JSON or NDJSON file is compared
    as JSON values (member order and number spelling are free, [ENC-2], [ENC-4]); any other file byte for byte."""
    if expected is None:
        return ["the expected output is missing"]
    if actual is None:
        return ["nothing was written"]
    out = []
    for name in sorted(set(expected) | set(actual)):
        label = name or "the file"
        kind = Path(name).suffix if name else suffix
        if name not in actual:
            out.append(f"{label}: not written")
        elif name not in expected:
            out.append(f"{label}: written, not expected")
        elif kind in JSON_SUFFIXES and json_values(expected[name], kind) is not None:
            if not equal(json_values(expected[name], kind), json_values(actual[name], kind)):
                out.append(f"{label}: differs as JSON values")
        elif expected[name] != actual[name]:
            a, b = expected[name].decode("utf-8", "replace"), actual[name].decode("utf-8", "replace")
            at = next((i for i, (x, y) in enumerate(zip(a, b)) if x != y), min(len(a), len(b)))
            out.append(f"{label}: differs at character {at}: expected {a[at:at + 60]!r}, got {b[at:at + 60]!r}")
    return out


def check_steps(folder, spec, update, problems):
    for n, step in enumerate(spec.get("steps", []), start=1):
        expected_path = folder / step["expected"]
        with tempfile.TemporaryDirectory() as tmp:
            out = Path(tmp) / "out"
            args = [str(out) if a == "{out}" else a for a in step["args"]]
            code, _, err = run_tool("aef_interop.py", args, folder)
            if code != 0:
                problems.append(f"step {n} ({' '.join(step['args'][:1])}): exit {code}: {err.strip()}")
                continue
            if out.is_dir():
                result = verify(out)
                if result["outcome"] not in ("unsealed", "intact") or result["problems"]:
                    problems.append(f"step {n}: the run it wrote does not verify: {result}")
            if update:
                if expected_path.is_dir():
                    shutil.rmtree(expected_path)
                elif expected_path.exists():
                    expected_path.unlink()
                if out.is_dir():
                    shutil.copytree(out, expected_path)
                else:
                    shutil.copyfile(out, expected_path)
                continue
            problems += [f"step {n}, {step['expected']}: {d}"
                         for d in differences(tree(expected_path), tree(out), Path(step["expected"]).suffix)]


def check_refusals(folder, spec, problems):
    for refusal in spec.get("refusals", []):
        with tempfile.TemporaryDirectory() as tmp:
            out = Path(tmp) / "out"
            args = [str(out) if a == "{out}" else a for a in refusal["args"]]
            code, _, err = run_tool("aef_interop.py", args, folder)
            where = f"refusal {' '.join(refusal['args'][:2])}"
            if code != 2:
                problems.append(f"{where}: exit {code}, not 2")
            elif refusal["says"] not in err:
                problems.append(f"{where}: the message does not name {refusal['says']}: {err.strip()}")
            if out.exists():
                problems.append(f"{where}: something was written")


def check_runs(folder, spec, problems):
    for name, outcome in spec.get("runs", {}).items():
        result = verify(folder / name)
        if result != {"outcome": outcome, "problems": []}:
            problems.append(f"{name}: aef_verify.py run gives {result}, not {outcome} with no problem")


# ---------------------------------------------------------------------------- the pages

def page_text(name):
    with open(INTEROP / name, encoding="utf-8") as f:  # universal newlines: the page may be checked out with CRLF
        return f.read()


def section(text, heading):
    """The lines of the section under a ## or ### heading, up to the next heading of the same or a higher level."""
    lines = text.split("\n")
    for i, line in enumerate(lines):
        m = re.match(r"(#{2,3}) (.*)$", line)
        if m and m.group(2).strip() == heading:
            level, body = len(m.group(1)), []
            for after in lines[i + 1:]:
                h = re.match(r"(#{2,3}) ", after)
                if h and len(h.group(1)) <= level:
                    break
                body.append(after)
            return body
    return None


def blocks(lines):
    """The fenced code blocks of a section, in order: (info string, text)."""
    out, current, info = [], None, None
    for line in lines:
        if line.startswith("```"):
            if current is None:
                current, info = [], line[3:].strip()
            else:
                out.append((info, "\n".join(current)))
                current = None
        elif current is not None:
            current.append(line)
    return out


def bullets(lines):
    """{list name: [bullet text, ...]} of a section whose lists follow a bold lead such as **AEF → OpenTelemetry.**"""
    lists, name, current = {}, None, None
    for line in lines + [""]:
        lead = re.match(r"\*\*(.+?)\.\*\*\s*$", line)
        if lead:
            name = lead.group(1)
            lists[name] = []
        elif line.startswith("- ") and name is not None:
            current = [line[2:]]
            lists[name].append(current)
        elif line.startswith("  ") and current is not None:
            current.append(line.strip())
        else:
            current = None
    return {k: [" ".join(" ".join(b).split()) for b in v] for k, v in lists.items()}


def normal(text):
    return " ".join(text.split())


def equal(a, b):
    """JSON values compared as values: numbers by value (1 equals 1.0; NaN, Inspect's unscored value, equals NaN),
    objects by members, whatever their order."""
    if isinstance(a, bool) or isinstance(b, bool):
        return type(a) is type(b) and a == b
    if isinstance(a, (int, float)) and isinstance(b, (int, float)):
        return a == b or (a != a and b != b)
    if isinstance(a, dict) and isinstance(b, dict):
        return a.keys() == b.keys() and all(equal(a[k], b[k]) for k in a)
    if isinstance(a, list) and isinstance(b, list):
        return len(a) == len(b) and all(equal(x, y) for x, y in zip(a, b))
    return type(a) is type(b) and a == b


def pointer(value, path):
    for token in path.split("/")[1:]:
        token = token.replace("~1", "/").replace("~0", "~")
        value = value[int(token)] if isinstance(value, list) else value[token]
    return value


def example_value(folder, ref):
    path = folder / ref["file"]
    text = path.read_bytes().decode("utf-8")
    if "line" in ref:
        value = json.loads(text.split("\n")[ref["line"] - 1])
    else:
        value = json.loads(text)  # Inspect's NaN is read as Python reads it
    if "pointer" in ref:
        value = pointer(value, ref["pointer"])
    for drop in ref.get("drop", []):
        *parents, last = drop.split("/")[1:]
        holder = pointer(value, "/" + "/".join(parents)) if parents else value
        del holder[last.replace("~1", "/").replace("~0", "~")]
    if "member" in ref:
        value = {ref["member"]: value}
    return value


def check_page_blocks(folder, spec, problems, covered):
    for entry in spec.get("pageBlocks", []):
        where = f"{entry['page']}, {entry['section']}, block {entry['block']}"
        body = section(page_text(entry["page"]), entry["section"])
        found = blocks(body) if body is not None else []
        if not 1 <= entry["block"] <= len(found):
            problems.append(f"{where}: no such block")
            continue
        covered.add((entry["page"], entry["section"], entry["block"]))
        info, text = found[entry["block"] - 1]
        try:
            shown = json.loads("{" + text + "}") if "member" in entry["equals"] else json.loads(text)
        except ValueError as error:
            problems.append(f"{where}: the block does not parse: {error}")
            continue
        if not equal(shown, example_value(folder, entry["equals"])):
            problems.append(f"{where} differs from {entry['equals']}")


def check_sections(spec, problems):
    text = page_text(spec["page"])
    for heading in spec.get("sections", []):
        if section(text, heading) is None:
            problems.append(f"{spec['page']} has no section {heading!r}")


# ---------------------------------------------------------------------------- the round trip

def read_run(folder):
    folder = Path(folder)
    run = aef_produce.read_json(folder / "run.json")
    lines = aef_produce.read_ndjson(folder / "results.ndjson")
    files = {}
    for p in sorted(folder.rglob("*")):
        rel = p.relative_to(folder).as_posix()
        if p.is_file() and rel not in ("run.json", "results.ndjson"):
            group = rel.split("/")[0] if rel.split("/")[0] in ("blobs", "overlays", "ext") else rel
            files.setdefault("file:" + group, {})[rel] = p.read_bytes()
    return run, lines, files


def flatten(value, prefix=""):
    if isinstance(value, dict) and value:
        out = {}
        for k, v in value.items():
            out.update(flatten(v, f"{prefix}.{k}" if prefix else k))
        return out
    return {prefix: value}


def instant(text):
    return aef_produce._instant(text, "a time") if isinstance(text, str) else None


def same(a, b):
    return equal(a, b)  # an absent member reads as null ([ENC-2]): get() gives None for both


def trip_outcomes(folder, rt):
    """{field: 'kept' | 'lost' | 'added'} over the whole trip (a field lost on any line is lost)."""
    run, lines, files = read_run(folder / rt["original"])
    back_run, back_lines, back_files = read_run(folder / rt["result"])
    if rt.get("match") == "resultId":  # a trip that keeps result ids (through Inspect): a line comes back as one
        by_id = {line["resultId"]: line for line in back_lines}
        counterparts = [[by_id[line["resultId"]]] if line["resultId"] in by_id else [] for line in lines]
    else:  # through OpenTelemetry: the k-th logs line holds the events of the k-th result line (OT-2), in order
        through = aef_produce.read_ndjson(folder / rt["through"])
        if len(through) != len(lines):
            raise Failure(f"{rt['through']} has {len(through)} lines for {len(lines)} result lines (OT-2)")
        counts = [sum(1 for r in request["resourceLogs"] for s in r["scopeLogs"] for e in s["logRecords"]
                      if e.get("eventName") == "gen_ai.evaluation.result") for request in through]
        starts = [sum(counts[:i]) for i in range(len(counts))]
        counterparts = [back_lines[s:s + c] for s, c in zip(starts, counts)]
        if sum(counts) != len(back_lines):
            raise Failure(f"{rt['result']} has {len(back_lines)} result lines, the events give {sum(counts)}")
    seen = {}

    def mark(field, kept):
        """kept: True, False (lost) or "added"; over the trip, lost wins over added, and added over kept."""
        outcomes = {seen.get(field), kept if isinstance(kept, str) else "kept" if kept else "lost"}
        seen[field] = next(o for o in ("lost", "added", "kept") if o in outcomes)

    for line, back in zip(lines, counterparts):
        if not back:
            mark("results:(the line itself)", False)
            continue
        accounted = {"schemaVersion"}
        for field, value in line.items():
            if field == "schemaVersion":
                continue
            if field == "scores":
                if len(value) > 1:
                    mark("results:scores (two or more on one line)", False)
                for j, score in enumerate(value):
                    there = (back[j].get("scores") or [{}])[0] if j < len(back) else {}
                    for sub, v in score.items():
                        mark(f"results:scores[].{sub}", same(v, there.get(sub)))
                accounted.add("scores")
            elif field == "reasoning":
                kept = all("reasoning" in b and same(value, b["reasoning"]) for b in back)
                mark("results:reasoning", kept)
                accounted.add("reasoning")
                if not kept and "reason" not in line:  # the blob's text may come back as the reason
                    blob = value["blob"].split(":", 1)[1]
                    text = (folder / rt["original"] / "blobs" / "sha256" / blob[:2] / blob).read_bytes().decode()
                    mark("results:reasoning (its text, as reason)", all(b.get("reason") == text[:4096] for b in back))
                    accounted.add("reason")
            elif field == "startedAt" and "endedAt" not in line:
                mark("results:startedAt (as endedAt, the line having no endedAt)",
                     all(instant(b.get("endedAt")) == instant(value) for b in back))
                accounted.add("endedAt")
            elif field in ("startedAt", "endedAt"):
                mark(f"results:{field}", all(instant(b.get(field)) == instant(value) for b in back))
                accounted.add(field)
            else:
                mark(f"results:{field}", all(same(value, b.get(field)) for b in back))
                accounted.add(field)
        for b in back:
            for field in b:
                if field not in accounted and field not in line:
                    mark(f"results:{field}", "added")
    matched = {id(b) for back in counterparts for b in back}
    if any(id(b) not in matched for b in back_lines):
        mark("results:(a line of its own)", "added")

    asserted = back_run.get("imported", {}).get("asserted", [])
    mine, theirs = flatten(run), flatten(back_run)
    for path, value in mine.items():
        if path == "schemaVersion":
            continue
        supplied = any(path == a or path.startswith(a + ".") for a in asserted)
        equal_here = path in theirs and (instant(value) == instant(theirs[path]) if path in ("startedAt", "endedAt")
                                         else same(value, theirs[path]))  # times compare as instants ([ENC-8])
        mark(f"run.json:{path}", not supplied and equal_here)
    for path in theirs:
        if path not in mine:
            mark(f"run.json:{path}", "added")
    for group, content in files.items():
        mark(group, back_files.get(group) == content)
    for group in back_files:
        if group not in files:
            mark(group, "added")
    return seen


def matches(pattern, field):
    """A pattern names a field and everything under it: run.json covers run.json:subject.ref, which suite covers in
    turn; the longest pattern that matches a field accounts for it."""
    return field == pattern or any(field.startswith(pattern + c) for c in ".[:")


def check_round_trip(folder, spec, problems):
    rt = spec.get("roundTrip")
    if rt is None:
        return
    observed = trip_outcomes(folder, rt)
    entries = [(kind, e) for kind in ("kept", "lost", "added") for e in rt.get(kind, [])]
    owner = {}
    for field, outcome in observed.items():
        candidates = [(len(p), kind, id(e), p) for kind, e in entries for p in e.get("fields", []) + e.get("absent", [])
                      if matches(p, field)]
        if not candidates:
            problems.append(f"round trip: {field} is {outcome}, and no entry accounts for it")
            continue
        _, kind, entry_id, pattern = max(candidates)
        entry = next(e for _, e in entries if id(e) == entry_id)
        if pattern in entry.get("absent", []):
            problems.append(f"round trip: {field} is {outcome}, but the entry declares {pattern} absent from this run")
        elif kind != outcome:
            problems.append(f"round trip: {field} is {outcome}, declared {kind}")
        owner.setdefault(id(entry), set()).add(pattern)
    for kind, entry in entries:
        for pattern in entry.get("fields", []):
            if pattern not in owner.get(id(entry), set()):
                problems.append(f"round trip: {kind} {pattern} is declared, and nothing in this run is {kind} there")
        if not entry.get("fields") and not entry.get("absent"):
            problems.append(f"round trip: the {kind} entry {entry} names no field")

    text = normal(page_text(spec["page"]))
    for kind, entry in entries:
        if kind != "lost" and normal(entry["page"]) not in text:
            problems.append(f"round trip: {spec['page']} does not say {entry['page']!r}")
    lists = bullets(section(page_text(spec["page"]), "What does not carry over") or [])
    cited = {}
    for _, entry in ((k, e) for k, e in entries if k == "lost"):
        items = [b for b in lists.get(entry["list"], []) if b.startswith(normal(entry["item"]))]
        if len(items) != 1:
            problems.append(f"round trip: {len(items)} bullets of {entry['list']!r} start with {entry['item']!r}")
            continue
        cited.setdefault(entry["list"], set()).add(items[0])
    for name in rt.get("listsCovered", []):
        for item in lists.get(name, []):
            if item not in cited.get(name, set()):
                problems.append(f"round trip: the bullet {item[:60]!r}... of {name!r} is cited by no lost entry")


# ---------------------------------------------------------------------------- main

def main(argv):
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", newline="\n")
    update, quiet = "--update" in argv[1:], "--quiet" in argv[1:]
    unknown = [a for a in argv[1:] if a not in ("--update", "--quiet")]
    if unknown:
        print(__doc__, file=sys.stderr)
        return 2
    folders = sorted(p.parent for p in EXAMPLES.glob("*/expected.json"))
    if not folders:
        print(f"no examples under {EXAMPLES}", file=sys.stderr)
        return 1
    failed, covered, sections = 0, set(), set()
    for folder in folders:
        spec = json.loads((folder / "expected.json").read_text(encoding="utf-8"))
        problems = []
        try:
            check_sections(spec, problems)
            check_steps(folder, spec, update, problems)
            check_refusals(folder, spec, problems)
            check_runs(folder, spec, problems)
            check_round_trip(folder, spec, problems)
            check_page_blocks(folder, spec, problems, covered)
        except (Failure, aef_produce.InputError, OSError, KeyError, ValueError) as error:
            problems.append(f"{type(error).__name__}: {error}")
        sections |= {(e["page"], e["section"]) for e in spec.get("pageBlocks", [])}
        failed += bool(problems)
        if problems or not quiet:
            print(f"{'FAIL' if problems else 'ok  '} {folder.name}: {spec.get('direction', '')}")
        for p in problems:
            print(f"     {p}")
    for page, heading in sorted(sections):  # every block of a section the examples check is checked
        for n in range(1, len(blocks(section(page_text(page), heading) or [])) + 1):
            if (page, heading, n) not in covered:
                failed += 1
                print(f"FAIL {page}, {heading}: block {n} is equal to no example's data")
    print(f"{len(folders)} examples, {len(covered)} page blocks: "
          + ("all as expected" if not failed else f"{failed} failure(s)") + (" (expected outputs rewritten)" if update else ""))
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
