# Mission Control browser acceptance

`run.sh` installs the CLI tool and opens the legacy workspace pages.
`governance.mjs` serves the built SPA with controlled GraphQL responses to cover
UI edge cases. `governance-live.mjs` launches the production Mission Control host
against actual SQLite, sealed AEF evidence, signed overlays, a requirement
catalogue, a signed checkpoint and stored OTLP observations. It does not
intercept HTTP or substitute API responses.

## Real offline governance journey

Prerequisites: .NET 10, Node 20+, restored .NET dependencies, installed SPA and
browser-smoke npm dependencies, and an installed Playwright Chromium browser.
For installed Microsoft Edge, set `PLAYWRIGHT_CHANNEL=msedge`. Once dependencies
are available, these checks need no network services or model/provider calls.

From the repository root:

```sh
npm --prefix src/AgentEval.MissionControl.Spa run build
dotnet build src/AgentEval.MissionControl -f net10.0 --no-restore
dotnet run --project scripts/mc-browser-smoke/governance-fixture -- /absolute/new/fixture-directory
node scripts/mc-browser-smoke/governance-live.mjs /absolute/new/fixture-directory/fixture.json
```

Use a new disposable fixture directory on every run; the generator refuses an
existing directory. The final assertion deliberately corrupts that fixture's
result file to verify refusal instead of a cached success. The harness stops
its host and browser on success or failure. Keep the directory for diagnostics
(`host.log`, and `browser-failure.txt` on failure); remove it when finished.
The host binds only to `127.0.0.1:5057`; `MC_SMOKE_PORT` overrides the port.

The synthetic fixture explicitly declares a live target to exercise requirement
eligibility, but invokes no agent. It creates ephemeral test signing keys and
retains only the public trust policy. Acceptance covers source navigation,
metrics, exact observation integers and conflicts, signed decision history,
authorized waiver expiry, checkpoint signatures/anchors, missing selection and
post-index evidence tampering. This is a source-build acceptance path; CLI
package installation is covered separately by `run.sh`. To run the governance
journey against an installed package too, set `MC_SMOKE_HOST_ROOT` to its
`tools/net10.0/any` directory. The harness then uses both the packaged Mission
Control assembly and its packaged `wwwroot`, rather than source build outputs.
