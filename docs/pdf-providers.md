# Optional PDF providers

PDF generation now requires three explicit choices: install a provider package, register it, and request PDF output. The default `AgentEval` package, `AddAgentEvalAll()`, and stock `agenteval` CLI do not install or initialize a PDF engine. Evaluations, AEF persistence, Markdown, HTML, and reading already-captured PDF bytes remain independent.

These examples describe the source/local-package implementation on the improvement branch. The new packages are not claimed to be published until a release publishes them.

## Library usage

Reference `AgentEval` and the matching-version `AgentEval.Rendering.PdfSharp` package. Then register the provider in the host's service collection:

```csharp
using AgentEval.Rendering.Pdf;
using AgentEval.Rendering.PdfSharp;
using AgentEval.Reporting;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();
services.AddAgentEvalPdfSharp();
using var host = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
using var scope = host.CreateScope();

var registry = scope.ServiceProvider.GetRequiredService<IPdfRendererRegistry>();
var pdf = registry.ForEvalResults("pdfsharp"); // IEvalResultRenderer
byte[] bytes = await pdf.RenderAsync(result, options, cancellationToken);
await File.WriteAllBytesAsync("report.pdf", bytes, cancellationToken);
```

Registration performs no layout, file writes, logo reads, or font initialization. Resolve the selected provider before expensive evaluation if PDF output is required. Installing a package alone does not change a host's registrations or an installed dotnet tool.

`IEvalResultRenderer` remains the generic result boundary. Rich family facades preserve their own summaries and metadata and delegate to the same registry:

```csharp
using AgentEval.Compliance.Gdpr.Reporting.Pdf;
using AgentEval.Compliance.EuAiAct.Reporting.Pdf;
using AgentEval.Evals.Agentic.Reporting.Pdf;
using AgentEval.RedTeam.Reporting.Pdf;

await new GDPRPdfRenderer(articles, registry).RenderAsync(gdprEvidence, "gdpr.pdf", cancellationToken);
await new EuAiActPdfRenderer(euArticles, registry).RenderAsync(euEvidence, "eu.pdf", cancellationToken);
await new AgenticPdfRenderer(registry).RenderAsync(agenticResult, "agentic.pdf", cancellationToken);
await new PdfReportGenerator(registry).SaveAsync(redTeamResult, "security.pdf", cancellationToken: cancellationToken);
```

These types stay in their original namespaces, and their original constructor signatures remain available to existing binaries. Parameterless construction is allowed for projecting/inspecting report facts, but rendering without a registry throws a configuration error. It does not create an engine implicitly. `CreateDocument` provides engine-neutral facts for a custom renderer or semantic inspection. This is a breaking generation change: applications must supply a registered provider before rendering. The former public `CrossPlatformFontResolver` is intentionally removed from the default package; consumers of that type must update and recompile, and hosts that customize PDFsharp fonts now configure their own resolver alongside the optional provider.

Compliance scenario content is redacted before providers see it. Known sensitive cases are redacted; when sensitivity metadata is unavailable, scenario text is withheld while IDs, scores and status remain available. Pass the appropriate article registry to disclose known-safe scenario input. Missing measurements remain missing; changing a renderer never recalculates a verdict.

For configured GDPR and EU AI Act judges, report attestations derive the evaluator key, instrument ID, version, effective locale and full digest from the observed leaf provenance. Those facts take precedence over a caller-supplied legacy `PromptVersions` map. Repeated identical instruments are deduplicated; differing identities for one result key are marked `MIXED`, and legacy leaves in a configured report are marked `UNIDENTIFIED`. Fully unconfigured legacy reports retain their previous map/default behavior. Attestation never copies captured system, user or retry text, and an instrument's locale does not establish that its rubric has been calibrated for that language.

## Select another engine

Implement the small `IPdfReportRenderer` interface in a customer-owned package. Register it before building a new host; adding registrations to an already-built host's service collection does not update that host:

