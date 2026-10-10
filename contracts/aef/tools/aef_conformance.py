#!/usr/bin/env python3
"""The AEF 1.0 conformance runner (spec 09 §9.3): runs every vector of conformance/ through an implementation and
compares its result with the expected one. Standard library only.

The implementation is tools/aef_verify.py (with tools/aef_produce.py for the write operations, and tools/aef_runner.py
for `job`), called in-process, or any program that follows aef_verify.py's command-line contract (one command per
operation, input paths as arguments, one JSON value on standard output), given with --command. Each vector's `kind`
names the operation:

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
  summarize     write-vectors/summarize/                  summarize DIR REQUEST
                                                                             the summary.json written: writer schema,
                                                                             each entry (sums under §3.6's tolerance),
                                                                             and the run verifier on the run with it
  produce       write-vectors/produce/                    produce SCENARIO OUT
                                                                             the run written in a fresh OUT: its four
                                                                             files; run.json and metrics.json as given;
                                                                             the lines as a set, matched by case, path
                                                                             and trial, member by member; summary.json
                                                                             as summarize's; and the run verifier:
                                                                             unsealed, no problems
  seal-write    write-vectors/seal-write/                seal-write COPY --sealed-by B --sealed-at T
                                                                             on a fresh copy of the run: writer schema,
                                                                             subjects, predicate, nothing else changed,
                                                                             and the run verifier: intact
  sign          write-vectors/sign/                       sign FILE KEY --payload-type T
                                                                             the envelope: one signature under the
                                                                             key id, standard base64, verified for the
                                                                             identity by the signature operation; for
                                                                             Ed25519 the signature bytes
  job           jobs/                                     job PLAN RUNNER TARGET OUT --at T
                                                                             in a fresh OUT, with the vector's
                                                                             environment: the stream and the runs as
                                                                             spec 09 §9.3 judges them (STRM-3, STRM-4,
                                                                             each run intact, the runs and cases
                                                                             expected, spend, estimate, no credential
                                                                             written)

The write operations (summarize, produce, seal-write, sign, job) are judged by the reference verifier, in-process,
whichever implementation is under test: aef_verify.py checks what the implementation wrote.

When conformance/index.json exists (CONF-1), the vectors are read from it and every file's SHA-256 is checked
against it first: a vector with a file that does not match, or an extra file, fails without being run (CONF-3). An
entry of kind "fixture", or one whose folder holds no expected.json (shared keys, shared plans), is checked but not
run. --class selects vectors by the conformance classes the index gives them (spec 09 §9.1 when folders are walked).
Otherwise the folders are walked; a folder that does not exist is skipped with a notice.

Usage:
  python aef_conformance.py [--corpus DIR] [--index FILE | --no-index] [--command "CMD ..."] [--kind KIND ...]
                            [--class NAME ...] [--level intact|signed] [--sign-algorithms ecdsa-p256,ed25519]
                            [--quiet]
      --sign-algorithms names the algorithms a Sealer signs with (spec 09 §9.1: a signer uses one of SIG-2's two);
      the sign vectors of the others are skipped and counted as skipped. The default is both.
  python aef_conformance.py --self-check
      Runs the corpus once per mutation of the verifier (each switches one check off: the seal digest, the
      manifest's byte order, the I-JSON duplicate-member check, the $-at-end-of-input pattern rule, the summary
      recomputation, the overlay runHash check, the numeric order of line numbers, the anchor list, the trust
      policy's key list), and once per break of a writer in aef_produce.py (typed absences left out of N, sums in
      binary64, trial lines counted, result ids without the trial, parents found by path, trial children without
      their trial, rollup counts and agreement, aggregation counts, a weight-0 child without component, a
      contradictory scenario written, a file left out of the seal, paths in segment order, another signing key,
      unpadded base64), and once per break of the reference runner aef_runner.py (a limit checked against a case's
      cost or duration instead of its bound, the budget checked exactly or by the job's spend alone, case ids
      prefixed, credentials not resolved or an empty one resolved, a suite named twice taken, a container plan taken,
      a fixed severity, no lane on the lines, a computed suite digest, the plan's judges copied into each run, the
      clock's range left unchecked, a target's unknown member ignored). Each mutation must make some vector fail that
      passes unmutated.
Exit status: 0 when every vector passes (and, with --self-check, every mutation is caught), else 1.
"""
from __future__ import annotations

import argparse
import atexit
import base64
import concurrent.futures
import hashlib
import itertools
import json
import os
import re
import secrets
import shlex
import shutil
import subprocess
import sys
import tempfile
from collections import OrderedDict
from fractions import Fraction
from pathlib import Path

TOOLS = Path(__file__).resolve().parent
if str(TOOLS) not in sys.path:  # python -I leaves the script's own folder out of sys.path
    sys.path.insert(0, str(TOOLS))

import aef_produce  # noqa: E402
import aef_runner  # noqa: E402
import aef_verify  # noqa: E402

CORPUS = TOOLS.parent / "1" / "conformance"


# ---------------------------------------------------------------------------- engines

class InProcess:
    """The reference implementation, called as a library with the same arguments as its command line: aef_verify.py,
    aef_produce.py for the write operations, and aef_runner.py for job."""
    name = "aef_verify.py, aef_produce.py and aef_runner.py (in-process)"

    def call(self, argv, env=None):
        """env: the environment variables to set (a value) or remove (None) for this call only (spec 09 §9.3)."""
        argv = [str(a) for a in argv]
        module = (aef_produce if argv and argv[0] in aef_produce.OPERATIONS else
                  aef_runner if argv and argv[0] in aef_runner.OPERATIONS else aef_verify)
        saved = {name: os.environ.get(name) for name in env or {}}
        try:
            _apply(os.environ, env or {})
            return module.dispatch(argv)
        except (aef_verify.InputError, aef_produce.InputError, aef_runner.InputError) as error:
            return {"error": str(error)}
        finally:
            _apply(os.environ, saved)


def _apply(environ, changes):
    """Sets each variable given a value, and removes each given None."""
    for name, value in changes.items():
        if value is None:
            environ.pop(name, None)
        else:
            environ[name] = value


class External:
    """Another implementation, driven through the command-line contract."""

    def __init__(self, command):
        self.command = shlex.split(command, posix=os.name != "nt")
        self.name = command

    def call(self, argv, env=None):
        environ = None
        if env:
            environ = dict(os.environ)
            _apply(environ, env)
        done = subprocess.run(self.command + [str(a) for a in argv], capture_output=True, env=environ)
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
    "encoding": ["Run verifier"], "reader-only": ["Reader"], "chain": ["Overlay verifier"],
    "overlay-view": ["Overlay verifier"], "checkpoint": ["Checkpoint verifier"], "lane": ["Checkpoint verifier"],
    "decision": ["Checkpoint verifier", "Decision engine"], "plan": ["Runner"], "matching": ["Runner"],
    "stream": ["Stream verifier"], "plan-conformance": ["Stream verifier"],
    "summarize": ["Producer"], "produce": ["Producer"], "seal-write": ["Sealer"], "sign": ["Sealer"],
    "job": ["Runner"],
}
WRITE_KINDS = ("summarize", "produce", "seal-write", "sign")  # write-vectors/<kind>/<name>/ (spec 09 §9.2.1)
SIGN_ALGORITHMS = ("ecdsa-p256", "ed25519")  # SIG-2: a signer uses one of these; a sign vector names the one it needs


