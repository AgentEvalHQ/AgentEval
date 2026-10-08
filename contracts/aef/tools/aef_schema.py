#!/usr/bin/env python3
"""A JSON Schema (draft 2020-12) validator for the AEF 1.0 schemas, standard library only, so the reference tools check
documents without a third-party package. It is a reference validator, not a general-purpose one.

It covers the keywords the AEF schemas use, with their 2020-12 meaning, and the rest of the same vocabularies that need
no annotation tracking:
  core         $schema (2020-12 only), $id, $ref, $defs, $comment
  applicators  allOf, anyOf, oneOf, not, if, then, else, properties, patternProperties, additionalProperties,
               propertyNames, dependentSchemas, prefixItems, items, contains
  assertions   type, enum, const, multipleOf, minimum, maximum, exclusiveMinimum, exclusiveMaximum, minLength,
               maxLength, pattern, minItems, maxItems, uniqueItems, minContains, maxContains, minProperties,
               maxProperties, required, dependentRequired
  annotations  title, description, default, examples, deprecated, readOnly, writeOnly, format (never asserted)
Any other keyword in a schema ($anchor, $dynamicRef, unevaluatedProperties, a misspelling) is a SchemaError naming it,
never silently ignored. Every $ref is resolved and every pattern compiled when the schemas load.

Values compare as JSON values: 2.0 is an integer, 1 equals 1.0, true is not 1, and a length counts code points.
Patterns are ECMA-262 (1/spec/02-encoding.md, ENC-14 and ENC-15) compiled to Python's re with ECMA's meaning: '$'
matches only at the end of the input, never before a final newline; '.' matches no line terminator; \\d \\w \\b are
ASCII; \\s is ECMA's white space. A construct the two engines read differently and that has no translation is a
SchemaError. A pattern searches: it is unanchored unless it anchors itself.

Usage:
  python aef_schema.py --self-test                    unit checks, then the corpus against the jsonschema package
  python aef_schema.py writer|reader SCHEMA FILE...   validate files (NDJSON line by line) against SCHEMA (run,
                                                      decision#/$defs/input, ...); exits 1 if one is invalid
"""
import functools
import json
import operator
import re
import sys
import warnings
from dataclasses import dataclass
from decimal import Decimal
from fractions import Fraction
from pathlib import Path
from urllib.parse import unquote, urldefrag, urljoin

DIALECT = "https://json-schema.org/draft/2020-12/schema"
AEF = Path(__file__).resolve().parents[1] / "1"
TYPES = ("null", "boolean", "object", "array", "number", "integer", "string")

ONE_SCHEMA = {"additionalProperties", "propertyNames", "items", "contains", "not", "if", "then", "else"}
SCHEMA_MAP = {"$defs", "properties", "patternProperties", "dependentSchemas"}
SCHEMA_LIST = {"allOf", "anyOf", "oneOf", "prefixItems"}
COUNTS = {"minLength", "maxLength", "minItems", "maxItems", "minContains", "maxContains", "minProperties", "maxProperties"}
BOUNDS = {"minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum"}
ASSERTIONS = COUNTS | BOUNDS | {"type", "enum", "const", "multipleOf", "pattern", "uniqueItems", "required", "dependentRequired"}
ANNOTATIONS = {"title", "description", "default", "examples", "deprecated", "readOnly", "writeOnly", "format"}
KEYWORDS = {"$schema", "$id", "$ref", "$comment"} | ONE_SCHEMA | SCHEMA_MAP | SCHEMA_LIST | ASSERTIONS | ANNOTATIONS


class SchemaError(Exception):
    """The schemas are wrong, or use something this validator does not implement."""


@dataclass(frozen=True)
class Error:
    """Why an instance is invalid: where (a JSON Pointer into the instance; '' is the whole document), the keyword that
    refused it, and a short message. For anyOf and oneOf, context holds the errors of each branch."""
    path: str
    keyword: str
    message: str
    context: tuple = ()

    def __str__(self):
        return f"{self.path or '(document)'}: {self.message} [{self.keyword}]"


# ---------------------------------------------------------------------------- patterns

_SPACE = "\\t\\n\\x0b\\x0c\\r \\xa0\\u1680\\u2000-\\u200a\\u2028\\u2029\\u202f\\u205f\\u3000\\ufeff"  # ECMA \s
_OUTSIDE = {"d": "[0-9]", "D": "[^0-9]", "w": "[A-Za-z0-9_]", "W": "[^A-Za-z0-9_]", "s": f"[{_SPACE}]",
            "S": f"[^{_SPACE}]", "b": "(?a:\\b)", "B": "(?a:\\B)"}
_INSIDE = {"d": "0-9", "w": "A-Za-z0-9_", "s": _SPACE, "b": "\\b"}
_KEPT = set("fnrtvx0123456789")  # the same in both engines (Python checks \x's two hex digits)
_QUANTIFIER = re.compile(r"\{[0-9]+(?:,[0-9]*)?\}")
_GROUP_NAME = re.compile(r"<([A-Za-z_][A-Za-z0-9_]*)>")
_LOW_SURROGATE = re.compile(r"\\u([dD][c-fC-F][0-9a-fA-F]{2})")


