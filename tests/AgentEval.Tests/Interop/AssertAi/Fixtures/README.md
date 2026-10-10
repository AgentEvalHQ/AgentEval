# ASSERT fixtures

Files from, or derived from, Microsoft's ASSERT (`responsibleai/ASSERT`, PyPI `assert-ai`), read at commit
`e03aa809f9367696e72d47f19dbcf0ed01d1a74f` (main, 2026-10-06). Release `v0.3.0` (`04b713fe`) has the same HTTP
target, `test_set.jsonl`, `inference_set.jsonl`, `scores.jsonl` and `taxonomy.json` formats. Nothing was run to make
them: there are no real `scores.jsonl` or `inference_set.jsonl` files in the ASSERT repository.

| File | What it is | Source | Licence |
|---|---|---|---|
| `taxonomy.disability_representation.json` | A real taxonomy, 26 categories (5 permissible) | `sample-testsets/harms/disability_representation/taxonomy.json` | CDLA-Permissive-2.0 |
| `test_set.disability_representation.jsonl` | Four real test-set rows (2 prompt, 2 scenario) | the same folder's `test_set.jsonl`, lines 1, 2, 51, 52 | CDLA-Permissive-2.0 |
| `permissibility_split.json` | Score rows and the harm / over-refusal rates ASSERT's own test expects | `tests/test_results.py` lines 83-148, Python literals as JSON | MIT |
| `http_endpoint_tool_events.json` | An endpoint reply with tool events and how ASSERT turns it into a transcript | `tests/test_http_endpoint_events.py` lines 91-147 | MIT |
| `request.*.json` | The exact bodies ASSERT POSTs to an endpoint (prompt case, third scenario turn, sandbox with `case_id`) | built from `core/session.py:779-801` with Python's `json.dumps` defaults | MIT |
| `inference_set.endpoint_prompt_case.jsonl` | The `inference_set.jsonl` row ASSERT writes for an endpoint exchange | built from `stages/inference.py:361-447` | MIT |
| `scores.constructed.jsonl`, `taxonomy.constructed.json` | One score row per status (`ok`, `scoring_skipped`, `judge_failed`, `filter_skipped`) and the taxonomy they refer to | built from `stages/judge.py:221-323` | MIT |

"Built from" files follow the code's shape, key order and separators; their values are invented.

`LICENSE-ASSERT-MIT.txt` is ASSERT's licence (Copyright (c) Microsoft Corporation). `LICENSE-CDLA-Permissive-2.0.txt`
is the licence of ASSERT's `sample-testsets/`; its clause 2.1 asks that it accompany the shared data.