class Vector:
    def __init__(self, kind, vid, path, expected=None, item=None, classes=None):
        self.kind, self.id, self.path, self.expected, self.item = kind, vid, Path(path), expected, item
        self.classes = classes if classes is not None else KIND_CLASSES.get(kind, [])
        self.guard = None  # with index.json: refuses a file read outside the vector's own (checked) files
        self.algorithm = expected.get("algorithm") if isinstance(expected, dict) else None  # a sign vector's


FOLDER_KINDS = [  # (folder, default kind when expected.json has none)
    ("valid", "run"), ("runs", "run"), ("encoding", "encoding"), ("seal-vectors", "seal"),
    ("chain-vectors", "chain"), ("overlay-views", "overlay-view"), ("invalid", "document"),
    ("reader-only", "reader-only"), ("checkpoints", "checkpoint"), ("lane-vectors", "lane"),
    ("signature-vectors", "signature"),
    ("rulings", "run"),  # pin_vectors.py: vectors of several kinds, each named in its expected.json
    ("limits", "run"),  # generated by the runner from the recipe in expected.json (spec 09 §9.2)
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
    writes = corpus / "write-vectors"
    if writes.is_dir():
        for kind in WRITE_KINDS:
            for d in sorted(p for p in (writes / kind).iterdir() if p.is_dir()) if (writes / kind).is_dir() else []:
                vectors.append(Vector(kind, f"write-vectors/{kind}/{d.name}", d, read_json(d / "expected.json")))
    else:
        notices.append("notice: write-vectors/ does not exist; its vectors are skipped")
    jobs = corpus / "jobs"
    if jobs.is_dir():
        for d in sorted(p for p in jobs.iterdir() if (p / "expected.json").is_file()):
            vectors.append(Vector("job", f"jobs/{d.name}", d, read_json(d / "expected.json")))
    else:
        notices.append("notice: jobs/ does not exist; its vectors are skipped")
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


class GenerateSkipped(Exception):
    """A generated vector this platform cannot build (a symbolic link without the privilege): skipped, and counted."""


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
        v.level = entry.get("level")
        v.algorithm = entry.get("algorithm", v.algorithm)  # index.json gives a sign vector's algorithm
        vectors.append(v)
    return vectors, refusals


# ---------------------------------------------------------------------------- running one vector

def _problems(value):
    return [list(p) for p in value] if isinstance(value, list) else value


def compare(diffs, label, actual, expected):
    if actual != expected:
        diffs.append(f"{label}: expected {json.dumps(expected, ensure_ascii=False)}, "
                     f"got {json.dumps(actual, ensure_ascii=False)}")


_GENERATED = {}  # vector id -> the folder generated for it, once per process (implementations only read it)
_GENERATED_COUNT = itertools.count(1)


def _generated(v):
    """Spec 09 §9.2: the folder a vector's `generate` recipe gives. The vector's own files are copied, then each step
    is applied in order: copy (a corpus file or folder into the vector), remove, files (that many empty files, named
    0 to N-1), write and append (a file of text parts, each repeated)."""
    if v.id in _GENERATED:
        return _GENERATED[v.id]
    if not _GENERATED:
        root = Path(tempfile.mkdtemp(prefix="aef-generated-"))
        atexit.register(shutil.rmtree, root, True)
        _GENERATED[None] = root
    folder = _GENERATED[None] / f"v{next(_GENERATED_COUNT)}"  # a skipped vector's folder is never reused
    shutil.copytree(v.path, folder)
    corpus = v.path.parent.parent
    for step in v.expected["generate"]:
        (op, arg), = step.items()
        for rel in (arg if op in ("copy", "link") else [arg] if op == "remove" else [arg[0]]):
            # §9.2.1: relative, `/`-separated, no `..`: a recipe never reaches outside the corpus or the vector
            if not isinstance(rel, str) or rel.startswith("/") or "\\" in rel or ":" in rel or ".." in rel.split("/"):
                raise ValueError(f"{v.id}: a generate path that is not relative and inside: {rel!r}")
        if op == "copy":
            source, target = corpus / arg[0], folder / arg[1]
            if v.guard is not None:
                for f in ([source] if source.is_file() else sorted(p for p in source.rglob("*") if p.is_file())):
                    v.guard.check(f)
            (shutil.copytree if source.is_dir() else shutil.copyfile)(source, target)
        elif op == "remove":
            target = folder / arg
            shutil.rmtree(target) if target.is_dir() else target.unlink()
        elif op == "files":
            (folder / arg[0]).mkdir(parents=True, exist_ok=True)
            for i in range(arg[1]):
                (folder / arg[0] / str(i)).write_bytes(b"")
        elif op in ("write", "append"):
            with open(folder / arg[0], "wb" if op == "write" else "ab") as out:
                for text, repeat in arg[1]:
                    out.write(text.encode("utf-8") * repeat)
        elif op == "link":  # a symbolic link at arg[0] to arg[1], both relative to the vector's folder
            link, target = folder / arg[0], folder / arg[1]
            link.parent.mkdir(parents=True, exist_ok=True)
            try:
                os.symlink(os.path.relpath(target, link.parent), link, target_is_directory=target.is_dir())
            except (OSError, NotImplementedError) as error:
                raise GenerateSkipped(f"this platform cannot create a symbolic link ({error})") from None
        else:
            raise ValueError(f"{v.id}: a generate step this runner does not know: {op}")
    _GENERATED[v.id] = folder
    return folder


def run_vector(engine, v, scratch):
    """The list of differences (empty when the vector passes)."""
    e, d, diffs = v.expected, v.path, []
    if isinstance(e, dict) and "generate" in e:
        d = _generated(v)
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
        if "assurance" in e["view"]:  # OVL-3, in the vectors that state it
            compare(diffs, "view.assurance", out.get("assurance"), e["view"]["assurance"])
    elif v.kind in ("document", "reader-only", "plan"):
        out = engine.call(["document", e["schema"], d / e.get("document", "document.json")])
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
                          + (["--at", e["at"]] if "at" in e else []) + _policy(e, d)  # LANE-9's evaluation time
                          + (["--envelope", d / e["envelope"]] if "envelope" in e else []))  # CKP-9
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
        if "anchors" in e:  # CKP-9, SIG-8, in the vectors that state it
            compare(diffs, "anchors", out.get("anchors"), e["anchors"])
    elif v.kind == "signature":
        argv = ["signature", d / e["envelope"], d / e["file"], d / e["policy"]]
        if "payloadType" in e:
            argv += ["--payload-type", e["payloadType"]]
        out = engine.call(argv)
        if e.get("policyRefused"):  # SIG-3: a policy with a key that cannot be used is refused as a whole
            return [] if "error" in out else [f"the policy was accepted: {json.dumps(out)}"]
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
        compare(diffs, "problems", _problems(out.get("problems")), _problems(e["problems"]))
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
    elif v.kind in WRITE_KINDS:
        judge_write(engine, v, scratch, diffs)
    elif v.kind == "job":
        _judge_job(engine, v, scratch, diffs)
    else:
        diffs.append(f"no operation for kind {v.kind!r} in this runner yet")
    return diffs


