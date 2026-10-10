# AgentEval with Microsoft's ASSERT

Sample P1 (`samples/AgentEval.Samples/Interop/01_AssertInterop.cs`) and the files it uses. The guide is
[`docs/assert-interop.md`](../../../docs/assert-interop.md).

## Offline (no credentials, no spend)

```bash
cd samples/AgentEval.Samples
dotnet run -- 106
```

1. A Microsoft Agent Framework agent (`BillingDesk`, two C# tools) is served at ASSERT's endpoint contract and sent the
   exact request ASSERT sends. The model's tokens are scripted; the agent, its tool loop and the HTTP server are real.
2. `example-run/` is read into AgentEval: ASSERT's harm and over-refusal rates, each case's verdict (including a failed
   judge and a case with no score row), and each verdict composed with an AgentEval deterministic check.
   **`example-run/` was written by hand in ASSERT's formats (assert-ai 0.3.0); ASSERT did not produce it.**
3. Three labelled conversations are written as an ASSERT judge-only run, ready for ASSERT's judge.

## Against a real ASSERT

Serve the agent on a real model (the one `AI_INFERENCE_PROVIDER` selects), then point ASSERT at it:

```bash
cd samples/AgentEval.Samples
dotnet run -- 106 --live          # prints the endpoint, e.g. http://localhost:<port>/assert, and keeps serving
```

`assert-eval-config.yaml` here is an ASSERT eval config for that endpoint: set the port it printed, an absolute
`artifacts_root`, and a model ASSERT can reach (it generates the taxonomy and the prompt cases, and judges). In
another terminal:

```bash
pip install "assert-ai==0.3.0"
assert-ai run --config assert-eval-config.yaml
agenteval assert-ai import <artifacts_root>/results/billing-safety/<run>
```

Use `localhost` in the endpoint: ASSERT refuses a literal `127.0.0.1` unless `ASSERT_ALLOW_PRIVATE_ENDPOINTS=1`.
ASSERT's own stages (behavior → taxonomy → test cases → judge) call its models, so this run costs what those calls
cost.

To calibrate ASSERT's judge on AgentEval's labelled cases instead:

```bash
agenteval assert-ai export --golden <golden.jsonl> --taxonomy <taxonomy.json> --judge-model <model> --out assert-kit
assert-ai run --config assert-kit/assert-judge-config.yaml
agenteval assert-ai calibrate assert-kit/results/agenteval/judge-1 --cases assert-kit/agenteval-cases.json
```
