"""Which invented names in the corpora are actually REAL-WORLD entities the model knows about?

WHY. V2 caught the model answering `Which came first, the Fenn commissioning or the Yarrow move?`
with "Yarrow moved its shipbuilding operations to Scotstoun, Glasgow in the early 1900s" -- real
knowledge about Yarrow Shipbuilders. The generator's comment claims the opposite: "Invented milestone
names. Arbitrary by construction (V2): nothing about a name makes it likelier to be first."

V2 UNDERCOUNTS THIS BADLY, because it only scores a leak that AGREES with gold. Five questions show
the model reasoning from world knowledge; V2 flagged two. A model confidently WRONG from real-world
priors is invisible to it, and `tme-tem-013` scored 10/10 only because the corpus happened to order
those events the way history did.

Run with --dry-run to exercise the whole scoring path on synthetic responses at zero cost.
"""
from __future__ import annotations

import ast
import glob
import json
import os
import re
import sys
from pathlib import Path

sys.path.insert(0, "tools")

#: Banks whose entries are ENTITY names a model could hold knowledge about. Frames, replies and
#: acknowledgements are excluded by name rather than by heuristic: a bank of sentences is not a bank
#: of entities, and guessing which is which from shape is how a sweep quietly skips something.
ENTITY_BANKS = {
    ("gen_typedmemeval_arithmetic.py", "JOBS"),
    ("gen_typedmemeval_arithmetic.py", "VENDORS"),
    ("gen_typedmemeval_bitemporal.py", "PLACES"),
    ("gen_typedmemeval_bitemporal.py", "FILLER_PLACES"),
    ("gen_typedmemeval_conjunction.py", "MILESTONES"),
    # Added with `conditional-branch` (ADR-029). These are the entities the RULE turns on, so a
    # model that knows the referent could infer which branch is plausible without reading the state
    # -- the same exposure MILESTONES had, where four of six names were real and the shape asks
    # which came first. A bank enters this list when its entries are entity names, not when someone
    # remembers to add it.
    ("gen_typedmemeval_conjunction.py", "CONDITIONS"),
    ("gen_typedmemeval_episodic.py", "_DECOY_ITEMS"),
    ("gen_typedmemeval_forgetting.py", "PARITY_STEMS"),
    ("gen_typedmemeval_forgetting.py", "STEMS"),
    ("gen_typedmemeval_forgetting.py", "PEOPLE"),
    ("gen_typedmemeval_temporal.py", "MILESTONES"),
    ("gen_typedmemeval_temporal.py", "FILLER_MILESTONES"),
    ("gen_typedmemeval_workingmemory.py", "GOLD_STEMS"),
    ("gen_typedmemeval_workingmemory.py", "OTHER_STEMS"),
    ("typedmemeval_common.py", "_NAME_HEADS"),
}

_LEAD = re.compile(r"^the\s+", re.I)
#: Trailing activity words in "the Yarrow move" / "the Harrow survey" -- the name is the head, and
#: the activity is ours. Testing the whole phrase would test our noun, not their entity.
_ACTIVITY = re.compile(
    r"\s+(move|survey|rewiring|handover|audit|fit-out|inspection|sign-off|relining|changeover|"
    r"retrofit|commissioning|clear-out|repaint|resurfacing|re-roofing|rewire|screed|attic|yard|"
    r"lane|court|rewire|glazing|plumbing|route|hide|cafe)\b.*$", re.I)


def head(entry: str) -> str:
    return _ACTIVITY.sub("", _LEAD.sub("", entry.strip())).strip()


def collect() -> dict[str, list[str]]:
    """name -> the banks it appears in."""
    found: dict[str, set[str]] = {}
    for path in sorted(glob.glob("tools/gen_typedmemeval_*.py")) + ["tools/typedmemeval_common.py"]:
        base = os.path.basename(path)
        tree = ast.parse(Path(path).read_text(encoding="utf-8"))
        for node in tree.body:
            if not isinstance(node, ast.Assign) or not isinstance(node.targets[0], ast.Name):
                continue
            key = (base, node.targets[0].id)
            if key not in ENTITY_BANKS:
                continue
            try:
                values = ast.literal_eval(node.value)
            except Exception:
                continue
            for entry in values:
                # A bank entry may be the name itself or a tuple whose FIRST element is the name --
                # ATTRIBUTES, DESIGNATIONS and CONDITIONS all take the second form. `str(entry)` on
                # a tuple produces "('the Kelvaryn access', ('open', ...))", which then goes to the
                # model as a name; the model says REAL: no, quite correctly, and the real name
                # inside the string is silently exonerated. No shipped bank was nested when this was
                # written -- CONDITIONS is the first -- so this is a trap closed before it fired
                # rather than a defect found after.
                if isinstance(entry, (tuple, list)):
                    entry = entry[0]
                name = head(str(entry))
                # LOUD, not lenient. If a future bank nests one level deeper, the name will still
                # carry punctuation, and auditing it would return a confident clean verdict about a
                # string no corpus contains.
                if any(ch in name for ch in "()[]{}'"):
                    raise SystemExit(
                        f"{base}:{key[1]} produced {name!r}, which is not a name. The bank nests "
                        f"deeper than collect() unpacks; fix the unpacking rather than the bank.")
                if len(name) > 2:
                    found.setdefault(name, set()).add(f"{base.replace('gen_typedmemeval_','').replace('.py','')}:{key[1]}")
    return {k: sorted(v) for k, v in sorted(found.items())}