def _policy(expected, folder):
    """['--policy', path] when the vector carries a trust policy (spec 09 §9.2.1), else []."""
    return ["--policy", folder / expected["policy"]] if "policy" in expected else []


# ---------------------------------------------------------------------------- the write-side vectors (spec 09 §9.3)
# The implementation computes or writes something; the runner judges it, partly through the reference verifier
# (aef_verify.py, in-process), whichever implementation is under test. Nothing is compared byte for byte except an
# Ed25519 signature: JSON formatting is free, and ECDSA signatures need not be deterministic.

_STANDARD_BASE64 = re.compile(r"(?:[A-Za-z0-9+/]{4})*(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=)?")


def _close(actual, expected):
    """§3.6: numbers match when they differ by at most 1e-9 x max(1, |expected|); null matches only null."""
    if actual is None or expected is None:
        return actual is None and expected is None
    if isinstance(actual, bool) or not isinstance(actual, (int, float)):
        return False
    return abs(actual - expected) <= 1e-9 * max(1.0, abs(expected))


def _files(root):
    """{path: bytes} of every file under root."""
    return {p.relative_to(root).as_posix(): p.read_bytes() for p in sorted(Path(root).rglob("*")) if p.is_file()}


def _fresh_copy(scratch, source):
    """A copy of a run folder in a new folder of the scratch directory: a write operation never runs in the corpus."""
    target = Path(tempfile.mkdtemp(dir=scratch)) / "run"
    shutil.copytree(source, target)
    return target


def judge_write(engine, v, scratch, diffs):
    e, d = v.expected, v.path
    if v.kind == "summarize":
        out = engine.call(["summarize", d / e.get("run", "run"), d / e["request"]])
        if e.get("refused"):  # an input that contradicts what the Producer must compute: an input error (exit 2)
            if not (isinstance(out, dict) and "error" in out):
                diffs.append(f"computed a summary for a request it must refuse: {json.dumps(out)}")
            return
        if not isinstance(out, dict) or "error" in out:
            diffs.append(out.get("error") if isinstance(out, dict) else f"not a JSON object: {json.dumps(out)}")
            return
        _judge_summary(diffs, out, e["summary"])
        if not aef_verify.document_ok("writer", "summary", out):
            diffs.append("the summary is not valid against the writer summary schema")
        work = _fresh_copy(scratch, d / e.get("run", "run"))
        (work / "summary.json").write_bytes((json.dumps(out, ensure_ascii=False, indent=2) + "\n").encode("utf-8"))
        verdict = aef_verify.op_run(work)
        compare(diffs, "the run verifier on the run with this summary.json", [verdict["outcome"],
                _problems(verdict["problems"])], [e.get("outcome", "unsealed"), []])
    elif v.kind == "produce":
        _judge_produce(engine, v, scratch, diffs)
    elif v.kind == "seal-write":
        _judge_seal_write(engine, v, scratch, diffs)
    elif v.kind == "sign":
        _judge_sign(engine, v, scratch, diffs)


def _judge_summary(diffs, out, want):
    """[SUM-2]-[SUM-9]: runId, lanes and entries in request order; counts, verdict, rule and aggregate exactly; sum,
    sumSq and value under §3.6's tolerance."""
    compare(diffs, "runId", out.get("runId"), want["runId"])
    lanes = [l for l in out.get("lanes")] if isinstance(out.get("lanes"), list) else []
    compare(diffs, "lanes", [l.get("lane") if isinstance(l, dict) else l for l in lanes], [l["lane"] for l in want["lanes"]])
    for have, lane in zip(lanes, want["lanes"]):
        entries = have.get("metrics") if isinstance(have, dict) and isinstance(have.get("metrics"), list) else []
        entries = [x if isinstance(x, dict) else {} for x in entries]
        compare(diffs, f"{lane['lane']}: entries (metric, path)", [[x.get("metric"), x.get("path")] for x in entries],
                [[x["metric"], x["path"]] for x in lane["metrics"]])
        for got, x in zip(entries, lane["metrics"]):
            label = f"{lane['lane']}/{x['metric']}@{x['path']}"
            for field in ("N", "n", "notMeasured", "verdict", "rule", "aggregate"):
                compare(diffs, f"{label} {field}", got.get(field, "<absent>"), x.get(field, "<absent>"))
            for field in ("sum", "sumSq", "value"):
                if not _close(got.get(field, "<absent>"), x[field]):
                    diffs.append(f"{label} {field}: expected {json.dumps(x[field])} (within 1e-9 x max(1, |x|)), "
                                 f"got {json.dumps(got.get(field, '<absent>'))}")


PRODUCED = ("metrics.json", "results.ndjson", "run.json", "summary.json")  # spec 09 §9.3: produce writes these
_ABSENT = "<absent>"


def _same(actual, expected):
    """Two JSON values are equal member by member, numbers under §3.6's rule (within 1e-9 x max(1, |expected|)), a
    boolean only to a boolean, two times as times ([ENC-8]: `...00Z` and `...00.000Z` are one instant)."""
    if isinstance(actual, bool) or isinstance(expected, bool):
        return type(actual) is type(expected) and actual == expected
    if isinstance(actual, str) and isinstance(expected, str) and actual != expected:
        a, b = aef_verify.time_key(actual), aef_verify.time_key(expected)
        return a is not None and a == b
    if isinstance(expected, (int, float)):
        return _close(actual, expected)
    if isinstance(expected, dict):
        return isinstance(actual, dict) and actual.keys() == expected.keys() and all(
            _same(actual[k], expected[k]) for k in expected)
    if isinstance(expected, list):
        return isinstance(actual, list) and len(actual) == len(expected) and all(map(_same, actual, expected))
    return type(actual) is type(expected) and actual == expected


def _as_given(run):
    """run.json as compared with the scenario's run (spec 09 §9.3): an absent contentCapture is `on` ([RUN-11])."""
    run = _without_nulls(run)
    if isinstance(run, dict) and "contentCapture" not in run:
        run = dict(run, contentCapture="on")
    return run


