// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using Profile = AgentEval.Microsoft365.DiagnosticProfile;

namespace AgentEval.Microsoft365;

/// <summary>
/// Connection settings for a Microsoft 365 agent evaluated over Work IQ A2A 1.0, in code via
/// <see cref="Microsoft365AgentFactory.BuildLive"/> or via <c>agenteval eval|bench --sut microsoft365 --m365-config &lt;file.json&gt;</c>.
/// Identifies the Work IQ endpoint, the optional agent, the Entra tenant and the public-client app registration
/// used for delegated device-code auth. It carries <b>no secret</b> — the token is acquired at run time and never
/// stored here — and makes no network call: <see cref="Load"/>, <see cref="Validate"/> and the resolvers are pure.
/// </summary>
public sealed record Microsoft365Config
{
    /// <summary>The public Work IQ A2A service root.</summary>
    public const string DefaultEndpoint = "https://workiq.svc.cloud.microsoft/a2a/";

    /// <summary>The delegated permission scope (admin consent required; includes Microsoft 365 reads <b>and writes</b> for the signed-in user).</summary>
    public const string DefaultScope = "api://workiq.svc.cloud.microsoft/WorkIQAgent.Ask";

    /// <summary>The only host trusted by default; a bearer token is never forwarded to a host outside <see cref="TrustedHosts"/>.</summary>
    public const string DefaultTrustedHost = "workiq.svc.cloud.microsoft";

    /// <summary>The report-facing name used when neither <see cref="AgentName"/> nor <see cref="AgentId"/> is set.</summary>
    public const string DefaultDisplayName = "Work IQ default agent";

    /// <summary>The Work IQ A2A service root (absolute <c>https</c> URL; defaults to <see cref="DefaultEndpoint"/>).</summary>
    public string Endpoint { get; init; } = DefaultEndpoint;

    /// <summary>
    /// The agent to target, as an opaque id (prefix and dot included, never normalised). Null targets the default
    /// Work IQ agent. Its card is discovered at <c>{Endpoint}/{AgentId}/.well-known/agent-card.json</c>.
    /// </summary>
    public string? AgentId { get; init; }

    /// <summary>The Entra tenant id the app registration lives in.</summary>
    public string TenantId { get; init; } = "";

    /// <summary>The Entra app-registration client id used for device-code auth (a public client — never a secret).</summary>
    public string AppClientId { get; init; } = "";

    /// <summary>The token scope; defaults to <see cref="DefaultScope"/>. The audience is Work IQ, not Microsoft Graph.</summary>
    public string Scope { get; init; } = DefaultScope;

    /// <summary>Optional display name for reports.</summary>
    public string? AgentName { get; init; }

    /// <summary>
    /// The diagnostic profile as free text so a bad value fails <see cref="Validate"/> with a clear, config-scoped
    /// error. Matches <see cref="Microsoft365.DiagnosticProfile"/> member names case-insensitively:
    /// <c>PublicAnswerOnly</c> (default when omitted) or <c>Native118Diagnostics</c> (experimental).
    /// </summary>
    public string? DiagnosticProfile { get; init; }

    /// <summary>
    /// Optional IANA time zone (for example <c>Europe/Zurich</c>). When set, every user message carries
    /// <c>metadata.Location { timeZone, timeZoneOffset }</c> with a DST-aware offset for the evaluation instant.
    /// </summary>
    public string? TimeZone { get; init; }

    /// <summary>
    /// Hosts the provider may send a bearer token to (card discovery and every advertised interface URL). The
    /// endpoint's host must be listed. Defaults to <see cref="DefaultTrustedHost"/>.
    /// </summary>
    public IReadOnlyList<string> TrustedHosts { get; init; } = new[] { DefaultTrustedHost };

    /// <summary>Report-facing name: <see cref="AgentName"/>, else <see cref="AgentId"/>, else <see cref="DefaultDisplayName"/>.</summary>
    public string DisplayName =>
        !string.IsNullOrWhiteSpace(AgentName) ? AgentName!
        : !string.IsNullOrWhiteSpace(AgentId) ? AgentId!
        : DefaultDisplayName;

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Loads + validates the config from a JSON file, or throws a clear, actionable, file-scoped error.</summary>
    /// <param name="file">The JSON file.</param>
    /// <exception cref="InvalidOperationException">The file is missing, unreadable, not valid JSON, or fails <see cref="Validate"/>.</exception>
    public static Microsoft365Config Load(FileInfo file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (!file.Exists)
        {
            throw new InvalidOperationException($"Microsoft 365 config file not found: {file.FullName}");
        }

        string json;
        try
        {
            json = File.ReadAllText(file.FullName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"Microsoft 365 config file could not be read ({file.Name}): {ex.Message}", ex);
        }

        Microsoft365Config? cfg;
        try
        {
            cfg = JsonSerializer.Deserialize<Microsoft365Config>(json, JsonOpts);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Invalid Microsoft 365 config JSON ({file.Name}): {ex.Message}", ex);
        }

        if (cfg is null)
        {
            throw new InvalidOperationException($"Microsoft 365 config deserialized to null: {file.FullName}");
        }

        cfg.Validate();
        return cfg;
    }