def translate_pattern(pattern):
    """The ECMA-262 pattern as a Python re pattern that matches the same strings (code point by code point, as ECMA's
    u mode does), or SchemaError."""
    out, i, n, in_class = [], 0, len(pattern), False

    def refuse(what):
        raise SchemaError(f"pattern {pattern!r}: {what}")

    while i < n:
        c = pattern[i]
        quantifier = False
        if c == "\\":
            if i + 1 == n:
                refuse("it ends in a lone backslash")
            e, i = pattern[i + 1], i + 2
            table = _INSIDE if in_class else _OUTSIDE
            if e in table:
                out.append(table[e])
            elif e == "u" and pattern.startswith("{", i):
                end = pattern.find("}", i)
                digits = pattern[i + 1:end] if end > i else ""
                if not re.fullmatch("[0-9A-Fa-f]{1,6}", digits) or int(digits, 16) > 0x10FFFF:
                    refuse("\\u{...} needs a code point in hex")
                out.append(f"\\U{int(digits, 16):08x}")
                i = end + 1
            elif e == "u":
                high = pattern[i:i + 4]
                if not re.fullmatch("[0-9A-Fa-f]{4}", high):
                    refuse("\\u needs four hex digits")
                i += 4
                low = _LOW_SURROGATE.match(pattern, i)
                if 0xD800 <= int(high, 16) <= 0xDBFF and low:  # a surrogate pair is one code point
                    out.append(f"\\U{0x10000 + (int(high, 16) - 0xD800) * 0x400 + int(low.group(1), 16) - 0xDC00:08x}")
                    i = low.end()
                else:
                    out.append("\\u" + high)
            elif e == "c" and i < n and pattern[i].isascii() and pattern[i].isalpha():
                out.append(f"\\x{ord(pattern[i]) % 32:02x}")
                i += 1
            elif e == "k" and not in_class and _GROUP_NAME.match(pattern, i):
                m = _GROUP_NAME.match(pattern, i)
                out.append(f"(?P={m.group(1)})")
                i = m.end()
            elif e in _KEPT or not (e.isascii() and e.isalnum()):
                out.append("\\" + e)
            else:
                refuse(f"\\{e} {'inside a class ' if in_class else ''}is not an escape both engines read the same way")
        elif in_class:
            if c == "]":
                in_class = False
                out.append(c)
            else:
                out.append("\\" + c if c in "[&~|" else c)  # literal in ECMA; Python reserves them for set operations
            i += 1
        elif c == "[":
            negated = pattern.startswith("^", i + 1)
            start = i + 1 + negated
            if pattern.startswith("]", start):  # ECMA: [] matches nothing, [^] matches any character
                out.append("[\\s\\S]" if negated else "(?!)")
                i = start + 1
            else:
                out.append("[^" if negated else "[")
                in_class, i = True, start
        elif c == "$":
            out.append("\\Z")  # the end of the input, never before a final newline
            i += 1
        elif c == ".":
            out.append("[^\\n\\r\\u2028\\u2029]")
            i += 1
        elif c == "(" and pattern.startswith("(?", i):
            rest = pattern[i + 2:i + 4]
            name = _GROUP_NAME.match(pattern, i + 2)
            if rest[:1] in (":", "=", "!") or rest in ("<=", "<!"):
                out.append("(?")
                i += 2
            elif name:
                out.append(f"(?P<{name.group(1)}>")
                i = name.end()
            else:
                refuse(f"'(?{rest[:1]}' is not ECMA-262")
        elif c == "{":
            m = _QUANTIFIER.match(pattern, i)
            out.append(m.group() if m else "\\{")  # not a quantifier: a literal brace, as ECMA reads it
            i, quantifier = (m.end(), True) if m else (i + 1, False)
        elif c in "}]":
            out.append("\\" + c)
            i += 1
        else:
            out.append(c)
            i += 1
            quantifier = c in "*+?"
        if quantifier and pattern.startswith("+", i):
            refuse("a possessive quantifier is not ECMA-262")
    return "".join(out)


@functools.lru_cache(maxsize=None)
def compile_pattern(pattern):
    """The compiled pattern (cached), with ECMA-262's meaning. Use .search: JSON Schema patterns are unanchored."""
    translated = translate_pattern(pattern)
    with warnings.catch_warnings():
        warnings.simplefilter("error")  # a FutureWarning about a set operation means the engines may disagree
        try:
            return re.compile(translated)
        except (re.error, Warning) as e:
            raise SchemaError(f"pattern {pattern!r} does not compile: {e}") from None


# ---------------------------------------------------------------------------- JSON values

def _is_number(x):
    return isinstance(x, (int, float)) and not isinstance(x, bool)


def _is_type(x, name):
    if name == "string":
        return isinstance(x, str)
    if name == "object":
        return isinstance(x, dict)
    if name == "array":
        return isinstance(x, list)
    if name == "boolean":
        return isinstance(x, bool)
    if name == "null":
        return x is None
    if name == "number":
        return _is_number(x)
    return _is_number(x) and (isinstance(x, int) or x.is_integer())  # integer: an integral number, 2.0 included


def _canon(x):
    """A hashable form in which two values are equal exactly when they are equal as JSON: 1 and 1.0 are, true and 1
    are not, and objects compare without regard to member order."""
    if isinstance(x, bool):
        return "b", x
    if isinstance(x, (int, float)):
        return "n", x  # Python compares int and float exactly, and hashes equal numbers alike
    if isinstance(x, str):
        return "s", x
    if x is None:
        return ("z",)
    if isinstance(x, list):
        return "a", tuple(_canon(v) for v in x)
    return "o", frozenset((k, _canon(v)) for k, v in x.items())


def _equal(a, b):
    return _canon(a) == _canon(b)


def _exact(x):
    """A number as an exact fraction of the decimal it was written as (repr gives back the JSON text's digits)."""
    return Fraction(x) if isinstance(x, int) else Fraction(Decimal(repr(x)))


def _show(value, limit=80):
    text = json.dumps(value)
    return text if len(text) <= limit else text[:limit - 3] + "..."


def _pointer(path):
    return "".join("/" + str(p).replace("~", "~0").replace("/", "~1") for p in path)


# ---------------------------------------------------------------------------- the schema set