def _without_nulls(value):
    """The JSON value with every null-valued member left out, at every depth ([ENC-2])."""
    if isinstance(value, dict):
        return {k: _without_nulls(v) for k, v in value.items() if v is not None}
    if isinstance(value, list):
        return [_without_nulls(v) for v in value]
    return value


def _as_written(line):
    """A result line with each absence the spec gives a value written as that value's absence, so two writers that
    choose differently compare equal: a null parentResultId (a root, [RES-5]), a null threshold or score, an
    unmeasured count of 0 ([RES-6]), an empty decisive list; and decisive in any order, since none is given."""
    if not isinstance(line, dict):
        return line
    line = _without_nulls(line)  # ENC-2: null and absence mean the same on a result line's optional fields
    aggregation = line.get("aggregation")
    if isinstance(aggregation, dict):
        aggregation = {k: v for k, v in aggregation.items() if not (k in ("threshold", "score") and v is None)}
        unmeasured = aggregation.get("unmeasured")
        if isinstance(unmeasured, dict):
            unmeasured = {k: v for k, v in unmeasured.items() if not (_same(v, 0) and not isinstance(v, bool))}
            if unmeasured:
                aggregation["unmeasured"] = unmeasured
            else:
                del aggregation["unmeasured"]
        decisive = aggregation.get("decisive")
        if decisive == []:
            del aggregation["decisive"]
        elif isinstance(decisive, list) and all(isinstance(d, str) for d in decisive):
            aggregation["decisive"] = sorted(decisive)
        line["aggregation"] = aggregation
    return line


def _node_key(line):
    """A line's place in the run: its case, path and trial (spec 09 §9.3 matches lines by them)."""
    return (line.get("caseId"), line.get("path"), line.get("trial")) if isinstance(line, dict) else None


def _label(key):
    case, path, trial = key
    return f"{case}@{path}" + ("" if trial is None else f"#{trial}")


def _read_lines(data, where, diffs):
    """The objects of an NDJSON file (None, with a difference, when it does not read)."""
    lines = []
    try:
        text = data.decode("utf-8")
    except UnicodeDecodeError as error:
        diffs.append(f"{where} is not UTF-8: {error}")
        return None
    if text and ("\r" in text or not text.endswith("\n") or "\n\n" in text or text.startswith("\n")):
        diffs.append(f"{where} is not NDJSON (a CR, a blank line or an unended last line, [ENC-5])")
        return None
    for i, raw in enumerate(text.split("\n")[:-1] if text else [], start=1):
        try:
            lines.append(aef_verify.load_json_bytes(raw.encode("utf-8")))
        except aef_verify.EncodingProblem as error:
            diffs.append(f"{where}:{i} is not an I-JSON object: {error}")
            return None
    return lines


def _judge_produce(engine, v, scratch, diffs):
    """[RES-4]-[RES-8], [SUM-2]-[SUM-9]: the run written from the scenario in a fresh folder holds exactly the four
    files; run.json and metrics.json are the scenario's, as given; the lines are the expected ones as a set (matched
    by case, path and trial, member by member, numbers under §3.6's rule), each valid against the writer result
    schema; summary.json is judged as summarize's output is; and the run verifies unsealed with no problems. A
    refused scenario writes nothing."""
    e, d = v.expected, v.path
    scenario_file = d / e["scenario"]
    out_dir = Path(tempfile.mkdtemp(dir=scratch)) / "run"  # does not exist yet: the operation creates it
    out = engine.call(["produce", scenario_file, out_dir])
    written = _files(out_dir) if out_dir.is_dir() else {}
    if e.get("refused"):  # a scenario that contradicts itself: an input error (exit 2), and nothing written
        if not (isinstance(out, dict) and "error" in out):
            diffs.append(f"wrote a run for a scenario it must refuse: {json.dumps(out)}")
        if written:
            diffs.append(f"wrote files although it refused: {', '.join(sorted(written))}")
        return
    if not isinstance(out, dict) or "error" in out:
        diffs.append(out.get("error") if isinstance(out, dict) else f"not a JSON object: {json.dumps(out)}")
        return
    scenario = read_json(scenario_file)
    compare(diffs, "the files written", sorted(written), list(PRODUCED))
    documents = {}
    for name in ("run.json", "metrics.json", "summary.json"):
        if name in written:
            try:
                documents[name] = aef_verify.load_json_bytes(written[name])
            except aef_verify.EncodingProblem as error:
                diffs.append(f"{name} is not an I-JSON document: {error}")
    for name, member in (("run.json", "run"), ("metrics.json", "metrics")):
        # ENC-2: on an optional field, null and absence mean the same (neither file gives null a meaning of its own)
        normal = _as_given if name == "run.json" else _without_nulls
        if name in documents and not _same(normal(documents[name]), normal(scenario[member])):
            diffs.append(f"{name}: not the scenario's {member}, as given: got {json.dumps(documents[name])}")

    want = {}
    for line in (d / e["results"]).read_bytes().decode("utf-8").splitlines():
        line = json.loads(line)
        want[_node_key(line)] = _as_written(line)
    lines = _read_lines(written["results.ndjson"], "results.ndjson", diffs) if "results.ndjson" in written else None
    if lines is not None:
        compare(diffs, "results (the operation's output)", out.get("results"), len(lines))
        have = {}
        for i, line in enumerate(lines, start=1):
            if not aef_verify.document_ok("writer", "result", line):
                diffs.append(f"results.ndjson:{i} is not valid against the writer result schema")
            key = _node_key(line)
            if key in have:
                diffs.append(f"results.ndjson:{i}: a second line for case, path and trial {_label(key)}")
            have.setdefault(key, _as_written(line))
        for key in sorted(set(want) - set(have), key=str):
            diffs.append(f"no line for case, path and trial {_label(key)}")
        for key in sorted(set(have) - set(want), key=str):
            diffs.append(f"a line for case, path and trial {_label(key)}, which the scenario has not")
        for key in sorted(set(want) & set(have), key=str):
            got, expected = have[key], want[key]
            for member in sorted(set(got) | set(expected)):
                if not _same(got.get(member, _ABSENT), expected.get(member, _ABSENT)):
                    diffs.append(f"{_label(key)} {member}: expected {json.dumps(expected.get(member, _ABSENT))}, "
                                 f"got {json.dumps(got.get(member, _ABSENT))}")
    if "summary.json" in documents:
        _judge_summary(diffs, documents["summary.json"], e["summary"])
        if not aef_verify.document_ok("writer", "summary", documents["summary.json"]):
            diffs.append("summary.json is not valid against the writer summary schema")
    verdict = aef_verify.op_run(out_dir)
    compare(diffs, "the run verifier on the run written", [verdict["outcome"], _problems(verdict["problems"])],
            ["unsealed", []])


