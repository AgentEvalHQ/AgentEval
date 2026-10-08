#!/usr/bin/env python3
"""The AEF 1.0 conformance runner (spec 09 §9.3): runs every vector of conformance/ through an implementation and
compares its result with the expected one. Standard library only.

The implementation is tools/aef_verify.py, called in-process, or any program that follows aef_verify.py's command-line
contract (one command per operation, input paths as arguments, one JSON value on standard output), given with
--command. Each vector's `kind` names the operation:

  kind          vectors                                   operation          compared
  run           valid/, runs/                             run DIR [--policy P] [--anchors A]
                                                                             outcome, problems (ordered), and
                                                                             signedBy / anchored / withheld
  encoding      encoding/                                 run DIR            outcome, problems (ordered)
  seal          seal-vectors/                             seal DIR [--policy P]
                                                                             problems (ordered), manifest bytes
  chain         chain-vectors/                            chain DIR          problems (ordered)
  overlay-view  overlay-views/                            view DIR --at T [--policy P]
                                                                             the effective view, field by field
  document      invalid/                                  document S FILE    writer and reader verdicts
  reader-only   reader-only/                              document S FILE    verdicts, and each `reads` entry
  checkpoint    checkpoints/                              checkpoint FILE    verdicts, and CKP-7 codes (ordered)
  lane          lane-vectors/                             lanes CP --runs D [--policy P]
                                                                             each lane's result, problems (ordered)
  signature     signature-vectors/                        signature E F P    envelope result, signatures, verifiesFor
  decision      decision-vectors/                         decide FILE        the output, or that it refuses
  plan          protocol/plans/, protocol/runners/        document S FILE    writer and reader verdicts
  matching      protocol/matching/                        match PLAN RUNNER  matches
  stream        protocol/streams/                         stream EV PLAN     problems (ordered)
  plan-conformance  protocol/plan-conformance/              conform EV PLAN RUNS [--policy P]
                                                                             problems (ordered)
  paths         paths.json                                paths FILE         problems per item (ordered)
  result-id     result-ids.json                           result-id ...      the result id

When conformance/index.json exists (CONF-1), the vectors are read from it and every file's SHA-256 is checked
against it first: a vector with a file that does not match, or an extra file, fails without being run (CONF-3). An
entry of kind "fixture", or one whose folder holds no expected.json (shared keys, shared plans), is checked but not
run. --class selects vectors by the conformance classes the index gives them (spec 09 §9.1 when folders are walked).
Otherwise the folders are walked; a folder that does not exist is skipped with a notice.

Usage:
  python aef_conformance.py [--corpus DIR] [--index FILE | --no-index] [--command "CMD ..."] [--kind KIND ...]
                            [--class NAME ...] [--quiet]
  python aef_conformance.py --self-check
      Runs the corpus once per mutation of the verifier (each switches one check off: the seal digest, the
      manifest's byte order, the I-JSON duplicate-member check, the $-at-end-of-input pattern rule, the summary
      recomputation, the overlay runHash check, the numeric order of line numbers, the anchor list, the trust
      policy's key list). Each mutation must make some vector fail that passes unmutated.
Exit status: 0 when every vector passes (and, with --self-check, every mutation is caught), else 1.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import shlex
import subprocess
import sys
import tempfile
from collections import OrderedDict
from pathlib import Path

TOOLS = Path(__file__).resolve().parent
if str(TOOLS) not in sys.path:  # python -I leaves the script's own folder out of sys.path
    sys.path.insert(0, str(TOOLS))

import aef_verify  # noqa: E402

CORPUS = TOOLS.parent / "1" / "conformance"


# ---------------------------------------------------------------------------- engines

class InProcess:
    """The reference verifier, called as a library with the same arguments as its command line."""
    name = "aef_verify.py (in-process)"

    def call(self, argv):
        try:
            return aef_verify.dispatch([str(a) for a in argv])
        except aef_verify.InputError as error:
            return {"error": str(error)}


class External:
    """Another implementation, driven through the command-line contract."""

    def __init__(self, command):
        self.command = shlex.split(command, posix=os.name != "nt")
        self.name = command

    def call(self, argv):
        done = subprocess.run(self.command + [str(a) for a in argv], capture_output=True)
        if done.returncode != 0:
            return {"error": f"exit {done.returncode}: {done.stderr.decode('utf-8', 'replace').strip()}"}
        try:
            return json.loads(done.stdout.decode("utf-8"))
        except ValueError as error:
            return {"error": f"standard output is not one JSON value: {error}"}


# ---------------------------------------------------------------------------- vectors

def read_json(path):
    return json.loads(Path(path).read_bytes().decode("utf-8"))


# Spec 09 §9.1: the classes each kind of vector tests, for --class when the folders are walked (index.json names
# each vector's classes itself).
KIND_CLASSES = {
    "document": ["Producer", "Reader"], "run": ["Producer", "Run verifier"], "result-id": ["Producer"],
    "paths": ["Producer", "Run verifier"], "seal": ["Sealer", "Run verifier"], "signature": ["Sealer", "Run verifier"],
    "encoding": ["Reader", "Run verifier"], "reader-only": ["Reader"], "chain": ["Overlay verifier"],
    "overlay-view": ["Overlay verifier"], "checkpoint": ["Checkpoint verifier"], "lane": ["Checkpoint verifier"],
    "decision": ["Checkpoint verifier", "Decision engine"], "plan": ["Runner"], "matching": ["Runner"],
    "stream": ["Stream verifier"], "plan-conformance": ["Stream verifier"],
}


class Vector:
    def __init__(self, kind, vid, path, expected=None, item=None, classes=None):
        self.kind, self.id, self.path, self.expected, self.item = kind, vid, Path(path), expected, item
        self.classes = classes if classes is not None else KIND_CLASSES.get(kind, [])
        self.guard = None  # with index.json: refuses a file read outside the vector's own (checked) files


FOLDER_KINDS = [  # (folder, default kind when expected.json has none)
    ("valid", "run"), ("runs", "run"), ("encoding", "encoding"), ("seal-vectors", "seal"),
    ("chain-vectors", "chain"), ("overlay-views", "overlay-view"), ("invalid", "document"),
    ("reader-only", "reader-only"), ("checkpoints", "checkpoint"), ("lane-vectors", "lane"),
    ("signature-vectors", "signature"),
]


def walk(corpus, notices):
    """The vectors of the corpus folders, in a fixed order."""
    vectors = []
    for folder, default in FOLDER_KINDS:
        root = corpus / folder
        if not root.is_dir():
            notices.append(f"notice: {folder}/ does not exist; its vectors are skipped")
            continue
        for d in sorted(p for p in root.iterdir() if p.is_dir()):
            if not (d / "expected.json").is_file():
                continue  # signature-vectors/keys/
            expected = read_json(d / "expected.json")
            vectors.append(Vector(expected.get("kind", default), f"{folder}/{d.name}", d, expected))
    root = corpus / "decision-vectors"
    if root.is_dir():
        for f in sorted(root.glob("*.json")):
            vectors.append(Vector("decision", f"decision-vectors/{f.name}", f, read_json(f)))
    else:
        notices.append("notice: decision-vectors/ does not exist; its vectors are skipped")
    protocol = corpus / "protocol"
    if protocol.is_dir():
        for sub, kind in (("plans", "plan"), ("runners", "plan"), ("matching", "matching"), ("streams", "stream"),
                          ("plan-conformance", "plan-conformance")):
            for d in sorted(p for p in (protocol / sub).iterdir() if p.is_dir()) if (protocol / sub).is_dir() else []:
                vectors.append(Vector(kind, f"protocol/{sub}/{d.name}", d, read_json(d / "expected.json")))
    else:
        notices.append("notice: protocol/ does not exist; its vectors are skipped")
    for name, kind in (("paths.json", "paths"), ("result-ids.json", "result-id")):
        f = corpus / name
        if not f.is_file():
            notices.append(f"notice: {name} does not exist; its vectors are skipped")
            continue
        for i, item in enumerate(read_json(f)):
            label = item.get("name", i) if isinstance(item, dict) else i
            vectors.append(Vector(kind, f"{name}#{label}", f, item, item))
    return vectors


def _index_files(entry):
    """{path relative to the entry's folder (its own folder, or the folder of a file vector): hex digest}, from
    either {"files": {path: digest}} or {"files": [{"path", "sha256"}]}."""
    files = entry.get("files", entry.get("sha256", {}))
    if isinstance(files, list):
        files = {f.get("path"): f.get("sha256") for f in files if isinstance(f, dict)}
    return {p: str(d).split(":", 1)[-1] for p, d in files.items()}


class Guard:
    """The corpus files index.json lists, and whether each matches its SHA-256 (CONF-3). A vector reads only files
    that do."""

    def __init__(self):
        self.listed, self.bad = {}, set()

    def add(self, path, digest):
        path = path.resolve()
        self.listed[path] = digest
        if not path.is_file() or hashlib.sha256(path.read_bytes()).hexdigest() != digest:
            self.bad.add(path)

    def check(self, path):
        """Refuses a file the index does not list or that does not match it."""
        path = Path(path).resolve()
        if path not in self.listed:
            raise CorpusRefused(f"{path.name} is not listed in index.json")
        if path in self.bad:
            raise CorpusRefused(f"{path.name} does not match its SHA-256 in index.json")


class CorpusRefused(Exception):
    pass


def from_index(corpus, index_path):
    """(vectors, refusals): the vectors index.json lists (CONF-1), each checked file by file (CONF-3): a vector with
    a listed file that is missing or changed, or with a file in its folder that no entry lists, is refused. An entry
    whose folder holds no expected.json lists shared files (keys, plans) and is not run itself."""
    index = read_json(index_path)
    entries = index.get("vectors", index) if isinstance(index, dict) else index
    guard, plan = Guard(), []
    for entry in entries:
        base = corpus / entry.get("path", "")
        folder = base if base.is_dir() else base.parent
        listed = _index_files(entry)
        for p, digest in listed.items():
            guard.add(folder / p, digest)
        plan.append((entry, base, folder, listed))
    vectors, refusals = [], []
    for entry, base, folder, listed in plan:
        vid, kind, classes = entry.get("id"), entry.get("kind"), entry.get("classes")
        wrong = sorted(p for p in listed if (folder / p).resolve() in guard.bad)
        if base.is_dir():
            wrong += sorted(f.relative_to(base).as_posix() for f in base.rglob("*")
                            if f.is_file() and f.resolve() not in guard.listed)
        if wrong:
            refusals.append((kind, vid, f"files that do not match index.json: {', '.join(wrong)}", classes))
            continue
        if kind in ("paths", "result-id"):
            for i, item in enumerate(read_json(base)):
                label = item.get("name", i) if isinstance(item, dict) else i
                vectors.append(Vector(kind, f"{vid}#{label}", base, item, item, classes))
            continue
        expected_file = base / "expected.json" if base.is_dir() else base
        if kind == "fixture" or not expected_file.is_file():
            continue  # shared files, checked but not run (signature-vectors/keys, protocol/streams/shared)
        v = Vector(kind, vid, base, read_json(expected_file), classes=classes)
        v.guard = guard
        vectors.append(v)
    return vectors, refusals


# ---------------------------------------------------------------------------- running one vector

def _problems(value):
    return [list(p) for p in value] if isinstance(value, list) else value


def compare(diffs, label, actual, expected):
    if actual != expected:
        diffs.append(f"{label}: expected {json.dumps(expected, ensure_ascii=False)}, "
                     f"got {json.dumps(actual, ensure_ascii=False)}")


def run_vector(engine, v, scratch):
    """The list of differences (empty when the vector passes)."""
    e, d, diffs = v.expected, v.path, []
    if v.kind in ("run", "encoding"):
        argv = ["run", d / e.get("run", "run")]
        if "policy" in e:
            argv += ["--policy", d / e["policy"]]
        if "anchors" in e:
            argv += ["--anchors", d / e["anchors"]]
        out = engine.call(argv)
        if "error" in out:
            return [out["error"]]
        compare(diffs, "outcome", out.get("outcome"), e["outcome"])
        compare(diffs, "problems", _problems(out.get("problems")), _problems(e["problems"]))
        for field in ("signedBy", "anchored", "withheld"):  # §4.5's further levels, whenever the vector states them
            if field in e:
                compare(diffs, field, out.get(field, "<not reported>"), e[field])
        if "withheld" not in e and "withheld" in out:
            diffs.append(f"withheld: not expected, got {out['withheld']}")
    elif v.kind == "seal":
        out = engine.call(["seal", d / e.get("run", "run")] + _policy(e, d))
        if "error" in out:
            return [out["error"]]
        compare(diffs, "problems", _problems(out.get("problems")), _problems(e["problems"]))
        if "manifest" in e:
            want = (d / e["manifest"]).read_bytes().decode("utf-8")
            compare(diffs, "manifest", out.get("manifest"), want)
    elif v.kind == "chain":
        out = engine.call(["chain", d / e.get("run", "run")])
        if "error" in out:
            return [out["error"]]
        compare(diffs, "problems", _problems(out.get("problems")), _problems(e["problems"]))
    elif v.kind == "overlay-view":
        out = engine.call(["view", d / e.get("run", "run"), "--at", e["at"]] + _policy(e, d))
        if "error" in out:
            return [out["error"]]
        for field in ("results", "reviews", "waivers", "withheld", "unsealedEvents"):
            compare(diffs, f"view.{field}", out.get(field), e["view"].get(field))
    elif v.kind in ("document", "reader-only", "plan"):
        out = engine.call(["document", e["schema"], d / "document.json"])
        if "error" in out:
            return [out["error"]]
        for side in ("writer", "reader"):
            compare(diffs, side, out.get(side), e[side])
        for field, value in (e.get("reads") or {}).items():
            compare(diffs, f"reads[{field}]", (out.get("reads") or {}).get(field, "<not read>"), value)
    elif v.kind == "checkpoint":
        out = engine.call(["checkpoint", d / "document.json"])
        if "error" in out:
            return [out["error"]]
        for side in ("writer", "reader"):
            compare(diffs, side, out.get(side), e[side])
        if "problems" in e:
            compare(diffs, "problems", out.get("problems"), e["problems"])
    elif v.kind == "lane":
        out = engine.call(["lanes", d / e.get("checkpoint", "checkpoint.json"), "--runs", d / e.get("runs", "runs")]
                          + _policy(e, d))
        if "error" in out:
            return [out["error"]]
        got = out.get("lanes") or []
        for i, want in enumerate(e["lanes"]):
            have = got[i] if i < len(got) else None
            if have is None or have.get("lane") != want["lane"]:
                diffs.append(f"lanes[{i}]: expected lane {want['lane']!r}, got {have!r}")
                continue
            compare(diffs, f"lanes[{want['lane']}].result", have.get("result"), want["result"])
        if len(got) != len(e["lanes"]):
            diffs.append(f"lanes: expected {len(e['lanes'])}, got {len(got)}")
        compare(diffs, "problems", _problems(out.get("problems")), _problems(e["problems"]))
    elif v.kind == "signature":
        argv = ["signature", d / e["envelope"], d / e["file"], d / e["policy"]]
        if "payloadType" in e:
            argv += ["--payload-type", e["payloadType"]]
        out = engine.call(argv)
        if "error" in out:
            return [out["error"]]
        want_envelope = e.get("envelopeResult", e.get("envelope") if e.get("envelope") in (None, "malformed", "payload-mismatch") else None)
        compare(diffs, "envelopeResult", out.get("envelopeResult"), want_envelope)
        compare(diffs, "signatures", out.get("signatures"), e["signatures"])
        compare(diffs, "verifiesFor", out.get("verifiesFor"), e["verifiesFor"])
    elif v.kind == "decision":
        out = engine.call(["decide", d])
        if "expectedError" in e:
            if "error" not in out:
                diffs.append(f"decided what it must refuse ({e['expectedError']}): {json.dumps(out.get('output'))}")
        else:
            compare(diffs, "output", out.get("output", out), e["expected"])
        if e.get("schemaInvalid") or e.get("readerOnly"):
            f = scratch / "decision-input.json"
            f.write_bytes(json.dumps(e["input"], ensure_ascii=False).encode("utf-8"))
            check = engine.call(["document", "decision#/$defs/input", f])
            if e.get("schemaInvalid"):
                compare(diffs, "reader verdict of the input", check.get("reader"), "invalid")
            if e.get("readerOnly"):
                compare(diffs, "writer verdict of the input", check.get("writer"), "invalid")
                compare(diffs, "reader verdict of the input", check.get("reader"), "valid")
    elif v.kind == "matching":
        out = engine.call(["match", d / "plan.json", d / "runner.json"])
        compare(diffs, "matches", out.get("matches", out), e["matches"])
    elif v.kind == "stream":
        if v.guard is not None:
            v.guard.check(d / e["plan"])  # the shared plan lives outside the vector's folder
        out = engine.call(["stream", d / "events.ndjson", d / e["plan"]])
        if "error" in out:
            return [out["error"]]
        want = [[p["where"], p["problem"]] for p in e["problems"]]
        compare(diffs, "problems", _problems(out.get("problems")), want)
    elif v.kind == "plan-conformance":
        out = engine.call(["conform", d / e["events"], d / e["plan"], d / e["runs"]] + _policy(e, d))
        if "error" in out:
            return [out["error"]]
        compare(diffs, "problems", _problems(out.get("problems")), _problems(e["problems"]))
    elif v.kind == "paths":
        f = scratch / "paths.json"
        f.write_bytes(json.dumps(v.item["paths"], ensure_ascii=False).encode("utf-8"))
        out = engine.call(["paths", f])
        if "error" in out:
            return [out["error"]]
        compare(diffs, "problems", _problems(out.get("problems")), _problems(v.item["problems"]))
    elif v.kind == "result-id":
        item = v.item
        argv = ["result-id", item["runId"], item["caseId"], item["path"]]
        if item.get("trial") is not None:
            argv.append(json.dumps(item["trial"]))
        out = engine.call(argv)
        compare(diffs, "resultId", out.get("resultId", out), item["resultId"])
    else:
        diffs.append(f"no operation for kind {v.kind!r} in this runner yet")
    return diffs


def _policy(expected, folder):
    """['--policy', path] when the vector carries a trust policy (spec 09 §9.2.1), else []."""
    return ["--policy", folder / expected["policy"]] if "policy" in expected else []


def run_all(engine, vectors, refusals, quiet=False, show=print):
    """{kind: [passed, failed]} and the failing ids."""
    tally, failing = OrderedDict(), []
    with tempfile.TemporaryDirectory(prefix="aef-conformance-") as scratch:
        for kind, vid, why, _ in refusals:
            tally.setdefault(kind, [0, 0])[1] += 1
            failing.append(vid)
            show(f"FAIL  {kind:<12} {vid}\n      refused: {why}")
        for v in vectors:
            try:
                diffs = run_vector(engine, v, Path(scratch))
            except Exception as error:  # a crash is a failure of that vector, not of the runner
                diffs = [f"{type(error).__name__}: {error}"]
            tally.setdefault(v.kind, [0, 0])[1 if diffs else 0] += 1
            if diffs:
                failing.append(v.id)
                show(f"FAIL  {v.kind:<12} {v.id}")
                for line in diffs:
                    show(f"      {line}")
            elif not quiet:
                show(f"pass  {v.kind:<12} {v.id}")
    return tally, failing


def summary(tally, show=print):
    show("")
    show(f"{'kind':<14}{'pass':>6}{'fail':>6}")
    for kind, (ok, bad) in tally.items():
        show(f"{kind:<14}{ok:>6}{bad:>6}")
    total_ok = sum(t[0] for t in tally.values())
    total_bad = sum(t[1] for t in tally.values())
    show(f"{'total':<14}{total_ok:>6}{total_bad:>6}")
    return total_bad


def self_check(vectors, refusals):
    """Runs the corpus under each mutation; every one must make a vector fail."""
    engine = InProcess()
    caught_all = True
    print("self-check: each mutation switches one check of aef_verify.py off; the corpus must notice")
    _, baseline = run_all(engine, vectors, refusals, quiet=True, show=lambda *a: None)
    print(f"  unmutated: {len(baseline)} vector(s) fail{': ' + ', '.join(baseline) if baseline else ''}"
          + (" (a mutation is caught only by a vector that passes unmutated)" if baseline else ""))
    for name, what in aef_verify.KNOWN_MUTATIONS.items():
        aef_verify.set_mutations({name})
        try:
            _, failing = run_all(engine, vectors, refusals, quiet=True, show=lambda *a: None)
        finally:
            aef_verify.set_mutations(set())
        new = [vid for vid in failing if vid not in baseline]
        caught_all &= bool(new)
        sample = ", ".join(new[:4]) + (f" and {len(new) - 4} more" if len(new) > 4 else "")
        print(f"  {'caught' if new else 'MISSED'}  {name:<18} ({what}): "
              f"{len(new)} more vector(s) fail{': ' + sample if new else ''}")
    return caught_all


def main(argv):
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="backslashreplace")
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("--corpus", default=str(CORPUS))
    parser.add_argument("--index", help="an index.json to use instead of <corpus>/index.json")
    parser.add_argument("--no-index", action="store_true", help="walk the folders even when index.json exists")
    parser.add_argument("--command", help="drive this program through the command-line contract instead")
    parser.add_argument("--kind", action="append", help="run only vectors of this kind (repeatable)")
    parser.add_argument("--class", dest="classes", action="append", metavar="NAME",
                        help="run only vectors of this conformance class, e.g. 'Run verifier' (repeatable)")
    parser.add_argument("--quiet", action="store_true", help="print failures only")
    parser.add_argument("--self-check", action="store_true")
    a = parser.parse_args(argv[1:])
    corpus = Path(a.corpus).resolve()
    notices = []
    index = Path(a.index) if a.index else corpus / "index.json"
    if a.index and not index.is_file():
        parser.error(f"--index {a.index}: no such file")
    if index.is_file() and not a.no_index:
        vectors, refusals = from_index(corpus, index)
        print(f"corpus {corpus} from {index.name} (sha256:{hashlib.sha256(index.read_bytes()).hexdigest()})")
    else:
        vectors, refusals = walk(corpus, notices), []
        print(f"corpus {corpus} (no index.json: folders walked)")
    for n in notices:
        print(n)
    if a.kind:
        vectors = [v for v in vectors if v.kind in a.kind]
        refusals = [r for r in refusals if r[0] in a.kind]
    if a.classes:
        wanted = {c.lower() for c in a.classes}
        known = {c.lower() for cs in KIND_CLASSES.values() for c in cs}
        if wanted - known:
            parser.error(f"unknown class(es): {', '.join(sorted(wanted - known))}")
        vectors = [v for v in vectors if wanted & {c.lower() for c in v.classes}]
        refusals = [r for r in refusals if wanted & {c.lower() for c in (r[3] or KIND_CLASSES.get(r[0], []))}]
    if a.self_check:
        if a.command:
            parser.error("--self-check mutates the in-process verifier; it does not take --command")
        return 0 if self_check(vectors, refusals) else 1
    engine = External(a.command) if a.command else InProcess()
    print(f"implementation: {engine.name}")
    tally, _ = run_all(engine, vectors, refusals, quiet=a.quiet)
    return 1 if summary(tally) else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
