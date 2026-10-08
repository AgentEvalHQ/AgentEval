#!/usr/bin/env python3
"""Generates the AEF field reference (contracts/aef/1/reference/) from the writer schemas: one page per schema, every
field with its type, whether it is required, its bounds, its allowed values and its description.

The reference is informative and generated: edit the schemas' descriptions, not the pages. `--check` fails when the
pages on disk are not what the schemas give (for a CI job or a pre-commit check).

Usage: python contracts/aef/tools/gen_reference.py [--check]
"""
import json
import sys
from pathlib import Path

AEF = Path(__file__).resolve().parents[1] / "1"
WRITER = AEF / "schemas" / "writer"
OUT = AEF / "reference"

ORDER = ["run", "result", "metrics", "summary", "evidence", "gate-decision", "seal", "overlay-event", "overlay-seal",
         "checkpoint", "decision", "run-plan", "runner", "runner-event", "common"]


def load_all():
    return {p.name.replace(".schema.json", ""): json.loads(p.read_text(encoding="utf-8")) for p in WRITER.glob("*.schema.json")}


SCHEMAS = load_all()


def resolve(ref, current):
    """('common', {...}) for a $ref such as 'common.schema.json#/$defs/id' or '#/$defs/laneRule'."""
    file, _, pointer = ref.partition("#")
    name = file.replace(".schema.json", "") if file else current
    node = SCHEMAS[name]
    for part in [p for p in pointer.split("/") if p]:
        node = node[part.replace("~1", "/").replace("~0", "~")]
    return name, node


def link(ref, current):
    file, _, pointer = ref.partition("#")
    name = file.replace(".schema.json", "") if file else current
    anchor = pointer.split("/")[-1] if pointer else ""
    target = f"{name}.md" if name != current else ""
    return f"[{anchor or name}]({target}#{anchor.lower()})" if anchor else f"[{name}]({name}.md)"


def type_of(node, current):
    if not isinstance(node, dict):
        return "any" if node is True else "nothing"
    if "$ref" in node:
        return link(node["$ref"], current)
    if "const" in node:
        return f"`{json.dumps(node['const'], ensure_ascii=False)}`"
    if "enum" in node:
        return "one of " + ", ".join(f"`{json.dumps(v, ensure_ascii=False)}`" for v in node["enum"])
    for key in ("oneOf", "anyOf"):
        if key in node:
            return " or ".join(type_of(b, current) for b in node[key])
    t = node.get("type")
    if t == "array":
        return f"array of {type_of(node.get('items', True), current)}"
    if isinstance(t, list):
        return " or ".join(t)
    return t or "object" if "properties" in node else (t or "any")


def bounds(node):
    out = []
    pairs = [("minLength", "≥ {} chars"), ("maxLength", "≤ {} chars"), ("minItems", "≥ {} items"), ("maxItems", "≤ {} items"),
             ("minimum", "≥ {}"), ("maximum", "≤ {}"), ("exclusiveMinimum", "> {}"), ("exclusiveMaximum", "< {}"),
             ("minProperties", "≥ {} members"), ("maxProperties", "≤ {} members")]
    for key, fmt in pairs:
        if isinstance(node, dict) and key in node:
            out.append(fmt.format(node[key]))
    if isinstance(node, dict) and node.get("uniqueItems"):
        out.append("unique")
    if isinstance(node, dict) and "pattern" in node:
        out.append(f"pattern `{node['pattern']}`")
    return "; ".join(out)


def cell(text):
    return str(text).replace("|", "\\|").replace("\n", " ")


def fields(node, current, prefix, rows, depth=0):
    """Rows (path, type, required, bounds, description) for an object schema, descending into inline objects."""
    if not isinstance(node, dict) or depth > 6:
        return
    required = set(node.get("required", []))
    for name, sub in (node.get("properties") or {}).items():
        path = f"{prefix}{name}"
        desc = sub.get("description", "") if isinstance(sub, dict) else ""
        if isinstance(sub, dict) and "$ref" in sub and not desc:
            desc = resolve(sub["$ref"], current)[1].get("description", "")
        rows.append((path, type_of(sub, current), "yes" if name in required else "", bounds(sub), desc))
        inner = sub
        if isinstance(sub, dict) and sub.get("type") == "array" and isinstance(sub.get("items"), dict):
            inner = sub["items"]
            path += "[]"
        if isinstance(inner, dict) and "properties" in inner:
            fields(inner, current, path + ".", rows, depth + 1)