def _judge_seal_write(engine, v, scratch, diffs):
    """[SEAL-1]-[SEAL-5] on a fresh copy of the run: the seal.json written is valid against the writer schema, lists
    the expected manifest's paths and digests in its order, holds the expected predicate (times as times), and is
    the only change; the run verifier then finds the copy intact."""
    e, d = v.expected, v.path
    work = _fresh_copy(scratch, d / e.get("run", "run"))
    before = _files(work)
    out = engine.call(["seal-write", work, "--sealed-by", e["sealedBy"], "--sealed-at", e["sealedAt"]])
    after = _files(work)
    changed = sorted(p for p in set(before) | set(after) if p != "seal.json" and before.get(p) != after.get(p))
    if e.get("refused"):  # [SEAL-1]: the run cannot be sealed; the operation refuses it and writes nothing
        if not (isinstance(out, dict) and "error" in out):
            diffs.append(f"sealed a run it must refuse: {json.dumps(out)}")
        if after != before:
            diffs.append(f"changed the run folder although it refused: {sorted(set(before) ^ set(after)) or changed}")
        return
    if not isinstance(out, dict) or "error" in out:
        diffs.append(out.get("error") if isinstance(out, dict) else f"not a JSON object: {json.dumps(out)}")
        return
    manifest = (d / e["manifest"]).read_bytes()
    compare(diffs, "runHash", out.get("runHash"), hashlib.sha256(manifest).hexdigest())
    if changed:
        diffs.append(f"files other than seal.json added, removed or changed: {', '.join(changed)}")
    if "seal.json" not in after:
        diffs.append("no seal.json was written")
        return
    try:
        seal = aef_verify.load_json_bytes(after["seal.json"])
    except aef_verify.EncodingProblem as error:
        diffs.append(f"seal.json is not an I-JSON document: {error}")
        return
    if not aef_verify.document_ok("writer", "seal", seal):
        diffs.append("seal.json is not valid against the writer seal schema")
    want_subjects = [[line.split("  ", 2)[2], line.split("  ", 2)[0]] for line in manifest.decode("utf-8").splitlines()]
    subjects = [[s.get("name"), (s.get("digest") or {}).get("sha256") if isinstance(s.get("digest"), dict) else None]
                for s in seal.get("subject") or [] if isinstance(s, dict)]
    compare(diffs, "subjects [name, sha256], in order", subjects, want_subjects)
    predicate = seal.get("predicate") if isinstance(seal.get("predicate"), dict) else {}
    compare(diffs, "predicate members", sorted(predicate), sorted(e["predicate"]))
    for field, value in e["predicate"].items():
        got = predicate.get(field, "<absent>")
        if field in ("closedAt", "sealedAt"):  # compared as times ([ENC-8]), at the full precision written
            if aef_verify.time_key(got) is None or aef_verify.time_key(got) != aef_verify.time_key(value):
                diffs.append(f"predicate.{field}: expected the time {value}, got {json.dumps(got)}")
        else:
            compare(diffs, f"predicate.{field}", got, value)
    verdict = aef_verify.op_run(work)
    compare(diffs, "the run verifier on the sealed copy", [verdict["outcome"], _problems(verdict["problems"])],
            ["intact", []])


def _judge_sign(engine, v, scratch, diffs):
    """[SIG-1]-[SIG-3]: the envelope has one signature under the key's id and standard, padded base64, and the
    signature operation verifies it for the key's identity under the vector's trust policy."""
    e, d = v.expected, v.path
    key = d / e["key"]
    if v.guard is not None:
        v.guard.check(key)  # the test keys live outside the vector's folder (signature-vectors/keys/)
    out = engine.call(["sign", d / e["file"], key, "--payload-type", e["payloadType"]])
    if not isinstance(out, dict) or "error" in out:
        diffs.append(out.get("error") if isinstance(out, dict) else f"not a JSON object: {json.dumps(out)}")
        return
    compare(diffs, "payloadType", out.get("payloadType"), e["payloadType"])
    signatures = out.get("signatures")
    if not (isinstance(signatures, list) and len(signatures) == 1 and isinstance(signatures[0], dict)):
        diffs.append(f"signatures: expected one signature, got {json.dumps(signatures)}")
        signatures = [{}]
    compare(diffs, "keyid", signatures[0].get("keyid"), e["keyid"])
    for field, text in (("payload", out.get("payload")), ("sig", signatures[0].get("sig"))):
        if not (isinstance(text, str) and _STANDARD_BASE64.fullmatch(text)
                and base64.b64encode(base64.b64decode(text)).decode("ascii") == text):
            diffs.append(f"{field}: not base64 in the standard alphabet with padding ([SIG-1]): {json.dumps(text)}")
    envelope = scratch / "sign-envelope.dsse.json"
    envelope.write_bytes(json.dumps(out, ensure_ascii=False).encode("utf-8"))
    verdict = aef_verify.op_signature(envelope, d / e["file"], d / e["policy"], e["payloadType"])
    compare(diffs, "envelopeResult (the signature operation)", verdict["envelopeResult"], None)
    compare(diffs, "signatures (the signature operation)", verdict["signatures"],
            [{"keyid": e["keyid"], "result": "verified", "identity": e["identity"]}])
    compare(diffs, "verifiesFor (the signature operation)", verdict["verifiesFor"], [e["identity"]])
    if "sig" in e:  # Ed25519 is deterministic (RFC 8032): these bytes and no others
        compare(diffs, "sig (Ed25519)", signatures[0].get("sig"), e["sig"])


# ---------------------------------------------------------------------------- the job vectors (spec 09 §9.3)

JOB_RUN_FILES = {"run.json": "run", "results.ndjson": "result", "metrics.json": "metrics", "summary.json": "summary",
                 "seal.json": "seal"}  # the files of a run the judge checks against their writer schemas
SUBJECT_KINDS = aef_verify._writer_enum("run.schema.json#/properties/subject/properties/kind/enum")


def job_environment(env):
    """{variable: a fresh random value, the empty string, or None to remove it} for a vector's `env` ({name: "set",
    "empty" or "absent"}, spec 09 §9.3)."""
    value = {"set": lambda: f"aef-secret-{secrets.token_hex(16)}", "empty": lambda: "", "absent": lambda: None}
    return {name: value[state]() for name, state in env.items()}


def _job(engine, v, scratch, diffs):
    """Runs a job vector's operation in a fresh OUT; returns (OUT, the operation's output, the variables set)."""
    e, d = v.expected, v.path
    for name in ("runner", "target", "plan"):
        if v.guard is not None:
            v.guard.check(d / e[name])  # the shared runner manifest lives outside the vector's folder
    out_dir = Path(tempfile.mkdtemp(dir=scratch)) / "out"  # does not exist yet: the operation creates it
    env = job_environment(e.get("env") or {})
    out = engine.call(["job", d / e["plan"], d / e["runner"], d / e["target"], out_dir, "--at", e["at"]], env=env)
    return out_dir, out, env