class SchemaSet:
    """A folder's schemas: every $ref resolved and every pattern compiled when it is built.

    validate() and is_valid() take a schema by name: 'run' is run.schema.json, 'decision#/$defs/input' is that
    subschema of decision.schema.json; a file name or an absolute URI (with a JSON Pointer fragment) works too."""

    def __init__(self, documents, base_uri="file:///aef-schemas/"):
        """documents: {file name: parsed schema}. A document is known by its $id, if it has one, and by base_uri joined
        with its file name, so a relative $ref names a sibling file either way."""
        self.documents = dict(documents)
        self.keywords = set()  # every keyword the schemas use
        self._resources, self._base, self._refs, self._uris, self._targets = {}, {}, {}, {}, {}
        pending = []
        for name, schema in self.documents.items():
            retrieval = urljoin(base_uri, name)
            self._resources[retrieval] = schema
            self._uris[name] = self._walk(schema, retrieval, f"{name}#", pending)
        for node, where in pending:
            self._refs[id(node)] = self._resolve(urljoin(self._base[id(node)], node["$ref"]), f"{where} ($ref)")

    def _walk(self, node, base, where, pending):
        """Checks one schema and its subschemas; returns its base URI."""
        if isinstance(node, bool):
            return base
        if not isinstance(node, dict):
            raise SchemaError(f"{where}: a schema is an object or a boolean")
        if "$id" in node:
            if not isinstance(node["$id"], str):
                raise SchemaError(f"{where}: $id is a string")
            uri, fragment = urldefrag(urljoin(base, node["$id"]))
            if fragment:
                raise SchemaError(f"{where}: $id {node['$id']!r} has a fragment")
            if self._resources.setdefault(uri, node) is not node:
                raise SchemaError(f"{where}: $id {uri} names two schemas")
            base = uri
        self._base[id(node)] = base
        for key, value in node.items():
            at = f"{where}/{key.replace('~', '~0').replace('/', '~1')}"
            if key not in KEYWORDS:
                raise SchemaError(f"{at}: keyword {key!r} is not implemented by aef_schema.py")
            self.keywords.add(key)
            _check_value(key, value, at)
            if key in ONE_SCHEMA:
                self._walk(value, base, at, pending)
            elif key in SCHEMA_MAP:
                for k, sub in value.items():
                    self._walk(sub, base, f"{at}/{k.replace('~', '~0').replace('/', '~1')}", pending)
            elif key in SCHEMA_LIST:
                for k, sub in enumerate(value):
                    self._walk(sub, base, f"{at}/{k}", pending)
            elif key == "$ref":
                pending.append((node, where))
        return base

    def _resolve(self, uri, where):
        document, fragment = urldefrag(uri)
        node = self._resources.get(document)
        if node is None:
            raise SchemaError(f"{where}: {uri} names no schema of this set")
        pointer = unquote(fragment)
        if pointer and not pointer.startswith("/"):
            raise SchemaError(f"{where}: {uri}: a plain-name fragment ($anchor) is not implemented")
        for token in pointer.split("/")[1:]:
            token = token.replace("~1", "/").replace("~0", "~")
            if isinstance(node, dict) and token in node:
                node = node[token]
            elif isinstance(node, list) and re.fullmatch("0|[1-9][0-9]*", token) and int(token) < len(node):
                node = node[int(token)]
            else:
                raise SchemaError(f"{where}: {uri} points at nothing")
        if not isinstance(node, bool) and id(node) not in self._base:
            raise SchemaError(f"{where}: {uri} points at something that is not a schema")
        return node

    def uri(self, schema):
        """The absolute URI (with its fragment, if any) of a schema given by name, file name or URI."""
        document, sep, fragment = schema.partition("#")
        if ":" not in document:
            name = document if document.endswith(".json") else document + ".schema.json"
            if name not in self._uris:
                raise SchemaError(f"no schema named {document!r}; known: {', '.join(sorted(self._uris))}")
            document = self._uris[name]
        return document + sep + fragment

    def _target(self, schema):
        if schema not in self._targets:
            self._targets[schema] = self._resolve(self.uri(schema), schema)
        return self._targets[schema]

    def validate(self, schema, instance):
        """Every error, in schema order; empty when the instance is valid."""
        return list(self._errors(self._target(schema), instance, ()))

    def is_valid(self, schema, instance):
        return self._valid(self._target(schema), instance)

    def _valid(self, schema, instance):
        return next(self._errors(schema, instance, ()), None) is None

    def _errors(self, schema, instance, path):
        if schema is True:
            return
        if schema is False:
            yield Error(_pointer(path), "false", "no value is allowed here")
            return
        for key, value in schema.items():
            check = _CHECKS.get(key)
            if check is not None:
                yield from check(self, value, instance, schema, path)

    def _branches(self, subs, instance, path):
        return tuple(Error(e.path, e.keyword, f"branch {k}: {e.message}", e.context)
                     for k, sub in enumerate(subs) for e in self._errors(sub, instance, path))


def _check_value(key, value, at):
    """Refuses a keyword value 2020-12 does not allow, where getting it wrong would change what validates."""
    def bad(what):
        raise SchemaError(f"{at}: {what}")

    if key == "items" and isinstance(value, list):
        bad("items is one schema in 2020-12 (an array of schemas is prefixItems)")
    elif key in SCHEMA_MAP and not isinstance(value, dict):
        bad(f"{key} is an object of schemas")
    elif key in SCHEMA_LIST and not (isinstance(value, list) and (value or key == "prefixItems")):
        bad(f"{key} is a non-empty array of schemas")
    elif key == "$schema" and value != DIALECT:
        bad(f"$schema is {value!r}; this validator implements {DIALECT} only")
    elif key == "$ref" and not isinstance(value, str):
        bad("$ref is a string")
    elif key == "type":
        names = [value] if isinstance(value, str) else value
        if not (isinstance(names, list) and names and all(t in TYPES for t in names) and len(set(names)) == len(names)):
            bad(f"type {value!r} is not a JSON type or a list of them")
    elif key in ("enum", "examples") and not isinstance(value, list):
        bad(f"{key} is an array")
    elif key in BOUNDS and not _is_number(value):
        bad(f"{key} is a number")
    elif key == "multipleOf" and not (_is_number(value) and value > 0):
        bad("multipleOf is a number greater than 0")
    elif key in COUNTS and not (_is_type(value, "integer") and value >= 0):
        bad(f"{key} is a non-negative integer")
    elif key == "pattern":
        if not isinstance(value, str):
            bad("pattern is a string")
        compile_pattern(value)
    elif key == "patternProperties":
        for p in value:
            compile_pattern(p)
    elif key == "required" and not _names(value):
        bad("required is an array of distinct strings")
    elif key == "dependentRequired" and not (isinstance(value, dict) and all(_names(v) for v in value.values())):
        bad("dependentRequired maps names to arrays of distinct strings")
    elif key == "uniqueItems" and not isinstance(value, bool):
        bad("uniqueItems is a boolean")


def _names(value):
    return isinstance(value, list) and all(isinstance(v, str) for v in value) and len(set(value)) == len(value)


# ---------------------------------------------------------------------------- keywords

def _type(vs, types, x, schema, path):
    names = [types] if isinstance(types, str) else types
    if not any(_is_type(x, t) for t in names):
        yield Error(_pointer(path), "type", f"{_show(x)} is not of type {' or '.join(names)}")


def _enum(vs, values, x, schema, path):
    if not any(_equal(x, v) for v in values):
        yield Error(_pointer(path), "enum", f"{_show(x)} is not one of {_show(values)}")


def _const(vs, value, x, schema, path):
    if not _equal(x, value):
        yield Error(_pointer(path), "const", f"{_show(x)} is not {_show(value)}")


def _multiple_of(vs, value, x, schema, path):
    if _is_number(x) and (_exact(x) / _exact(value)).denominator != 1:
        yield Error(_pointer(path), "multipleOf", f"{_show(x)} is not a multiple of {_show(value)}")


def _bound(keyword, holds, words):
    def check(vs, value, x, schema, path):
        if _is_number(x) and not holds(x, value):
            yield Error(_pointer(path), keyword, f"{_show(x)} is {words} {_show(value)}")
    return check


def _count(keyword, kind, noun, low):
    def check(vs, value, x, schema, path):
        if _is_type(x, kind):
            size = len(x)
            if size < value if low else size > value:
                yield Error(_pointer(path), keyword,
                            f"{_show(x)} has {size} {noun}, {'fewer' if low else 'more'} than {int(value)}")
    return check


def _pattern(vs, value, x, schema, path):
    if isinstance(x, str) and not compile_pattern(value).search(x):
        yield Error(_pointer(path), "pattern", f"{_show(x)} does not match {value}")


