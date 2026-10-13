# OTLP dialect research and normalization contract

Research checkpoint: 2026-10-14. This resolves the accepted Mission Control RI1
inventory before implementing the normalizer. Export support does not establish
identical attribute semantics or default export configuration.

| Framework | Primary evidence | Receiver decision |
| --- | --- | --- |
| Mastra | [Exporter source](https://github.com/mastra-ai/mastra/blob/main/observability/otel-exporter/src/gen-ai-semantics.ts) maps operations into GenAI attributes and retains custom Mastra fields. | Normalize recognized GenAI fields; keep custom operations as reported. |
| Vercel AI SDK | [Telemetry documentation](https://ai-sdk.dev/docs/ai-sdk-core/telemetry) documents modern GenAI and legacy ai.* fields. | Include a distinct legacy field profile; do not infer semantics from framework name alone. |
| Google ADK | [Python tracing source](https://github.com/google/adk-python/blob/main/src/google/adk/telemetry/tracing.py) emits GenAI attributes; [observability docs](https://adk.dev/observability/) cover framework/language-specific setup. | Match fields actually received; preserve scope/schema metadata. |
| Genkit | [Advanced telemetry configuration](https://genkit.dev/docs/js/observability/advanced-configuration/) describes OpenTelemetry instrumentation and separate export/content controls. | No blanket native-dialect equivalence claim. Map recognized fields; other fields remain unsupported. |
| Eino | [APMPlus callback source](https://github.com/cloudwego/eino-ext/blob/main/callbacks/apmplus/apmplus.go) and [setup](https://github.com/cloudwego/eino-ext/blob/main/callbacks/apmplus/README.md) show callback-based telemetry and sessions. | Exporter choice controls the wire format; require OTLP/HTTP at this receiver and match recognized fields. |
| LangChainGo | [Callback interface](https://github.com/tmc/langchaingo/blob/main/callbacks/callbacks.go) exposes model/chain/tool lifecycle hooks. | This alone does not prove a native OTLP dialect. Accept an adapter's OTLP without inventing a LangChainGo mapping. |
| Pydantic AI | [Instrumentation documentation](https://pydantic.dev/docs/ai/integrations/logfire/) describes OTLP export and GenAI conventions, including versioned instrumentation. | GenAI field profile; preserve instrumentation version; never sum parent aggregate usage with child usage. |
| Strands | [Trace documentation](https://strandsagents.com/docs/user-guide/sdk/observability-evaluation/traces/) lists current and legacy token aliases and uses gen_ai.system for framework identity on some spans. | Map token aliases with conflict detection. Keep system identity separate from provider identity. |
| OpenAI Agents SDK | [Official tracing guide](https://developers.openai.com/api/docs/guides/agents/integrations-observability) establishes built-in tracing, but does not establish direct OTLP/HTTP compatibility. | Require an OTLP exporter/adapter; do not treat native trace records as OTLP. No calls to the hosted service are needed. |

## Versioned mapping policy

The first mapping revision is `otlp-normalization/1`. It recognizes explicit
field families, not guessed framework names or parsed span-name conventions:

- OTel GenAI fields are checked against the pinned
  [1.37.0 attribute registry](https://github.com/open-telemetry/semantic-conventions/blob/v1.37.0/model/gen-ai/registry.yaml).
- OpenInference field meanings are checked against its
  [semantic conventions](https://github.com/Arize-ai/openinference/blob/main/spec/semantic_conventions.md)
  at this dated checkpoint. Its hosting provider, API system and generic model
  fields are kept distinct; generic model is not guessed into requested or
  returned model.
- Legacy GenAI token aliases and Vercel ai.* fields receive separate profile IDs.
  Modern and legacy values that disagree are conflicts, never silently preferred.

Producer resource/scope schema URLs and instrumentation versions remain verbatim
metadata. Missing source versions stay unspecified; applying a mapping revision
is not certification that a producer implements a particular convention version.
Unknown field families remain explicitly unrecognized. No mapping converts
telemetry status or evaluator-shaped spans into evaluation verdicts.

Normalize only allowlisted structural fields. Each field retains source location,
attribute key, mapping profile, typed candidate value and any type/value issue.
Consensus is present only when valid candidates agree; missing, invalid and
conflicting are distinct. Invalid candidates cannot be rescued by a valid alias.
Token counts must be nonnegative integers and are never inferred from totals or
summed across parent/child spans. Content filtering and durable observation
storage remain separate implementation steps.

## Delivered API and field map

`IOtlpTraceNormalizer.Normalize` returns immutable `OtlpNormalizedSpan` records.
The result records the mapping revision, source indices/IDs/times, resource and
scope schema URLs, scope name/version, matched profiles and the number of
unmapped span attributes. It does not retain arbitrary raw attributes, event
payloads, span names or status messages. Declared structural string fields can
still contain sensitive producer data; this API does not certify redaction.

| Canonical field | Recognized source keys |
| --- | --- |
| operation | gen_ai.operation.name |
| operation.native | ai.operationId |
| provider | gen_ai.provider.name; llm.provider; ai.model.provider |
| system | gen_ai.system; llm.system |
| model.request | gen_ai.request.model; llm.request.model_name; ai.model.id |
| model.response | gen_ai.response.model; llm.response.model_name; ai.response.model |
| model.reported | llm.model_name |
| embedding.model | embedding.model_name |
| span.role | openinference.span.kind |
| session.id | gen_ai.conversation.id; session.id; gen_ai.session.id |
| agent.id / agent.name | corresponding gen_ai.agent.* keys |
| tool.name | gen_ai.tool.name; tool.name; ai.toolCall.name |
| tool.call.id | gen_ai.tool.call.id; ai.toolCall.id |
| tokens.input | gen_ai.usage.input_tokens; gen_ai.usage.prompt_tokens; llm.token_count.prompt; ai.usage.promptTokens |
| tokens.output | gen_ai.usage.output_tokens; gen_ai.usage.completion_tokens; llm.token_count.completion; ai.usage.completionTokens |
| tokens.total | gen_ai.usage.total_tokens; llm.token_count.total |
| service.name / service.namespace / service.instance.id | corresponding resource attributes only |

`Field(name)` returns Missing if no recognized source was supplied. Present
means all candidates are well-typed and agree. Conflicting valid values produce
Conflict with no selected value; any invalid candidate otherwise produces
Invalid. Candidate sources are ordinally sorted for stable output. Invalid
candidate payloads are omitted, with a stable issue code instead. Profiles
identify field mappings, not a conclusively detected producer/framework/version.
Resource identity is never taken from span-level service-looking attributes.

The normalizer applies span/attribute/item/string budgets and cancellation
without changing the decoded batch. Missing token values are not zero; parent
and child usage remains separate; no total is inferred. Tests cover current,
legacy and mixed aliases, conflicts, invalid values, provenance, ordering,
unsupported/content fields, resource bounds and parent/child usage.
