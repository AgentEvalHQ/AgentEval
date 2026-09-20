// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Decisions;

/// <summary>
/// Where a System One decision model is reached and as what. Two providers speak this protocol today —
/// TypeSafe directly, and OpenRouter as a relay — and they differ only in base URL and model id, so
/// the same client serves both. Build with <see cref="ForTypeSafe"/> or <see cref="ForOpenRouter"/>,
/// or set the members yourself for a proxy.
/// </summary>
/// <remarks>
/// A class rather than a record on purpose: a record's generated <c>ToString()</c> would print the
/// API key into any log line that formats the options.
/// </remarks>
public sealed class SystemOneClientOptions
{
    /// <summary>TypeSafe's own endpoint (<c>POST</c>), per their API reference.</summary>
    public const string TypeSafeEndpoint = "https://api.typesafe.ai/v1/systemone";

    /// <summary>OpenRouter's System One relay endpoint (<c>POST</c>), per OpenRouter's TypeSafe SDK guide. OpenRouter also serves the same body at <c>/api/alpha/decisions</c>; this one is the path their SDK guide documents.</summary>
    public const string OpenRouterEndpoint = "https://openrouter.ai/api/v1/systemone";

    /// <summary>
    /// TypeSafe's documented flagship alias. An alias moves; the <see cref="DecisionResponse.Model"/> the
    /// provider echoes back is the resolved build and is what provenance records. Pin a versioned id
    /// (TypeSafe's reference shows the <c>jev-1.13.0</c> form) for a reproducible baseline.
    /// </summary>
    public const string TypeSafeDefaultModel = "jev-latest";

    /// <summary>The pinned Jev release on OpenRouter. <c>~typesafe/jev-latest</c> is the moving alias.</summary>
    public const string OpenRouterDefaultModel = "typesafe/jev-1.13";

    /// <summary>The full request URL, including the path.</summary>
    public required Uri Endpoint { get; init; }

    /// <summary>The bearer token. Never logged, never in an exception message.</summary>
    public required string ApiKey { get; init; }

    /// <summary>The model id sent when a <see cref="DecisionRequest.Model"/> does not override it.</summary>
    public required string Model { get; init; }

    /// <summary>A short provider label for logs and provenance notes, e.g. <c>typesafe</c> or <c>openrouter</c>.</summary>
    public string ProviderName { get; init; } = "system-one";

    /// <summary>Options for TypeSafe's own endpoint.</summary>
    /// <param name="apiKey">The TypeSafe API key.</param>
    /// <param name="model">The model id; defaults to <see cref="TypeSafeDefaultModel"/>.</param>
    public static SystemOneClientOptions ForTypeSafe(string apiKey, string model = TypeSafeDefaultModel) => new()
    {
        Endpoint = new Uri(TypeSafeEndpoint),
        ApiKey = apiKey,
        Model = model,
        ProviderName = "typesafe",
    };

    /// <summary>Options for OpenRouter's relay of the same protocol.</summary>
    /// <param name="apiKey">The OpenRouter API key.</param>
    /// <param name="model">The model id; defaults to <see cref="OpenRouterDefaultModel"/>.</param>
    public static SystemOneClientOptions ForOpenRouter(string apiKey, string model = OpenRouterDefaultModel) => new()
    {
        Endpoint = new Uri(OpenRouterEndpoint),
        ApiKey = apiKey,
        Model = model,
        ProviderName = "openrouter",
    };
}