    /// <summary>
    /// Throws if <c>tenantId</c>, <c>appClientId</c>, <c>endpoint</c> or <c>scope</c> is blank; if the endpoint is
    /// not an absolute <c>https</c> URL without query/fragment; if <c>trustedHosts</c> is empty, has a blank entry,
    /// or omits the endpoint's host; if <c>agentId</c> contains a path separator, query/fragment character,
    /// whitespace or control character; if <c>diagnosticProfile</c> is not a recognised profile name; or if
    /// <c>timeZone</c> is not a time zone id this system knows.
    /// </summary>
    /// <exception cref="InvalidOperationException">The config is invalid; the message names the field and the valid values.</exception>
    public void Validate()
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(TenantId)) { missing.Add("tenantId"); }
        if (string.IsNullOrWhiteSpace(AppClientId)) { missing.Add("appClientId"); }
        if (string.IsNullOrWhiteSpace(Endpoint)) { missing.Add("endpoint"); }
        if (string.IsNullOrWhiteSpace(Scope)) { missing.Add("scope"); }

        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                "Microsoft 365 config is missing required field(s): " + string.Join(", ", missing) +
                ". Required: tenantId, appClientId (endpoint and scope have defaults and must not be blank when set).");
        }

        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out var endpoint)
            || !string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Microsoft 365 config field 'endpoint' must be an absolute https URL, got: '{Endpoint}'. Default: {DefaultEndpoint}");
        }

        if (!string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment))
        {
            throw new InvalidOperationException(
                $"Microsoft 365 config field 'endpoint' must not carry a query string or fragment, got: '{Endpoint}'.");
        }

        if (TrustedHosts is null || TrustedHosts.Count == 0)
        {
            throw new InvalidOperationException(
                $"Microsoft 365 config field 'trustedHosts' must list at least one host (default: [\"{DefaultTrustedHost}\"]).");
        }

        if (TrustedHosts.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidOperationException("Microsoft 365 config field 'trustedHosts' contains a blank entry.");
        }

        if (!TrustedHosts.Any(h => string.Equals(h.Trim(), endpoint.Host, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"Microsoft 365 config field 'trustedHosts' does not include the endpoint host '{endpoint.Host}'; " +
                "a bearer token is never sent to a host outside trustedHosts, so discovery would be refused.");
        }

        if (!string.IsNullOrWhiteSpace(AgentId) && AgentId!.Any(IsForbiddenInAgentId))
        {
            throw new InvalidOperationException(
                "Microsoft 365 config field 'agentId' must be a single opaque path segment: no '/', '\\', '?', '#', whitespace or control characters.");
        }

        if (!TryResolveProfile(out _))
        {
            throw new InvalidOperationException(
                $"Microsoft 365 config field 'diagnosticProfile' has an unrecognized value: '{DiagnosticProfile}'. Valid values " +
                "(case-insensitive): " + string.Join(", ", ValidProfileNames) + ".");
        }

        if (!string.IsNullOrWhiteSpace(TimeZone) && !TimeZoneInfo.TryFindSystemTimeZoneById(TimeZone!, out _))
        {
            throw new InvalidOperationException(
                $"Microsoft 365 config field 'timeZone' is not a known time zone id on this system: '{TimeZone}'. Use an IANA id such as Europe/Zurich.");
        }
    }

    /// <summary>
    /// Resolves <see cref="DiagnosticProfile"/> to the enum — <see cref="Microsoft365.DiagnosticProfile.PublicAnswerOnly"/> when blank,
    /// matched case-insensitively against the member names otherwise.
    /// </summary>
    /// <exception cref="InvalidOperationException">The value is not a recognised profile name. Call <see cref="Validate"/> first so this never fires on a validated config.</exception>
    public Profile ResolveProfile()
    {
        if (TryResolveProfile(out var profile))
        {
            return profile;
        }

        throw new InvalidOperationException(
            $"Microsoft 365 config field 'diagnosticProfile' has an unrecognized value: '{DiagnosticProfile}'. Valid values " +
            "(case-insensitive): " + string.Join(", ", ValidProfileNames) + ".");
    }

    /// <summary>The validated endpoint as a URI, normalised to end with <c>/</c> so relative resolution never drops its last path segment.</summary>
    /// <exception cref="InvalidOperationException"><see cref="Endpoint"/> is not an absolute https URL.</exception>
    public Uri ResolveEndpointUri()
    {
        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out var endpoint)
            || !string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Microsoft 365 config field 'endpoint' must be an absolute https URL, got: '{Endpoint}'.");
        }

        return endpoint.AbsoluteUri.EndsWith('/') ? endpoint : new Uri(endpoint.AbsoluteUri + "/", UriKind.Absolute);
    }

    /// <summary>
    /// The agent-card URL: <c>{Endpoint}/.well-known/agent-card.json</c> for the default agent, or
    /// <c>{Endpoint}/{AgentId}/.well-known/agent-card.json</c> for a selected agent. Built by string concatenation
    /// on purpose: relative <see cref="Uri"/> resolution would drop the endpoint's <c>a2a</c> segment or the agent segment.
    /// </summary>
    /// <exception cref="InvalidOperationException"><see cref="Endpoint"/> is not an absolute https URL.</exception>
    public Uri ResolveCardUri()
    {
        var root = ResolveEndpointUri().AbsoluteUri.TrimEnd('/');
        var path = string.IsNullOrWhiteSpace(AgentId)
            ? $"{root}/.well-known/agent-card.json"
            : $"{root}/{AgentId!.Trim()}/.well-known/agent-card.json";
        return new Uri(path, UriKind.Absolute);
    }

    private bool TryResolveProfile(out Profile profile)
    {
        if (string.IsNullOrWhiteSpace(DiagnosticProfile))
        {
            profile = Profile.PublicAnswerOnly;
            return true;
        }

        return Enum.TryParse(DiagnosticProfile.Trim(), ignoreCase: true, out profile) && Enum.IsDefined(profile);
    }

    private static bool IsForbiddenInAgentId(char c) =>
        c is '/' or '\\' or '?' or '#' || char.IsWhiteSpace(c) || char.IsControl(c);

    private static IEnumerable<string> ValidProfileNames => Enum.GetNames<Profile>();
}
