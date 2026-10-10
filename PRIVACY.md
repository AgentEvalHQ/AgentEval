# Privacy Statement

## Summary

- AgentEval has no telemetry, analytics, crash reporting or update check. It does not send usage data to its authors or to anyone else.
- AgentEval opens network connections only for the features listed below. Each one stays off until you turn it on: with a command-line flag, an environment variable, or by creating the relevant object in your own code. Environment variables need care, because the CLI can use a provider key that is already set for another tool (see [Provider auto-detection](#provider-auto-detection)).
- Results, transcripts and reports are written to local files (by default under `.agenteval/`). AgentEval does not upload them.

## Model calls you make through the library

The library's evaluators, judges, red-team attacks, memory benchmarks and Gatekeeper call models only through the `IChatClient` or `AIAgent` instances you pass in. Where those calls go, and what they carry, depends on how you configure those clients.

Your agents, tested through AgentEval, may call external services (such as Azure OpenAI or OpenAI). Those calls are made by **your code and your agents**. You are responsible for understanding and complying with the privacy policies and terms of service of the services they use.

## Network features in the library

| Feature | Contacts | Sends | Turned on by |
|---------|----------|-------|--------------|
| Benchmark pack download (`PackDownloader`) | The pack's data URL. Built-in catalog: `raw.githubusercontent.com` (HarmBench, CyberSecEval) and `huggingface.co` (JailbreakBench). Or any URL you give it. | One HTTP GET for that URL. Nothing from your evaluation. | Calling `PackDownloader.DownloadAsync`. CLI: `agenteval redteam --pack <name or url>`; catalog packs also need `--accept-license`. |
| Live package-registry check (`HttpPackageRegistry`) | `pypi.org`, `registry.npmjs.org`, `api.nuget.org` | One HTTP GET per package name, with the name in the URL. The names come from the responses of the agent under test. Answers are cached for the lifetime of the object. | Creating an `HttpPackageRegistry`. CLI: `agenteval redteam --package-registry live` (the default is `none`). |
| Decision-model judge (`SystemOneDecisionClient`, preview API) | `api.typesafe.ai` (TypeSafe) or `openrouter.ai` (OpenRouter), or the endpoint you set | A POST with the model id, the input and output being judged, one yes/no question per criterion, and your API key as a bearer token. | Creating a `SystemOneDecisionClient`. CLI: `agenteval bench gdpr calibrate --decisions` or `agenteval bench eu-ai-act calibrate --decisions`, with `TYPESAFE_API_KEY` or `OPENROUTER_API_KEY` set; these commands send the calibration cases. |
| Gatekeeper HTTP egress handler (`GatekeeperHttpMessageHandler`) | DNS, for the host of each request your code sends through it | Nothing of its own. It resolves the host, checks the address against your allow-list, and then forwards or blocks your request. | Sending requests through it, for example with `GatekeeperHttpMessageHandler.CreateHttpClient`. |
| Gatekeeper containment pool (`ContainmentHttpClientPool`) | Whatever your tool functions request | Nothing of its own. It creates the `HttpClient` pipelines your functions use. | Creating the pool. |
| Copilot Studio connector (`AgentEval.MAF.CopilotStudio`, shipped inside the CLI) | Microsoft Entra ID, for MSAL device-code sign-in, and the Copilot Studio API for the environment and agent named in your configuration | Sign-in requests for your tenant and app registration. Then each prompt (a test case or an attack probe) as a conversation turn; the agent's replies come back. | CLI: `--sut copilot-studio --copilotstudio-config <file>`. `agenteval redteam` also requires `--i-understand-live-side-effects`. |

The Copilot Studio connector keeps an MSAL token cache in your local application-data folder (`AgentEval/msal_cache/`), protected by DPAPI on Windows, the Keychain on macOS, and the keyring (libsecret) on Linux. It is never written as a plain-text file.

### Memory benchmark HTML report

When `JsonFileBaselineStore` saves a memory baseline, it copies a `report.html` page into the store's root folder. This is on by default (`MemoryReportingOptions.AutoCopyReportTemplate`). When that page is opened in a browser, directly or through `JsonFileBaselineStore.OpenReport`, the browser downloads Chart.js 4.4.1 from `cdnjs.cloudflare.com` and fonts from `fonts.googleapis.com`. The report data is not sent; those hosts see an ordinary browser request (your IP address and browser headers). `OpenReport` serves the page from a local web server bound to `127.0.0.1`.

## Model calls made by the CLI

The CLI builds model clients from your flags and environment variables, and sends each one the prompts its command needs:

- **Agent under test**: `--endpoint` (with `--model` and `--api-key`), `--azure` (with `--deployment-name`), `--from-env`, or `--sut`. It receives test inputs, scenario prompts or attack probes, and its responses are graded.
- **LLM judge for the `bench` commands and their `calibrate` subcommands**: the endpoint in `AZURE_OPENAI_JUDGE_ENDPOINT`, `AZURE_OPENAI_JUDGE_API_KEY` and `AZURE_OPENAI_JUDGE_DEPLOYMENT`, or, when those are unset, the provider described under [Provider auto-detection](#provider-auto-detection). It receives the judge prompt, the case, and the response being graded. If no judge is configured, the command stops with an error. A `--sut mock` run uses a built-in placeholder judge and sends nothing to any provider.
- **`--judge` and `--attacker`** on `agenteval redteam`, and `--judge` on `agenteval eval`: the judge receives the probes or test inputs and the responses being graded; the attacker model receives the attack goal and the conversation so far.
- **Gatekeeper judge gates** (`agenteval gatekeeper`): `--endpoint` or `--azure`, or, with neither flag, the `AZURE_OPENAI_ENDPOINT`, `AZURE_OPENAI_DEPLOYMENT` and `AZURE_OPENAI_API_KEY` variables. The model receives the content each gate inspects.

The SDKs used for these calls (the OpenAI SDK, the Azure SDK and MSAL) may add their own client-identification headers, such as a User-Agent with the SDK version.

### Provider auto-detection

When `AI_INFERENCE_PROVIDER` is not set, the CLI uses the first provider whose variables are present, in this order:

1. Azure OpenAI: `AZURE_OPENAI_ENDPOINT`, `AZURE_OPENAI_API_KEY`, `AZURE_OPENAI_DEPLOYMENT`
2. Bitdeer: `BITDEER_API_KEY` (default endpoint `https://api-inference.bitdeer.ai/v1`)
3. OpenAI: `OPENAI_API_KEY` (default endpoint `https://api.openai.com/v1`)
4. Azure AI Foundry: `FOUNDRY_ENDPOINT`, `FOUNDRY_API_KEY`, `FOUNDRY_MODEL`
5. OpenAI-compatible endpoint: `OPENAI_COMPATIBLE_ENDPOINT`, `OPENAI_COMPATIBLE_MODEL`

A key that is already in your environment for another tool, such as `OPENAI_API_KEY`, is therefore enough for a judge-graded `bench` command to send its prompts to that provider. Set `AI_INFERENCE_PROVIDER` to choose the provider explicitly.

## Mission Control

`agenteval mc serve` (.NET 10 only) starts a local web server on `127.0.0.1` that reads your `.agenteval/` workspace.

At `/graphql` it serves Hot Chocolate's Nitro GraphQL IDE (ChilliCream.Nitro.App 30.0.2), which runs in your browser. Mission Control changes two of Nitro's defaults:

- `ServeMode` is set to `Embedded`, so the IDE's files come from the copy bundled in the ChilliCream.Nitro.App package. With Nitro's default (`Latest`), the Mission Control server would fetch them from `cdn.chillicream.com` each time the IDE is opened.
- `DisableTelemetry` is set to `true`, so the IDE does not send its usage ping to `telemetry.chillicream.com` (a device id, the operating system, the user agent, and the Nitro application type and version).

One request has no Nitro option: the IDE's browser code checks connectivity with a GET to `api.chillicream.cloud/status` when the page opens and every 30 seconds while it stays open. Signing in to a ChilliCream account from the IDE contacts ChilliCream's identity and API servers. Mission Control sends a `Content-Security-Policy` header with `connect-src 'self'` on its responses, which tells the browser to block requests to other hosts; this has not been tested in a browser. None of this happens unless you open `/graphql` in a browser.

## Local only

These features look network-related but stay on your machine:

- Run provenance records Git details by running `git rev-parse`, `git status` and `git describe` locally.
- The Mission Control health check calls `127.0.0.1` only.
- The LongMemEval benchmark reads a dataset file that you download yourself (`LONGMEMEVAL_DATASET_PATH`). AgentEval does not download it.

## Test Data Responsibility

AgentEval processes test cases, evaluation prompts, and agent responses that **you provide**. You are responsible for ensuring that your test data does not contain sensitive, personal, or regulated information unless you have appropriate controls in place. Every model call listed above sends that data to the endpoint you configured.

## Dependencies

AgentEval depends on Microsoft .NET libraries and third-party packages listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). These dependencies may have their own privacy policies. AgentEval does not enable telemetry in its dependencies, and it configures no OpenTelemetry exporter. Mission Control switches off the Nitro IDE's usage ping, as described above; AgentEval does not change other dependencies' defaults, such as the client-identification headers the SDKs add to model calls.

Note: AgentEval's CI/CD configuration explicitly disables .NET CLI telemetry (`DOTNET_CLI_TELEMETRY_OPTOUT: true`).

---

*Last updated: October 2026*
