// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.CommandLine;

namespace AgentEval.Cli.Commands.Gatekeeper;

/// <summary>The shared model-source flags for <c>inspect</c> (judge gates) and <c>calibrate</c>.</summary>
internal sealed class ModelOptions
{
    public Option<string?> Model { get; } = new("--model") { Description = "Model name (required for OpenAI-compatible endpoints; the Azure path uses --deployment-name instead)" };
    public Option<string?> Endpoint { get; } = new("--endpoint") { Description = "OpenAI-compatible endpoint URL (with --model), or the Azure endpoint (with --azure)" };
    public Option<bool> Azure { get; } = new("--azure") { Description = "Use Azure OpenAI (needs --deployment-name and an endpoint via --endpoint or AZURE_OPENAI_ENDPOINT)" };
    public Option<string?> Deployment { get; } = new("--deployment-name") { Description = "Azure OpenAI deployment name (required when using --azure)" };
    public Option<string?> ApiKey { get; } = new("--api-key") { Description = "API key (or set OPENAI_API_KEY / AZURE_OPENAI_API_KEY env var)" };

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