def _judge_job(engine, v, scratch, diffs):
    e, d = v.expected, v.path
    out_dir, out, env = _job(engine, v, scratch, diffs)
    if e.get("refused"):  # spec 09 §9.3: an input error (exit 2), with nothing written in OUT
        if not (isinstance(out, dict) and "error" in out):
            diffs.append(f"ran a job it must refuse as an input error: {json.dumps(out)}")
        written = sorted(_files(out_dir)) if out_dir.is_dir() else []
        if written:
            diffs.append(f"wrote files although it refused: {', '.join(written)}")
        return
    diffs.extend(judge_job_output(out_dir, out, d / e["plan"], d / e["target"], e,
                                  [value for value in env.values() if value], at=e["at"]))


def _exact_sum(values):
    """A sum computed exactly and rounded once ([SUM-5]), as the binary64 value written."""
    return float(sum((Fraction(x) for x in values), Fraction(0)))


def _plan_secrets(plan):
    """What a runner never writes (PLAN-3, PLAN-4): every credential reference's path, and a bare value written where
    a reference belongs."""
    refs = plan.get("credentialRefs") if isinstance(plan, dict) else None
    found = []
    for ref in refs if isinstance(refs, list) else []:
        if isinstance(ref, str):
            found.append(ref)
        elif isinstance(ref, dict) and isinstance(ref.get("path"), str):
            found.append(ref["path"])
    return [s for s in found if s]


def _enc13_name(text):
    """ENC-13: a name derived from free text: each UTF-8 byte from ! to ~ but % kept, every other byte as %XX; - for an
    empty name, %2D for -; beyond 256 characters, the first 239, ~ and 16 hex digits of the SHA-256 of the text."""
    name = "".join(chr(b) if 0x21 <= b <= 0x7E and b != 0x25 else "%{:02X}".format(b) for b in text.encode("utf-8"))
    if name in ("", "-"):
        return {"": "-", "-": "%2D"}[name]
    return name if len(name) <= 256 else name[:239] + "~" + hashlib.sha256(text.encode("utf-8")).hexdigest()[:16]


def _derived(plan):
    """PLAN-10: (subject.kind, deployment.ref) a run of the plan has: the ref's kind when run.json knows it, else
    other; the plan's deployment, or endpoint: and its endpoint encoded as ENC-13 says, or none."""
    subject = plan["subject"]
    kind = subject["ref"].split(":", 1)[0]
    ref = subject.get("deployment") or ("endpoint:" + _enc13_name(subject["endpoint"]) if "endpoint" in subject else None)
    return (kind if kind in SUBJECT_KINDS else "other"), ref


