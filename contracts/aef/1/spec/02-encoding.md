# 2. Encoding

The rules in this document hold for every file AEF defines, in every class. A file that breaks one is invalid; a
verifier reports it as `encoding` (§3.9) under the file's path.

## 2.1 JSON documents

- **[ENC-1]** A JSON file is one JSON value ([RFC 8259]) encoded in UTF-8, with no byte-order mark. Its top-level value
  is an object.
- **[ENC-2]** Every JSON text is an I-JSON message ([RFC 7493]): no object has two members with the same name, and no
  string contains an unpaired surrogate (an escape `\ud800`–`\udfff` that is not part of a valid pair). A reader
  **MUST** refuse a document that breaks this, rather than keep the first or the last of two members: implementations
  disagree on which, and a sealed file that two readers read differently defeats the seal. Member order and
  whitespace are free, and a reader never depends on them (a writer may follow the schema's `properties` order).
  On an optional field, `null` and absence mean the same, except where a schema gives `null` its own meaning
  (a summary entry's `value` when `n` is 0, a checkpoint's `outcome` before `decided`); a writer **SHOULD** omit the
  field, and writing `null` is valid wherever the schema allows it.
- **[ENC-3]** Numbers are finite: no `NaN` or `Infinity`, and no literal whose value overflows binary64 (`1e400`);
  such a file is reported as `encoding`. A literal too small for binary64 (`1e-400`) is not an overflow: it reads as
  the nearest binary64 value (0) and is not refused.
- **[ENC-4]** Every number is read as an IEEE 754 binary64 value, as JSON parsers read it, and compared as one
  (amounts such as `spentUsd` and `maxUsd` included). A field the schemas type as an integer holds an integral value
  of at most 2^53 − 1 (9,007,199,254,740,991) in magnitude, so every binary64 reader reads it exactly: `2`, `2.0`
  and `2e0` are the integer 2; a fractional value, or one beyond that range, is refused (the schemas bound every
  integer field, so it is reported as `schema`). A writer writes integers in plain digits, and other numbers in any
  form that reads back as the same binary64 value (the shortest such form is recommended): every reader reads them
  alike, so no verdict depends on the form, though two writers' bytes, and so their run hashes, may differ. String lengths in the
  schemas (`minLength`, `maxLength`) count Unicode code points, as JSON Schema defines them: not bytes, UTF-16 code
  units or user-perceived characters.

## 2.2 NDJSON files

- **[ENC-5]** An NDJSON file is a sequence of JSON objects, one per line, UTF-8 without a byte-order mark. Lines are
  separated by LF (0x0A) only; a writer writes no CR; every line, the last included, ends in LF; there are no blank
  lines. An empty file (zero bytes) is valid and holds no lines.
- **[ENC-6]** A reader splits on LF alone. U+2028 and U+2029 inside a JSON string are text, not line breaks.
- **[ENC-7]** Each line is a JSON text under §2.1. A file whose last line does not end in LF is incomplete: a reader
  **MUST NOT** treat it as a finished file (for a closed run it is invalid; for an event stream it is still being
  written, §6.4). `overlays/events.ndjson` is judged line by line instead of as a file ([OVL-5]): it grows after the
  run is sealed, and a crash in it must cost one line, never the chain.

## 2.3 Values

- **[ENC-8] Times** are RFC 3339 timestamps in UTC, ending in `Z`, with zero to nine fraction digits, in the years
  0001 to 9999 (`2026-10-08T12:00:00Z`, `2026-10-08T12:00:00.123456789Z`; RFC 3339 allows year 0000, which date
  libraries disagree on). A date that does not exist (`2026-02-31`) is invalid and is never rolled over. Times compare at the full precision written; an implementation **MUST NOT** round them.
- **[ENC-9] Durations** are ISO 8601 durations of days, hours and minutes: `P`, then optionally `<n>D`, then
  optionally `T` followed by `<n>H`, `<n>M` or both in that order, each `<n>` one to five digits; at least one part,
  and no `T` without one. `P14D`, `PT36H`, `PT90M`, `P1DT12H30M` are durations; `P`, `PT`, `P1DT`, `PT30S`, `P1W` and
  `PT1M1H` are not. A checkpoint lane's freshness and a run plan's timeout are both durations (schema `common`,
  `$defs/duration`).
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
  printable ASCII without spaces, at most the length their schema allows, and compared byte for byte. So is a
  **typed reference** (`ref`, schema `common`, `$defs/ref`: a subject, a deployment, a suite, a label set, a gate, a
  template): a kind (a lower-case letter, then lower-case letters, digits and `-`), a colon, and a name of 1 to 256
  printable ASCII characters without spaces (`agent:support/support-triage`). `subject.ref` is a comparability axis
  ([LANE-6]), so two writers that derive a ref from the same free-text name ("Support Agent") must write the same
  bytes. A writer that derives a ref's name from free text **SHOULD** encode it so:
  - take the name's UTF-8 bytes; keep each byte from `!` to `~` (0x21–0x7E) except `%`, and write every other byte,
    and `%`, as `%` and two upper-case hex digits (a space is `%20`, `é` is `%C3%A9`): `agent:Support%20Agent`;
  - an empty name is `-`;
  - when the result is longer than 256 characters, keep its first 239 and add `~` and the first 16 lower-case hex
    characters of the SHA-256 of the name's UTF-8 bytes (256 in all), so two long names stay apart.

## 2.5 Patterns

- **[ENC-14]** Patterns in the schemas are ECMA-262 regular expressions (the dialect JSON Schema names), written
  without lookaround, backreferences or possessive forms, so that every common engine compiles them (ECMA-262, RE2,
  Python, .NET, Java). They also use no `\d`, `\w`, `\s`, `\b` or unescaped `.`, no inline options and no named
  groups: engines compile those alike but match them differently (non-ASCII digits and letters, line terminators),
  so the ASCII classes are spelled out (`[0-9]`, `[A-Za-z0-9_]`).
- **[ENC-15]** A pattern beginning with `^` and ending with `$` matches the **whole** string: `$` matches only at the
  end of the input, as in ECMA-262 and RE2. An implementation whose engine also lets `$` match before a final newline
  (Python's `re`, .NET) **MUST** compile patterns so that it does not (for example by replacing a final `$` with `\Z`
  in Python or `\z` in .NET). The corpus holds values ending in a newline that a conforming validator refuses.
- **[ENC-16]** Patterns are the rule; `format` is an annotation. Times, URIs and ids are checked by their patterns,
  whether or not a validator asserts `format`. In this specification a document is **valid against a schema** when
  the schema accepts it, with numbers read as [ENC-4] says, and every timestamp in it is a time that exists
  ([ENC-8]): `2026-02-31T00:00:00Z` matches the pattern and is still refused.

## 2.6 Limits

So that a reader can bound its work, and a hostile file cannot exhaust it:

- **[ENC-17]** A writer **MUST NOT** exceed, and a reader **MUST** refuse, anything beyond these limits, so that two
  readers never disagree on a file. Each is checked on the bytes before the content is trusted (a size, a count of
  LFs, a depth scan that counts `{` and `[` outside strings, whatever else the bytes hold), and a file or line beyond
  a limit is `limit` whatever else is wrong with it:

  | Limit | Value |
  |---|---|
  | JSON nesting depth (objects and arrays) | 64 |
  | Size of a JSON file or of one NDJSON line, except the next row | 4 MiB |
  | Size of `seal.json` or a batch seal: they list every sealed file | 40 MiB |
  | Size of a DSSE envelope (`*.dsse.json`): the base64 of a seal, 4/3 of its size | 56 MiB |
  | Lines in one NDJSON file | 1,000,000 |
  | Files in one run folder, not counting `seal.json`, `attestation.dsse.json` and `overlays/` (a run at the limit can still be sealed and take overlays) | 100,000 |
  | Overlay batches (so files under `overlays/`: at most one events file and two per batch) | 9,999 |
  | Size of one blob, or of one NDJSON file | 1 GiB |

  Depth counts the top-level value as 1 (`{}` is at depth 1, `{"a": []}` at depth 2). A line's size does not include
  its LF.
- **[ENC-18]** A reader that refuses something for a limit reports `limit` (§3.9) under its path: one NDJSON line over
  the size or depth limit at `<file>:<line>` (the file's other lines are still read), a file over its size or line
  count at the file, the number of files at the run folder's path, `.`, and the number of files under `overlays/` at
  `overlays`; it **MUST NOT** read a truncated part of it as the whole. A reader **MUST NOT** refuse
  anything within the limits.

String lengths and array sizes have their own bounds in the schemas.

## 2.7 The extension point

- **[ENC-19]** Every document and every NDJSON line a producer, host or runner writes **MAY** carry `ext`, an object,
  except the in-toto statements (`seal.json`, batch seals), whose shape in-toto fixes, and the decision function's
  input and output, which are computed. Its members are the producer's;
  their names **SHOULD** be reverse-DNS or prefixed by the producer's name (`agenteval.*`), and it **SHOULD** stay
  small (a few kilobytes): it is read with every document. A reader ignores what it
  does not know in `ext`, and nothing in this specification depends on it. `ext` **MUST NOT** carry a secret (§8.4).
