// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using A2A;
using AgentEval.Microsoft365.Auth;
using AgentEval.Microsoft365.Diagnostics;
using AgentEval.Microsoft365.Protocol;
using Microsoft.Identity.Client;

namespace AgentEval.Microsoft365.Readiness;

/// <summary>The config, auth, discovery and protocol stages of <see cref="ReadinessRun"/>.</summary>
internal sealed partial class ReadinessRun
{
    // ---------------------------------------------------------------------------------------------------------
    // 1. Config (pure: no network, runs before the diagnostics open so they carry the config hash)
    // ---------------------------------------------------------------------------------------------------------

    private ReadinessStageResult LoadConfig()
    {
        var started = _clock.GetTimestamp();
        var evidence = new ReadinessEvidence();
        if (_configFile is not null)
        {
            evidence.Add("config file", _configFile.FullName);
            if (TryHashFile(_configFile) is { } fileSha256)
            {
                evidence.Add("config file sha256", fileSha256);
            }
        }
        else
        {
            evidence.Add("config", "given in code (Microsoft365Config)");
        }

        Microsoft365Config config;
        try
        {
            config = _configInput ?? Microsoft365Config.Load(_configFile!);
            config.Validate();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            AddEnvironment(evidence);
            evidence.Add("error", ex.Message);
            var issue = WorkIqIssueClassifier.FromException(ex, WorkIqTraceStage.Config);
            return Failed(
                ReadinessStage.Config,
                FirstLine(ex.Message),
                "The connector cannot use this config, so no other stage can run. Nothing was sent over the network.",
                ConfigAction(ex.Message),
                issue,
                evidence) with { ElapsedMs = Elapsed(started) };
        }

        _config = config;
        var profile = _options.Profile ?? config.ResolveProfile();
        var cardUri = config.ResolveCardUri();
        evidence.Add("endpoint", config.ResolveEndpointUri().AbsoluteUri);
        evidence.Add("card URI", cardUri.AbsoluteUri);
        evidence.Add("agent", string.IsNullOrWhiteSpace(config.AgentId) ? "default Work IQ agent (agentId not set)" : config.AgentId!.Trim());
        evidence.Add("display name", config.DisplayName);
        evidence.Add("tenant id", config.TenantId.Trim());
        evidence.Add("app (client) id", config.AppClientId.Trim());
        evidence.Add("scope", config.Scope.Trim());
        evidence.Add("profile", $"{profile} ({ProfileSource(config, profile)})");
        evidence.Add("trusted hosts", string.Join(", ", config.TrustedHosts.Select(h => h.Trim())));
        evidence.Add("time zone", string.IsNullOrWhiteSpace(config.TimeZone) ? "not set (no Location metadata is sent with a prompt)" : config.TimeZone!.Trim());
        evidence.Add("config sha256", WorkIqDiagnosticsFactory.ComputeConfigSha256(config));
        AddEnvironment(evidence);

        var warnings = new List<string>();
        if (profile == DiagnosticProfile.Native118Diagnostics)
        {
            warnings.Add("Profile native118 is EXPERIMENTAL: it requests two diagnostic output modes observed in Microsoft's native evaluator 1.18.0; it is not a documented contract.");
        }

        // Shape checks only (no network): the sign-in is the authority, but a value that cannot be right saves a round trip.
        if (!Guid.TryParse(config.TenantId.Trim(), out _) && !config.TenantId.Trim().Contains('.', StringComparison.Ordinal))
        {
            warnings.Add($"tenantId '{config.TenantId.Trim()}' is neither a GUID nor a domain name: the sign-in will most likely fail with {WorkIqAadstsCatalog.TenantNotFoundCode} (tenant not found). Use the Directory (tenant) ID from the app registration's Overview page.");
        }

        if (!Guid.TryParse(config.AppClientId.Trim(), out _))
        {
            warnings.Add($"appClientId '{config.AppClientId.Trim()}' is not a GUID: an app registration's Application (client) ID always is, so the sign-in will most likely fail with {WorkIqAadstsCatalog.ApplicationNotFoundCode} (application not found). Copy it from the app registration's Overview page.");
        }

        return Passed(
            ReadinessStage.Config,
            $"config valid: {config.DisplayName}; {(string.IsNullOrWhiteSpace(config.AgentId) ? "default agent" : "agent " + config.AgentId!.Trim())}; tenant {config.TenantId.Trim()}; profile {profile}",
            "The config is complete and consistent. Nothing was sent yet.",
            "Nothing to do.",
            evidence,
            warnings) with { ElapsedMs = Elapsed(started) };
    }

