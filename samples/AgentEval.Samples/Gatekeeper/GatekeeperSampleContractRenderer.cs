// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json;

namespace AgentEval.Samples;

/// <summary>
/// Renders the static threat/guarantee contract that makes every Gatekeeper sample's claim auditable before it runs.
/// The embedded manifest contains only reviewed, content-free metadata; runtime arguments, secrets, identities, and
/// containment keys are never accepted by this renderer.
/// </summary>
internal static class GatekeeperSampleContractRenderer
{
    private const string ResourceName = "AgentEval.Samples.Gatekeeper.sample-manifest.json";

    private static readonly Lazy<IReadOnlyDictionary<string, SampleContract>> Contracts =
        new(LoadContracts, LazyThreadSafetyMode.ExecutionAndPublication);

    public static void Print(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        if (!Contracts.Value.TryGetValue(id, out var contract))
        {
            throw new InvalidDataException($"Gatekeeper sample contract '{id}' is not present in the embedded manifest.");
        }

        var showFull = string.Equals(
            Environment.GetEnvironmentVariable("AGENTEVAL_GATEKEEPER_SHOW_CONTRACTS"),
            "true",
            StringComparison.OrdinalIgnoreCase);

        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine($"\n--- Gatekeeper sample contract {contract.Id}: {contract.Name} ---");
        Console.ResetColor();

        if (!showFull)
        {
            PrintCompactField("Threat:", Join(contract.Threats));
            PrintCompactField("Guarantee:", contract.Guarantee);
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("   (AGENTEVAL_GATEKEEPER_SHOW_CONTRACTS=true prints the full audited contract)\n");
            Console.ResetColor();
            return;
        }

        Console.WriteLine($"   Threat:           {Join(contract.Threats)}");
        Console.WriteLine($"   Protected seam:   {Join(contract.Boundaries)}");
        Console.WriteLine($"   Gates/mechanisms: {Join(contract.Mechanisms)}");
        Console.WriteLine($"   Composition:      {contract.CompositionMode} — {contract.CompositionRationale}");
        Console.WriteLine($"   Guarantee:        {contract.Guarantee}");
        Console.WriteLine($"   Non-guarantee:    {contract.NonGuarantee}");
        Console.WriteLine($"   Execution:        {contract.ExecutionMode}; effects: {Join(contract.ExternalEffects)}");
        Console.WriteLine($"   Benign control:   {contract.BenignControl}");
        Console.WriteLine($"   Pass oracle:      {contract.PassOracle}");
        Console.WriteLine("--- End sample contract ---\n");
    }

    private static IReadOnlyDictionary<string, SampleContract> LoadContracts()
    {
        var assembly = typeof(GatekeeperSampleContractRenderer).Assembly;
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidDataException($"Embedded Gatekeeper sample manifest '{ResourceName}' was not found.");

        var manifest = JsonSerializer.Deserialize<SampleManifest>(
            stream,
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                MaxDepth = 16,
            })
            ?? throw new InvalidDataException("The embedded Gatekeeper sample manifest is empty.");

        if (!string.Equals(manifest.SchemaVersion, "1.0", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Unsupported Gatekeeper sample manifest schema '{manifest.SchemaVersion}'.");
        }

        var result = new Dictionary<string, SampleContract>(StringComparer.Ordinal);
        foreach (var contract in manifest.Samples)
        {
            if (string.IsNullOrWhiteSpace(contract.Id) || !result.TryAdd(contract.Id, contract))
            {
                throw new InvalidDataException(
                    $"The embedded Gatekeeper sample manifest contains a missing or duplicate id '{contract.Id}'.");
            }
        }

        return result;
    }

    private static string Join(IReadOnlyList<string> values) => string.Join(", ", values);

    private static void PrintCompactField(string label, string value)
    {
        const int WrapWidth = 84;
        var first = true;
        foreach (var line in Wrap(value, WrapWidth))
        {
            Console.WriteLine(first ? $"   {label,-10} {line}" : $"   {new string(' ', 10)} {line}");
            first = false;
        }
    }

    private static IEnumerable<string> Wrap(string text, int width)
    {
        var line = new System.Text.StringBuilder();
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > width)
            {
                yield return line.ToString();
                line.Clear();
            }

            if (line.Length > 0)
            {
                line.Append(' ');
            }

            line.Append(word);
        }

        if (line.Length > 0)
        {
            yield return line.ToString();
        }
    }

    private sealed class SampleManifest
    {
        public required string SchemaVersion { get; init; }

        public required IReadOnlyList<SampleContract> Samples { get; init; }
    }

    private sealed class SampleContract
    {
        public required string Id { get; init; }

        public required string Name { get; init; }

        public required string ExecutionMode { get; init; }

        public required IReadOnlyList<string> Boundaries { get; init; }

        public required IReadOnlyList<string> Mechanisms { get; init; }

        public required IReadOnlyList<string> Threats { get; init; }

        public required string CompositionMode { get; init; }

        public required string CompositionRationale { get; init; }

        public required IReadOnlyList<string> ExternalEffects { get; init; }

        public required string Guarantee { get; init; }

        public required string NonGuarantee { get; init; }

        public required string BenignControl { get; init; }

        public required string PassOracle { get; init; }
    }
}
