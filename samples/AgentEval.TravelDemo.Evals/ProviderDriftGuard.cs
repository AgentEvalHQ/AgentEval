// SPDX-License-Identifier: MIT
// Copyright (c) 2026 ECS2026 Demo

using AgentEval.Providers;

namespace AgentEval.TravelDemo.Evals;

/// <summary>
/// Startup check that AgentEval.TravelDemo's own provider resolution (<see cref="Config"/>) agrees with
/// AgentEval's <see cref="InferenceProviderEnvironment"/>.
/// </summary>
/// <remarks>
/// <para>
/// TravelDemo is a pure MAF project — it must not reference AgentEval — so it carries a small copy of the
/// provider rules. A copy drifts silently: a new default model or a changed detection order in Core would
/// leave the agent and the judge on one host while the rest of AgentEval names another. This compares the
/// two on what decides where the requests go — provider, endpoint, model, key — and stops the run before
/// any model call when they differ. The key is compared for equality only and never printed.
/// </para>
/// <para>
/// One difference is by design: TravelDemo lets an Azure setup omit <c>AZURE_OPENAI_DEPLOYMENT</c> and falls
/// back to <see cref="Config.PreferredDeployment"/>; Core requires the variable. That fallback may decide the
/// MODEL, never the PROVIDER, so Core is handed it only where it cannot change which provider wins: Azure was
/// chosen explicitly, or no provider has the variables Core requires (<see cref="AppliesAzureFallback"/>).
/// Everywhere else the comparison is against Core's raw resolution, so a setup where TravelDemo would pick
/// Azure while every other AgentEval entry point picks Bitdeer, OpenAI or Foundry is reported as drift.
/// <c>--model</c> is not compared: it overrides the model after resolution, for the agent and the judge alike.
/// </para>
/// <para>
/// <c>--selftest</c> runs <see cref="Compare"/> over a matrix of fake environments (<see cref="TravelDemoSelfTest"/>),
/// so a drift can be caught offline before it shows up as a stopped eval.
/// </para>
/// </remarks>
internal static class ProviderDriftGuard
{
    /// <summary>The two resolutions of one variable source, and what differs between them (empty = they agree).</summary>
    internal sealed record Comparison(TravelProviderSettings Travel, InferenceProviderSettings Core, IReadOnlyList<string> Differences);

    /// <summary>True when the two resolutions agree; otherwise prints what differs and returns false.</summary>
    public static bool Check() => Report(Compare(Environment.GetEnvironmentVariable), Config.ShowModel);

    /// <summary>
    /// Prints the outcome of a comparison and returns true when it agrees. Separate from <see cref="Check"/> so the
    /// self-test can check what a drift prints in hidden mode without a real drift in the environment.
    /// </summary>
    internal static bool Report(Comparison comparison, bool showModel)
    {
        var (travel, core, differences) = comparison;

        if (differences.Count == 0)
        {
            // Hidden by default (Config.ShowModel): the line confirms the check ran without naming the provider.
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine(showModel
                ? $"  Provider check: AgentEval.TravelDemo and AgentEval resolve the same provider ({travel.ProviderTag})."
                : "  Provider check: AgentEval.TravelDemo and AgentEval resolve the same provider.");
            Console.ResetColor();
            return true;
        }

        // Which fields differ is printed either way (field names only); the provider, model and endpoint each side
        // resolved only with --show-model — this can trip on stage, where the host is not shown.
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine("\n  ❌ Provider drift — stopping before any model call.");
        if (showModel)
        {
            Console.WriteLine($"     AgentEval.TravelDemo Config           : {Describe(travel.ProviderTag, travel.Model, travel.Endpoint, travel.Diagnostic)}");
            Console.WriteLine($"     AgentEval InferenceProviderEnvironment: {Describe(core.ProviderTag, core.Model, core.Endpoint, core.Diagnostic)}");
        }
        else
        {
            Console.WriteLine($"     The two resolvers disagree; re-run with --show-model (or {Config.ShowModelVariable}=1) for details.");
        }
        Console.WriteLine($"     They differ in: {string.Join(", ", differences)}.");
        Console.WriteLine("     TravelDemo carries its own copy of the provider rules because it does not reference AgentEval.");
        Console.WriteLine("     Bring samples/AgentEval.TravelDemo/Config.cs back in line with");
        Console.WriteLine("     src/AgentEval.Core/Providers/InferenceProviderEnvironment.cs.\n");
        Console.ResetColor();
        return false;
    }

