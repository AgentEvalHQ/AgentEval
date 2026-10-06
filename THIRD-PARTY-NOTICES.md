# Third-Party Notices

AgentEval is licensed under the MIT License (see [LICENSE](LICENSE)). The published packages depend on, or redistribute, the third-party packages listed below. Each is used under its own licence.

This file is packed at the root of both published NuGet packages, `AgentEval` and `AgentEval.Cli`, so it travels with the binaries. The licence texts that must accompany redistributed copies are reproduced in [Licence texts](#licence-texts) at the end.

How this list was made: the package lists come from each packable project's `PackageReference` items, the project references it builds on, and the dependency graph NuGet restores for it (`obj/project.assets.json`), for the versions pinned in `Directory.Packages.props`. Each licence was read from that package version's `.nuspec` in the local NuGet cache (the `<license>` element, or `<licenseUrl>` where there is no `<license>`). Last checked: 2026-10-02.

## What is published

| Package | Kind | What it contains |
|---------|------|------------------|
| `AgentEval` | Library | Its own assembly plus 13 internal projects compiled into the same package: AgentEval.Abstractions, .Core, .Compliance.Core, .Compliance.Gdpr, .Compliance.EuAiAct, .DataLoaders, .Evals.Agentic, .Evals.Performance, .MAF, .Memory, .RedTeam, .RedTeam.Gatekeeper and .Rendering.Pdf. Third-party assemblies are **not** copied into the `.nupkg`: they are declared as NuGet dependencies, and NuGet restores them, with their own dependencies, into your project. |
| `AgentEval.Cli` | .NET tool (`agenteval`) | The CLI and every project it references, including AgentEval.MAF.CopilotStudio and, in the net10.0 build only, AgentEval.MissionControl. A .NET tool package carries its dependencies inside the `.nupkg`, so this package **redistributes** the third-party assemblies listed in both the library section and the CLI section below. |

`AgentEval.MAF.CopilotStudio` sets `IsPackable=true`, but the release workflow packs and publishes only `AgentEval` and `AgentEval.Cli`, so it is not on NuGet. Its code reaches users only inside `AgentEval.Cli`. See [AgentEval.MAF.CopilotStudio (not published)](#agentevalmafcopilotstudio-not-published).

Not published: `AgentEval.MAF.AgentHooks` (`IsPackable=false`), the samples, the tests, and the Mission Control web UI (`src/AgentEval.MissionControl.Spa`, an npm project). The release workflow does not build the web UI, so its npm dependencies are not in the CLI package.

This file covers packages. Datasets you download yourself, such as red-team packs fetched with `agenteval redteam --pack` or the LongMemEval dataset, carry their own licences.

## AgentEval (library)

Direct dependencies, declared in the package:

| Package | Version | Licence | Source |
|---------|---------|---------|--------|
| Microsoft.Agents.AI | 1.23.0 | MIT | https://github.com/microsoft/agent-framework |
| Microsoft.Agents.AI.Workflows | 1.23.0 | MIT | https://github.com/microsoft/agent-framework |
| Microsoft.Extensions.AI | 10.10.0 | MIT | https://github.com/dotnet/extensions |
| Microsoft.Extensions.AI.Evaluation.Quality | 10.10.0 | MIT | https://github.com/dotnet/extensions |
| Microsoft.Extensions.DependencyInjection | 10.0.8 | MIT | https://github.com/dotnet/dotnet |
| System.Numerics.Tensors | 10.0.12 | MIT | https://github.com/dotnet/dotnet |
| YamlDotNet | 16.3.0 | MIT | https://github.com/aaubry/YamlDotNet |
| JsonSchema.Net | 7.3.4 | MIT | https://github.com/json-everything/json-everything |
| PDFsharp-MigraDoc | 6.2.4 | MIT | https://github.com/empira/PDFsharp |
| OpenTelemetry.Api | 1.18.0 | Apache-2.0 | https://github.com/open-telemetry/opentelemetry-dotnet |
| QuestPDF | 2026.2.4 | QuestPDF licence: Community MIT licence or a paid licence, see [QuestPDF](#questpdf) | https://github.com/QuestPDF/library |

OpenTelemetry.Api is referenced so that the version consumers resolve stays at or above the release that fixes GHSA-g94r-2vxg-569j. AgentEval's own code does not call it.

Transitive dependencies. The versions are the ones this repository restores with central transitive pinning; your project may resolve different versions.

| Package | Version | Licence |
|---------|---------|---------|
| Google.Protobuf (via Microsoft.ML.Tokenizers) | 3.30.2 | BSD-3-Clause |
| Humanizer.Core (via JsonPointer.Net) | 2.14.1 | MIT |
| Json.More.Net | 2.1.1 | MIT |
| JsonPointer.Net | 5.3.1 | MIT |
| Microsoft.Agents.AI.Abstractions | 1.23.0 | MIT |
| Microsoft.ML.Tokenizers | 2.0.0 | MIT |
| PDFsharp | 6.2.4 | MIT |
| Microsoft.Extensions.AI.Abstractions | 10.10.1 | MIT |
| Microsoft.Extensions.AI.Evaluation | 10.10.0 | MIT |
| Microsoft.Extensions.Compliance.Abstractions | 10.10.0 | MIT |
| Microsoft.Extensions.VectorData.Abstractions | 10.10.0 | MIT |
| Microsoft.Extensions.Caching.Abstractions, Microsoft.Extensions.DependencyInjection.Abstractions, Microsoft.Extensions.FileSystemGlobbing, Microsoft.Extensions.Logging.Abstractions, Microsoft.Extensions.Primitives | 10.0.12 | MIT |
| Microsoft.Extensions.ObjectPool | 8.0.31 / 9.0.20 / 10.0.12 (per target framework) | MIT |
| System.Diagnostics.DiagnosticSource, System.IO.Hashing, System.IO.Pipelines, System.Text.Encodings.Web, System.Text.Json, System.Threading.Channels | 10.0.12 | MIT |
| System.Security.Cryptography.Pkcs | 8.0.1 | MIT |

## AgentEval.Cli (.NET tool)

The tool contains everything listed for the library, plus the packages below. Where the .NET runtime already provides an assembly, the installed tool may load the runtime's copy instead of the one from the package.

Direct dependencies of the CLI and of the projects it references:

| Package | Version | Licence | Source |
|---------|---------|---------|--------|
| System.CommandLine | 2.0.3 | MIT | https://github.com/dotnet/command-line-api |
| Azure.AI.OpenAI | 2.8.0-beta.1 | MIT | https://github.com/Azure/azure-sdk-for-net |
| Microsoft.Extensions.AI.OpenAI | 10.10.1 | MIT | https://github.com/dotnet/extensions |
| Microsoft.Agents.CopilotStudio.Client | 1.3.171-beta | MIT, by URL only (see note) | https://github.com/microsoft/Agents-for-net |
| Microsoft.Agents.Core | 1.3.171-beta | MIT, by URL only (see note) | https://github.com/microsoft/Agents-for-net |
| Microsoft.Identity.Client | 4.89.0 | MIT | https://github.com/AzureAD/microsoft-authentication-library-for-dotnet |
| Microsoft.Identity.Client.Extensions.Msal | 4.89.0 | MIT | https://github.com/AzureAD/microsoft-authentication-library-for-dotnet |
| Microsoft.Extensions.Http | 10.0.8 | MIT | https://github.com/dotnet/dotnet |
| HotChocolate.AspNetCore (net10.0 build only, via Mission Control) | 16.0.0 | MIT | https://github.com/ChilliCream/graphql-platform |

Note: the two Microsoft.Agents packages carry no licence expression and no licence file. Their `.nuspec` gives only a `licenseUrl` that points to the MIT licence of https://github.com/microsoft/Agents-for-net.

Transitive dependencies, both builds:

| Package | Version | Licence |
|---------|---------|---------|
| Azure.Core | 1.50.0 | MIT |
| OpenAI | 2.14.0 | MIT |
| System.ClientModel | 1.15.0 | MIT |
| Microsoft.IdentityModel.Abstractions | 8.14.0 | MIT |
| System.Security.Cryptography.ProtectedData | 4.5.0 | MIT (by `licenseUrl`; the package includes `LICENSE.TXT`) |
| Microsoft.Bcl.AsyncInterfaces | 8.0.0 | MIT |
| Microsoft.Extensions.Configuration, Microsoft.Extensions.Configuration.Binder, Microsoft.Extensions.Diagnostics, Microsoft.Extensions.Logging, Microsoft.Extensions.Options.ConfigurationExtensions | 10.0.8 | MIT |
| Microsoft.Extensions.Configuration.Abstractions, Microsoft.Extensions.Diagnostics.Abstractions, Microsoft.Extensions.FileProviders.Abstractions, Microsoft.Extensions.Hosting.Abstractions, Microsoft.Extensions.Options, System.Memory.Data | 10.0.10 | MIT |
| System.Net.ServerSentEvents | 10.0.11 | MIT |

Transitive dependencies, net10.0 build only (Mission Control):

| Package | Version | Licence |
|---------|---------|---------|
| 41 further HotChocolate.* packages and 5 GreenDonut.* packages | 16.0.0 | MIT |
| Yarp.ReverseProxy (via ChilliCream.Nitro.App) | 2.3.0 | MIT |
| ChilliCream.Nitro.App (via HotChocolate.AspNetCore) | 30.0.2 | ChilliCream License 1.0, see [ChilliCream Nitro](#chillicream-nitro) |

## AgentEval.MAF.CopilotStudio (not published)

This package is not on NuGet (see above). If it were packed as configured, it would contain AgentEval.Abstractions, AgentEval.Core and AgentEval.MAF, and would declare these dependencies: Microsoft.Agents.AI, Microsoft.Agents.AI.Workflows, Microsoft.Extensions.AI.Evaluation.Quality, OpenTelemetry.Api, Microsoft.Agents.CopilotStudio.Client, Microsoft.Agents.Core, Microsoft.Identity.Client, Microsoft.Identity.Client.Extensions.Msal and Microsoft.Extensions.Http, at the versions above. Its restored dependency graph contains no licence that is not already listed in this file.

## Licences that need attention

### QuestPDF

QuestPDF is not under a plain MIT licence.

- **The licence in the package.** QuestPDF 2026.2.4 ships `PackageLicense.md`, and its package metadata sets `requireLicenseAcceptance`. That file offers the "QuestPDF Community MIT License" free of charge to companies with annual gross revenue under USD 1M; to open-source projects, charitable organisations, and evaluation, learning or training use; and to anyone using QuestPDF as a transitive dependency. Other companies with annual gross revenue over USD 1M need a paid Professional licence (up to 10 developers using QuestPDF) or Enterprise licence (more than 10).
- **The current terms.** The terms published at https://www.questpdf.com/license/ (Community License version 3.0, effective 6 July 2026, read on 2026-10-02) differ from the file in the package. Among other changes, they state that public-sector entities and publicly traded companies are not eligible for the Community licence regardless of revenue, and they describe the transitive-dependency case as users whose own code does not directly call QuestPDF APIs. Read the current terms yourself; this summary is not legal advice.
- **What AgentEval sets.** Four renderers run `QuestPDF.Settings.License ??= LicenseType.Community` in their static constructors: `PdfEvalResultRenderer` (AgentEval.Rendering.Pdf), `GDPRPdfRenderer`, `EuAiActPdfRenderer` and `AgenticPdfRenderer`. That line declares which QuestPDF licence applies; it does not grant one. It sets Community only when no licence type has been set yet. If your code sets `QuestPDF.Settings.License` (for example to `LicenseType.Professional`) before the first of these renderers is used, your value is kept. The setting is process-wide: if your code has not set it by then, the Community value also applies to your own QuestPDF use in that process.
- **When QuestPDF runs.** In the library, only when you use one of those four renderers. In the CLI, the `bench` commands for gdpr, eu-ai-act, agentic, owasp, mitre, nist and perf write a `report.pdf` on every run, with no option to turn it off, and the report-rendering commands write PDFs as well.
- **What you need.** Whether you need a paid licence depends on QuestPDF's terms, not on the value AgentEval sets. An organisation that is not eligible for the Community licence (for example, one above the revenue threshold) and whose use is not covered by the transitive-dependency terms needs its own QuestPDF Professional or Enterprise licence before it produces PDF output with AgentEval.

QuestPDF also bundles native components (Skia, HarfBuzz, qpdf, libjpeg-turbo, libpng, libwebp, zlib, expat, wuffs, libgrapheme and others) and the Lato font (SIL Open Font License 1.1). Their licence texts ship inside the QuestPDF package, in `ExternalDependencyLicenses/` and `LatoFont/OFL.txt`. QuestPDF's build targets copy the `LatoFont/` folder, `OFL.txt` included, into the build output. The native libraries (the package's `runtimes/` assets) reach the build output as well, and with it the `AgentEval.Cli` tool package. `ExternalDependencyLicenses/` is not copied, and this file does not reproduce those texts; read them in the QuestPDF package.

### ChilliCream Nitro

`ChilliCream.Nitro.App` 30.0.2 is a dependency of HotChocolate.AspNetCore. Mission Control uses it to serve the Nitro GraphQL IDE at `/graphql`, and `AgentEval.Cli` redistributes it in its net10.0 build.

It is licensed under the ChilliCream License 1.0 (https://chillicream.com/licensing/chillicream-license), a source-available licence, not an OSI-approved open-source licence. It permits use, copying and distribution, subject to conditions that include:

- anyone who gets a copy of any part of the software must also get a copy of the licence terms;
- licensing, copyright and other notices must not be removed or obscured;
- licence-key functionality must not be moved, changed, disabled or circumvented.

The full text ships in the package as `LICENSE`. Because `AgentEval.Cli` passes a copy of the software on, that text is reproduced in full under [ChilliCream License 1.0](#chillicream-license-10) below, and this file ships inside the `AgentEval.Cli` package.

Mission Control sets two Nitro options, `ServeMode` (to serve the bundled copy of the IDE) and `DisableTelemetry`; see [PRIVACY.md](PRIVACY.md#mission-control). It does not change Nitro's code.

### Apache-2.0 and BSD-3-Clause

| Package | Licence | Copyright (from the `.nuspec`) | Licence text |
|---------|---------|--------------------------------|--------------|
| OpenTelemetry.Api 1.18.0 | Apache-2.0 | Copyright The OpenTelemetry Authors | [Apache License 2.0](#apache-license-20) below (the package also includes `LICENSE.TXT` and `THIRD-PARTY-NOTICES.TXT`) |
| Google.Protobuf 3.30.2 | BSD-3-Clause | Copyright 2015, Google Inc. | [BSD-3-Clause (Google.Protobuf)](#bsd-3-clause-googleprotobuf) below |

## Build-time only (not in any package)

Referenced with `PrivateAssets="All"`, so they are used during the build and ship in no package.

| Package | Version | Licence | Source |
|---------|---------|---------|--------|
| Microsoft.SourceLink.GitHub (with Microsoft.SourceLink.Common and Microsoft.Build.Tasks.Git) | 10.0.401 | MIT | https://github.com/dotnet/dotnet |

## Not published

### Experimental adapter

`AgentEval.MAF.AgentHooks` is not packaged (`IsPackable=false`).

| Package | Version | Licence | Source |
|---------|---------|---------|--------|
| ResponsibleAI.AgentHooks | 0.1.0-alpha.5 | MIT | https://github.com/responsibleai/agent-hooks |

### Tests

| Package | Version | Licence | Source |
|---------|---------|---------|--------|
| Microsoft.NET.Test.Sdk | 18.3.0 | MIT | https://github.com/microsoft/vstest |
| xunit | 2.9.3 | Apache-2.0 | https://github.com/xunit/xunit |
| xunit.runner.visualstudio | 2.8.2 | Apache-2.0 | https://github.com/xunit/visualstudio.xunit |
| Verify.Xunit | 28.8.1 | MIT | https://github.com/VerifyTests/Verify |
| coverlet.collector | 8.0.0 | MIT | https://github.com/coverlet-coverage/coverlet |
| PdfPig | 0.1.10 | Apache-2.0 | https://github.com/UglyToad/PdfPig |
| Microsoft.AspNetCore.Mvc.Testing | 10.0.0 | MIT | https://github.com/dotnet/dotnet |
| Microsoft.Extensions.TimeProvider.Testing | 10.7.0 | MIT | https://github.com/dotnet/extensions |
| Microsoft.Extensions.Hosting.Abstractions | 10.0.10 | MIT | https://github.com/dotnet/dotnet |
| Microsoft.Agents.AI.A2A | 1.23.0-preview.260928.1 | MIT | https://github.com/microsoft/agent-framework |
| Microsoft.Agents.AI.AgentHooks | 1.23.0-alpha.260928.1 | MIT | https://github.com/microsoft/agent-framework |

### Samples

| Package | Version | Licence | Source |
|---------|---------|---------|--------|
| Microsoft.Agents.AI.OpenAI | 1.23.0 | MIT | https://github.com/microsoft/agent-framework |
| Microsoft.Agents.AI.Harness | 1.23.0 | MIT | https://github.com/microsoft/agent-framework |
| Microsoft.Agents.AI.Workflows.Generators | 1.23.0 | MIT | https://github.com/microsoft/agent-framework |
| Microsoft.Agents.AI.Foundry | 1.23.0-preview.260928.1 | MIT | https://github.com/microsoft/agent-framework |
| Microsoft.Agents.AI.A2A | 1.23.0-preview.260928.1 | MIT | https://github.com/microsoft/agent-framework |
| Azure.AI.Projects | 3.0.0-beta.3 | MIT | https://github.com/Azure/azure-sdk-for-net |
| Azure.Identity | 1.18.0 | MIT | https://github.com/Azure/azure-sdk-for-net |
| ModelContextProtocol.Core | 1.4.0 | Apache-2.0 | https://github.com/modelcontextprotocol/csharp-sdk |
| Microsoft.SemanticKernel, Microsoft.SemanticKernel.Agents.Core (AgentEval.NuGetConsumer only) | 1.72.0 | MIT | https://github.com/microsoft/semantic-kernel |

## Summary

Licences found in what is published:

- **MIT**: most packages.
- **Apache-2.0**: OpenTelemetry.Api, in both published packages.
- **BSD-3-Clause**: Google.Protobuf, a transitive dependency of both published packages.
- **QuestPDF licence** (Community MIT licence or paid, depending on who uses it and how): QuestPDF, in both published packages.
- **ChilliCream License 1.0** (source-available): ChilliCream.Nitro.App, in the net10.0 build of `AgentEval.Cli` only.

No package in these restored dependency graphs declares a GPL, LGPL or AGPL licence. QuestPDF bundles the Lato font under the SIL Open Font License 1.1.

## Licence texts

The texts below are copied verbatim from the files named with each one.

### MIT

Applies to every package listed above as MIT. The permission notice, copied from this repository's [LICENSE](LICENSE):

```text
Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

Copyright notices of the MIT-licensed packages that the published packages declare or carry, as given in each package's `.nuspec` (`<copyright>`):

| Packages | Copyright notice |
|----------|------------------|
| `Microsoft.*`, `System.*` and `Azure.*` packages, and Yarp.ReverseProxy | © Microsoft Corporation. All rights reserved. |
| OpenAI | Copyright (c) 2026 OpenAI (https://openai.com) |
| `HotChocolate.*` and `GreenDonut.*` (net10.0 build of `AgentEval.Cli` only) | Copyright © 2021 ChilliCream Inc. |
| YamlDotNet | Copyright (c) Antoine Aubry and contributors |
| Humanizer.Core | Copyright (c) .NET Foundation and Contributors |
| PDFsharp, PDFsharp-MigraDoc | © 2026 empira |
| JsonSchema.Net, JsonPointer.Net, Json.More.Net | None given in the `.nuspec`; see https://github.com/json-everything/json-everything |

QuestPDF is not in this table: its terms are summarised under [QuestPDF](#questpdf), and its own licence file, `PackageLicense.md`, ships in the QuestPDF package.

### Apache License 2.0

Applies to OpenTelemetry.Api (Copyright The OpenTelemetry Authors). Copied from `LICENSE.TXT` in the OpenTelemetry.Api 1.18.0 package, up to the end of the terms:

```text
                                 Apache License
                           Version 2.0, January 2004
                        http://www.apache.org/licenses/

   TERMS AND CONDITIONS FOR USE, REPRODUCTION, AND DISTRIBUTION

   1. Definitions.

      "License" shall mean the terms and conditions for use, reproduction,
      and distribution as defined by Sections 1 through 9 of this document.

      "Licensor" shall mean the copyright owner or entity authorized by
      the copyright owner that is granting the License.

      "Legal Entity" shall mean the union of the acting entity and all
      other entities that control, are controlled by, or are under common
      control with that entity. For the purposes of this definition,
      "control" means (i) the power, direct or indirect, to cause the
      direction or management of such entity, whether by contract or
      otherwise, or (ii) ownership of fifty percent (50%) or more of the
      outstanding shares, or (iii) beneficial ownership of such entity.

      "You" (or "Your") shall mean an individual or Legal Entity
      exercising permissions granted by this License.

      "Source" form shall mean the preferred form for making modifications,
      including but not limited to software source code, documentation
      source, and configuration files.

      "Object" form shall mean any form resulting from mechanical
      transformation or translation of a Source form, including but
      not limited to compiled object code, generated documentation,
      and conversions to other media types.

      "Work" shall mean the work of authorship, whether in Source or
      Object form, made available under the License, as indicated by a
      copyright notice that is included in or attached to the work
      (an example is provided in the Appendix below).

      "Derivative Works" shall mean any work, whether in Source or Object
      form, that is based on (or derived from) the Work and for which the
      editorial revisions, annotations, elaborations, or other modifications
      represent, as a whole, an original work of authorship. For the purposes
      of this License, Derivative Works shall not include works that remain
      separable from, or merely link (or bind by name) to the interfaces of,
      the Work and Derivative Works thereof.

      "Contribution" shall mean any work of authorship, including
      the original version of the Work and any modifications or additions
      to that Work or Derivative Works thereof, that is intentionally
      submitted to Licensor for inclusion in the Work by the copyright owner
      or by an individual or Legal Entity authorized to submit on behalf of
      the copyright owner. For the purposes of this definition, "submitted"
      means any form of electronic, verbal, or written communication sent
      to the Licensor or its representatives, including but not limited to
      communication on electronic mailing lists, source code control systems,
      and issue tracking systems that are managed by, or on behalf of, the
      Licensor for the purpose of discussing and improving the Work, but
      excluding communication that is conspicuously marked or otherwise
      designated in writing by the copyright owner as "Not a Contribution."

      "Contributor" shall mean Licensor and any individual or Legal Entity
      on behalf of whom a Contribution has been received by Licensor and
      subsequently incorporated within the Work.

   2. Grant of Copyright License. Subject to the terms and conditions of
      this License, each Contributor hereby grants to You a perpetual,
      worldwide, non-exclusive, no-charge, royalty-free, irrevocable
      copyright license to reproduce, prepare Derivative Works of,
      publicly display, publicly perform, sublicense, and distribute the
      Work and such Derivative Works in Source or Object form.

   3. Grant of Patent License. Subject to the terms and conditions of
      this License, each Contributor hereby grants to You a perpetual,
      worldwide, non-exclusive, no-charge, royalty-free, irrevocable
      (except as stated in this section) patent license to make, have made,
      use, offer to sell, sell, import, and otherwise transfer the Work,
      where such license applies only to those patent claims licensable
      by such Contributor that are necessarily infringed by their
      Contribution(s) alone or by combination of their Contribution(s)
      with the Work to which such Contribution(s) was submitted. If You
      institute patent litigation against any entity (including a
      cross-claim or counterclaim in a lawsuit) alleging that the Work
      or a Contribution incorporated within the Work constitutes direct
      or contributory patent infringement, then any patent licenses
      granted to You under this License for that Work shall terminate
      as of the date such litigation is filed.

   4. Redistribution. You may reproduce and distribute copies of the
      Work or Derivative Works thereof in any medium, with or without
      modifications, and in Source or Object form, provided that You
      meet the following conditions:

      (a) You must give any other recipients of the Work or
          Derivative Works a copy of this License; and

      (b) You must cause any modified files to carry prominent notices
          stating that You changed the files; and

      (c) You must retain, in the Source form of any Derivative Works
          that You distribute, all copyright, patent, trademark, and
          attribution notices from the Source form of the Work,
          excluding those notices that do not pertain to any part of
          the Derivative Works; and

      (d) If the Work includes a "NOTICE" text file as part of its
          distribution, then any Derivative Works that You distribute must
          include a readable copy of the attribution notices contained
          within such NOTICE file, excluding those notices that do not
          pertain to any part of the Derivative Works, in at least one
          of the following places: within a NOTICE text file distributed
          as part of the Derivative Works; within the Source form or
          documentation, if provided along with the Derivative Works; or,
          within a display generated by the Derivative Works, if and
          wherever such third-party notices normally appear. The contents
          of the NOTICE file are for informational purposes only and
          do not modify the License. You may add Your own attribution
          notices within Derivative Works that You distribute, alongside
          or as an addendum to the NOTICE text from the Work, provided
          that such additional attribution notices cannot be construed
          as modifying the License.

      You may add Your own copyright statement to Your modifications and
      may provide additional or different license terms and conditions
      for use, reproduction, or distribution of Your modifications, or
      for any such Derivative Works as a whole, provided Your use,
      reproduction, and distribution of the Work otherwise complies with
      the conditions stated in this License.

   5. Submission of Contributions. Unless You explicitly state otherwise,
      any Contribution intentionally submitted for inclusion in the Work
      by You to the Licensor shall be under the terms and conditions of
      this License, without any additional terms or conditions.
      Notwithstanding the above, nothing herein shall supersede or modify
      the terms of any separate license agreement you may have executed
      with Licensor regarding such Contributions.

   6. Trademarks. This License does not grant permission to use the trade
      names, trademarks, service marks, or product names of the Licensor,
      except as required for reasonable and customary use in describing the
      origin of the Work and reproducing the content of the NOTICE file.

   7. Disclaimer of Warranty. Unless required by applicable law or
      agreed to in writing, Licensor provides the Work (and each
      Contributor provides its Contributions) on an "AS IS" BASIS,
      WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or
      implied, including, without limitation, any warranties or conditions
      of TITLE, NON-INFRINGEMENT, MERCHANTABILITY, or FITNESS FOR A
      PARTICULAR PURPOSE. You are solely responsible for determining the
      appropriateness of using or redistributing the Work and assume any
      risks associated with Your exercise of permissions under this License.

   8. Limitation of Liability. In no event and under no legal theory,
      whether in tort (including negligence), contract, or otherwise,
      unless required by applicable law (such as deliberate and grossly
      negligent acts) or agreed to in writing, shall any Contributor be
      liable to You for damages, including any direct, indirect, special,
      incidental, or consequential damages of any character arising as a
      result of this License or out of the use or inability to use the
      Work (including but not limited to damages for loss of goodwill,
      work stoppage, computer failure or malfunction, or any and all
      other commercial damages or losses), even if such Contributor
      has been advised of the possibility of such damages.

   9. Accepting Warranty or Additional Liability. While redistributing
      the Work or Derivative Works thereof, You may choose to offer,
      and charge a fee for, acceptance of support, warranty, indemnity,
      or other liability obligations and/or rights consistent with this
      License. However, in accepting such obligations, You may act only
      on Your own behalf and on Your sole responsibility, not on behalf
      of any other Contributor, and only if You agree to indemnify,
      defend, and hold each Contributor harmless for any liability
      incurred by, or claims asserted against, such Contributor by reason
      of your accepting any such warranty or additional liability.

   END OF TERMS AND CONDITIONS
```

### BSD-3-Clause (Google.Protobuf)

Applies to Google.Protobuf 3.30.2. The package carries no licence file, so this is the `LICENSE` file of the protobuf repository at tag `v30.2` (https://github.com/protocolbuffers/protobuf/blob/v30.2/LICENSE):

```text
Copyright 2008 Google Inc.  All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are
met:

    * Redistributions of source code must retain the above copyright
notice, this list of conditions and the following disclaimer.
    * Redistributions in binary form must reproduce the above
copyright notice, this list of conditions and the following disclaimer
in the documentation and/or other materials provided with the
distribution.
    * Neither the name of Google Inc. nor the names of its
contributors may be used to endorse or promote products derived from
this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
"AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT
OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

Code generated by the Protocol Buffer compiler is owned by the owner
of the input file used when generating it.  This code is not
standalone and requires a support library to be linked with it.  This
support library is itself covered by the above license.
```

### ChilliCream License 1.0

Applies to ChilliCream.Nitro.App 30.0.2. Copied from the `LICENSE` file in that package:

```text
Copyright (c) ChilliCream, Inc.

Source code in this repository is covered by the ChilliCream License 1.0 license as designated by a licensing file in a subdirectory or file header. The default throughout the repository is a license under the ChilliCream License 1.0, unless a file header or a licensing file in a subdirectory specifies another license.

---

# ChilliCream License 1.0

URL: https://chillicream.com/licensing/chillicream-license

## Acceptance

By using the software, you agree to all of the terms and conditions below.

## Copyright License

The licensor grants you a non-exclusive, royalty-free, worldwide,
non-sublicensable, non-transferable license to use, copy, distribute, make
available, and prepare derivative works of the software, in each case subject to
the limitations and conditions below.

## Limitations

You may not move, change, disable, or circumvent the license key functionality
in the software, and you may not remove or obscure any functionality in the
software that is protected by the license key.

You may not alter, remove, or obscure any licensing, copyright, or other notices
of the licensor in the software. Any use of the licensor’s trademarks is subject
to applicable law.

## Patents

The licensor grants you a license, under any patent claims the licensor can
license, or becomes able to license, to make, have made, use, sell, offer for
sale, import and have imported the software, in each case subject to the
limitations and conditions in this license. This license does not cover any
patent claims that you cause to be infringed by modifications or additions to
the software. If you or your company make any written claim that the software
infringes or contributes to infringement of any patent, your patent license for
the software granted under these terms ends immediately. If your company makes
such a claim, your patent license ends immediately for work on behalf of your
company.

## Notices

You must ensure that anyone who gets a copy of any part of the software from you
also gets a copy of these terms.

If you modify the software, you must include in any modified copies of the
software prominent notices stating that you have modified the software.

## No Other Rights

These terms do not imply any licenses other than those expressly granted in
these terms.

## Termination

If you use the software in violation of these terms, such use is not licensed,
and your licenses will automatically terminate. If the licensor provides you
with a notice of your violation, and you cease all violation of this license no
later than 30 days after you receive that notice, your licenses will be
reinstated retroactively. However, if you violate these terms after such
reinstatement, any additional violation of these terms will cause your licenses
to terminate automatically and permanently.

## No Liability

_As far as the law allows, the software comes as is, without any warranty or
condition, and the licensor will not be liable to you for any damages arising
out of these terms or the use or nature of the software, under any kind of
legal claim._

## Definitions

The **licensor** is the entity offering these terms, and the **software** is the
software the licensor makes available under these terms, including any portion
of it.

**you** refers to the individual or entity agreeing to these terms.

**your company** is any legal entity, sole proprietorship, or other kind of
organization that you work for, plus all organizations that have control over,
are under the control of, or are under common control with that
organization. **control** means ownership of substantially all the assets of an
entity, or the power to direct its management and policies by vote, contract, or
otherwise. Control can be direct or indirect.

**your licenses** are all the licenses granted to you for the software under
these terms.

**use** means anything you do with the software requiring one of your licenses.

**trademark** means trademarks, service marks, and similar rights.
```

---

*Update this file when dependencies change. `Directory.Packages.props` holds the pinned versions.*