def _unique_items(vs, value, x, schema, path):
    if value and isinstance(x, list):
        seen = {}
        for k, item in enumerate(x):
            first = seen.setdefault(_canon(item), k)
            if first != k:
                yield Error(_pointer(path), "uniqueItems", f"items {first} and {k} are equal")
                return


def _member(vs, keyword, sub, value, path, refusal):
    """A member or item that keyword applies sub to: a false sub refuses it in the keyword's name."""
    if sub is False:
        yield Error(_pointer(path), keyword, f"is not allowed: {refusal}")
    else:
        yield from vs._errors(sub, value, path)


def _prefix_items(vs, subs, x, schema, path):
    if isinstance(x, list):
        for k, (sub, item) in enumerate(zip(subs, x)):
            yield from _member(vs, "prefixItems", sub, item, path + (k,), "the schema forbids this item")


def _items(vs, sub, x, schema, path):
    if isinstance(x, list):
        for k in range(len(schema.get("prefixItems", ())), len(x)):
            yield from _member(vs, "items", sub, x[k], path + (k,), "the array has no place for it")


def _contains(vs, sub, x, schema, path):
    if isinstance(x, list):
        matching = sum(1 for item in x if vs._valid(sub, item))
        low, high = schema.get("minContains", 1), schema.get("maxContains")
        if matching < low:
            yield Error(_pointer(path), "minContains" if "minContains" in schema else "contains",
                        f"{matching} items match contains, fewer than {int(low)}")
        if high is not None and matching > high:
            yield Error(_pointer(path), "maxContains", f"{matching} items match contains, more than {int(high)}")


def _properties(vs, subs, x, schema, path):
    if isinstance(x, dict):
        for name, sub in subs.items():
            if name in x:
                yield from _member(vs, "properties", sub, x[name], path + (name,), "the schema forbids this member")


def _pattern_properties(vs, subs, x, schema, path):
    if isinstance(x, dict):
        for pattern, sub in subs.items():
            for name in x:
                if compile_pattern(pattern).search(name):
                    yield from _member(vs, "patternProperties", sub, x[name], path + (name,), "the schema forbids this member")


def _additional_properties(vs, sub, x, schema, path):
    if isinstance(x, dict):
        declared = schema.get("properties", {})
        patterns = [compile_pattern(p) for p in schema.get("patternProperties", ())]
        for name in x:
            if name not in declared and not any(p.search(name) for p in patterns):
                yield from _member(vs, "additionalProperties", sub, x[name], path + (name,), "no schema declares this member")


def _property_names(vs, sub, x, schema, path):
    if isinstance(x, dict):
        for name in x:
            first = next(vs._errors(sub, name, ()), None)
            if first is not None:
                yield Error(_pointer(path), "propertyNames", f"the member name {_show(name)} is not allowed: {first.message}")


def _required(vs, names, x, schema, path):
    if isinstance(x, dict):
        for name in names:
            if name not in x:
                yield Error(_pointer(path), "required", f"{_show(name)} is required")


def _dependent_required(vs, deps, x, schema, path):
    if isinstance(x, dict):
        for present, names in deps.items():
            if present in x:
                for name in names:
                    if name not in x:
                        yield Error(_pointer(path), "dependentRequired", f"{_show(name)} is required when {_show(present)} is present")


def _dependent_schemas(vs, deps, x, schema, path):
    if isinstance(x, dict):
        for present, sub in deps.items():
            if present in x:
                yield from vs._errors(sub, x, path)


def _all_of(vs, subs, x, schema, path):
    for sub in subs:
        yield from vs._errors(sub, x, path)


def _any_of(vs, subs, x, schema, path):
    if not any(vs._valid(sub, x) for sub in subs):
        yield Error(_pointer(path), "anyOf", f"matches none of the {len(subs)} anyOf branches", vs._branches(subs, x, path))


def _one_of(vs, subs, x, schema, path):
    matched = [k for k, sub in enumerate(subs) if vs._valid(sub, x)]
    if not matched:
        yield Error(_pointer(path), "oneOf", f"matches none of the {len(subs)} oneOf branches", vs._branches(subs, x, path))
    elif len(matched) > 1:
        yield Error(_pointer(path), "oneOf", f"matches oneOf branches {', '.join(map(str, matched))}; exactly one may match")


def _not(vs, sub, x, schema, path):
    if vs._valid(sub, x):
        yield Error(_pointer(path), "not", "matches the schema under not, which it must not")


def _if(vs, condition, x, schema, path):
    branch = "then" if vs._valid(condition, x) else "else"
    if branch in schema:
        yield from vs._errors(schema[branch], x, path)


def _ref(vs, ref, x, schema, path):
    yield from vs._errors(vs._refs[id(schema)], x, path)


_CHECKS = {
    "$ref": _ref, "type": _type, "enum": _enum, "const": _const, "multipleOf": _multiple_of,
    "minimum": _bound("minimum", operator.ge, "less than the minimum"),
    "maximum": _bound("maximum", operator.le, "greater than the maximum"),
    "exclusiveMinimum": _bound("exclusiveMinimum", operator.gt, "not greater than"),
    "exclusiveMaximum": _bound("exclusiveMaximum", operator.lt, "not less than"),
    "minLength": _count("minLength", "string", "characters", True), "maxLength": _count("maxLength", "string", "characters", False),
    "minItems": _count("minItems", "array", "items", True), "maxItems": _count("maxItems", "array", "items", False),
    "minProperties": _count("minProperties", "object", "members", True),
    "maxProperties": _count("maxProperties", "object", "members", False),
    "pattern": _pattern, "uniqueItems": _unique_items, "prefixItems": _prefix_items, "items": _items, "contains": _contains,
    "properties": _properties, "patternProperties": _pattern_properties, "additionalProperties": _additional_properties,
    "propertyNames": _property_names, "required": _required, "dependentRequired": _dependent_required,
    "dependentSchemas": _dependent_schemas, "allOf": _all_of, "anyOf": _any_of, "oneOf": _one_of, "not": _not, "if": _if,
}
# $schema, $id, $comment, $defs, then, else (read by if), minContains and maxContains (read by contains) and the
# annotations have no check of their own.


# ---------------------------------------------------------------------------- loading

_SURROGATE = re.compile("[\ud800-\udfff]")


