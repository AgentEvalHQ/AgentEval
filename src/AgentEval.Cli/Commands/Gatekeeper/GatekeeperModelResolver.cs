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

                // Fall back to AZURE_OPENAI_ENDPOINT when --endpoint is omitted (CreateAzure throws on a null endpoint).
                var azEndpoint = string.IsNullOrWhiteSpace(endpoint) ? Env("AZURE_OPENAI_ENDPOINT") : endpoint;
                if (string.IsNullOrWhiteSpace(azEndpoint))
                {
                    stderr.WriteLine("  Error: --azure needs an endpoint — pass --endpoint <url> or set AZURE_OPENAI_ENDPOINT.");
                    return new(null, null, ExitCodes.UsageError);
                }

                var c = VerboseLog.Wrap(EndpointFactory.CreateAzure(azEndpoint, deploymentName!, apiKey), "judge");
                return new(c, Fingerprint("azure", azEndpoint, deploymentName!), ExitCodes.Success);
            }

            if (!string.IsNullOrWhiteSpace(endpoint))
            {
                if (string.IsNullOrWhiteSpace(model))
                {
                    stderr.WriteLine("  Error: --endpoint requires --model <name>.");
                    return new(null, null, ExitCodes.UsageError);
                }

                var c = VerboseLog.Wrap(EndpointFactory.CreateOpenAICompatible(endpoint!, model!, apiKey), "judge");
                return new(c, Fingerprint("openai", endpoint, model!), ExitCodes.Success);
            }

            // Env fallback — build it the same way as the explicit --azure branch (EndpointFactory.CreateAzure) rather
            // than AzureChatAgentFactory, whose missing-config path writes benchmark-worded errors straight to
            // Console.Error (duplicating gatekeeper's own message + bypassing the injected stderr). CreateAzure resolves
            // the key itself (apiKey ?? AZURE_OPENAI_API_KEY), so an explicit --api-key overrides the env var — consistent
            // with the other branches. We gate only on endpoint+deployment; a missing key surfaces via the outer catch.
            var envEndpoint   = Env("AZURE_OPENAI_ENDPOINT");
            var envDeployment = Env("AZURE_OPENAI_DEPLOYMENT");
            if (!string.IsNullOrWhiteSpace(envEndpoint) && !string.IsNullOrWhiteSpace(envDeployment))
            {
                var c = VerboseLog.Wrap(EndpointFactory.CreateAzure(envEndpoint!, envDeployment!, apiKey), "judge");   // apiKey ?? env key, resolved inside
                return new(c, Fingerprint("azure", envEndpoint, envDeployment!), ExitCodes.Success);
            }

            stderr.WriteLine("  Error: no model backing. Pass --azure --deployment-name <d>, or --endpoint <url> --model <m>, " +
                             "or set AZURE_OPENAI_ENDPOINT / _API_KEY / _DEPLOYMENT.");
            return new(null, null, ExitCodes.UsageError);
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or ArgumentException)
        {
            // Config/usage errors — a malformed --endpoint URL, a missing key/deployment — so CI can tell
            // "fix the flags/env" (exit 2) apart from an unexpected runtime failure (exit 3). FormatException
            // covers UriFormatException; ArgumentException covers ArgumentNullException.
            stderr.WriteLine($"  Error: {ex.Message}");
            return new(null, null, ExitCodes.UsageError);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            stderr.WriteLine($"  Error building the model client: {ex.Message}");
            return new(null, null, ExitCodes.RuntimeError);
        }
    }

    /// <summary>A stable, secret-free fingerprint: <c>provider:authority+path:model@&lt;shorthash&gt;</c>.</summary>
    public static string Fingerprint(string provider, string? endpoint, string model)
    {
        // Uri.Authority is host:port with default ports normalized away (443/80 omitted) and userinfo excluded; we
        // append the normalized path so both localhost:8080 vs :8081 AND same-host reverse-proxy base paths
        // (…/team-a/v1 vs …/team-b/v1, which CreateOpenAICompatible honors) get distinct fingerprints — a cert can't
        // leak across endpoints — while https://x, https://x:443 and a trailing slash stay equal. No secret enters it.
        var endpointKey = "env";
        if (Uri.TryCreate(endpoint, UriKind.Absolute, out var u))
        {
            endpointKey = u.Authority + u.AbsolutePath.TrimEnd('/');   // "/" and "/v1/" collapse to "" and "/v1"
        }

        var raw = $"{provider}:{endpointKey}:{model}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))[..12].ToLowerInvariant();
        return $"{provider}:{endpointKey}:{model}@{hash}";
    }

    private static string? Env(string key) => Environment.GetEnvironmentVariable(key);
}