```csharp
using AgentEval.Rendering.Pdf;
using AgentEval.Reporting;
using Microsoft.Extensions.DependencyInjection;

var customerServices = new ServiceCollection();
customerServices.AddAgentEvalPdfProvider<CustomerPdfRenderer>("customer");
using var customerHost = customerServices.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
using var customerScope = customerHost.CreateScope();
var customerRegistry = customerScope.ServiceProvider.GetRequiredService<IPdfRendererRegistry>();
var renderer = customerRegistry.ForEvalResults("customer");
byte[] customerPdf = await renderer.RenderAsync(result, options, cancellationToken);
```

The provider receives immutable `ReportDocument` sections, plain text, rectangular tables and portable presentation options. It does not receive family evaluators or vendor-specific layout objects. Treat report text as data; do not fetch URLs in evidence. Generic `IncludeProvenance=false` removes judge/model/prompt/token-cost facts before provider access.

One registered provider can be selected without an ID. Multiple providers require an explicit ID. Unknown IDs and conflicting registrations fail; registration order does not choose a winner. Repeating the same ID and implementation is idempotent. A rendering failure does not invoke another provider.

The registry is scoped, so a customer provider can depend on scoped host services. Resolve it from the current host scope. Provider implementations are transient unless the host explicitly registers that type with another lifetime.

## CLI usage

Benchmarks preserve their non-PDF outputs and no longer generate `report.pdf` automatically. `agenteval render` and `agenteval compliance render` render Markdown from the same verified source selection.

For PDF generation, install the separate companion from the local feed during development:

```powershell
dotnet tool install --global AgentEval.Cli.Pdf --version "<matching-package-version>" --add-source ./nupkgs
agenteval-pdf render --run .agenteval/aef/runs/<year>/<month>/<run-id> --output ./report.pdf
```

Replace `<matching-package-version>` with the exact version built into the local feed, including any prerelease suffix, and keep it aligned with the AgentEval/provider version you use. An explicit version selects that prerelease; without it, tool installation searches for stable releases by default. `--global` installs the command into your user's tool directory so the following `agenteval-pdf` command can be invoked directly. This local-feed example does not imply that the new package is already published.

The companion explicitly registers PDFsharp. It verifies the selected AEF run and renders captured original GDPR, EU AI Act or agentic report content without invoking an agent or judge. If a rich report was not captured, it fails. `--family generic` explicitly requests a summary of the available recorded AEF fields; it does not reconstruct missing rich content. The library's red-team facade remains available for an in-memory `RedTeamResult`; generic AEF rendering preserves recorded red-team fields without inventing an uncaptured executive envelope.

Derived output must be outside the sealed source run. Existing files require `--overwrite`, including files created by another writer while rendering is underway. Cancellation or rendering failure does not modify source evidence. Existing captured-PDF download/read paths do not require this tool or a PDF provider.

## Versions, fonts and licensing

The optional provider pins PDFsharp/MigraDoc **6.2.4**, the reviewed MIT version. PDFsharp **6.5.0** changed to AGPL/commercial licensing, so upgrading requires a separate review. The existing patched `System.Security.Cryptography.Pkcs` dependency floor remains in force. QuestPDF is removed from the default and optional PDFsharp graphs; no QuestPDF license setting is applied.

The provider embeds unchanged Lato Regular/Bold/Italic/BoldItalic fonts with their SIL Open Font License 1.1 text. It does not require Windows Arial or a Linux font installation. Fonts initialize only during rendering. A host's existing PDFsharp resolver is respected; that resolver is responsible for supporting the requested font family. PDFsharp font settings are process-wide, so custom hosts should configure them before their first render.

See `THIRD-PARTY-NOTICES.md` and each package's license files for the exact distributed components. Previous QuestPDF-based releases retain their own pinned package terms; this migration does not reinterpret those terms.
