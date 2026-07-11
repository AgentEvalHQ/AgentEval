// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.CommandLine;

namespace AgentEval.Cli.Commands.Gatekeeper;

/// <summary>The shared model-source flags for <c>inspect</c> (judge gates) and <c>calibrate</c>.</summary>
internal sealed class ModelOptions
{
    public Option<string?> Model { get; } = new("--model") { Description = "Model / deployment name (needs a backing endpoint or the AZURE_OPENAI_* env trio)" };
    public Option<string?> Endpoint { get; } = new("--endpoint") { Description = "OpenAI-compatible endpoint URL (with --model), or the Azure endpoint (with --azure)" };
    public Option<bool> Azure { get; } = new("--azure") { Description = "Use Azure OpenAI (with --deployment-name)" };
    public Option<string?> Deployment { get; } = new("--deployment-name") { Description = "Azure deployment name (with --azure)" };
    public Option<string?> ApiKey { get; } = new("--api-key") { Description = "API key (else read from the provider's usual env var)" };

    public void AddTo(Command cmd)
    {
        cmd.Options.Add(Model);
        cmd.Options.Add(Endpoint);
        cmd.Options.Add(Azure);
        cmd.Options.Add(Deployment);
        cmd.Options.Add(ApiKey);
    }

    public (bool Azure, string? Endpoint, string? Deployment, string? Model, string? ApiKey) Read(ParseResult p) =>
        (p.GetValue(Azure), p.GetValue(Endpoint), p.GetValue(Deployment), p.GetValue(Model), p.GetValue(ApiKey));
}
