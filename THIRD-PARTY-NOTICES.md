# Third-Party Notices

AgentEval uses the following third-party libraries. Each is used under its respective license.

## Production Dependencies (shipped in NuGet package)

| Package | Version | License | URL |
|---------|---------|---------|-----|
| Microsoft.Agents.AI | 1.1.0 | MIT | https://github.com/microsoft/agents |
| Microsoft.Agents.AI.OpenAI | 1.1.0 | MIT | https://github.com/microsoft/agents |
| Microsoft.Agents.AI.Workflows | 1.1.0 | MIT | https://github.com/microsoft/agents |
| Microsoft.Extensions.AI | 10.4.0 | MIT | https://github.com/dotnet/extensions |
| Microsoft.Extensions.AI.OpenAI | 10.4.0 | MIT | https://github.com/dotnet/extensions |
| Microsoft.Extensions.AI.Evaluation.Quality | 10.4.0 | MIT | https://github.com/dotnet/extensions |
| Microsoft.Extensions.DependencyInjection | 9.0.0 | MIT | https://github.com/dotnet/runtime |
| Microsoft.Extensions.Hosting.Abstractions | 9.0.0 | MIT | https://github.com/dotnet/runtime |
| Azure.AI.OpenAI | 2.8.0-beta.1 | MIT | https://github.com/Azure/azure-sdk-for-net |
| Azure.Identity | 1.18.0 | MIT | https://github.com/Azure/azure-sdk-for-net |
| System.Numerics.Tensors | 10.0.4 | MIT | https://github.com/dotnet/runtime |
| System.CommandLine | 2.0.3 | MIT | https://github.com/dotnet/command-line-api |
| YamlDotNet | 16.3.0 | MIT | https://github.com/aaubry/YamlDotNet |
| PdfSharp-MigraDoc | 6.2.4 | MIT | https://github.com/empira/PDFsharp |

## Build/Tooling Dependencies (not shipped)

| Package | Version | License | URL |
|---------|---------|---------|-----|
| Microsoft.SourceLink.GitHub | 8.0.0 | MIT | https://github.com/dotnet/sourcelink |

## Test Dependencies (not shipped)

| Package | Version | License | URL |
|---------|---------|---------|-----|
| Microsoft.NET.Test.Sdk | 18.3.0 | MIT | https://github.com/microsoft/vstest |
| xunit | 2.9.3 | Apache-2.0 | https://github.com/xunit/xunit |
| xunit.runner.visualstudio | 2.8.2 | Apache-2.0 | https://github.com/xunit/visualstudio.xunit |
| Verify.Xunit | 28.8.1 | MIT | https://github.com/VerifyTests/Verify |
| coverlet.collector | 8.0.0 | MIT | https://github.com/coverlet-coverage/coverlet |

## Summary

All production dependencies are licensed under **MIT**. Test-only dependencies include Apache-2.0 (xunit), which is compatible with MIT and is not distributed with the AgentEval package.

No copyleft-licensed dependencies are used.

---

*This file should be updated when dependencies change. See `Directory.Packages.props` for current versions.*