    private string ProfileSource(Microsoft365Config config, DiagnosticProfile effective)
    {
        if (_options.Profile is null)
        {
            return string.IsNullOrWhiteSpace(config.DiagnosticProfile) ? "default: diagnosticProfile not set in the config" : "from the config's diagnosticProfile";
        }

        // The host names its own source (a CLI option, a demo option); the check itself does not assume a command line.
        var source = string.IsNullOrWhiteSpace(_options.ProfileSource) ? "the options" : _options.ProfileSource.Trim();
        var configured = config.ResolveProfile();
        return configured == effective
            ? $"from {source}; the config says the same"
            : $"from {source}; it overrides the config's {configured}";
    }

    private static void AddEnvironment(ReadinessEvidence evidence)
    {
        var environment = ReadinessEnvironment.Current();
        evidence.Add("adapter version", WorkIqDiagnostics.AdapterVersion);
        evidence.Add("A2A version", WorkIqDiagnostics.A2AVersion);
        evidence.Add("OS", environment.OperatingSystem);
        evidence.Add(".NET", environment.DotNet);
        var offset = TimeZoneInfo.Local.GetUtcOffset(DateTimeOffset.UtcNow);
        evidence.Add("machine time zone", $"{environment.MachineTimeZone} (UTC{(offset < TimeSpan.Zero ? "-" : "+")}{offset.Duration():hh\\:mm})");
    }

