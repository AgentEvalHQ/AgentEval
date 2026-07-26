// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text;
using System.Text.Json;

namespace AgentEval.MAF.Gatekeeper;

/// <summary>Strict configuration failure raised while loading a Gatekeeper tool-contract document.</summary>
public sealed class GatekeeperContractConfigurationException : Exception
{
    internal GatekeeperContractConfigurationException(
        string errorCode,
        string jsonPath,
        string detail)
        : base($"Gatekeeper contract configuration error [{errorCode}] at {jsonPath}: {detail}")
    {
        ErrorCode = errorCode;
        JsonPath = jsonPath;
    }

    /// <summary>Stable machine-readable error code that never contains configured values.</summary>
    public string ErrorCode { get; }

    /// <summary>JSON path at which validation failed.</summary>
    public string JsonPath { get; }
}

internal static class ToolContractJsonLimits
{
    internal const int MaxPayloadBytes = 1024 * 1024;
    internal const int MaxJsonDepth = 32;
    internal const int MaxContracts = 256;
    internal const int MaxPredicatesPerContract = 64;
    internal const int MaxNameChars = 256;
    internal const int MaxKeywordsPerPredicate = 256;
    internal const int MaxKeywordChars = 4096;
}

internal static class ToolContractJsonParser
{
    private const string SchemaVersion = "gatekeeper.contract/1";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly IReadOnlySet<string> RootProperties =
        new HashSet<string>(["schema", "contracts"], StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> ContractProperties =
        new HashSet<string>(["tool", "predicates"], StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> PiiProperties =
        new HashSet<string>(["kind", "argument"], StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> RecipientDomainProperties =
        new HashSet<string>(["kind", "argument", "allowedDomains"], StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> MaxDistinctProperties =
        new HashSet<string>(["kind", "argument", "max"], StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> ShellMetacharProperties =
        new HashSet<string>(["kind", "argument", "dialect"], StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> SequenceProperties =
        new HashSet<string>(["kind", "triggerTools"], StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> PathProperties =
        new HashSet<string>(["kind", "argument", "allowedRoots", "basePath"], StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> DeniedKeywordProperties =
        new HashSet<string>(["kind", "argument", "keywords"], StringComparer.Ordinal);

    internal static IReadOnlyList<ToolContract> Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        ValidatePayloadSize(json);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(
                json,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = ToolContractJsonLimits.MaxJsonDepth,
                });
        }
        catch (JsonException)
        {
            throw Error("invalid_json", "$", "payload is not strict JSON.");
        }

        using (document)
        {
            var root = document.RootElement;
            ValidateNoDuplicateProperties(root, "$", 0);
            RequireKind(root, JsonValueKind.Object, "$", "root_type", "root must be an object.");
            ValidateProperties(root, RootProperties, "$", "unknown_root_property");

            var schema = RequireString(root, "schema", "$", ToolContractJsonLimits.MaxNameChars);
            if (!string.Equals(schema, SchemaVersion, StringComparison.Ordinal))
            {
                throw Error("unsupported_schema", "$.schema", "schema must be gatekeeper.contract/1.");
            }

            var contractsElement = RequireProperty(root, "contracts", "$", JsonValueKind.Array);
            var contractCount = contractsElement.GetArrayLength();
            if (contractCount is < 1 or > ToolContractJsonLimits.MaxContracts)
            {
                throw Error(
                    "contract_count_limit",
                    "$.contracts",
                    $"contract count must be 1..{ToolContractJsonLimits.MaxContracts}.");
            }

            var contracts = new List<ToolContract>(contractCount);
            var toolNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var contractIndex = 0;
            foreach (var contractElement in contractsElement.EnumerateArray())
            {
                var path = $"$.contracts[{contractIndex}]";
                RequireKind(contractElement, JsonValueKind.Object, path, "contract_type", "contract must be an object.");
                ValidateProperties(contractElement, ContractProperties, path, "unknown_contract_property");

                var toolName = RequireName(contractElement, "tool", path);
                if (!toolNames.Add(toolName))
                {
                    throw Error("duplicate_tool", path + ".tool", "tool names must be unique case-insensitively.");
                }

                var predicatesElement = RequireProperty(contractElement, "predicates", path, JsonValueKind.Array);
                var predicateCount = predicatesElement.GetArrayLength();
                if (predicateCount is < 1 or > ToolContractJsonLimits.MaxPredicatesPerContract)
                {
                    throw Error(
                        "predicate_count_limit",
                        path + ".predicates",
                        $"predicate count must be 1..{ToolContractJsonLimits.MaxPredicatesPerContract}.");
                }

                var predicates = ParsePredicates(predicatesElement, path + ".predicates");
                contracts.Add(new ToolContract(toolName, predicates));
                contractIndex++;
            }

            return contracts.AsReadOnly();
        }
    }

    internal static string ReadFileOnce(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw Error("invalid_file_path", "$", "file path must be non-empty.");
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var bytes = new byte[ToolContractJsonLimits.MaxPayloadBytes + 1];
            var total = 0;
            while (total < bytes.Length)
            {
                var read = stream.Read(bytes, total, bytes.Length - total);
                if (read == 0)
                {
                    break;
                }

                total += read;
            }

            if (total > ToolContractJsonLimits.MaxPayloadBytes || stream.ReadByte() >= 0)
            {
                throw Error(
                    "payload_too_large",
                    "$",
                    $"UTF-8 payload exceeds {ToolContractJsonLimits.MaxPayloadBytes} bytes.");
            }

            var offset = total >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
            try
            {
                return StrictUtf8.GetString(bytes, offset, total - offset);
            }
            catch (DecoderFallbackException)
            {
                throw Error("invalid_utf8", "$", "file is not valid UTF-8.");
            }
        }
        catch (GatekeeperContractConfigurationException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            throw Error("file_io", "$", "contract file could not be read.");
        }
    }

    private static IReadOnlyList<ContractPredicate> ParsePredicates(JsonElement array, string arrayPath)
    {
        var predicates = new List<ContractPredicate>(array.GetArrayLength());
        var identities = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var element in array.EnumerateArray())
        {
            var path = $"{arrayPath}[{index}]";
            RequireKind(element, JsonValueKind.Object, path, "predicate_type", "predicate must be an object.");
            var kind = RequireString(element, "kind", path, ToolContractJsonLimits.MaxNameChars);
            var argument = string.Equals(kind, "forbiddenIfPrecededBy", StringComparison.Ordinal)
                ? null
                : RequireName(element, "argument", path);
            var identity = kind + "\0" + argument;
            if (!identities.Add(identity))
            {
                throw Error(
                    "duplicate_predicate",
                    path,
                    "predicate kind and argument must be unique within a contract.");
            }

            ContractPredicate predicate = kind switch
            {
                "piiScan" => ParsePii(element, argument!, path),
                "recipientDomainAllowList" => ParseRecipientDomains(element, argument!, path),
                "maxDistinctValues" => ParseMaxDistinctValues(element, argument!, path),
                "shellMetacharDeny" => ParseShellMetacharDeny(element, argument!, path),
                "forbiddenIfPrecededBy" => ParseForbiddenIfPrecededBy(element, path),
                "pathContainment" => ParsePathContainment(element, argument!, path),
                "deniedKeywords" => ParseDeniedKeywords(element, argument!, path),
                _ => throw Error("unknown_predicate_kind", path + ".kind", "predicate kind is not supported."),
            };
            predicates.Add(predicate);
            index++;
        }

        return predicates.AsReadOnly();
    }

    private static PiiPredicate ParsePii(JsonElement element, string argument, string path)
    {
        ValidateProperties(element, PiiProperties, path, "unknown_predicate_property");
        return new PiiPredicate(argument);
    }

    private static PathContainmentPredicate ParsePathContainment(
        JsonElement element,
        string argument,
        string path)
    {
        ValidateProperties(element, PathProperties, path, "unknown_predicate_property");
        var rootsElement = RequireProperty(element, "allowedRoots", path, JsonValueKind.Array);
        var count = rootsElement.GetArrayLength();
        if (count is < 1 or > PathContainmentPolicy.MaxAllowedRoots)
        {
            throw Error(
                "root_count_limit",
                path + ".allowedRoots",
                $"allowed-root count must be 1..{PathContainmentPolicy.MaxAllowedRoots}.");
        }

        var roots = new string[count];
        var index = 0;
        foreach (var rootElement in rootsElement.EnumerateArray())
        {
            var rootPath = $"{path}.allowedRoots[{index}]";
            RequireKind(rootElement, JsonValueKind.String, rootPath, "root_type", "allowed root must be a string.");
            var root = rootElement.GetString()!;
            if (root.Length > PathContainmentPolicy.MaxPathChars)
            {
                throw Error(
                    "root_length_limit",
                    rootPath,
                    $"allowed root exceeds {PathContainmentPolicy.MaxPathChars} characters.");
            }

            if (string.IsNullOrWhiteSpace(root))
            {
                throw Error("empty_root", rootPath, "allowed root must be non-empty.");
            }

            roots[index] = root;
            index++;
        }

        string? basePath = null;
        if (element.TryGetProperty("basePath", out var baseElement))
        {
            var basePathJsonPath = path + ".basePath";
            RequireKind(
                baseElement,
                JsonValueKind.String,
                basePathJsonPath,
                "base_path_type",
                "base path must be a string.");
            basePath = baseElement.GetString()!;
            if (basePath.Length > PathContainmentPolicy.MaxPathChars)
            {
                throw Error(
                    "base_path_length_limit",
                    basePathJsonPath,
                    $"base path exceeds {PathContainmentPolicy.MaxPathChars} characters.");
            }

            if (string.IsNullOrWhiteSpace(basePath))
            {
                throw Error("empty_base_path", basePathJsonPath, "base path must be non-empty.");
            }
        }

        try
        {
            return new PathContainmentPredicate(argument, roots, basePath);
        }
        catch (ArgumentException)
        {
            throw Error("invalid_path_configuration", path, "path configuration is invalid for this host.");
        }
    }

    private static ForbiddenIfPrecededByPredicate ParseForbiddenIfPrecededBy(
        JsonElement element,
        string path)
    {
        ValidateProperties(element, SequenceProperties, path, "unknown_predicate_property");
        var triggersElement = RequireProperty(element, "triggerTools", path, JsonValueKind.Array);
        var count = triggersElement.GetArrayLength();
        if (count is < 1 or > ForbiddenIfPrecededByPredicate.MaxTriggerTools)
        {
            throw Error(
                "trigger_count_limit",
                path + ".triggerTools",
                $"trigger-tool count must be 1..{ForbiddenIfPrecededByPredicate.MaxTriggerTools}.");
        }

        var triggers = new string[count];
        var index = 0;
        foreach (var triggerElement in triggersElement.EnumerateArray())
        {
            var triggerPath = $"{path}.triggerTools[{index}]";
            RequireKind(
                triggerElement,
                JsonValueKind.String,
                triggerPath,
                "trigger_type",
                "trigger tool must be a string.");
            var trigger = triggerElement.GetString()!;
            if (trigger.Length > ForbiddenIfPrecededByPredicate.MaxTriggerToolChars)
            {
                throw Error(
                    "trigger_length_limit",
                    triggerPath,
                    $"trigger tool exceeds {ForbiddenIfPrecededByPredicate.MaxTriggerToolChars} characters.");
            }

            if (string.IsNullOrWhiteSpace(trigger))
            {
                throw Error("empty_trigger", triggerPath, "trigger tool must be non-empty.");
            }

            triggers[index] = trigger;
            index++;
        }

        try
        {
            return new ForbiddenIfPrecededByPredicate(triggers);
        }
        catch (ArgumentException)
        {
            throw Error("invalid_trigger", path + ".triggerTools", "trigger-tool text is invalid.");
        }
    }

    private static ShellMetacharDenyPredicate ParseShellMetacharDeny(
        JsonElement element,
        string argument,
        string path)
    {
        ValidateProperties(element, ShellMetacharProperties, path, "unknown_predicate_property");
        var dialectName = RequireString(element, "dialect", path, ToolContractJsonLimits.MaxNameChars);
        var dialect = dialectName switch
        {
            "PowerShell" => ShellDialect.PowerShell,
            "PosixSh" => ShellDialect.PosixSh,
            "Cmd" => ShellDialect.Cmd,
            _ => throw Error("invalid_shell_dialect", path + ".dialect", "shell dialect is not supported."),
        };

        return new ShellMetacharDenyPredicate(argument, dialect);
    }


    private static RecipientDomainAllowListPredicate ParseRecipientDomains(
        JsonElement element,
        string argument,
        string path)
    {
        ValidateProperties(element, RecipientDomainProperties, path, "unknown_predicate_property");
        var domainsElement = RequireProperty(element, "allowedDomains", path, JsonValueKind.Array);
        var count = domainsElement.GetArrayLength();
        if (count is < 1 or > RecipientDomainPolicy.MaxAllowedDomains)
        {
            throw Error(
                "domain_count_limit",
                path + ".allowedDomains",
                $"allowed-domain count must be 1..{RecipientDomainPolicy.MaxAllowedDomains}.");
        }

        var domains = new string[count];
        var index = 0;
        foreach (var domainElement in domainsElement.EnumerateArray())
        {
            var domainPath = $"{path}.allowedDomains[{index}]";
            RequireKind(
                domainElement,
                JsonValueKind.String,
                domainPath,
                "domain_type",
                "allowed domain must be a string.");
            var domain = domainElement.GetString()!;
            if (domain.Length > RecipientDomainPolicy.MaxDomainChars)
            {
                throw Error(
                    "domain_length_limit",
                    domainPath,
                    $"allowed domain exceeds {RecipientDomainPolicy.MaxDomainChars} characters.");
            }

            if (string.IsNullOrWhiteSpace(domain))
            {
                throw Error("empty_domain", domainPath, "allowed domain must be non-empty.");
            }

            domains[index] = domain;
            index++;
        }

        try
        {
            return new RecipientDomainAllowListPredicate(argument, domains);
        }
        catch (ArgumentException)
        {
            throw Error("invalid_domain", path + ".allowedDomains", "allowed-domain text is invalid.");
        }
    }

    private static MaxDistinctValuesPredicate ParseMaxDistinctValues(
        JsonElement element,
        string argument,
        string path)
    {
        ValidateProperties(element, MaxDistinctProperties, path, "unknown_predicate_property");
        var maxElement = RequireProperty(element, "max", path, JsonValueKind.Number);
        var rawMax = maxElement.GetRawText();
        if (rawMax.Length == 0 || rawMax.Any(character => character is < '0' or > '9') ||
            !maxElement.TryGetInt32(out var max) ||
            max is < MaxDistinctValuesPredicate.Minimum or > MaxDistinctValuesPredicate.Maximum)
        {
            throw Error(
                "distinct_value_limit",
                path + ".max",
                $"maximum distinct values must be an integer from {MaxDistinctValuesPredicate.Minimum} to {MaxDistinctValuesPredicate.Maximum}.");
        }

        return new MaxDistinctValuesPredicate(argument, max);
    }

    private static DeniedKeywordsPredicate ParseDeniedKeywords(JsonElement element, string argument, string path)
    {
        ValidateProperties(element, DeniedKeywordProperties, path, "unknown_predicate_property");
        var keywordsElement = RequireProperty(element, "keywords", path, JsonValueKind.Array);
        var count = keywordsElement.GetArrayLength();
        if (count is < 1 or > ToolContractJsonLimits.MaxKeywordsPerPredicate)
        {
            throw Error(
                "keyword_count_limit",
                path + ".keywords",
                $"keyword count must be 1..{ToolContractJsonLimits.MaxKeywordsPerPredicate}.");
        }

        var keywords = new string[count];
        var index = 0;
        foreach (var elementKeyword in keywordsElement.EnumerateArray())
        {
            var keywordPath = $"{path}.keywords[{index}]";
            RequireKind(
                elementKeyword,
                JsonValueKind.String,
                keywordPath,
                "keyword_type",
                "keyword must be a string.");
            var keyword = elementKeyword.GetString()!;
            if (keyword.Length > ToolContractJsonLimits.MaxKeywordChars)
            {
                throw Error(
                    "keyword_length_limit",
                    keywordPath,
                    $"keyword exceeds {ToolContractJsonLimits.MaxKeywordChars} characters.");
            }

            if (string.IsNullOrWhiteSpace(keyword))
            {
                throw Error("empty_keyword", keywordPath, "keyword must be non-empty.");
            }

            keywords[index] = keyword;
            index++;
        }

        try
        {
            return new DeniedKeywordsPredicate(argument, keywords);
        }
        catch (ArgumentException)
        {
            throw Error("invalid_keyword", path + ".keywords", "keyword text is invalid.");
        }
    }

    private static void ValidatePayloadSize(string json)
    {
        int byteCount;
        try
        {
            byteCount = StrictUtf8.GetByteCount(json);
        }
        catch (EncoderFallbackException)
        {
            throw Error("invalid_utf8", "$", "payload contains invalid Unicode text.");
        }

        if (byteCount > ToolContractJsonLimits.MaxPayloadBytes)
        {
            throw Error(
                "payload_too_large",
                "$",
                $"UTF-8 payload exceeds {ToolContractJsonLimits.MaxPayloadBytes} bytes.");
        }
    }

    private static void ValidateNoDuplicateProperties(JsonElement element, string path, int depth)
    {
        if (depth > ToolContractJsonLimits.MaxJsonDepth)
        {
            throw Error("depth_limit", path, $"JSON depth exceeds {ToolContractJsonLimits.MaxJsonDepth}.");
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                var propertyPath = path + ".*";
                if (!names.Add(property.Name))
                {
                    throw Error("duplicate_property", propertyPath, "duplicate JSON property is not allowed.");
                }

                ValidateNoDuplicateProperties(property.Value, propertyPath, depth + 1);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                ValidateNoDuplicateProperties(item, $"{path}[{index}]", depth + 1);
                index++;
            }
        }
    }

    private static void ValidateProperties(
        JsonElement element,
        IReadOnlySet<string> allowed,
        string path,
        string errorCode)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
            {
                throw Error(errorCode, path + ".*", "unknown property is not allowed.");
            }
        }
    }

