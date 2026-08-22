// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentEval.PartnerDeskDemo.Tools;

/// <summary>One synthetic partner-company record. Every field is invented; see <c>Data/partners.json</c>.</summary>
public sealed record PartnerRecord
{
    /// <summary>Synthetic register identifier, e.g. <c>PTR-0001</c>.</summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>Synthetic company name.</summary>
    [JsonPropertyName("company")]
    public required string Company { get; init; }

    /// <summary>Synthetic contact person.</summary>
    [JsonPropertyName("contact")]
    public required string Contact { get; init; }

    /// <summary>Synthetic contact address on a reserved <c>.example</c> domain.</summary>
    [JsonPropertyName("email")]
    public required string Email { get; init; }

    /// <summary>Synthetic sector label.</summary>
    [JsonPropertyName("sector")]
    public required string Sector { get; init; }

    /// <summary>Synthetic canton code.</summary>
    [JsonPropertyName("canton")]
    public required string Canton { get; init; }

    /// <summary>Synthetic annual contract value in CHF.</summary>
    [JsonPropertyName("annualContractValueChf")]
    public required long AnnualContractValueChf { get; init; }

    /// <summary>Synthetic internal risk band.</summary>
    [JsonPropertyName("riskBand")]
    public required string RiskBand { get; init; }
}

/// <summary>
/// The in-memory partner register behind <c>query_partner_database</c>. Loaded once from
/// <c>Data/partners.json</c>; there is no database, connection string, or network call anywhere in this type.
/// </summary>
public sealed class PartnerRegister
{
    private readonly IReadOnlyList<PartnerRecord> _records;

    private PartnerRegister(IReadOnlyList<PartnerRecord> records) => _records = records;

    /// <summary>Every record, in file order. Record #1 is Alpina Logistik AG.</summary>
    public IReadOnlyList<PartnerRecord> Records => _records;

    /// <summary>The number of records in the register.</summary>
    public int Count => _records.Count;

    /// <summary>Loads the register from <c>Data/partners.json</c> next to the demo binary.</summary>
    public static PartnerRegister Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Data", "partners.json");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"The synthetic partner register was not found at '{path}'.", path);
        }

        using var stream = File.OpenRead(path);
        var file = JsonSerializer.Deserialize<RegisterFile>(
            stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true, MaxDepth = 8 })
            ?? throw new InvalidDataException($"The synthetic partner register at '{path}' is empty.");

        if (file.Partners.Count == 0)
        {
            throw new InvalidDataException($"The synthetic partner register at '{path}' contains no records.");
        }

        return new PartnerRegister(file.Partners);
    }

    /// <summary>Case-insensitive lookup by company name; returns <see langword="null"/> when nothing matches.</summary>
    public PartnerRecord? FindByName(string partnerName)
    {
        var needle = partnerName.Trim();
        return _records.FirstOrDefault(record =>
                   string.Equals(record.Company, needle, StringComparison.OrdinalIgnoreCase))
               ?? _records.FirstOrDefault(record =>
                   record.Company.Contains(needle, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The first <paramref name="limit"/> records, in file order.</summary>
    public IReadOnlyList<PartnerRecord> Take(int limit) =>
        _records.Take(Math.Clamp(limit, 1, _records.Count)).ToArray();

    private sealed class RegisterFile
    {
        public IReadOnlyList<PartnerRecord> Partners { get; init; } = [];
    }
}
