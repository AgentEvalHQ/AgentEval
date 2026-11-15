// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
using AgentEval.Reporting;
using Microsoft.Extensions.DependencyInjection;

namespace AgentEval.Rendering.PdfSharp.Tests
{
    internal static class PdfTestRegistry
    {
        internal static IPdfRendererRegistry Create() => new ServiceCollection().AddAgentEvalPdfSharp()
            .BuildServiceProvider().GetRequiredService<IPdfRendererRegistry>();
    }
}

namespace AgentEval.Tests.Rendering.Pdf
{
    [CollectionDefinition(Name, DisableParallelization = true)]
    public sealed class PdfProviderCollection { public const string Name = "PdfProvider"; }
}