def judge_job_output(out_dir, out, plan_path, target_path, e, secret_values, at=None):
    """Spec 09 §9.3: the differences between what a job wrote in OUT (and printed) and the expected job e: `terminal`,
    `limit`, `runs` (each `suite`, `status`, `cases` as [caseId, state] in order), `spentUsd`, `estimated` (or null),
    and, with `at`, `endsAt`. secret_values: the values given to credential variables, never to be written."""
    diffs = []
    plan_bytes = Path(plan_path).read_bytes()
    plan = json.loads(plan_bytes.decode("utf-8"))
    target = read_json(target_path)
    cost = {(s["ref"], s["version"], c["caseId"]): c["usd"] for s in target["suites"] for c in s["cases"]}
    if not isinstance(out, dict) or "error" in out:
        return [out.get("error") if isinstance(out, dict) else f"not a JSON object: {json.dumps(out)}"]
    written = _files(out_dir) if out_dir.is_dir() else {}
    if "events.ndjson" not in written:
        return [f"no events.ndjson in OUT (it holds {', '.join(sorted(written)) or 'nothing'})"]
    events, data = [], written["events.ndjson"]
    lines = _read_lines(data, "events.ndjson", diffs)
    if lines is None:
        return diffs
    for i, event in enumerate(lines, start=1):
        if not (isinstance(event, dict) and aef_verify.document_ok("writer", "runner-event", event)):
            diffs.append(f"events.ndjson:{i} is not valid against the writer runner-event schema")
            event = event if isinstance(event, dict) else {}
        events.append(event)
    if not events:
        return diffs + ["events.ndjson holds no event"]
    compare(diffs, "events (the operation's output)", out.get("events"), len(events))
    first, last = events[0], events[-1]
    kinds = [x.get("kind") for x in events]
    compare(diffs, "the first event", first.get("kind"),
            "job.refused" if e["terminal"] == "job.refused" else "job.accepted")
    compare(diffs, "the terminal event and its limit", [last.get("kind"), last.get("limit")], [e["terminal"], e["limit"]])
    if at is not None:  # §9.2.1: the job's clock
        if aef_verify.time_key(first.get("at")) != aef_verify.time_key(at):
            diffs.append(f"the first event is at {first.get('at')}, not at the clock's start {at}")
        if "endsAt" in e and aef_verify.time_key(last.get("at")) != aef_verify.time_key(e["endsAt"]):
            diffs.append(f"the terminal event is at {last.get('at')}, not at {e['endsAt']} (the start, the seconds of "
                         f"the cases run and closeSeconds per run sealed)")

    # The files: the stream, and one folder per run announced.
    announced = []
    for x in events:
        if x.get("kind") == "evidence.produced" and x.get("runId") not in announced:
            announced.append(x.get("runId"))
    folders = {name.split("/")[1] for name in written if name.startswith("runs/") and name.count("/") >= 2}
    others = sorted(n for n in written if n != "events.ndjson" and not (n.startswith("runs/") and n.count("/") >= 2))
    if others or folders != set(announced):
        diffs.append(f"OUT holds {sorted(written)}: not only events.ndjson and runs/<runId>/ for the runs announced "
                     f"({announced})")

    # STRM-3 and STRM-4, by the reference verifier; a plan the reader refuses gets one job.refused naming it.
    events_path = out_dir / "events.ndjson"
    readable = isinstance(plan, dict) and aef_verify.schema_valid("reader", "run-plan", plan)
    if readable:
        stream = aef_verify.op_stream(events_path, plan_path)["problems"]
        if stream:
            diffs.append(f"STRM-3 finds {stream}")
        conform = aef_verify.op_conform(events_path, plan_path, out_dir)["problems"]
        if conform:
            diffs.append(f"STRM-4 finds {conform}")
    else:
        compare(diffs, "the one event for a plan the reader refuses", [kinds, first.get("planId"), first.get("planDigest")],
                [["job.refused"], plan.get("planId") if isinstance(plan, dict) else None,
                 hashlib.sha256(plan_bytes).hexdigest()])

    # Each run, in the order announced.
    accepted = first if first.get("kind") == "job.accepted" else {}
    compare(diffs, "the runs announced", len(announced), len(e["runs"]))
    kind, deployment = _derived(plan) if readable else (None, None)
    plan_suites = {(s.get("ref"), s.get("version")): s for s in plan.get("suites") or [] if isinstance(s, dict)} \
        if readable else {}
    fixture = {(s["ref"], s["version"], c["caseId"]): c for s in target["suites"] for c in s["cases"]}
    # §9.2.1's clock: a run's endedAt is the end of its last case; closing and sealing it then take closeSeconds.
    ended, clock = [], aef_verify.time_key(at) if at is not None else None
    for r in e["runs"]:
        for c, _ in r["cases"]:
            seconds = fixture.get((r["suite"]["ref"], r["suite"]["version"], c), {}).get("seconds", 0)
            clock = None if clock is None else (clock[0] + int(seconds), clock[1])
        ended.append(clock)
        clock = None if clock is None else (clock[0] + int(target["closeSeconds"]), clock[1])
    for k, run_id in enumerate(announced):
        folder = out_dir / "runs" / str(run_id)
        want = e["runs"][k] if k < len(e["runs"]) else None
        where = f"run {k + 1} ({run_id})"
        verdict = aef_verify.op_run(folder)
        if [verdict["outcome"], verdict["problems"]] != ["intact", []]:
            diffs.append(f"{where}: the run verifier gives {verdict['outcome']} {verdict['problems']}, not intact")
        for name, schema in JOB_RUN_FILES.items():
            if (folder / name).is_file() and aef_verify.op_document(schema, folder / name)["writer"] != "valid":
                diffs.append(f"{where}: {name} is not valid against the writer {schema} schema")
        try:
            doc = aef_verify.load_json_bytes((folder / "run.json").read_bytes())
            results = _read_lines((folder / "results.ndjson").read_bytes(), f"{where} results.ndjson", diffs) or []
            summary = aef_verify.load_json_bytes((folder / "summary.json").read_bytes())
            metrics = aef_verify.load_json_bytes((folder / "metrics.json").read_bytes())
        except (OSError, aef_verify.EncodingProblem) as error:
            diffs.append(f"{where}: {error}")
            continue
        get = aef_verify.get
        compare(diffs, f"{where} execution.targetMode", get(doc, "execution", "targetMode"), "scripted")
        if doc.get("judges") not in (None, []):  # §9.2.1: the scripted target grades with no model (RUN-9)
            diffs.append(f"{where} names judges, though no model graded its cases: {json.dumps(doc['judges'])}")
        compare(diffs, f"{where} provenance (RUN-12)", doc.get("provenance"),
                {key: accepted.get(key) for key in ("planId", "planDigest", "jobId", "runnerId")})
        compare(diffs, f"{where} subject.kind and deployment.ref (PLAN-10)",
                [get(doc, "subject", "kind"), get(doc, "deployment", "ref")], [kind, deployment])
        if want is None:
            continue
        suite = [get(doc, "suite", "ref"), get(doc, "suite", "version")]
        compare(diffs, f"{where} suite", suite, [want["suite"]["ref"], want["suite"]["version"]])
        compare(diffs, f"{where} status", doc.get("status"), want["status"])
        planned = plan_suites.get((want["suite"]["ref"], want["suite"]["version"]), {})
        lane = planned.get("lane")
        # PLAN-8: the plan's digest, or none (suite.digest is LANE-6's suite-content axis)
        compare(diffs, f"{where} suite.digest (PLAN-8)", get(doc, "suite", "digest"), planned.get("digest"))
        if k < len(ended) and ended[k] is not None and aef_verify.time_key(doc.get("endedAt")) != ended[k]:
            diffs.append(f"{where} endedAt {doc.get('endedAt')}: not the end of its last case on §9.2.1's clock")
        roots = {}
        for i, line in enumerate(results, start=1):
            if not isinstance(line, dict) or line.get("parentResultId") is not None or line.get("path") != "check":
                diffs.append(f"{where} results.ndjson:{i}: not a case's root at path check")
            elif line.get("caseId") in roots:
                diffs.append(f"{where} results.ndjson:{i}: a second line for case {line.get('caseId')}")
            else:
                roots[line.get("caseId")] = [line.get("state"), line.get("severity"), line.get("lane")]
        compare(diffs, f"{where} cases (caseId: state, severity, lane)", roots,
                {c: [s, fixture.get((suite[0], suite[1], c), {}).get("severity"), lane] for c, s in want["cases"]})
        # §9.2.1: metrics.json declares pass-rate; summary.json has the suite's lane, pass-rate at check, or no lane
        declared = [m for m in (metrics.get("metrics") if isinstance(metrics, dict) else None) or []
                    if isinstance(m, dict) and m.get("id") == "pass-rate"]
        compare(diffs, f"{where} metrics.json pass-rate", [[m.get("kind"), m.get("direction"), m.get("scale")]
                                                          for m in declared],
                [["rate", "higher_better", {"min": 0, "max": 1}]])
        lanes = [[l.get("lane"), [[x.get("metric"), x.get("path")] for x in l.get("metrics") or [] if isinstance(x, dict)]]
                 for l in summary.get("lanes") or [] if isinstance(l, dict)]
        compare(diffs, f"{where} summary.json lanes (lane, [metric, path])", lanes,
                [[lane, [["pass-rate", "check"]]]] if lane is not None else [])
        compare(diffs, f"{where} summary.json cost.totalUsd", get(summary, "cost", "totalUsd"),
                _exact_sum(cost.get((suite[0], suite[1], c), 0) for c, _ in want["cases"]))
    # The cases as they completed, in order, each naming its run when it names one.
    completed = [x for x in events if x.get("kind") == "case.completed"]
    expected_cases = [(c, s, k) for k, r in enumerate(e["runs"]) for c, s in r["cases"]]
    compare(diffs, "case.completed (caseId, state), in order", [[x.get("caseId"), x.get("state")] for x in completed],
            [[c, s] for c, s, _ in expected_cases])
    for x, (c, _, k) in zip(completed, expected_cases):
        if "runId" in x and k < len(announced) and x["runId"] != announced[k]:
            diffs.append(f"case.completed for {c} names the run {x['runId']}, not {announced[k]}")
    spends = [x.get("spentUsd") for x in events if x.get("kind") == "spend.updated"]
    compare(diffs, "the last spend.updated", (spends or [0])[-1], e["spentUsd"])
    estimates = [{key: x.get(key) for key in ("cases", "usdLow", "usdHigh")}
                 for x in events if x.get("kind") == "plan.estimated"]
    compare(diffs, "plan.estimated", estimates, [] if e["estimated"] is None else [e["estimated"]])

    # PLAN-3, PLAN-4: no credential value and no credential path in any byte written or printed.
    secrets_ = [s.encode("utf-8") for s in list(secret_values) + _plan_secrets(plan)]
    for name, content in sorted(written.items()) + [("standard output", json.dumps(out).encode("utf-8"))]:
        for secret in secrets_:
            if secret in content:
                diffs.append(f"{name} holds a credential's value or path (PLAN-3)")
    return diffs


