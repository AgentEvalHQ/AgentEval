// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text;
using AgentEval.Cli.Infrastructure;
using Microsoft.Extensions.AI;

namespace AgentEval.Cli.Commands.Gatekeeper;

/// <summary>The resolved model client + a stable fingerprint (for the calibration certificate), or an exit code.</summary>
internal sealed record ModelResolution(IChatClient? Client, string? Fingerprint, int ExitCode);

/// <summary>
/// Builds the judge <see cref="IChatClient"/> from CLI flags, mirroring <c>eval</c>'s branching: explicit Azure,
/// explicit OpenAI-compatible endpoint, or the <c>AZURE_OPENAI_*</c> env trio. Also computes a model
/// <b>fingerprint</b> (provider + host + model/deployment, hashed) so a calibration certificate is tied to the exact
/// model it certifies — inline readiness is model-specific.
/// </summary>
internal static class GatekeeperModelResolver
{
    public static ModelResolution Resolve(
        bool azure, string? endpoint, string? deploymentName, string? model, string? apiKey, TextWriter stderr)
    {
        try
        {
            if (azure)
            {
                if (string.IsNullOrWhiteSpace(deploymentName))
                {
                    stderr.WriteLine("  Error: --azure requires --deployment-name <deployment>.");
                    return new(null, null, ExitCodes.UsageError);
                }

                var c = EndpointFactory.CreateAzure(endpoint, deploymentName!, apiKey);
                return new(c, Fingerprint("azure", endpoint ?? Env("AZURE_OPENAI_ENDPOINT"), deploymentName!), ExitCodes.Success);
            }

            if (!string.IsNullOrWhiteSpace(endpoint))
            {
                if (string.IsNullOrWhiteSpace(model))
                {
                    stderr.WriteLine("  Error: --endpoint requires --model <name>.");
                    return new(null, null, ExitCodes.UsageError);
                }

                var c = EndpointFactory.CreateOpenAICompatible(endpoint!, model!, apiKey);
                return new(c, Fingerprint("openai", endpoint, model!), ExitCodes.Success);
            }

            var (envClient, envDeployment, _) = AzureChatAgentFactory.TryBuildChatClientFromEnv();
            if (envClient is not null)
            {
                return new(envClient, Fingerprint("azure", Env("AZURE_OPENAI_ENDPOINT"), envDeployment ?? model ?? "unknown"), ExitCodes.Success);
            }

            stderr.WriteLine("  Error: no model backing. Pass --azure --deployment-name <d>, or --endpoint <url> --model <m>, " +
                             "or set AZURE_OPENAI_ENDPOINT / _API_KEY / _DEPLOYMENT.");
            return new(null, null, ExitCodes.UsageError);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            stderr.WriteLine($"  Error building the model client: {ex.Message}");
            return new(null, null, ExitCodes.RuntimeError);
        }
    }

    /// <summary>A stable, secret-free fingerprint: <c>provider:host:model@&lt;shorthash&gt;</c>.</summary>
    public static string Fingerprint(string provider, string? endpoint, string model)
    {
        var host = Uri.TryCreate(endpoint, UriKind.Absolute, out var u) ? u.Host : "env";
        var raw = $"{provider}:{host}:{model}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))[..12].ToLowerInvariant();
        return $"{provider}:{host}:{model}@{hash}";
    }

    private static string? Env(string key) => Environment.GetEnvironmentVariable(key);
}
