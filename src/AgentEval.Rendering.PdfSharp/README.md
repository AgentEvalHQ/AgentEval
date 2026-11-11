# AgentEval PDFsharp provider

Optional PDF generation for AgentEval. This source package uses reviewed PDFsharp/MigraDoc **6.2.4 (MIT)** and bundles unchanged Lato fonts under SIL OFL 1.1. It does not use QuestPDF. PDFsharp 6.5 has different licensing; do not treat an upgrade as license-neutral.

```csharp
using AgentEval.Rendering.Pdf;
using AgentEval.Rendering.PdfSharp;
using AgentEval.Reporting;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();
services.AddAgentEvalPdfSharp();
using var host = services.BuildServiceProvider();
var pdf = host.GetRequiredService<IPdfRendererRegistry>().ForEvalResults("pdfsharp");
byte[] bytes = await pdf.RenderAsync(result, options);
```

Installation does not register a provider, and registration does not generate files or initialize fonts. Explicitly request a render. The host may register its own `IPdfReportRenderer` with `AddAgentEvalPdfProvider<T>("customer")`; select an ID when more than one provider is registered.

The provider respects an existing PDFsharp font resolver; that resolver must support the selected font family. Otherwise the provider uses its bundled font bytes without requiring an operating-system font installation. No URL in report evidence is fetched.

During development, build and pack from source; publication is a separate release action. See `docs/pdf-providers.md` for migration and optional CLI usage.
