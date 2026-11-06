# Microsoft 365 (Work IQ A2A 1.0) fixture corpus

**All data in this folder is synthetic.** Nothing here was captured from a Microsoft service or a tenant; every
id, name, URL, timestamp, answer and citation is invented for the provider's design (see
[docs/microsoft365.md](../../../../docs/microsoft365.md)). Hosts are `*.example.invalid` or the public Work IQ
endpoint; no real people, organisations or tenants appear.

Every `*.json` and `*.txt` file is an embedded resource (`AgentEval.Tests.csproj` glob
`Microsoft365/Fixtures/**/*.json;**/*.txt`), named `AgentEval.Tests.Microsoft365.Fixtures.<folder>.<file>`.
`FixtureCatalogTests` (`tests/AgentEval.Tests/Microsoft365/FixtureCatalogTests.cs`) guards the catalog: the
embedded set must equal the table below, every file must parse, and the 1.0 envelopes must round-trip through
the A2A SDK's own `A2AJsonUtilities.DefaultOptions`.

## Wire conventions (A2A 1.0.0-preview2, verified against the decompiled SDK)

- Property names are the SDK's `JsonSerializerDefaults.Web` camelCase of the CLR names; the SDK declares no
  `[JsonPropertyName]` except `jsonrpc`. There are **no `kind` discriminators**.
- Enums are strings: `TASK_STATE_SUBMITTED|WORKING|COMPLETED|FAILED|CANCELED|INPUT_REQUIRED|REJECTED|AUTH_REQUIRED`,
  `ROLE_USER|ROLE_AGENT`.