def load_json(text):
    """Strict JSON (1/spec/02-encoding.md, ENC-1 to ENC-3): a byte-order mark, a member named twice, an unpaired
    surrogate or NaN/Infinity is a ValueError."""
    def members(pairs):
        obj = {}
        for k, v in pairs:
            if k in obj:
                raise ValueError(f"the member {k!r} appears twice")
            obj[k] = v
        return obj

    def constant(name):
        raise ValueError(f"{name} is not a JSON number")

    def paired(x):
        if isinstance(x, str) and _SURROGATE.search(x):
            raise ValueError(f"{_show(x)} holds an unpaired surrogate")
        for v in x if isinstance(x, list) else [*x, *x.values()] if isinstance(x, dict) else ():
            paired(v)

    value = json.loads(text, object_pairs_hook=members, parse_constant=constant)  # json.loads refuses a BOM
    paired(value)
    return value


def load_schemas(folder):
    """The SchemaSet of every *.schema.json in folder."""
    folder = Path(folder).resolve()
    files = sorted(folder.glob("*.schema.json"))
    if not files:
        raise SchemaError(f"no *.schema.json in {folder}")
    return SchemaSet({f.name: load_json(f.read_bytes().decode("utf-8")) for f in files}, folder.as_uri() + "/")


def read_documents(path):
    """(label, document) for a JSON file, or for each line of an NDJSON file (split on LF alone, ENC-6)."""
    text = Path(path).read_bytes().decode("utf-8")
    if not str(path).endswith(".ndjson"):
        return [(str(path), load_json(text))]
    lines = text.split("\n")
    if lines[-1] == "":
        lines.pop()
    return [(f"{path}:{k}", load_json(line)) for k, line in enumerate(lines, start=1)]


# ---------------------------------------------------------------------------- self-test

# (schema, instance, valid). Each is checked here and by jsonschema with the same pattern semantics.
UNIT_CASES = [(schema, instance, valid) for schema, cases in [
    # type: an integer is an integral number; a boolean is neither an integer nor a number
    ({"type": "integer"}, [(2, True), (2.0, True), (2.5, False), (True, False)]),
    ({"type": "number"}, [(1.5, True), (False, False)]), ({"type": ["string", "null"]}, [(None, True), (0, False)]),
    ({"type": "object"}, [([], False)]), ({"type": "array"}, [({}, False)]), ({"type": "boolean"}, [(0, False)]),
    ({"type": "null"}, [(False, False)]),
    # const, enum and uniqueItems compare JSON values: 1 equals 1.0, true is not 1
    ({"const": 1}, [(1.0, True), (True, False)]), ({"const": 1.0}, [(1, True)]), ({"const": True}, [(1, False)]),
    ({"enum": [1.0, "a"]}, [(1, True)]), ({"enum": [True]}, [(1, False)]), ({"enum": [0]}, [(False, False)]),
    ({"enum": ["1"]}, [(1, False)]), ({"const": [1, {"a": 2}]}, [([1.0, {"a": 2.0}], True)]),
    ({"const": {"a": 1}}, [({"a": 1, "b": 2}, False)]), ({"const": None}, [(0, False)]), ({"const": [0]}, [([False], False)]),
    ({"uniqueItems": True}, [([1, 1.0], False), ([1, True], True), ([[0], [False]], True),
                             ([{"a": 1, "b": 2}, {"b": 2, "a": 1.0}], False)]),
    ({"uniqueItems": False}, [([1, 1], True)]),
    # lengths count code points, not UTF-16 units
    ({"maxLength": 1}, [("\U0001F44D", True)]), ({"minLength": 2}, [("\U0001F44D", False)]),
    ({"maxLength": 2}, [("\U0001F44D" * 3, False), (5, True)]), ({"minLength": 3, "maxLength": 3}, [("\u00e9\U0001F44Da", True)]),
    # patterns: ECMA-262, searched, '$' only at the end of the input
    ({"pattern": "^[a-z]+$"}, [("abc", True), ("abc\n", False), (5, True)]), ({"pattern": "b"}, [("abc", True)]),
    ({"pattern": "^b"}, [("abc", False)]), ({"pattern": "^(a$|b)"}, [("a\n", False)]), ({"pattern": "^a\\$$"}, [("a$", True)]),
    ({"pattern": "^[$]$"}, [("$", True)]), ({"pattern": "^.$"}, [("\U0001F44D", True), ("\r", False), ("\u2028", False)]),
    ({"pattern": "^\\d$"}, [("\u0663", False)]), ({"pattern": "^\\w$"}, [("\u00e9", False)]),
    ({"pattern": "^\\s$"}, [("\u00a0", True), ("\ufeff", True), ("\x1c", False)]), ({"pattern": "^[\\d.]+$"}, [("1.5", True)]),
    ({"pattern": "^[^\\u0000-\\u001f]+$"}, [("a\u001fb", False)]), ({"pattern": "^\\ud83d\\udc4d$"}, [("\U0001F44D", True)]),
    ({"pattern": "a{2}"}, [("aa", True)]), ({"pattern": "^a{,2}$"}, [("a{,2}", True)]), ({"pattern": "\\bx"}, [("\u00e9x", True)]),
    ({"pattern": "^(?<y>a)\\k<y>$"}, [("aa", True)]), ({"pattern": "^[]"}, [("a", False)]), ({"pattern": "^[^]$"}, [("\n", True)]),
    # numbers: exact comparison; multipleOf on the decimal written
    ({"minimum": 1}, [(1, True), (0.999, False)]), ({"exclusiveMinimum": 1}, [(1, False)]),
    ({"exclusiveMaximum": 1}, [(0.999, True)]), ({"maximum": 1}, [(1.0, True), (True, True)]), ({"minimum": 0}, [("-1", True)]),
    ({"multipleOf": 2}, [(4.0, True), (5, False)]), ({"multipleOf": 0.5}, [(1.5, True)]),
    ({"multipleOf": 0.0001}, [(0.0075, True), (0.00751, False)]), ({"type": "integer", "multipleOf": 0.123456789}, [(1e308, False)]),
    # objects
    ({"required": ["a"]}, [({}, False), ([], True)]), ({"properties": {"a": {"type": "string"}}}, [({"a": 1}, False)]),
    ({"properties": {"a": {}}, "patternProperties": {"^x-": {}}, "additionalProperties": False},
     [({"a": 1, "x-y": 2}, True), ({"b": 1}, False)]),
    ({"patternProperties": {"^x-": {"type": "integer"}}}, [({"x-a": "s"}, False), ({"y-a": "s"}, True)]),
    ({"additionalProperties": {"type": "integer"}}, [({"a": 1, "b": 2.0}, True), ({"a": "x"}, False)]),
    ({"propertyNames": {"pattern": "^[a-z]+$"}}, [({"ab": 1}, True), ({"Ab": 1}, False), ({"ab\n": 1}, False), ({}, True)]),
    ({"minProperties": 1}, [({}, False)]), ({"maxProperties": 1}, [({"a": 1, "b": 2}, False)]),
    ({"dependentRequired": {"a": ["b"]}}, [({"a": 1}, False), ({"b": 1}, True)]),
    ({"dependentSchemas": {"a": {"required": ["b"]}}}, [({"a": 1}, False), ({}, True)]),
    # arrays
    ({"items": {"type": "integer"}}, [([1, 2.0], True), ([1, "x"], False)]),
    ({"prefixItems": [{"type": "string"}], "items": False}, [(["a"], True), (["a", 1], False), ([1], False), ([], True)]),
    ({"prefixItems": [{"type": "string"}], "items": {"type": "integer"}}, [(["a", 1, 2], True), (["a", "b"], False)]),
    ({"contains": {"type": "integer"}}, [(["a"], False), (["a", 1], True), ([], False)]),
    ({"contains": {"type": "integer"}, "minContains": 2}, [([1, "a"], False), ([1, 2], True)]),
    ({"contains": {"type": "integer"}, "maxContains": 1}, [([1, 2], False), ([1], True)]),
    ({"contains": {"type": "integer"}, "minContains": 0}, [([], True)]), ({"minContains": 2}, [([], True)]),
    ({"minItems": 1}, [([], False)]), ({"maxItems": 1}, [([1, 2], False)]),
    # applicators
    ({"allOf": [{"type": "integer"}, {"minimum": 2}]}, [(1, False), (2, True)]),
    ({"anyOf": [{"type": "integer"}, {"type": "null"}]}, [(None, True), ("x", False)]),
    ({"oneOf": [{"type": "integer"}, {"minimum": 0}]}, [(5, False), (-1, True), (1.5, True), (-1.5, False)]),
    ({"not": {"type": "integer"}}, [(1, False), ("x", True)]),
    ({"if": {"properties": {"a": {"const": 1}}, "required": ["a"]}, "then": {"required": ["b"]}, "else": {"required": ["c"]}},
     [({"a": 1, "b": 0}, True), ({"a": 1}, False), ({"a": 2, "c": 0}, True), ({"a": 2}, False), ({}, False)]),
    ({"then": {"required": ["b"]}, "else": {"required": ["b"]}}, [({}, True)]),
    ({"if": {"type": "integer"}, "else": {"type": "null"}}, [("x", False), (1, True)]),
    # boolean schemas, wherever they stand
    (True, [(1, True)]), (False, [(1, False)]), ({"properties": {"a": False}}, [({"a": 1}, False), ({}, True)]),
    ({"prefixItems": [False]}, [([1], False)]), ({"patternProperties": {"^a": False}}, [({"ab": 1}, False)]),
    ({"$defs": {"f": False}, "properties": {"a": {"$ref": "#/$defs/f"}}}, [({"a": 1}, False)]),
    ({"properties": {"a": {"allOf": [False]}}}, [({"a": 1}, False)]), ({"propertyNames": False}, [({"a": 1}, False), ({}, True)]),
    ({"dependentSchemas": {"a": False}}, [({"a": 1}, False)]),
    # annotations never assert
    ({"format": "date-time"}, [("not a time", True)]), ({"format": "email"}, [("x", True)]),
    ({"title": "t", "description": "d", "default": 1, "examples": [2], "deprecated": True, "readOnly": True, "writeOnly": True,
      "$comment": "c"}, [("x", True)]),
    # $ref inside a document, with escaped pointers and 2020-12 siblings
    ({"$defs": {"n": {"type": "integer"}}, "properties": {"a": {"$ref": "#/$defs/n"}}}, [({"a": "x"}, False), ({"a": 1}, True)]),
    ({"$defs": {"a/b": {"type": "integer"}, "c~d": {"type": "string"}, "e%f": {"type": "null"}},
      "properties": {"x": {"$ref": "#/$defs/a~1b"}, "y": {"$ref": "#/$defs/c~0d"}, "z": {"$ref": "#/$defs/e%25f"}}},
     [({"x": 1, "y": "s", "z": None}, True), ({"x": "s"}, False), ({"y": 1}, False), ({"z": 0}, False)]),
    ({"$defs": {"s": {"type": "string"}}, "$ref": "#/$defs/s", "maxLength": 1}, [("ab", False), (1, False), ("a", True)]),
] for instance, valid in cases]

