# Gatekeeper validation samples

This standalone runner keeps external validation explicit and independently executable:

```powershell
dotnet run --project samples/AgentEval.Gatekeeper.Validation -- calibrate
dotnet run --project samples/AgentEval.Gatekeeper.Validation -- remote
dotnet run --project samples/AgentEval.Gatekeeper.Validation -- mock-tools
```

## Phase 4

`calibrate` sends the reviewed inbound and outbound gold sets to the Azure OpenAI deployment configured by
`AZURE_OPENAI_ENDPOINT`, `AZURE_OPENAI_API_KEY`, and `AZURE_OPENAI_DEPLOYMENT`. It requires
`AGENTEVAL_A2A_I_UNDERSTAND_CALIBRATION_PAYLOADS=true` and does not contact an A2A endpoint.

`remote` uses the existing real A2A boundary sample. In addition to Azure configuration, it requires an absolute
HTTP(S) `AGENTEVAL_A2A_BASE_URL` and `AGENTEVAL_A2A_I_UNDERSTAND_LIVE_SIDE_EFFECTS=true`. It calibrates both axes,
stops unless both are inline-ready, resolves the standard agent card, and performs one bounded delegation.

Neither mode prints credentials, calibration payloads, judge evidence, or outbound boundary payloads.

## Phase 7 fixture

`mock-tools` is fully offline. It exercises narrow sample-local contracts and mock bodies for SQL, browser,
structured cloud operations, and package restore. It demonstrates fail-closed argument handling and API shape; it
does not establish that the sample grammars are safe for arbitrary real SQL dialects, browser runtimes, cloud CLIs,
or package managers.
