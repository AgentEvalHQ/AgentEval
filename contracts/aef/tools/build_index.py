#!/usr/bin/env python3
"""Writes conformance/index.json ([CONF-1]): every vector, its kind, the conformance classes it tests, the rules it
concerns, its path and the SHA-256 of each of its files. Run it last, after every generator.

The SHA-256 of index.json's bytes identifies the corpus version a conformance claim names ([CONF-4]).

Usage: python contracts/aef/tools/build_index.py
"""
import hashlib
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1] / "1" / "conformance"

CLASSES = {
    "document": ["Producer", "Reader"],
    "reader-only": ["Reader"],
    "encoding": ["Reader", "Run verifier"],
    "run": ["Run verifier"],
    "result-id": ["Producer"],
    "paths": ["Producer", "Run verifier"],
    "seal": ["Sealer", "Run verifier"],
    "chain": ["Overlay verifier"],
    "overlay-view": ["Overlay verifier"],
    "signature": ["Sealer", "Run verifier", "Overlay verifier", "Checkpoint verifier"],
    "checkpoint": ["Checkpoint verifier"],
    "lane": ["Checkpoint verifier"],
    "decision": ["Decision engine", "Checkpoint verifier"],
    "plan": ["Runner"],
    "matching": ["Runner"],
    "stream": ["Stream verifier"],
    "plan-conformance": ["Stream verifier"],
}


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def files_of(folder):
    return {p.relative_to(folder).as_posix(): sha(p) for p in sorted(folder.rglob("*"), key=lambda p: p.as_posix().encode()) if p.is_file()}


def entry(vid, kind, rules, path, files, classes=None):
    return {"id": vid, "kind": kind, "classes": classes or CLASSES[kind], "rules": rules, "path": path, "files": files}


def main():
    vectors = []
    for group in ("valid", "runs", "encoding", "seal-vectors", "chain-vectors", "overlay-views", "invalid", "reader-only",
                  "checkpoints", "lane-vectors", "signature-vectors"):
        folder = ROOT / group
        if not folder.exists():
            continue
        for d in sorted((p for p in folder.iterdir() if p.is_dir() and (p / "expected.json").exists()), key=lambda p: p.name.encode()):
            exp = json.loads((d / "expected.json").read_text(encoding="utf-8"))
            classes = None
            if group == "valid":
                classes = ["Producer", "Run verifier"]
            vectors.append(entry(f"{group}/{d.name}", exp["kind"], exp.get("rules", []), f"{group}/{d.name}", files_of(d), classes))
    keys = ROOT / "signature-vectors" / "keys"
    if keys.exists():
        vectors.append(entry("signature-vectors/keys", "fixture", ["SIG-3"], "signature-vectors/keys", files_of(keys),
                             ["Sealer", "Run verifier"]))
    for name, kind, rules in (("result-ids.json", "result-id", ["RES-4"]), ("paths.json", "paths", ["RUN-3"])):
        p = ROOT / name
        vectors.append(entry(name.removesuffix(".json"), kind, rules, name, {name: sha(p)}))
    for p in sorted((ROOT / "decision-vectors").glob("*.json"), key=lambda p: p.name.encode()):
        rules = json.loads(p.read_text(encoding="utf-8")).get("rules", [])  # each vector names its own rules
        vectors.append(entry(f"decision-vectors/{p.stem}", "decision", rules, f"decision-vectors/{p.name}", {p.name: sha(p)}))
    # A protocol vector names its rules in its expected.json; the folder's rules are for the shared files (and an
    # expected.json without rules).
    for sub, kind, rules in (("plans", "plan", ["PLAN-1"]), ("runners", "plan", ["PLAN-6"]), ("matching", "matching", ["PLAN-7"]),
                             ("streams", "stream", ["STRM-1", "STRM-2", "STRM-3"]),
                             ("plan-conformance", "plan-conformance", ["STRM-4"])):
        folder = ROOT / "protocol" / sub
        for d in sorted((p for p in folder.iterdir() if p.is_dir()), key=lambda p: p.name.encode()):
            own = json.loads((d / "expected.json").read_text(encoding="utf-8")).get("rules", rules)
            vectors.append(entry(f"protocol/{sub}/{d.name}", kind, own, f"protocol/{sub}/{d.name}", files_of(d)))
        shared = {p.name: sha(p) for p in sorted(folder.glob("*.json"), key=lambda p: p.name.encode())}
        if shared:  # files several vectors of the folder use (the stream vectors' plans): checked, never run
            vectors.append(entry(f"protocol/{sub}/shared", "fixture", rules, f"protocol/{sub}", shared, CLASSES[kind]))

    ids = [v["id"] for v in vectors]
    assert len(ids) == len(set(ids)), "duplicate vector id"
    indexed = {f"{v['path']}/{f}" if not v["path"].endswith(".json") else v["path"] for v in vectors for f in v["files"]}
    on_disk = {p.relative_to(ROOT).as_posix() for p in ROOT.rglob("*") if p.is_file() and p.name != "index.json"}
    unindexed = sorted(on_disk - indexed)
    assert not unindexed, f"files no vector lists: {unindexed[:10]}"
    doc = {"aef": "1.0", "description": "The AEF 1.0 conformance corpus (spec 09). The SHA-256 of this file's bytes "
                                        "identifies the corpus version.", "vectors": vectors}
    out = ROOT / "index.json"
    out.write_bytes((json.dumps(doc, indent=1, ensure_ascii=False) + "\n").encode("utf-8"))
    by_kind = {}
    for v in vectors:
        by_kind[v["kind"]] = by_kind.get(v["kind"], 0) + 1
    print(f"{len(vectors)} vectors, {len(indexed)} files; corpus sha256:{hashlib.sha256(out.read_bytes()).hexdigest()}")
    print(" ".join(f"{k}={n}" for k, n in sorted(by_kind.items())))


if __name__ == "__main__":
    main()
