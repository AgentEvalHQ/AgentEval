# 2. Encoding

The rules in this document hold for every file AEF defines, in every class. A file that breaks one is invalid; a
verifier reports it as `encoding` (§3.9) under the file's path.

## 2.1 JSON documents

- **[ENC-1]** A JSON file is one JSON value ([RFC 8259]) encoded in UTF-8, with no byte-order mark. Its top-level value
  is an object.
- **[ENC-2]** Every JSON text is an I-JSON message ([RFC 7493]): no object has two members with the same name, and no
  string contains an unpaired surrogate (an escape `\ud800`–`\udfff` that is not part of a valid pair). A reader
  **MUST** refuse a document that breaks this, rather than keep the first or the last of two members: implementations
  disagree on which, and a sealed file that two readers read differently defeats the seal.
- **[ENC-3]** Numbers are finite: no `NaN` or `Infinity`, and no literal whose value overflows binary64 (`1e400`);
  such a file is reported as `encoding`.
- **[ENC-4]** Every number is read as an IEEE 754 binary64 value, as JSON parsers read it, and compared as one
  (amounts such as `spentUsd` and `maxUsd` included). A field the schemas type as an integer holds an integral value
  of at most 2^53 − 1 (9,007,199,254,740,991) in magnitude, so every binary64 reader reads it exactly: `2`, `2.0`
  and `2e0` are the integer 2; a fractional value, or one beyond that range, is refused (the schemas bound every
  integer field, so it is reported as `schema`). A writer writes integers in plain digits.

## 2.2 NDJSON files

- **[ENC-5]** An NDJSON file is a sequence of JSON objects, one per line, UTF-8 without a byte-order mark. Lines are
  separated by LF (0x0A) only; a writer writes no CR; every line, the last included, ends in LF; there are no blank
  lines. An empty file (zero bytes) is valid and holds no lines.
- **[ENC-6]** A reader splits on LF alone. U+2028 and U+2029 inside a JSON string are text, not line breaks.
- **[ENC-7]** Each line is a JSON text under §2.1. A file whose last line does not end in LF is incomplete: a reader
  **MUST NOT** treat it as a finished file (for a closed run it is invalid; for an event stream it is still being
  written, §6.4).

## 2.3 Values

- **[ENC-8] Times** are RFC 3339 timestamps in UTC, ending in `Z`, with zero to nine fraction digits
  (`2026-10-08T12:00:00Z`, `2026-10-08T12:00:00.123456789Z`). A date that does not exist (`2026-02-31`) is invalid and
  is never rolled over. Times compare at the full precision written; an implementation **MUST NOT** round them.
- **[ENC-9] Durations** are ISO 8601 durations of days and hours only, each at most five digits, and not empty:
  `P14D`, `PT36H`, `P1DT12H`. Plan timeouts use hours and minutes (`PT2H`, `PT45M`, `PT1H30M`).
- **[ENC-10] Versions** of a subject, a suite or a producer are exact strings: printable ASCII without spaces
  (`[!-~]`), at most 128 characters, compared byte for byte. `latest` (in any case) is not a version: it is resolved
  before anything runs (§5.1).
- **[ENC-11] Digests** are written `sha256:` followed by 64 lower-case hex characters, except where a field holds the
  bare hex (`runHash`, blob names, in-toto `digest.sha256`).

## 2.4 Identifiers and names

- **[ENC-12]** The schemas' `$id`s (`https://agenteval.dev/aef/1/…`) and the in-toto predicate types
  (`https://agenteval.dev/aef/1/evidence`, `https://agenteval.dev/aef/1/overlay-batch`) are **names**, not locations:
  they identify AEF major version 1 and do not change when the files move. A reader **MUST NOT** fetch them.
- **[ENC-13]** Ids defined by AEF (`runId`, `resultId`, evidence ids, gate ids, event ids, checkpoint ids) are
  printable ASCII without spaces, at most the length their schema allows, and compared byte for byte.

## 2.5 Patterns

- **[ENC-14]** Patterns in the schemas are ECMA-262 regular expressions (the dialect JSON Schema names), written
  without lookaround, backreferences or possessive forms, so that every common engine compiles them (ECMA-262, RE2,
  Python, .NET, Java).
- **[ENC-15]** A pattern beginning with `^` and ending with `$` matches the **whole** string: `$` matches only at the
  end of the input, as in ECMA-262 and RE2. An implementation whose engine also lets `$` match before a final newline
  (Python's `re`, .NET) **MUST** compile patterns so that it does not (for example by replacing a final `$` with `\Z`
  in Python or `\z` in .NET). The corpus holds values ending in a newline that a conforming validator refuses.
- **[ENC-16]** Patterns are the rule; `format` is an annotation. Times, URIs and ids are checked by their patterns,
  whether or not a validator asserts `format`.

## 2.6 Limits

So that a reader can bound its work, and a hostile file cannot exhaust it:

- **[ENC-17]** A writer **MUST NOT** exceed, and a reader **MAY** refuse anything beyond:

  | Limit | Value |
  |---|---|
  | JSON nesting depth (objects and arrays) | 64 |
  | Size of a JSON file or of one NDJSON line | 4 MiB |
  | Lines in one NDJSON file | 1,000,000 |
  | Files in one run folder | 100,000 |
  | Size of one blob | 1 GiB |

- **[ENC-18]** A reader that refuses a file for a limit reports `limit` (§3.9) under its path (the run folder's, `.`,
  for the number of files); it **MUST NOT** read a truncated part of it as the whole. A reader **MUST NOT** refuse
  anything within the limits.

String lengths and array sizes have their own bounds in the schemas.

## 2.7 The extension point

- **[ENC-19]** Every document and every NDJSON line **MAY** carry `ext`, an object. Its members are the producer's;
  their names **SHOULD** be reverse-DNS or prefixed by the producer's name (`agenteval.*`), and it **SHOULD** stay
  small (a few kilobytes): it is read with every document. A reader ignores what it
  does not know in `ext`, and nothing in this specification depends on it. `ext` **MUST NOT** carry a secret (§8.4).