    /// <summary>
    /// Resolves <paramref name="getEnvironmentVariable"/> both ways and compares them. Pure: reads nothing but the
    /// source it is given, so the self-test can drive it with fake environments.
    /// </summary>
    internal static Comparison Compare(Func<string, string?> getEnvironmentVariable)
    {
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);
        var travel = Config.Resolve(getEnvironmentVariable);
        var core   = InferenceProviderEnvironment.Resolve(AppliesAzureFallback(getEnvironmentVariable)
            ? WithPreferredDeployment(getEnvironmentVariable)
            : getEnvironmentVariable);
        return new Comparison(travel, core, Differences(travel, core));
    }

    /// <summary>What decides where the requests go, field by field: provider, then endpoint, model and key.</summary>
    internal static IReadOnlyList<string> Differences(TravelProviderSettings travel, InferenceProviderSettings core)
    {
        var differences = new List<string>();
        if (travel.ProviderTag != core.ProviderTag) differences.Add("provider");
        if (travel.IsConfigured && core.IsConfigured)
        {
            if (!string.Equals(travel.Endpoint?.AbsoluteUri, core.Endpoint?.AbsoluteUri, StringComparison.Ordinal)) differences.Add("endpoint");
            if (!string.Equals(travel.Model, core.Model, StringComparison.Ordinal))                                   differences.Add("model");
            if (!string.Equals(travel.ApiKey, core.ApiKey, StringComparison.Ordinal))                                 differences.Add("API key (values not shown)");
            if (travel.UsesAzureProtocol != core.UsesAzureProtocol)                                                   differences.Add("protocol");
        }
        return differences;
    }

    /// <summary>
    /// True where TravelDemo's Azure deployment default may apply: Azure endpoint and key set, no deployment, and
    /// either <c>AI_INFERENCE_PROVIDER</c> names Azure or no provider has every variable Core requires — the only
    /// cases where the default cannot change which provider is selected.
    /// </summary>
    internal static bool AppliesAzureFallback(Func<string, string?> env)
    {
        bool Set(string name) => !string.IsNullOrWhiteSpace(env(name));
        if (Set("AZURE_OPENAI_DEPLOYMENT") || !Set("AZURE_OPENAI_ENDPOINT") || !Set("AZURE_OPENAI_API_KEY"))
            return false;

        var selector = env(InferenceProviderEnvironment.SelectorVariable);
        if (!string.IsNullOrWhiteSpace(selector))
            return InferenceProviderEnvironment.Parse(selector) == InferenceProvider.AzureOpenAI;

        return !Enum.GetValues<InferenceProvider>()
            .Any(p => p != InferenceProvider.None && InferenceProviderEnvironment.HasCredentials(p, env));
    }

    private static Func<string, string?> WithPreferredDeployment(Func<string, string?> env) =>
        name => name == "AZURE_OPENAI_DEPLOYMENT" ? Config.PreferredDeployment : env(name);

    private static string Describe(string tag, string? model, Uri? endpoint, string? diagnostic) =>
        tag == "none" ? $"none — {diagnostic}" : $"{tag} · {model} · endpoint {SafeEndpoint(endpoint)}";

    // Same rule as the banner: only a provider's published default URL is printed; anything the operator
    // supplied can name a private resource.
    private static string SafeEndpoint(Uri? endpoint) =>
        endpoint is null ? "(unset)"
        : endpoint.AbsoluteUri == new Uri(Config.BitdeerDefaultEndpoint).AbsoluteUri
          || endpoint.AbsoluteUri == new Uri(Config.OpenAIDefaultEndpoint).AbsoluteUri
            ? endpoint.AbsoluteUri
            : "(set, not shown)";
}
