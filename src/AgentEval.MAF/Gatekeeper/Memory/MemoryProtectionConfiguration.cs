// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Collections.ObjectModel;
using System.Text.Json;

namespace AgentEval.MAF.Gatekeeper.Memory;

/// <summary>
/// Strict, non-secret deployment configuration pinned to one reviewed memory-policy fingerprint.
/// Runtime collaborators remain code/DI registrations and are never activated by JSON type names.
/// </summary>
public sealed class MemoryProtectionConfiguration
{
    private const int MaximumJsonCharacters = 65_536;

    private MemoryProtectionConfiguration(
        string expectedPolicyFingerprint,
        MemoryCoverageLevel minimumCoverage,
        IReadOnlyList<string> sensitiveSinkTools)
    {
        ExpectedPolicyFingerprint = expectedPolicyFingerprint;
        MinimumCoverage = minimumCoverage;
        SensitiveSinkTools = sensitiveSinkTools;
    }

    /// <summary>Supported strict configuration schema.</summary>
    public const string SchemaVersion = "gatekeeper.memory-protection/1";

    /// <summary>Expected fingerprint of the exact code-built pipeline.</summary>
    public string ExpectedPolicyFingerprint { get; }

    /// <summary>Coverage threshold that must match the code-built policy.</summary>
    public MemoryCoverageLevel MinimumCoverage { get; }

    /// <summary>Exact downstream sink names protected from recalled values.</summary>
    public IReadOnlyList<string> SensitiveSinkTools { get; }

    /// <summary>Parses a bounded strict JSON document; unknown and duplicate properties are rejected.</summary>
    public static MemoryProtectionConfiguration ParseJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaximumJsonCharacters)
        {
            throw Error("document", "Configuration must be non-empty and no larger than 65536 characters.");
        }

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16,
            });
            var root = document.RootElement;
            if (root.ValueKind is not JsonValueKind.Object)
            {
                throw Error("root", "Configuration root must be an object.");
            }

            var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                if (!properties.TryAdd(property.Name, property.Value))
                {
                    throw Error("duplicate_property", "Duplicate property '" + property.Name + "'.");
                }

                if (property.Name is not ("schema" or "expectedPolicyFingerprint" or
                    "minimumCoverage" or "sensitiveSinkTools"))
                {
                    throw Error("unknown_property", "Unknown property '" + property.Name + "'.");
                }
            }

            var schema = RequiredString(properties, "schema");
            if (!string.Equals(schema, SchemaVersion, StringComparison.Ordinal))
            {
                throw Error("unsupported_schema", "Expected schema '" + SchemaVersion + "'.");
            }

            var fingerprint = MemoryDigest.Validate(
                RequiredString(properties, "expectedPolicyFingerprint"),
                "expectedPolicyFingerprint");
            var coverageText = RequiredString(properties, "minimumCoverage");
            if (!Enum.TryParse<MemoryCoverageLevel>(coverageText, ignoreCase: false, out var coverage) ||
                !Enum.IsDefined(coverage))
            {
                throw Error("invalid_coverage", "minimumCoverage must be a named MemoryCoverageLevel value.");
            }

            var sinks = ParseSinks(properties);
            return new MemoryProtectionConfiguration(fingerprint, coverage, sinks);
        }
        catch (MemoryProtectionConfigurationException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw Error("invalid_json", "Configuration is not valid strict JSON.", exception);
        }
        catch (ArgumentException exception)
        {
            throw Error("invalid_value", exception.Message, exception);
        }
    }

    /// <summary>Reads one UTF-8 configuration file once; live reload requires rebuilding the agent.</summary>
    public static MemoryProtectionConfiguration ReadFileOnce(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var info = new FileInfo(path);
        if (!info.Exists || info.Length is <= 0 or > MaximumJsonCharacters)
        {
            throw Error("file", "Configuration file is missing, empty, or too large.");
        }

        return ParseJson(File.ReadAllText(info.FullName));
    }

    private static IReadOnlyList<string> ParseSinks(IReadOnlyDictionary<string, JsonElement> properties)
    {
        if (!properties.TryGetValue("sensitiveSinkTools", out var element))
        {
            return [];
        }

        if (element.ValueKind is not JsonValueKind.Array || element.GetArrayLength() > 128)
        {
            throw Error("invalid_sinks", "sensitiveSinkTools must be an array with at most 128 entries.");
        }

        var result = new List<string>();
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind is not JsonValueKind.String)
            {
                throw Error("invalid_sink", "Every sensitive sink must be a string.");
            }

            var value = MemoryValidation.Identifier(item.GetString()!, "sensitiveSinkTools");
            if (!unique.Add(value))
            {
                throw Error("duplicate_sink", "Sensitive sink names must be unique case-insensitively.");
            }

            result.Add(value);
        }

        return new ReadOnlyCollection<string>(result);
    }

    private static string RequiredString(IReadOnlyDictionary<string, JsonElement> properties, string name)
    {
        if (!properties.TryGetValue(name, out var value) || value.ValueKind is not JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw Error("missing_property", "Required string property '" + name + "' is missing.");
        }

        return value.GetString()!;
    }

    private static MemoryProtectionConfigurationException Error(
        string reasonCode,
        string message,
        Exception? inner = null)
        => new(reasonCode, message, inner);
}

/// <summary>Safe strict-configuration failure with a bounded machine-readable reason.</summary>
public sealed class MemoryProtectionConfigurationException : InvalidOperationException
{
    internal MemoryProtectionConfigurationException(string reasonCode, string message, Exception? inner)
        : base(message, inner)
        => ReasonCode = reasonCode;

    /// <summary>Machine-readable configuration failure category.</summary>
    public string ReasonCode { get; }
}