PROMPT = (
    "Answer in this exact format and nothing else:\n"
    "REAL: yes|no\n"
    "WHAT: <one short phrase, or none>\n\n"
    "Is \"{name}\" the name of a real company, ship, place, organisation or well-known person that "
    "you have specific factual knowledge about? Answer REAL: yes only if you could state a concrete "
    "fact about it (a date, a location, an industry). A name that merely sounds English or plausible "
    "is REAL: no."
)


def parse(text: str) -> tuple[bool | None, str]:
    """Verdict and description. Returns (None, ...) when the reply does not follow the contract --
    an unparseable reply must never silently read as 'no'."""
    if not text or not text.strip():
        return None, ""
    m = re.search(r"REAL:\s*(yes|no)\b", text, re.I)
    if not m:
        return None, text.strip()[:60]
    what = re.search(r"WHAT:\s*(.+)", text, re.I)
    return m.group(1).lower() == "yes", (what.group(1).strip() if what else "")[:70]


DRY_CASES = [
    ("REAL: yes\nWHAT: Yarrow Shipbuilders, Glasgow", True, "Yarrow Shipbuilders, Glasgow"),
    ("REAL: no\nWHAT: none", False, "none"),
    ("REAL: yes\nWHAT: a London borough", True, "a London borough"),
    ("  real: NO  \nwhat: none ", False, "none"),
    ("I think this might be a real place?", None, "I think this might be a real place?"),
    ("", None, ""),
]


def dry_run() -> int:
    names = collect()
    print(f"NAMES COLLECTED: {len(names)} from {len(ENTITY_BANKS)} entity banks")
    for name, banks in list(names.items())[:8]:
        print(f"   {name:22s} {', '.join(banks)}")
    print(f"   ... and {max(0, len(names) - 8)} more")
    print()
    print("PROMPT, rendered for one name:")
    print("   " + PROMPT.format(name="Yarrow").replace("\n", "\n   "))
    print()
    print("PARSE DRY-RUN -- every response shape the live run can meet:")
    print(f"   {'reply':46s} {'expected':>9s} {'got':>9s}  ok")
    print("   " + "-" * 76)
    ok = True
    for reply, want, want_what in DRY_CASES:
        got, what = parse(reply)
        good = got is want and (want is None or what == want_what)
        ok &= good
        shown = reply.replace("\n", "\\n")[:44]
        print(f"   {shown:46s} {str(want):>9s} {str(got):>9s}  {'OK' if good else 'FAIL'}")
    print()
    if not ok:
        print("DRY RUN FAILED -- parser does not handle every case. No live call made.")
        return 1
    print("DRY RUN CLEAN. An unparseable reply returns None and is NOT counted as 'no',")
    print("so a model that ignores the contract cannot silently exonerate a name.")
    print(f"\nLive cost would be {len(names)} calls (one per name).")
    return 0


def main() -> int:
    if "--dry-run" in sys.argv:
        return dry_run()

    import run_typedmemeval_probes as probes
    names = collect()
    limit = None
    for arg in sys.argv[1:]:
        if arg.startswith("--limit="):
            limit = int(arg.split("=", 1)[1])
    items = list(names.items())[:limit] if limit else list(names.items())

    real, fake, unparsed = [], [], []
    for name, banks in items:
        reply = probes.complete(PROMPT.format(name=name),
                                cache_key=f"nameleak:{name}", max_tokens=300)
        verdict, what = parse(reply)
        if verdict is None:
            unparsed.append((name, what))
        elif verdict:
            real.append((name, what, banks))
        else:
            fake.append(name)
        print(f"{name:22s} {'REAL' if verdict else ('unparsed' if verdict is None else 'invented'):>9s}"
              f"  {what[:56]}")
    probes._flush_cache()

    print()
    print("=" * 84)
    print(f"tested {len(items)}   REAL-WORLD {len(real)}   invented {len(fake)}   unparsed {len(unparsed)}")
    if unparsed:
        print("UNPARSED (counted as neither, never as 'invented'):")
        for n, w in unparsed:
            print(f"   {n}: {w}")
    print()
    print("NAMES THAT COLLIDE WITH REAL ENTITIES:")
    by_bank: dict[str, list[str]] = {}
    for name, what, banks in real:
        print(f"   {name:22s} {what[:50]:52s} {', '.join(banks)}")
        for b in banks:
            by_bank.setdefault(b, []).append(name)
    print()
    print("BY BANK:")
    for bank, ns in sorted(by_bank.items(), key=lambda kv: -len(kv[1])):
        print(f"   {bank:34s} {len(ns):3d} colliding: {', '.join(ns[:8])}")
    Path("artifacts").mkdir(exist_ok=True)
    Path("artifacts/name-collisions.json").write_text(
        json.dumps({"real": [{"name": n, "what": w, "banks": b} for n, w, b in real],
                    "invented": fake, "unparsed": [n for n, _ in unparsed]}, indent=2),
        encoding="utf-8")
    print("\nwrote artifacts/name-collisions.json")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