- A part is a one-of `text` | `raw` (base64) | `url` | `data`, with optional `mediaType`, `filename`, `metadata`.
  (The protocol's "file" part is carried as `raw` or `url` in this SDK; there is no nested `file` object.)
- `SendMessage` → `result.task` **or** `result.message`. `GetTask` / `CancelTask` → the task **directly under
  `result`**. Streams: each SSE `data` is a complete JSON-RPC envelope whose `result` holds exactly one of
  `task` | `message` | `statusUpdate` | `artifactUpdate`.
- `[JsonRequired]` in the SDK: task `id`, `contextId`, `status`; status `state`; message `messageId`, `role`,
  `parts`; artifact `artifactId`, `parts`; status/artifact update `taskId`, `contextId`; card `name`,
  `description`, `version`, `supportedInterfaces`, `capabilities`, `skills`, `defaultInputModes`,
  `defaultOutputModes`; skill `id`, `name`, `description`, `tags`.
- Citation markers are literal private-use characters: `U+E200 cite (U+E202 turn{X}search{Y})+ U+E201`.
- Attributions live in `status.message.metadata.attributions` (task) or `message.metadata.attributions`
  (direct message); items carry `attributionType`, `attributionSource`, `providerDisplayName`, `seeMoreWebUrl`
  (sample-observed field names, not standard A2A).
- The confirmation data part follows the research's "Proposed arguments from confirmation": the part's `data`
  has an `input-required` key whose value has `type: "confirmation_required"`, `sourceRequestId` and an
  `adaptiveCard`; `adaptiveCard.actions[].data.message.action` and `.actionId` are **JSON-encoded strings** of an
  array of calls `{id, type, function: {name, arguments}}` where `function.arguments` is itself a JSON-encoded
  string. The `auth-required` key and its inner fields (`type`, `connectorDisplayName`, `signInUrl`) are
  **invented**; only the state and the media type are documented.

Shared identity: context `ctx-5e0c7a2b-91d4-4f8e-b6a3-0d2f8c1e7b55`; `send-task-submitted`, `gettask-completed`
and `gettask-working` share task `task-7a1e3c5b-9d2f-4e8a-b1c6-3f5d7e9a0b21`; every SSE stream uses task
`task-ab12cd34-ef56-4a78-9b01-23c45d67e89f` and artifact `artifact-answer-ab12cd34-0001`.

## Catalog

Lanes: **M13** streaming + task reducer, **M15** contract tests via `FakeWorkIqHandler` (M10 client, M11 auth,
M12 capture, M14 outcomes), **M20** conversation lifecycle, **M21** answer selection, **M22** evidence
normaliser, **M23** Native118 diagnostics decoder, **M30** CLI target.

| File | Models | Consumed by |
|---|---|---|
| `normalization-fixtures.json` | Research Appendix D verbatim: seven semantic normalisation cases (`input` → `expected` projections), not wire envelopes | M22, M23 |
| `envelopes/send-task-completed-with-answer-and-attributions.json` | `SendMessage` → completed task; `Answer` artifact with two text parts (single + compound citation markers, leading-space second chunk); terminal status message with empty parts and two attributions | M10, M15, M21, M22 |
| `envelopes/send-direct-message.json` | `SendMessage` → `result.message` (no task); text part with a marker; attributions in `message.metadata` | M10, M15, M21, M22 |
| `envelopes/send-task-submitted.json` | `SendMessage` (returnImmediately) → `TASK_STATE_SUBMITTED`, no artifacts | M10, M20 |
| `envelopes/gettask-completed.json` | `GetTask` shape (task directly under `result`), same ids as the submitted task, answer artifact + attributions | M10, M20 |
| `envelopes/gettask-working.json` | `GetTask` → `TASK_STATE_WORKING` with a progress status message, no artifacts | M10, M20 |
| `envelopes/send-task-failed-with-partial-artifact.json` | `TASK_STATE_FAILED` with an error status message and a partial `Answer` artifact (must never read as a successful answer) | M14, M21, M24 |
| `envelopes/send-task-input-required-confirmation.json` | `TASK_STATE_INPUT_REQUIRED`; status message carries text + the confirmation data part (`application/vnd.ms-workiq.interaction+json`) with a JSON-encoded proposed call | M14, M20, M23 (approval intents) |
| `envelopes/send-task-auth-required.json` | `TASK_STATE_AUTH_REQUIRED`; status message carries text + an `auth-required` data part (inner shape invented) | M14, M20 |
| `envelopes/send-task-rejected.json` | `TASK_STATE_REJECTED` with a reason in the status message | M14 |
| `envelopes/send-task-canceled.json` | `TASK_STATE_CANCELED`, no artifacts | M14, M20 |
| `envelopes/send-task-completed-with-developer-and-retrieval-info.json` | Completed task with `Answer` + `Developer Info` (`application/vnd.ms-workiq.developer-info`; `plugins.selectedFunctions`, `plugins.executions`) + `Retrieval Info` (`application/vnd.ms-workiq-internal.retrieval`; `executions[].debugInfo` with `queryString`, `maximumNumberOfResults`, `totalResultCount`, `latencyMs`, ranked `retrievalHits` with `webUrl`/`extracts`/`resourceMetadata`) | M21 (exclusion), M23 |
| `envelopes/send-task-completed-two-text-artifacts-ambiguous.json` | Completed task with two distinct artifacts both named `Answer` and conflicting text | M21 (`Ambiguous`) |
| `envelopes/send-task-completed-empty-answer.json` | Completed task; `Answer` artifact with no parts; status message with no metadata (attributions absent, not empty) | M21 (`Empty`), M22 |
| `envelopes/send-task-completed-with-file-parts.json` | Completed task whose `Answer` artifact mixes a text part, a `url` file part and a `raw` (base64) file part | M13/M21 (non-text parts preserved) |
| `envelopes/rpc-error-on-http-200-version-not-supported.json` | JSON-RPC error `-32009` (A2A `VersionNotSupported`) on HTTP 200 | M10 (`ProtocolVersionMismatch`) |
| `envelopes/rpc-error-on-http-200-method-not-found.json` | JSON-RPC error `-32601` (`MethodNotFound`) on HTTP 200 | M10 |
| `envelopes/http-429-body.json` | HTTP 429 body: a gateway-style error document, **not** a JSON-RPC envelope | M10 (`RateLimited`, backoff) |
| `envelopes/http-429-headers.txt` | The matching response head with `Retry-After: 7` | M10 |
| `envelopes/v03-shaped-response.json` | A 0.3-style body (`kind` fields, lowercase `completed`/`user`, task directly under `result`) that the 1.0 provider must **reject**; the SDK throws on the lowercase state | M10 (version coupling) |
| `envelopes/canceltask-completed-race.json` | `CancelTask` returning a task that already completed | M20 (cancel race) |
| `envelopes/canceltask-not-found-error.json` | `CancelTask` → error `-32001` (`TaskNotFound`) | M20 (`TaskUnavailable`) |
| `envelopes/canceltask-not-cancelable-error.json` | `CancelTask` → error `-32002` (`TaskNotCancelable`) | M20 |
| `sse/stream-happy-path.txt` | snapshot submitted → artifactUpdate `append:false` → artifactUpdate `append:true lastChunk:true` → statusUpdate working → statusUpdate completed with attributions (metadata arrives after the text) | M13, M15 |
| `sse/stream-replace-then-append.txt` | draft (`append:false`) → replacement (`append` omitted = false) → `append:true`; the draft must vanish | M13 |
| `sse/stream-two-artifacts-interleaved.txt` | `Answer` and `Follow-up questions` artifacts interleaved; keyed by `(taskId, artifactId)` | M13, M21 |
| `sse/stream-lastchunk-before-terminal-then-eof.txt` | `lastChunk:true` then EOF with no terminal status: `Interrupted`, not completed | M13, M20 |
| `sse/stream-eof-before-terminal.txt` | working + one chunk, then EOF | M13, M20 (GetTask reconcile) |
| `sse/stream-duplicate-frame.txt` | The same `append:true` frame twice, byte-identical: legitimate repetition, never deduplicated | M13 |
| `sse/stream-multiline-data.txt` | Two frames whose JSON payload is split over many `data:` lines (joined with LF by a conforming parser) | M13 (SSE parser) |
| `sse/stream-crlf.txt` | The happy path with CRLF line endings, bytes exact | M13 (SSE parser) |
| `sse/stream-with-comments-and-heartbeats.txt` | `: keep-alive` comment lines between and inside frames | M13 (SSE parser) |
| `sse/stream-with-id-and-event-fields.txt` | `id:`, `event:` and one `retry:` field per frame; ids must be preserved, not treated as a replay contract | M12 (capture), M13 |
| `sse/stream-malformed-event.txt` | A frame whose `result` has both `task` and `message` (invalid one-of) followed by a completed status: must be rejected, not skipped | M13 |
| `sse/stream-direct-message-only.txt` | A single `result.message` frame then EOF | M13, M21 |
| `sse/stream-failed-after-partial.txt` | working → partial chunk → `TASK_STATE_FAILED` with reason | M13, M14 |
| `sse/stream-input-required.txt` | working → `TASK_STATE_INPUT_REQUIRED` with the confirmation data part | M13, M20 |
| `sse/subscribe-first-frame-snapshot.txt` | `SubscribeToTask`: first frame is a working snapshot that already holds partial text; then the final chunk and completion. The snapshot **replaces** state | M13, M20 |
| `sse/stream-rpc-error-frame.txt` | A JSON-RPC `error` envelope inside the stream (HTTP 200), then EOF | M10, M13 |
| `cards/default-card-two-interfaces.json` | Default Work IQ card: `JSONRPC` 1.0 at `https://workiq.svc.cloud.microsoft/a2a/` plus an `HTTP+JSON` interface; `streaming: true`, `pushNotifications: false`; two skills | M10 (interface selection) |
| `cards/agent-specific-card.json` | Card at `/a2a/U_abc123.def456/` with an opaque routing `tenant`; the id must survive normalisation whole | M10 |
| `cards/card-untrusted-host.json` | `JSONRPC` interface whose host is `example.invalid`: trust policy must refuse it | M10, M11 |
| `cards/card-v03-only.json` | Only a `protocolVersion: "0.3"` interface (plus legacy top-level `url`/`protocolVersion`): no 1.0 interface to select | M10 |

## Regenerating

The files were produced by a throw-away generator (not checked in) and are maintained by hand from here on.
When adding a fixture: add the file, add a row above, add its relative name to `ExpectedFixtures` in
`FixtureCatalogTests`, and keep the SDK wire names.