    private static string RequireName(JsonElement parent, string propertyName, string parentPath)
    {
        var value = RequireString(parent, propertyName, parentPath, ToolContractJsonLimits.MaxNameChars);
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl))
        {
            throw Error("invalid_name", parentPath + "." + propertyName, "name must be non-empty and contain no control characters.");
        }

        return value.Trim();
    }

    private static string RequireString(
        JsonElement parent,
        string propertyName,
        string parentPath,
        int maxChars)
    {
        var element = RequireProperty(parent, propertyName, parentPath, JsonValueKind.String);
        var value = element.GetString()!;
        if (value.Length > maxChars)
        {
            throw Error(
                "string_length_limit",
                parentPath + "." + propertyName,
                $"string exceeds {maxChars} characters.");
        }

        return value;
    }

    private static JsonElement RequireProperty(
        JsonElement parent,
        string propertyName,
        string parentPath,
        JsonValueKind expectedKind)
    {
        if (!parent.TryGetProperty(propertyName, out var element))
        {
            throw Error("missing_property", parentPath + "." + propertyName, "required property is missing.");
        }

        RequireKind(
            element,
            expectedKind,
            parentPath + "." + propertyName,
            "property_type",
            $"property must be {expectedKind}.");
        return element;
    }

    private static void RequireKind(
        JsonElement element,
        JsonValueKind expected,
        string path,
        string errorCode,
        string detail)
    {
        if (element.ValueKind != expected)
        {
            throw Error(errorCode, path, detail);
        }
    }

    private static GatekeeperContractConfigurationException Error(
        string errorCode,
        string path,
        string detail)
        => new(errorCode, path, detail);
}