# Schemas this validator must refuse, and the word the refusal must name.
REFUSED = [
    ({"unevaluatedProperties": False}, "unevaluatedProperties"), ({"properties": {"a": {"foo": 1}}}, "'foo'"),
    ({"$anchor": "x"}, "$anchor"), ({"$dynamicRef": "#x"}, "$dynamicRef"), ({"definitions": {}}, "definitions"),
    ({"items": [{}]}, "prefixItems"), ({"type": "int"}, "type"), ({"$schema": "http://json-schema.org/draft-07/schema#"}, "$schema"),
    ({"$ref": "#/$defs/missing"}, "points at nothing"), ({"$ref": "other.schema.json"}, "names no schema"),
    ({"$ref": "#x"}, "$anchor"), ({"pattern": "\\Aa"}, "\\A"), ({"pattern": "a\\Z"}, "\\Z"), ({"pattern": "(?i)a"}, "(?i"),
    ({"pattern": "a++"}, "possessive"), ({"pattern": "\\p{L}"}, "\\p"), ({"pattern": "[\\D]"}, "\\D"), ({"pattern": "("}, "compile"),
    ({"minLength": -1}, "minLength"), ({"required": ["a", "a"]}, "required"),
]


_REFUSED_MEMBER = {"properties", "patternProperties", "additionalProperties", "prefixItems", "items"}


def _locations(errors):
    """Where the errors are, as jsonschema reports them: a member or item a false subschema refuses at its object or
    array (jsonschema drops the last step of the path when it enters a false subschema)."""
    return {e.path.rsplit("/", 1)[0] if e.keyword in _REFUSED_MEMBER else e.path for e in errors}


def _their_locations(errors):
    return {_pointer(e.absolute_path) for e in errors}