def run_all(engine, vectors, refusals, quiet=False, show=print, skipped=()):
    """{kind: [passed, failed, skipped]} and the failing ids. `skipped`: vectors not run (the sign vectors of an
    algorithm the implementation does not sign with), counted as such."""
    tally, failing = OrderedDict(), []
    with tempfile.TemporaryDirectory(prefix="aef-conformance-") as scratch:
        for kind, vid, why, _ in refusals:
            tally.setdefault(kind, [0, 0, 0])[1] += 1
            failing.append(vid)
            show(f"FAIL  {kind:<12} {vid}\n      refused: {why}")
        for v in skipped:
            tally.setdefault(v.kind, [0, 0, 0])[2] += 1
            if not quiet:
                show(f"skip  {v.kind:<12} {v.id} (needs {v.algorithm}, not in --sign-algorithms)")
        for v in vectors:
            try:
                diffs = run_vector(engine, v, Path(scratch))
            except GenerateSkipped as why:  # spec 09 §9.2.1: skipped where the platform cannot build it, and said so
                tally.setdefault(v.kind, [0, 0, 0])[2] += 1
                if not quiet:
                    show(f"skip  {v.kind:<12} {v.id} ({why})")
                continue
            except Exception as error:  # a crash is a failure of that vector, not of the runner
                diffs = [f"{type(error).__name__}: {error}"]
            tally.setdefault(v.kind, [0, 0, 0])[1 if diffs else 0] += 1
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
    skips = any(t[2] for t in tally.values())
    show(f"{'kind':<14}{'pass':>6}{'fail':>6}" + (f"{'skip':>6}" if skips else ""))
    for kind, (ok, bad, skip) in tally.items():
        show(f"{kind:<14}{ok:>6}{bad:>6}" + (f"{skip:>6}" if skips else ""))
    total_ok = sum(t[0] for t in tally.values())
    total_bad = sum(t[1] for t in tally.values())
    show(f"{'total':<14}{total_ok:>6}{total_bad:>6}" + (f"{sum(t[2] for t in tally.values()):>6}" if skips else ""))
    return total_bad


def _heavy(v):
    """A generated vector that creates more than 20,000 files or writes more than 8 MiB."""
    steps = v.expected.get("generate", []) if isinstance(v.expected, dict) else []
    files = sum(arg[1] for step in steps for op, arg in step.items() if op == "files")
    size = sum(len(text.encode("utf-8")) * n for step in steps for op, arg in step.items() if op in ("write", "append")
               for text, n in arg[1])
    return files > 20_000 or size > 8 * 1024 * 1024


def _load(corpus, index):
    """The vectors and refusals of a corpus, from its index when one is given."""
    return from_index(corpus, index) if index is not None else (walk(corpus, []), [])


def _mutation_failures(job):
    """The ids of the vectors that fail with one mutation switched on (None: unmutated), in a process of its own."""
    corpus, index, ids, module_name, name = job
    vectors, refusals = _load(corpus, index)
    vectors = [v for v in vectors if v.id in ids]
    refusals = [r for r in refusals if r[1] in ids]
    # A pool worker runs several jobs: switch off whatever the previous one switched on, in both modules, so that each
    # job runs exactly one mutation (or none).
    aef_verify.set_mutations(set())
    aef_produce.set_mutations(set())
    aef_runner.set_mutations(set())
    if name is not None:
        {"aef_verify": aef_verify, "aef_produce": aef_produce, "aef_runner": aef_runner}[module_name].set_mutations({name})
    _, failing = run_all(InProcess(), vectors, refusals, quiet=True, show=lambda *a: None)
    return failing


def self_check(vectors, refusals, corpus, index):
    """Runs the corpus under each mutation, in parallel processes; every mutation must make a vector fail."""
    caught_all = True
    print("self-check: each mutation switches one check of aef_verify.py off, or breaks one writer of aef_produce.py or "
          "one rule of aef_runner.py; the corpus must notice")
    # The heavy generated vectors (a run of 100,000 files, a 40 MiB seal) are left out: run once per mutation they
    # would make the self-check take an hour, and "limits" is caught by the light ones.
    ids = {v.id for v in vectors if not _heavy(v)}
    ids |= {r[1] for r in refusals}
    mutations = [(module, name, what) for module in (aef_verify, aef_produce, aef_runner)
                 for name, what in module.KNOWN_MUTATIONS.items()]
    jobs = [(corpus, index, ids, None, None)] + [(corpus, index, ids, m.__name__, name) for m, name, _ in mutations]
    with concurrent.futures.ProcessPoolExecutor(max_workers=min(len(jobs), os.cpu_count() or 1)) as pool:
        results = list(pool.map(_mutation_failures, jobs))
    baseline = results[0]
    print(f"  unmutated: {len(baseline)} vector(s) fail{': ' + ', '.join(baseline) if baseline else ''}"
          + (" (a mutation is caught only by a vector that passes unmutated)" if baseline else ""))
    for (module, name, what), failing in zip(mutations, results[1:]):
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
    parser.add_argument("--level", choices=("intact", "signed"), default="signed",
                        help="a Run verifier's level (spec 09 §9.1): 'intact' leaves out the vectors index.json marks "
                             "signed")
    parser.add_argument("--sign-algorithms", default=",".join(SIGN_ALGORITHMS), metavar="ALG[,ALG]",
                        help="the algorithms a Sealer signs with (spec 09 §9.1), of " + ", ".join(SIGN_ALGORITHMS)
                             + "; the sign vectors of the others are skipped and counted as such (default: both)")
    parser.add_argument("--quiet", action="store_true", help="print failures only")
    parser.add_argument("--self-check", action="store_true")
    a = parser.parse_args(argv[1:])
    algorithms = {x.strip() for x in a.sign_algorithms.split(",") if x.strip()}
    if not algorithms or algorithms - set(SIGN_ALGORITHMS):
        parser.error(f"--sign-algorithms: one or both of {', '.join(SIGN_ALGORITHMS)}")
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
    if a.level == "intact":
        vectors = [v for v in vectors if getattr(v, "level", None) != "signed"]
    # A signer uses one of SIG-2's algorithms: a Sealer passes the sign vectors of those it claims (spec 09 §9.1).
    skipped = [v for v in vectors if v.kind == "sign" and v.algorithm is not None and v.algorithm not in algorithms]
    vectors = [v for v in vectors if v not in skipped]
    if skipped:
        print(f"skipped: {len(skipped)} sign vector(s) of an algorithm not in --sign-algorithms "
              f"({', '.join(sorted(algorithms))})")
    if a.self_check:
        if a.command:
            parser.error("--self-check mutates the in-process verifier; it does not take --command")
        use_index = index.is_file() and not a.no_index
        return 0 if self_check(vectors, refusals, corpus, index if use_index else None) else 1
    engine = External(a.command) if a.command else InProcess()
    print(f"implementation: {engine.name}")
    tally, _ = run_all(engine, vectors, refusals, quiet=a.quiet, skipped=skipped)
    return 1 if summary(tally) else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
