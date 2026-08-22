// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json;

namespace AgentEval.PartnerDeskDemo.Demo;

/// <summary>
/// Prints the demo's threat/guarantee contract before anything runs, following the same convention the shipped
/// Gatekeeper samples use: the claim is written down, reviewed, and auditable before the code that makes it runs.
/// </summary>
/// <remarks>
/// The embedded <c>demo-manifest.json</c> carries the same eighteen reviewed, content-free fields
/// <c>samples/AgentEval.Samples/Gatekeeper/sample-manifest.json</c> requires. Runtime arguments, identities, and
/// containment keys are never accepted here.
/// </remarks>
public static class DemoContractRenderer
{
    private const string ResourceName = "AgentEval.PartnerDeskDemo.demo-manifest.json";

    private static readonly Lazy<DemoContract> Contract = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The single contract this demo declares.</summary>
    public static DemoContract Current => Contract.Value;

    /// <summary>Prints the contract. Set <c>AGENTEVAL_GATEKEEPER_SHOW_CONTRACTS=true</c> for every field.</summary>
    public static void Print(DemoOutput output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var contract = Contract.Value;

        var full = string.Equals(
            Environment.GetEnvironmentVariable("AGENTEVAL_GATEKEEPER_SHOW_CONTRACTS"),
            "true",
            StringComparison.OrdinalIgnoreCase);

        output.Line();
        output.Line(ConsoleColor.DarkCyan, $"--- Demo contract {contract.Id}: {contract.Name} ---");
        output.Paragraph("Threat:      " + string.Join(", ", contract.Threats));
        output.Paragraph("Guarantee:   " + contract.Guarantee);

        if (!full)
        {
            output.Line(ConsoleColor.DarkGray,
                "  (AGENTEVAL_GATEKEEPER_SHOW_CONTRACTS=true prints the full audited contract)");
            return;
        }

        output.Paragraph("Seams:       " + string.Join(", ", contract.Boundaries));
        output.Paragraph("Mechanisms:  " + string.Join(", ", contract.Mechanisms));
        output.Paragraph("Composition: " + contract.CompositionMode + " — " + contract.CompositionRationale);
        output.Paragraph("NOT claimed: " + contract.NonGuarantee);
        output.Paragraph("Execution:   " + contract.ExecutionMode);
        output.Paragraph("Effects:     " + string.Join("; ", contract.ExternalEffects));
        output.Paragraph("Benign ctrl: " + contract.BenignControl);
        output.Paragraph("Pass oracle: " + contract.PassOracle);
        output.Line(ConsoleColor.DarkCyan, "--- End demo contract ---");
    }

    private static DemoContract Load()
    {
        var assembly = typeof(DemoContractRenderer).Assembly;
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidDataException($"Embedded demo manifest '{ResourceName}' was not found.");

        var manifest = JsonSerializer.Deserialize<DemoManifest>(
            stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true, MaxDepth = 16 })
            ?? throw new InvalidDataException("The embedded demo manifest is empty.");

        if (!string.Equals(manifest.SchemaVersion, "1.0", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Unsupported demo manifest schema '{manifest.SchemaVersion}'.");
        }

        if (manifest.Samples.Count != 1)
        {
            throw new InvalidDataException("The demo manifest must declare exactly one contract.");
        }

        return manifest.Samples[0];
    }

    private sealed class DemoManifest
    {
        public required string SchemaVersion { get; init; }

        public required IReadOnlyList<DemoContract> Samples { get; init; }
    }
}

/// <summary>The reviewed, content-free claim one demo makes.</summary>
public sealed class DemoContract
{
    /// <summary>Stable identifier.</summary>
    public required string Id { get; init; }

    /// <summary>Human-readable name.</summary>
    public required string Name { get; init; }

    /// <summary>One-line description.</summary>
    public required string Description { get; init; }

    /// <summary>Execution mode: offline, live-model, live-boundary, or hybrid.</summary>
    public required string ExecutionMode { get; init; }

    /// <summary>Protected seams.</summary>
    public required IReadOnlyList<string> Boundaries { get; init; }

    /// <summary>Gates and mechanisms demonstrated.</summary>
    public required IReadOnlyList<string> Mechanisms { get; init; }

    /// <summary>Threats addressed.</summary>
    public required IReadOnlyList<string> Threats { get; init; }

    /// <summary>Whether the demo uses the supported composite builder or a deliberate low-level seam.</summary>
    public required string CompositionMode { get; init; }

    /// <summary>Why that composition mode was chosen.</summary>
    public required string CompositionRationale { get; init; }

    /// <summary>Every effect this demo has outside its own process memory.</summary>
    public required IReadOnlyList<string> ExternalEffects { get; init; }

    /// <summary>What the demonstrated configuration guarantees.</summary>
    public required string Guarantee { get; init; }

    /// <summary>What it explicitly does not guarantee.</summary>
    public required string NonGuarantee { get; init; }

    /// <summary>The benign control that shows the configuration is still useful.</summary>
    public required string BenignControl { get; init; }

    /// <summary>How a pass is decided.</summary>
    public required string PassOracle { get; init; }
}
