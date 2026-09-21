// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Decisions;

namespace AgentEval.Cli.Infrastructure;

/// <summary>
/// Resolves the decision-model transport from the environment, the same variables and the same precedence
/// the samples use: <c>TYPESAFE_API_KEY</c> or <c>OPENROUTER_API_KEY</c>, <c>JEV_TRANSPORT</c> to force one,
/// <c>JEV_MODEL</c> to pin a build.
/// </summary>
/// <remarks>
/// An unrecognised <c>JEV_TRANSPORT</c> selects nothing rather than guessing: a typo must not send a
/// calibration run to a host the operator did not name.
/// </remarks>
internal static class DecisionClientFactory
{
    private static string? Env(string name)
    {
        var v = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
    }

    /// <summary>The variables this factory reads, for diagnostics.</summary>
    public const string RequiredVariables = "TYPESAFE_API_KEY (or OPENROUTER_API_KEY)";

    /// <summary>
    /// Builds the client options, or returns <see langword="null"/> with a reason.
    /// </summary>
    public static (SystemOneClientOptions? Options, string? Diagnostic) TryResolve()
    {
        var typeSafe = Env("TYPESAFE_API_KEY");
        var openRouter = Env("OPENROUTER_API_KEY");
        var model = Env("JEV_MODEL");
        var forced = Env("JEV_TRANSPORT")?.ToLowerInvariant();

        // With neither key set and no transport forced, name both rather than whichever branch happened to
        // be reached: "OPENROUTER_API_KEY is not set" reads as a demand for that one key.
        if (forced is null && typeSafe is null && openRouter is null)
            return (null, "Neither TYPESAFE_API_KEY nor OPENROUTER_API_KEY is set.");

        bool useTypeSafe;
        switch (forced)
        {
            case null: useTypeSafe = typeSafe is not null; break;
            case "typesafe": useTypeSafe = true; break;
            case "openrouter": useTypeSafe = false; break;
            default:
                return (null, $"JEV_TRANSPORT='{forced}' is not one of typesafe | openrouter.");
        }

        if (useTypeSafe)
        {
            if (typeSafe is null)
                return (null, "TYPESAFE_API_KEY is not set.");
            return (model is null ? SystemOneClientOptions.ForTypeSafe(typeSafe) : SystemOneClientOptions.ForTypeSafe(typeSafe, model), null);
        }

        if (openRouter is null)
            return (null, "OPENROUTER_API_KEY is not set.");
        return (model is null ? SystemOneClientOptions.ForOpenRouter(openRouter) : SystemOneClientOptions.ForOpenRouter(openRouter, model), null);
    }
}