def _unit_checks(ecma):
    """(checks run, failure messages). ecma(schema) is a jsonschema validator with this module's pattern semantics."""
    failures = []
    for schema, instance, expected in UNIT_CASES:
        mine = SchemaSet({"case.schema.json": schema}).validate("case", instance)
        theirs = list(ecma(schema).iter_errors(instance))
        if (not mine) != expected or (not theirs) != expected or _locations(mine) != _their_locations(theirs):
            failures.append(f"{_show(schema)} on {_show(instance)}: expected {expected}, aef_schema {not mine} at "
                            f"{sorted(_locations(mine))}, jsonschema {not theirs} at {sorted(_their_locations(theirs))}")
    for schema, word in REFUSED:
        try:
            SchemaSet({"case.schema.json": schema})
            failures.append(f"{_show(schema)} was accepted; it must be refused")
        except SchemaError as e:
            if word not in str(e):
                failures.append(f"{_show(schema)} was refused without naming {word!r}: {e}")
    count = len(UNIT_CASES) + len(REFUSED)

    def check(what, ok):
        nonlocal count
        count += 1
        if not ok:
            failures.append(what)

    # $ref across files: relative to the referring file's $id, absolute, to a whole file, and between files with no $id.
    common = {"$schema": DIALECT, "$id": "https://example.test/s/common.schema.json",
              "$defs": {"id": {"type": "string", "pattern": "^[a-z]+$"}, "n": {"$ref": "#/$defs/id"}}}
    doc = {"$schema": DIALECT, "$id": "https://example.test/s/doc.schema.json", "type": "object",
           "properties": {"a": {"$ref": "common.schema.json#/$defs/id"}, "b": {"$ref": "https://example.test/s/common.schema.json#/$defs/n"},
                          "c": {"$ref": "#/$defs/whole"}},
           "$defs": {"whole": {"$ref": "common.schema.json"}}}
    s = SchemaSet({"common.schema.json": common, "doc.schema.json": doc})
    check("cross-file $ref: a valid document", s.is_valid("doc", {"a": "x", "b": "y", "c": 1}))
    check("cross-file $ref: relative", not s.is_valid("doc", {"a": "X"}))
    check("cross-file $ref: absolute, then a local $ref in the other file", not s.is_valid("doc", {"b": "x\n"}))
    check("a name with a fragment", s.is_valid("common#/$defs/id", "abc") and not s.is_valid("common#/$defs/id", "ABC"))
    check("a file name and an absolute URI", s.is_valid("doc.schema.json", {}) and not s.is_valid("https://example.test/s/common.schema.json#/$defs/n", 1))
    plain = SchemaSet({"a.schema.json": {"$ref": "b.schema.json#/$defs/x"}, "b.schema.json": {"$defs": {"x": {"type": "null"}}}}, "file:///s/")
    check("cross-file $ref between files without $id", plain.is_valid("a", None) and not plain.is_valid("a", 0))

    # Errors: the instance path is a JSON Pointer, with the keyword that refused.
    s = SchemaSet({"e.schema.json": {"type": "object", "additionalProperties": False, "required": ["r"],
                                      "properties": {"a/b": {"type": "array", "items": {"type": "integer"}}, "r": {}}}})
    errors = [(e.path, e.keyword) for e in s.validate("e", {"a/b": [1, "x"], "extra": 0})]
    check(f"error paths and keywords: {errors}", errors == [("/extra", "additionalProperties"), ("", "required"), ("/a~1b/1", "type")])
    errors = SchemaSet({"o.schema.json": {"oneOf": [{"type": "integer"}, {"minimum": 0}]}}).validate("o", 5)
    check(f"oneOf with two matches: {[str(e) for e in errors]}", len(errors) == 1 and errors[0].keyword == "oneOf" and "0, 1" in errors[0].message)
    check("is_valid agrees with validate", SchemaSet({"t.schema.json": {"type": "string"}}).validate("t", "x") == [])

    # The loader is strict JSON; a surrogate pair is one code point.
    for text in ('{"a": 1, "a": 2}', '{"a": "\\ud800"}', '["x\\udc00"]', '{"a": NaN}', '\ufeff{}'):
        try:
            load_json(text)
            check(f"load_json accepted {text!r}", False)
        except ValueError:
            check("", True)
    check("load_json reads a surrogate pair as one code point", load_json('"\\ud83d\\udc4d"') == "\U0001F44D")
    return count, failures


_RUN_FILES = {"run.json": "run", "metrics.json": "metrics", "summary.json": "summary", "seal.json": "seal",
              "results.ndjson": "result", "evidence.ndjson": "evidence", "gates.ndjson": "gate-decision",
              "overlays/events.ndjson": "overlay-event"}
_OVERLAY_SEAL = re.compile(r"overlays/seal-[0-9]{4}\.json")


def _corpus(root):
    """(documents, unmapped files, unreadable files). A document is (label, schema, instance, {set: expected verdict
    or None}). Verdicts come from expected.json, the valid/ folder and the decision vectors; None where the corpus
    states none."""
    docs, mapped, unreadable = [], set(), []
    valid = {"writer": "valid", "reader": "valid"}
    unknown = {"writer": None, "reader": None}

    def add(path, schema, expect, pick=None):
        mapped.add(path)
        label = path.relative_to(root).as_posix()
        try:
            found = read_documents(path)
        except ValueError as e:
            unreadable.append(f"{label}: {e}")
            return
        for where, instance in found:
            where = label + where[len(str(path)):]
            if pick is None:
                docs.append((where, schema, instance, expect))
            else:
                for suffix, picked_schema, picked, picked_expect in pick(instance):
                    docs.append((f"{where}{suffix}", picked_schema, picked, picked_expect))

    for meta_path in sorted(root.rglob("expected.json")):  # document folders: expected.json names the schema
        mapped.add(meta_path)
        try:
            meta = load_json(meta_path.read_bytes().decode("utf-8"))
        except ValueError as e:
            unreadable.append(f"{meta_path.relative_to(root).as_posix()}: {e}")
            continue
        document = meta_path.parent / "document.json"
        if isinstance(meta, dict) and "schema" in meta and document.exists():
            add(document, meta["schema"], {"writer": meta.get("writer"), "reader": meta.get("reader")})
    for run_json in sorted(root.rglob("run.json")):  # run folders: valid/<run>/ and the seal and chain vectors' copies
        run_dir = run_json.parent
        for f in sorted(p for p in run_dir.rglob("*") if p.is_file()):
            rel = f.relative_to(run_dir).as_posix()
            schema = _RUN_FILES.get(rel) or ("overlay-seal" if _OVERLAY_SEAL.fullmatch(rel) else None)
            if schema:
                add(f, schema, valid if run_dir.parent.name == "valid" else unknown)

    def vector(v):
        bad = {"writer": "invalid", "reader": "invalid"} if v.get("schemaInvalid") else unknown
        return ([("#input", "decision#/$defs/input", v["input"], bad)] if "input" in v else []) + \
               ([("#expected", "decision", v["expected"], valid)] if "expected" in v else [])

    for path in sorted((root / "decision-vectors").glob("*.json")):
        add(path, None, None, vector)
    protocol = root / "protocol"
    for path in sorted(protocol.rglob("*")):
        if path in mapped or not path.is_file():
            continue
        if path.suffix == ".json" and path.name.startswith("plan"):
            add(path, "run-plan", unknown)
        elif path.name == "runner.json":
            add(path, "runner", unknown)
        elif path.name == "events.ndjson":
            add(path, "runner-event", unknown)
    unmapped = sorted(p.relative_to(root).as_posix() for p in root.rglob("*")
                      if p.is_file() and p.suffix in (".json", ".ndjson") and p not in mapped)
    return docs, unmapped, unreadable