    private static string? TryHashFile(FileInfo file)
    {
        try
        {
            if (!File.Exists(file.FullName))
            {
                return null;
            }

            return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file.FullName))).ToLowerInvariant();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>What to do about a config error, naming the field the validation message names.</summary>
    internal static string ConfigAction(string message)
    {
        if (message.StartsWith("Microsoft 365 config file not found", StringComparison.Ordinal))
        {
            return "Check the path given to --m365-config: the file does not exist. Copy samples/datasets/m365-config.template.json and fill in tenantId and appClientId.";
        }

        if (message.StartsWith("Invalid Microsoft 365 config JSON", StringComparison.Ordinal))
        {
            return "Fix the JSON syntax at the line and position the message names, then run the doctor again.";
        }

        const string placeholderMarker = "still holds the template placeholder";
        if (message.Contains(placeholderMarker, StringComparison.Ordinal))
        {
            var quoted = message.IndexOf("field '", StringComparison.Ordinal);
            var name = quoted >= 0 && message.IndexOf('\'', quoted + 7) is var close && close > quoted + 7
                ? message[(quoted + 7)..close]
                : "the field the message names";
            return $"Replace the template placeholder in '{name}' with the real value (tenantId and appClientId are on the app registration's Overview page in the Entra admin center), then run the doctor again.";
        }

        const string missingMarker = "missing required field(s): ";
        var missing = message.IndexOf(missingMarker, StringComparison.Ordinal);
        if (missing >= 0)
        {
            var start = missing + missingMarker.Length;
            var end = message.IndexOf('.', start);
            var fields = end > start ? message[start..end] : message[start..];
            return $"Add the missing field(s) {fields.Trim()} to the config file (tenantId and appClientId come from the app registration's Overview page), then run the doctor again.";
        }

        const string fieldMarker = "field '";
        var field = message.IndexOf(fieldMarker, StringComparison.Ordinal);
        if (field >= 0)
        {
            var start = field + fieldMarker.Length;
            var end = message.IndexOf('\'', start);
            if (end > start)
            {
                return $"Fix the field '{message[start..end]}' in the config file as the message says, then run the doctor again.";
            }
        }

        return "Fix the config as the message says, then run the doctor again.";
    }

    // ---------------------------------------------------------------------------------------------------------
    // 2. Auth
    // ---------------------------------------------------------------------------------------------------------

    private async Task<ReadinessStageResult> RunAuthAsync(CancellationToken cancellationToken)
    {
        var provider = TokenProvider;
        var deviceCode = provider as WorkIqDeviceCodeTokenProvider;
        var evidence = new ReadinessEvidence();
        evidence.Add(
            "token provider",
            deviceCode is null
                ? $"{provider.GetType().Name} (supplied by the host; it reports no expiry, tenant or account)"
                : "MSAL device code (silent from the OS-encrypted cache first, device-code sign-in otherwise)");
        if (deviceCode is not null)
        {
            var cachePath = deviceCode.CacheFilePath;
            evidence.Add("token cache file", cachePath);
            evidence.Add("token cache file existed before this run", YesNo(File.Exists(cachePath)));
        }

        var started = _clock.GetTimestamp();
        using var stage = StageTokenSource(cancellationToken, _options.StageTimeout);
        bool received;
        try
        {
            var token = await provider.GetAccessTokenAsync(stage.Token).ConfigureAwait(false);
            received = !string.IsNullOrWhiteSpace(token);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (stage.IsCancellationRequested)
        {
            evidence.Add("token acquired", "no");
            evidence.Add("stage timeout", $"{Seconds(_options.StageTimeout)} s");
            evidence.Add("error", $"{ex.GetType().Name}: {FirstLine(ex.Message)}");
            if (_signInCodeExpiresOn is { } expired)
            {
                // A device-code prompt was shown: the stage waited for the code's own lifetime, not the stage timeout.
                var at = expired.UtcDateTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
                evidence.Add("device code expired at (UTC)", at);
                return Failed(
                    ReadinessStage.Auth,
                    $"sign-in not completed: the device code expired at {at} UTC before the sign-in finished",
                    "A device-code sign-in was started (the prompt above shows the page and the code), but nobody completed it before the code expired. Nothing was sent to Work IQ.",
                    "Run the doctor again, open the page the prompt names, enter the code and sign in as the test user while the code is valid (about 15 minutes).",
                    WorkIqIssue.Create(
                        WorkIqTraceStage.Auth,
                        WorkIqIssueCategory.Authentication,
                        "auth.sign_in_timeout",
                        $"the device code expired at {at} UTC before the sign-in was completed",
                        "the device-code sign-in was not completed before its code expired; run again and complete it in the browser"),
                    evidence) with { ElapsedMs = Elapsed(started) };
            }

            return Failed(
                ReadinessStage.Auth,
                $"sign-in not completed in time: no token within {Seconds(_options.StageTimeout)} s",
                "The token provider gave no token before the stage timeout, and no device-code sign-in was started (a cached sign-in or the host's token provider did not answer). Nothing was sent to Work IQ.",
                $"Run the doctor again; if it repeats, check the network path to login.microsoftonline.com (or the host's token provider) and raise --timeout-seconds (now {Seconds(_options.StageTimeout)} s).",
                WorkIqIssue.Create(
                    WorkIqTraceStage.Auth,
                    WorkIqIssueCategory.Authentication,
                    "auth.sign_in_timeout",
                    $"no token within the stage timeout of {Seconds(_options.StageTimeout)} s",
                    "the token provider did not answer in time and no device-code sign-in was started; run again"),
                evidence) with { ElapsedMs = Elapsed(started) };
        }
        catch (Exception ex)
        {
            return AuthFailed(ex, evidence) with { ElapsedMs = Elapsed(started) };
        }

        if (!received)
        {
            evidence.Add("token acquired", "no (the token provider returned an empty token)");
            return Failed(
                ReadinessStage.Auth,
                "the token provider returned an empty token",
                "No usable token was acquired, so no request can be authenticated.",
                "Run the doctor again and complete the sign-in; if a host supplies the token provider, check it returns the Work IQ access token.",
                WorkIqIssue.Create(WorkIqTraceStage.Auth, WorkIqIssueCategory.Authentication, "auth.empty_token", "the token provider returned an empty token", WorkIqIssueClassifier.CauseEmptyToken),
                evidence) with { ElapsedMs = Elapsed(started) };
        }

        evidence.Add("token acquired", "yes (the token itself is never shown, stored or traced)");
        var warnings = new List<string>();
        string summary;
        if (deviceCode?.LastTokenInfo is { } info)
        {
            evidence.Add("acquired", info.AcquiredSilently ? "silently from the token cache" : "by device-code sign-in");
            evidence.Add("expires on (UTC)", info.ExpiresOn.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            evidence.Add("tenant id (from the token)", info.TenantId);
            evidence.Add("tenant matches the config", TenantMatch(info.TenantId, Config.TenantId));
            evidence.Add("account", info.AccountHint);
            evidence.Add("scopes", info.Scopes.Count == 0 ? null : string.Join(" ", info.Scopes));
            if (TenantMatch(info.TenantId, Config.TenantId) == "no")
            {
                warnings.Add($"The token was issued for tenant {info.TenantId}, not the config's tenantId {Config.TenantId.Trim()}: the signed-in account may be a guest or from another tenant.");
            }

            if (info.ExpiresOn - _clock.GetUtcNow() < TimeSpan.FromMinutes(5))
            {
                warnings.Add("The token expires in less than five minutes; the next stages refresh it silently.");
            }

            summary = $"token acquired {(info.AcquiredSilently ? "silently from the cache" : "by device-code sign-in")}; tenant {info.TenantId ?? "not reported"}; expires {info.ExpiresOn.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC";
        }
        else
        {
            evidence.Add("token details", deviceCode is null ? "not reported by this token provider" : "not reported");
            summary = "token acquired from the host's token provider";
        }

        return Passed(
            ReadinessStage.Auth,
            summary,
            "Sign-in works: a delegated token for the Work IQ scope was acquired. The token itself never appears in any output.",
            "Nothing to do.",
            evidence,
            warnings) with { ElapsedMs = Elapsed(started) };
    }

    private ReadinessStageResult AuthFailed(Exception ex, ReadinessEvidence evidence)
    {
        var msal = FindMsal(ex);
        var issue = WorkIqIssueClassifier.FromException(msal ?? ex, WorkIqTraceStage.Auth);
        var aadsts = issue.AadstsCode ?? WorkIqAadstsCatalog.ExtractCode(ex.Message);
        evidence.Add("token acquired", "no");
        evidence.Add("AADSTS code", aadsts ?? "none in the error");
        if (msal is not null)
        {
            evidence.Add("MSAL error code", string.IsNullOrWhiteSpace(msal.ErrorCode) ? "none" : msal.ErrorCode);
            if (msal is MsalServiceException { StatusCode: > 0 } service)
            {
                evidence.Add("HTTP status (Entra)", service.StatusCode);
            }

            if (!string.IsNullOrWhiteSpace(msal.CorrelationId))
            {
                evidence.Add("correlation id", msal.CorrelationId);
            }
        }

        evidence.Add("error", issue.Message);

        string meaning;
        string action;
        if (WorkIqAadstsCatalog.TryGet(aadsts, out var entry))
        {
            meaning = Capitalize(entry.Cause) + ".";
            action = Capitalize(entry.Fix) + ".";
        }
        else if (aadsts is not null)
        {
            meaning = issue.SuggestedCause;
            action = $"Look {aadsts} up at {WorkIqAadstsCatalog.ReferenceUrl}, fix what it names, then run the doctor again; send the report folder back if it is unclear.";
        }
        else
        {
            meaning = issue.SuggestedCause;
            action = "Run the doctor again and complete the device-code sign-in; if it fails the same way, send the report folder back.";
        }

        var summary = aadsts is not null
            ? $"sign-in failed with {aadsts}" + (entry is null ? "" : $": {entry.Cause}")
            : $"sign-in failed: {issue.Code}: {FirstLine(msal?.Message ?? ex.Message)}";
        return Failed(ReadinessStage.Auth, summary, meaning, action, issue, evidence);
    }

    private static MsalException? FindMsal(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is MsalException msal)
            {
                return msal;
            }

            if (current is AggregateException { InnerExceptions.Count: > 0 } aggregate && FindMsal(aggregate.InnerExceptions[0]) is { } inner)
            {
                return inner;
            }
        }

        return null;
    }

    private static string TenantMatch(string? tokenTenant, string configTenant)
    {
        if (string.IsNullOrWhiteSpace(tokenTenant))
        {
            return "unknown (the token's tenant was not reported)";
        }

        if (!Guid.TryParse(tokenTenant, out var fromToken) || !Guid.TryParse(configTenant.Trim(), out var fromConfig))
        {
            return "not comparable (the config's tenantId is not a GUID)";
        }

        return fromToken == fromConfig ? "yes" : "no";
    }

    private static string Capitalize(string text) =>
        string.IsNullOrEmpty(text) ? text : char.ToUpperInvariant(text[0]) + text[1..];

    // ---------------------------------------------------------------------------------------------------------
    // 3. Discovery
    // ---------------------------------------------------------------------------------------------------------

    private async Task<ReadinessStageResult> RunDiscoveryAsync(CancellationToken cancellationToken)
    {
        var client = Client;
        var evidence = new ReadinessEvidence();
        evidence.Add("card URI", Config.ResolveCardUri().AbsoluteUri);
        evidence.Add("trusted hosts", string.Join(", ", Config.TrustedHosts.Select(h => h.Trim())));
        var mark = _trace.Count;
        var started = _clock.GetTimestamp();
        using var stage = StageTokenSource(cancellationToken, _options.StageTimeout);

        RawAgentCard card;
        try
        {
            card = await client.EnsureDiscoveredAsync(stage.Token).ConfigureAwait(false);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (WorkIqProtocolException ex)
        {
            return NetworkStageFailed(ReadinessStage.Discovery, ex.Failure, evidence, timedOut: stage.IsCancellationRequested) with { ElapsedMs = Elapsed(started) };
        }

        var fetched = EventsSince(mark).LastOrDefault(e => e.Code == "discovery.card_fetched");
        evidence.Add("HTTP status", fetched?.HttpStatus?.ToString(CultureInfo.InvariantCulture) ?? (fetched is null ? "not recorded (the card was fetched earlier in this run)" : null));
        evidence.Add("request-id", fetched is null ? "not recorded" : fetched.RequestId ?? "absent (the service sent no request-id header)");
        evidence.Add("bytes", Encoding.UTF8.GetByteCount(card.RawJson));
        evidence.Add("card name", card.Card.Name);
        evidence.Add("card version", card.Card.Version);
        var interfaces = card.Card.SupportedInterfaces ?? [];
        if (interfaces.Count == 0)
        {
            evidence.Add("interface", "none advertised");
        }

        for (var i = 0; i < interfaces.Count; i++)
        {
            var candidate = interfaces[i];
            evidence.Add(
                string.Create(CultureInfo.InvariantCulture, $"interface {i + 1}"),
                candidate is null ? "null entry" : $"{candidate.ProtocolBinding ?? "(no binding)"} · {candidate.ProtocolVersion ?? "(no version)"} · {HostOf(candidate.Url)}");
        }

        var selected = interfaces.FirstOrDefault(i => i is not null && Uri.TryCreate(i.Url, UriKind.Absolute, out var url) && url == card.SelectedEndpoint);
        evidence.Add(
            "chosen interface",
            $"{selected?.ProtocolBinding ?? WorkIqTrustPolicy.RequiredBinding} · {card.SelectedProtocolVersion} · {card.SelectedEndpoint.Host}{card.SelectedEndpoint.AbsolutePath}");
        evidence.Add("capabilities.streaming", Declared(card.Card.Capabilities?.Streaming));
        evidence.Add("capabilities.pushNotifications", Declared(card.Card.Capabilities?.PushNotifications));
        evidence.Add("skills", card.Card.Skills?.Count ?? 0);
        evidence.Add("card sha256", card.Sha256);

        return Passed(
            ReadinessStage.Discovery,
            $"card '{card.Card.Name}' fetched; {interfaces.Count} interface(s); chose {selected?.ProtocolBinding ?? WorkIqTrustPolicy.RequiredBinding} {card.SelectedProtocolVersion} at {card.SelectedEndpoint.Host}",
            "The agent card offers A2A 1.0 over JSON-RPC on a trusted host, and the token was accepted for the card.",
            "Nothing to do.",
            evidence) with { ElapsedMs = Elapsed(started) };
    }

    private static string HostOf(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : "(unparsable url)";

    private static string Declared(bool? value) => value is null ? "not declared" : value.Value ? "true" : "false";

    // ---------------------------------------------------------------------------------------------------------
    // 4. Protocol
    // ---------------------------------------------------------------------------------------------------------

    private async Task<ReadinessStageResult> RunProtocolAsync(CancellationToken cancellationToken)
    {
        var client = Client;
        var taskId = Microsoft365ReadinessCheck.ProtocolTaskIdPrefix + Guid.NewGuid().ToString("N");
        var evidence = new ReadinessEvidence();
        evidence.Add("request", $"GetTask for the random task id {taskId}; the expected answer is JSON-RPC TaskNotFound (-32001)");
        var filter = _issueFilter!;
        var started = _clock.GetTimestamp();
        using var stage = StageTokenSource(cancellationToken, _options.StageTimeout);
        try
        {
            WorkIqRpcResult<AgentTask> reply;
            using (filter.Expect(i => i.RpcErrorCode == (int)A2AErrorCode.TaskNotFound && string.Equals(i.TaskId, taskId, StringComparison.Ordinal)))
            using (client.ExpectFailure(f => f.RpcErrorCode == (int)A2AErrorCode.TaskNotFound && string.Equals(f.TaskId, taskId, StringComparison.Ordinal)))
            {
                reply = await client.GetTaskAsync(taskId, historyLength: null, stage.Token).ConfigureAwait(false);
            }

            var state = WorkIqA2AClient.RawState(reply.Value.Status?.State);
            evidence.Add("endpoint", EndpointText(client));
            evidence.Add("HTTP status", reply.HttpStatus);
            evidence.Add("JSON-RPC code", "none: a task came back");
            evidence.Add("returned task state", state ?? "(absent)");
            evidence.Add("request-id", reply.RequestId ?? "absent (the service sent no request-id header)");
            evidence.Add("elapsed ms", Ms(Elapsed(started)));
            return Passed(
                ReadinessStage.Protocol,
                $"the service returned a task (state {state ?? "absent"}) for a random task id",
                "The endpoint speaks A2A 1.0 JSON-RPC and accepted the token.",
                "Nothing to fix; send the report folder back so the maintainers can look at the unexpected task.",
                evidence,
                ["Unexpected: GetTask for a random task id returned a task instead of TaskNotFound (-32001). The reply was in the A2A 1.0 shape and the token was accepted, so this stage passes."]) with { ElapsedMs = Elapsed(started) };
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (WorkIqProtocolException ex) when (ex.Failure.RpcErrorCode == (int)A2AErrorCode.TaskNotFound)
        {
            var elapsed = Elapsed(started);
            evidence.Add("endpoint", EndpointText(client));
            evidence.Add("HTTP status", ex.Failure.HttpStatus?.ToString(CultureInfo.InvariantCulture));
            evidence.Add("JSON-RPC code", "-32001 (TaskNotFound)");
            evidence.Add("request-id", ex.Failure.RequestId ?? "absent (the service sent no request-id header)");
            evidence.Add("elapsed ms", Ms(elapsed));

            // The client traced the reply at Info as rpc.expected_error (no Error line, no issue); the filter is the
            // backstop for any other layer that would register it. Either way, say once why it is not an anomaly.
            _diagnostics.Trace(
                WorkIqTraceStage.Doctor,
                "doctor.protocol.expected_not_found",
                "the JSON-RPC TaskNotFound (-32001) above (rpc.expected_error) is the expected answer to the doctor's GetTask on a random task id; it is not an anomaly and was not written to the issue register",
                taskId: taskId,
                requestId: ex.Failure.RequestId,
                httpStatus: ex.Failure.HttpStatus,
                rpcErrorCode: ex.Failure.RpcErrorCode);

            return Passed(
                ReadinessStage.Protocol,
                "the service answered GetTask on a random task id with JSON-RPC TaskNotFound (-32001)",
                "The endpoint speaks A2A 1.0 JSON-RPC and accepted the token.",
                _requested.Contains(ReadinessStage.Probe)
                    ? "Nothing to do."
                    : "Nothing to do. Next: add --probe --i-understand-live-side-effects to send one real prompt.",
                evidence) with { ElapsedMs = elapsed };
        }
        catch (WorkIqProtocolException ex)
        {
            evidence.Add("endpoint", client.Card is null ? "not discovered" : EndpointText(client));
            return NetworkStageFailed(ReadinessStage.Protocol, ex.Failure, evidence, timedOut: stage.IsCancellationRequested) with { ElapsedMs = Elapsed(started) };
        }
    }

    private static string EndpointText(WorkIqA2AClient client) =>
        client.Card is { } card ? $"{card.SelectedEndpoint.Host}{card.SelectedEndpoint.AbsolutePath} (A2A {card.SelectedProtocolVersion})" : "not discovered";

    // ---------------------------------------------------------------------------------------------------------
    // Shared failure mapping of the network stages
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>Fails a discovery or protocol stage from the transport's structured failure: summary, meaning, action and the classified issue.</summary>
    private ReadinessStageResult NetworkStageFailed(ReadinessStage stage, WorkIqFailure failure, ReadinessEvidence evidence, bool timedOut)
    {
        var issue = WorkIqIssueClassifier.FromFailure(failure, UnderlyingStage(stage));
        evidence.Add("HTTP status", failure.HttpStatus?.ToString(CultureInfo.InvariantCulture) ?? "none (no HTTP response)");
        evidence.Add("JSON-RPC code", failure.RpcErrorCode?.ToString(CultureInfo.InvariantCulture) ?? "none");
        evidence.Add("request-id", failure.RequestId ?? "absent");
        evidence.Add("failure", $"{failure.Category} ({failure.Certainty}): {failure.SafeMessage}");
        if (timedOut)
        {
            evidence.Add("stage timeout", $"{Seconds(_options.StageTimeout)} s (reached)");
        }

        var during = stage == ReadinessStage.Protocol && failure.Operation == WorkIqOperation.Discover
            ? "discovery failed, so GetTask could not be sent: "
            : "";
        var summary = during + (failure switch
        {
            _ when WorkIqIssueClassifier.IsSignInNotCompleted(failure) =>
                _signInCodeExpiresOn is { } expires
                    ? $"sign-in not completed: the device code expired at {expires.UtcDateTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture)} UTC before the sign-in finished; nothing was sent"
                    : $"sign-in not completed: no token before the stage stopped waiting ({Seconds(_options.StageTimeout)} s); nothing was sent",
            { HttpStatus: 401 } or { Category: WorkIqFailureCategory.Unauthorized } =>
                $"HTTP 401 on {OperationText(failure)}: the token was rejected",
            { HttpStatus: 403 } or { Category: WorkIqFailureCategory.Forbidden } =>
                $"HTTP 403 on {OperationText(failure)}: admin consent is missing, the user is not in the usage-based billing plan, or Work IQ is not enabled",
            { Category: WorkIqFailureCategory.UntrustedHost } =>
                "the card points at a host outside trustedHosts; no token was sent there",
            { RpcErrorCode: (int)A2AErrorCode.VersionNotSupported } =>
                "the service rejected A2A 1.0 (JSON-RPC -32009 VersionNotSupported)",
            { Category: WorkIqFailureCategory.ProtocolVersionMismatch } when failure.Operation == WorkIqOperation.Discover =>
                "the card advertises no A2A 1.0 JSON-RPC interface (no 1.0 interface)",
            { Category: WorkIqFailureCategory.ProtocolVersionMismatch } =>
                "the reply has the A2A 0.3 shape; this connector speaks 1.0 only",
            { HttpStatus: 404 } when failure.Operation == WorkIqOperation.GetTask =>
                "HTTP 404 on GetTask instead of the JSON-RPC TaskNotFound error (-32001)",
            { HttpStatus: 404 } =>
                $"HTTP 404 on {OperationText(failure)}: the endpoint or agent was not found",
            { RpcErrorCode: { } code } =>
                $"JSON-RPC error {code.ToString(CultureInfo.InvariantCulture)} on {OperationText(failure)} instead of TaskNotFound (-32001)",
            { Category: WorkIqFailureCategory.Timeout } or { Category: WorkIqFailureCategory.Canceled } when timedOut =>
                $"no answer within the stage timeout of {Seconds(_options.StageTimeout)} s",
            _ => $"{failure.Category} on {OperationText(failure)}: {failure.SafeMessage}",
        });

        var meaning = failure switch
        {
            _ when WorkIqIssueClassifier.IsSignInNotCompleted(failure) =>
                "No token was acquired, so nothing was sent to Work IQ: the device-code sign-in this stage started was not completed (or the token provider did not answer) before the wait ended. This is not a network timeout.",
            { HttpStatus: 404 } when failure.Operation == WorkIqOperation.GetTask =>
                "The RPC endpoint answered HTTP 404 where a JSON-RPC endpoint answers a missing task with error -32001: either the selected interface URL is not the Work IQ JSON-RPC endpoint, or the service reports a missing task differently than A2A 1.0 says.",
            { Category: WorkIqFailureCategory.UntrustedHost } =>
                "Discovery found no A2A 1.0 JSON-RPC interface on a trusted host, so the token was never sent to it.",
            { Category: WorkIqFailureCategory.ProtocolVersionMismatch } when failure.Operation == WorkIqOperation.Discover =>
                "The agent card does not offer the protocol this connector speaks (A2A 1.0 over JSON-RPC). The connector never downgrades to 0.3.",
            _ => issue.SuggestedCause,
        };

        return Failed(stage, summary, meaning, ActionFor(failure), issue, evidence);
    }

    private static string OperationText(WorkIqFailure failure) => failure.Operation switch
    {
        WorkIqOperation.Discover => "the agent card",
        WorkIqOperation.GetTask => "GetTask",
        WorkIqOperation.CancelTask => "CancelTask",
        WorkIqOperation.Send => "SendMessage",
        WorkIqOperation.Stream => "SendStreamingMessage",
        WorkIqOperation.Subscribe => "SubscribeToTask",
        _ => "the request",
    };

    /// <summary>What a tester does about a transport failure, by its HTTP status, JSON-RPC code and category.</summary>
    private string ActionFor(WorkIqFailure failure) => failure switch
    {
        _ when WorkIqIssueClassifier.IsSignInNotCompleted(failure) =>
            "Run the doctor again (or `--stage auth` alone first) and finish the browser sign-in while the code is valid: open the page the prompt names, enter the code, sign in as the test user.",
        { HttpStatus: 401 } or { Category: WorkIqFailureCategory.Unauthorized } =>
            $"Run the doctor again to sign in afresh. Check that 'scope' is {Microsoft365Config.DefaultScope} and that tenantId and appClientId name the app registration that holds WorkIQAgent.Ask.",
        { HttpStatus: 403 } or { Category: WorkIqFailureCategory.Forbidden } =>
            "If the reply names the required scopes, ask an administrator to grant admin consent for WorkIQAgent.Ask. Otherwise check that the test user is in the usage-based billing plan (wait 30 minutes after adding them) and that Work IQ is enabled in the tenant. The steps are in docs/microsoft365-tenant-setup.md; then run again.",
        { Category: WorkIqFailureCategory.UntrustedHost } =>
            "Inspect the card (the trace's discovery events, or --m365-capture-raw, show its interface hosts). Add a host to trustedHosts only if it is clearly Microsoft's; otherwise send the report folder back.",
        { Category: WorkIqFailureCategory.ProtocolVersionMismatch } or { RpcErrorCode: (int)A2AErrorCode.VersionNotSupported } =>
            "Send the report folder back; nothing on your side causes this. Do not change the config to work around it.",
        { HttpStatus: 404 } when failure.Operation == WorkIqOperation.GetTask =>
            "Send the report folder back; the HTTP status and request-id identify the call.",
        { HttpStatus: 404 } or { Category: WorkIqFailureCategory.NotFound } =>
            "Check 'endpoint' and 'agentId' in the config (leave agentId null for the default agent); the agent must be reachable by the signed-in user.",
        { HttpStatus: 429 } or { Category: WorkIqFailureCategory.RateLimited } =>
            "Wait a few minutes (the issue register names the Retry-After when the service sent one) and run again.",
        { Category: WorkIqFailureCategory.Transport } =>
            "Check the network, proxy and DNS path from this machine to the endpoint host, then run again.",
        { Category: WorkIqFailureCategory.Timeout } or { Category: WorkIqFailureCategory.Canceled } =>
            $"Run again; if it repeats, raise --timeout-seconds (now {Seconds(_options.StageTimeout)} s) or check the network path to the endpoint.",
        { Category: WorkIqFailureCategory.Malformed } =>
            "Send the report folder back; a run with --m365-capture-raw <dir> shows what arrived.",
        _ => "Send the report folder back; the HTTP status, JSON-RPC code and request-id identify the call for Microsoft support.",
    };
}