def page(name):
    s = SCHEMAS[name]
    lines = [f"# {s.get('title', name)}", "",
             f"*Generated from [`schemas/writer/{name}.schema.json`](../schemas/writer/{name}.schema.json) by "
             "`tools/gen_reference.py`. Do not edit: edit the schema.*", ""]
    if s.get("description"):
        lines += [s["description"], ""]
    rows = []
    fields(s, name, "", rows)
    if rows:
        lines += ["| Field | Type | Required | Bounds | Description |", "|---|---|---|---|---|"]
        lines += [f"| `{cell(p)}` | {cell(t)} | {r} | {cell(b)} | {cell(d)} |" for p, t, r, b, d in rows]
        lines.append("")
    for key in ("oneOf", "anyOf"):
        if key in s and all(isinstance(b, dict) and "properties" in b for b in s[key]):
            lines += ["## Kinds", ""]
            for b in s[key]:
                kind = b.get("properties", {}).get("kind", {}).get("const", "?")
                lines += [f"### `{kind}`", ""]
                if b.get("description"):
                    lines += [b["description"], ""]
                rows = []
                fields(b, name, "", rows)
                lines += ["| Field | Type | Required | Bounds | Description |", "|---|---|---|---|---|"]
                lines += [f"| `{cell(p)}` | {cell(t)} | {r} | {cell(bd)} | {cell(d)} |" for p, t, r, bd, d in rows]
                lines.append("")
    for rule in s.get("allOf", []):
        if isinstance(rule, dict) and rule.get("description"):
            lines.append(f"- **Rule:** {rule['description']}")
    if s.get("allOf"):
        lines.append("")
    defs = s.get("$defs", {})
    if defs:
        lines += ["## Definitions", ""]
        for dname, d in defs.items():
            lines += [f"### {dname}", ""]
            if isinstance(d, dict) and d.get("description"):
                lines += [d["description"], ""]
            t, b = type_of(d, name), bounds(d)
            lines += [f"Type: {t}" + (f". Bounds: {b}" if b else ""), ""]
            rows = []
            fields(d, name, "", rows)
            if rows:
                lines += ["| Field | Type | Required | Bounds | Description |", "|---|---|---|---|---|"]
                lines += [f"| `{cell(p)}` | {cell(tt)} | {r} | {cell(bd)} | {cell(dd)} |" for p, tt, r, bd, dd in rows]
                lines.append("")
    return "\n".join(lines).rstrip("\n") + "\n"


def render():
    pages = {f"{n}.md": page(n) for n in ORDER if n in SCHEMAS}
    missing = sorted(set(SCHEMAS) - set(ORDER))
    assert not missing, f"schemas not in ORDER: {missing}"
    index = ["# Field reference", "", "*Generated from the writer schemas by `tools/gen_reference.py`. Informative: the "
             "schemas and the [specification](../spec/01-introduction.md) are the rule.*", "",
             "| Schema | What it describes |", "|---|---|"]
    for n in ORDER:
        desc = SCHEMAS[n].get("description", "").split(". ")[0].rstrip(".")
        index.append(f"| [{SCHEMAS[n].get('title', n)}]({n}.md) | {cell(desc)} |")
    pages["README.md"] = "\n".join(index) + "\n"
    return pages


def main(argv):
    pages = render()
    if "--check" in argv:
        # Line endings aside: a Windows checkout turns these pages' LF into CRLF (they are text, unlike the corpus).
        stale = [n for n, text in pages.items()
                 if not (OUT / n).exists() or (OUT / n).read_bytes().replace(b"\r\n", b"\n") != text.encode("utf-8")]
        print("reference is current" if not stale else f"stale: {stale}")
        return 1 if stale else 0
    OUT.mkdir(exist_ok=True)
    for old in OUT.glob("*.md"):
        if old.name not in pages:
            old.unlink()
    for n, text in pages.items():
        (OUT / n).write_bytes(text.encode("utf-8"))
    print(f"{len(pages)} pages written to {OUT}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