def self_test():
    try:
        import jsonschema
        from jsonschema.exceptions import ValidationError
        from referencing import Registry
        from referencing.jsonschema import DRAFT202012
    except ImportError:
        print("The self-test compares with the jsonschema package: pip install jsonschema")
        return 2

    def ecma_pattern(validator, pattern, instance, schema):
        if validator.is_type(instance, "string") and not compile_pattern(pattern).search(instance):
            yield ValidationError(f"{instance!r} does not match {pattern!r}")

    no_format = jsonschema.FormatChecker(formats=())  # format is an annotation: assert nothing
    stock_class = jsonschema.Draft202012Validator
    ecma_class = jsonschema.validators.extend(stock_class, {"pattern": ecma_pattern})
    failed = 0

    count, failures = _unit_checks(lambda schema: ecma_class(schema, format_checker=no_format))
    print(f"unit checks: {count - len(failures)} of {count} pass")
    for f in failures:
        print(f"  FAIL {f}")
    failed += len(failures)

    sets, theirs = {}, {}
    for side in ("writer", "reader"):
        try:
            sets[side] = load_schemas(AEF / "schemas" / side)
        except (SchemaError, ValueError) as e:
            print(f"FAIL the {side} schemas do not load: {e}")
            return 1
        ss = sets[side]
        theirs[side] = {}
        for kind, cls in (("stock", stock_class), ("ecma", ecma_class)):
            # A $ref into a resource that declares $schema makes jsonschema switch to the class registered for that
            # dialect, which would drop the ECMA pattern keyword: that variant reads the schemas without $schema
            # (the resources are 2020-12 by DRAFT202012 either way).
            docs = {name: {k: v for k, v in doc.items() if k != "$schema"} if kind == "ecma" and isinstance(doc, dict)
                    else doc for name, doc in ss.documents.items()}
            registry = Registry().with_resources((ss._uris[name], DRAFT202012.create_resource(doc))
                                                 for name, doc in docs.items())

            def validator(schema, cls=cls, ss=ss, registry=registry, cache={}):
                uri = ss.uri(schema)
                if uri not in cache:
                    cache[uri] = cls({"$ref": uri}, registry=registry, format_checker=no_format)
                return cache[uri]
            theirs[side][kind] = validator
        meta_validator = stock_class(stock_class.META_SCHEMA)
        meta = [f"{name}: {e.message}" for name, doc in ss.documents.items() for e in meta_validator.iter_errors(doc)]
        print(f"{side} schemas: {len(ss.documents)} files, valid against the 2020-12 meta-schema"
              + ("" if not meta else f" EXCEPT {len(meta)}:"))
        for m in meta:
            print(f"  {m}")
    used = sorted(sets["writer"].keywords | sets["reader"].keywords)
    print(f"keywords the schemas use ({len(used)}, every one implemented): {' '.join(used)}")

    docs, unmapped, unreadable = _corpus(AEF / "conformance")
    agree, explained, unexplained, contradicted, located = 0, [], [], [], []
    for label, schema, instance, expect in docs:
        for side in ("writer", "reader"):
            what = f"{side} {label} ({schema})"
            try:
                errors = sets[side].validate(schema, instance)
                stock = theirs[side]["stock"](schema).is_valid(instance)
                ecma_errors = list(theirs[side]["ecma"](schema).iter_errors(instance))
            except Exception as e:  # a schema missing from the set, or jsonschema failing on it
                unexplained.append(f"{what}: {type(e).__name__}: {e}")
                continue
            mine, ecma = not errors, not ecma_errors
            verdict = "valid" if mine else "invalid"
            expected = expect.get(side)
            if mine == stock == ecma:
                agree += 1
            else:
                why = "pattern semantics" if mine == ecma else "keyword logic"
                line = (f"{what}: aef_schema {verdict}, jsonschema {'valid' if stock else 'invalid'} ({why}), "
                        f"expected.json {expected or 'says nothing'}")
                (explained if expected == verdict else unexplained).append(line)
            if _locations(errors) != _their_locations(ecma_errors):  # same pattern semantics: the same places
                located.append(f"{what}: aef_schema at {sorted(_locations(errors))}, jsonschema at {sorted(_their_locations(ecma_errors))}")
            if expected is not None and expected != verdict:
                contradicted.append(f"{what}: expected {expected}, aef_schema {verdict}{'' if mine else f': {errors[0]}'}")

    folders = {}
    for label, *_ in docs:
        folders[label.split("/")[0]] = folders.get(label.split("/")[0], 0) + 1
    print(f"corpus: {len(docs)} documents (JSON files, NDJSON lines, decision-vector inputs and outputs: "
          f"{', '.join(f'{k} {v}' for k, v in sorted(folders.items()))}), {2 * len(docs)} comparisons (writer and reader)")
    print(f"  agree with jsonschema: {agree}")
    print(f"  disagree, explained by expected.json: {len(explained)}")
    for line in explained:
        print(f"    {line}")
    print(f"  disagree, unexplained: {len(unexplained)}")
    for line in unexplained:
        print(f"    {line}")
    print(f"  error locations differ from jsonschema's (same pattern semantics): {len(located)}")
    for line in located:
        print(f"    {line}")
    failed += len(unexplained) + len(located)
    if unreadable:
        print(f"  unreadable ({len(unreadable)}): " + "; ".join(unreadable))
    if unmapped:
        print(f"  not mapped to a schema ({len(unmapped)}): {', '.join(unmapped)}")
    if contradicted:
        print(f"corpus verdicts the validators contradict ({len(contradicted)}; the schemas or the corpus are out of step,"
              " not a validator mismatch):")
        for line in contradicted:
            print(f"    {line}")
    print("PASS" if not failed else f"FAIL: {failed} unexplained")
    return 1 if failed or not docs else 0


def main(argv):
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(errors="backslashreplace")
    if argv[1:] == ["--self-test"]:
        return self_test()
    if len(argv) < 4:
        print(__doc__)
        return 2
    side, schema, files = argv[1], argv[2], argv[3:]
    ss = load_schemas(AEF / "schemas" / side if side in ("writer", "reader") else side)
    invalid = 0
    for f in files:
        for label, instance in read_documents(f):
            errors = ss.validate(schema, instance)
            invalid += bool(errors)
            for e in errors:
                print(f"{label}: {e}")
                for c in e.context:
                    print(f"{label}:   {c}")
    return 1 if invalid else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
