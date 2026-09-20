// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Net.Http.Headers;
using System.Text;

namespace AgentEval.Decisions;

/// <summary>
/// <see cref="IDecisionClient"/> over the System One HTTP protocol: one <c>POST</c> carrying
/// <c>{ model, state, questions }</c>, one reply carrying <c>{ model, answers, usage }</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Strict on the way in.</b> Every question asked must come back with an answer of the matching
/// type and every probability must be a finite number in [0, 1]; anything else is a
/// <see cref="DecisionClientException"/> with <see cref="DecisionFailureKind.InvalidResponse"/>, never
/// a made-up value. The parser does not manufacture a confidence for a yes/no answer, because the
/// protocol does not carry one.
/// </para>
/// <para>
/// <b>No retries.</b> A 429 or 529 is thrown with <see cref="DecisionClientException.IsTransient"/>
/// set so the caller can back off deliberately and record that it did. A retry hidden in here would
/// silently double the latency and cost an eval reports.
/// </para>
/// <para>
/// <b>Dry runs.</b> <see cref="RenderRequest"/> returns the exact bytes a call would send, through the
/// same serializer the call uses, so a caller can print every payload before spending anything.
/// </para>
/// <para>
/// The wire format itself — serialisation, parsing, failure classification — lives in
/// <see cref="SystemOneProtocol"/>; the options in <see cref="SystemOneClientOptions"/>.
/// </para>
/// </remarks>
public sealed class SystemOneDecisionClient : IDecisionClient, IDisposable
{
    private static readonly TimeSpan s_defaultTimeout = TimeSpan.FromSeconds(60);

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly SystemOneClientOptions _options;

    /// <summary>Creates a client.</summary>
    /// <param name="options">Endpoint, key and default model.</param>
    /// <param name="httpClient">
    /// Optional caller-owned <see cref="HttpClient"/> (for a resilience pipeline, a test handler, or a
    /// shared pool). When <see langword="null"/> the client creates and owns one with a 60 s timeout.
    /// </param>
    public SystemOneDecisionClient(SystemOneClientOptions options, HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        // The same rule as every provider endpoint in AgentEval.Providers: absolute https, or http to a
        // loopback host for local testing. Nothing else may carry the bearer — not ftp://127.0.0.1 either.
        if (!AgentEval.Providers.InferenceProviderEnvironment.TryValidateEndpoint(options.Endpoint?.OriginalString, out _, out var why))
            throw new ArgumentException($"Endpoint '{options.Endpoint?.OriginalString}' {why}", nameof(options));
        if (string.IsNullOrWhiteSpace(options.ApiKey))
            throw new ArgumentException("ApiKey is missing.", nameof(options));
        if (string.IsNullOrWhiteSpace(options.Model))
            throw new ArgumentException("Model is missing.", nameof(options));

        _options = options;
        _ownsHttp = httpClient is null;
        _http = httpClient ?? new HttpClient { Timeout = s_defaultTimeout };
    }

    /// <summary>The request URL this client posts to.</summary>
    public Uri Endpoint => _options.Endpoint;

    /// <summary>The model id sent when a request does not override it.</summary>
    public string Model => _options.Model;

    /// <summary>The provider label from the options.</summary>
    public string ProviderName => _options.ProviderName;

    /// <summary>
    /// The exact JSON a <see cref="DecideAsync"/> call would send for <paramref name="request"/>,
    /// produced by the same serializer. Print this in a dry run before the first paid call.
    /// </summary>
    /// <param name="request">The request to render.</param>
    /// <param name="defaultModel">The model to write when the request does not name one.</param>
    public static string RenderRequest(DecisionRequest request, string defaultModel)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultModel);
        return SystemOneProtocol.SerializeRequest(request, defaultModel);
    }

    /// <inheritdoc/>
    public async Task<DecisionResponse> DecideAsync(DecisionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var json = SystemOneProtocol.SerializeRequest(request, _options.Model);
        using var message = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var host = _options.Endpoint.Host;
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(message, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;   // the caller's cancellation, not a provider failure
        }
        catch (OperationCanceledException ex)
        {
            // HttpClient signals its own timeout as a cancellation the caller did not request.
            throw new DecisionClientException(
                DecisionFailureKind.ProviderUnavailable,
                $"The decision call to {host} timed out after {_http.Timeout.TotalSeconds:F0}s without a response.",
                innerException: ex);
        }
        catch (HttpRequestException ex)
        {
            throw new DecisionClientException(
                DecisionFailureKind.Unknown,
                $"The decision call to {host} failed before a response: {ex.Message}",
                innerException: ex);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            // A provider that echoes the request (some 4xx bodies quote the headers) would otherwise
            // put the bearer into our own exception message. Only error bodies are excerpted, so only
            // they are redacted: redacting a success body would corrupt it when the key is a short
            // string that also occurs inside field names (a "k" key turns "input_tokens" into
            // "input_to[redacted]ens"). A success body is parsed verbatim and never quoted.
            if (!response.IsSuccessStatusCode)
                throw SystemOneProtocol.ClassifyFailure((int)response.StatusCode, body.Replace(_options.ApiKey, "[redacted]", StringComparison.Ordinal), host);

            try
            {
                return SystemOneProtocol.ParseResponse(body, request.Questions, host);
            }
            catch (DecisionClientException ex) when (ex.Message.Contains(_options.ApiKey, StringComparison.Ordinal))
            {
                // A malformed 2xx body is excerpted into the exception. If a provider echoed the
                // bearer into that body, the excerpt is the one place it could surface; scrub it.
                throw new DecisionClientException(
                    ex.Kind,
                    ex.Message.Replace(_options.ApiKey, "[redacted]", StringComparison.Ordinal),
                    ex.StatusCode,
                    ex.InnerException);
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_ownsHttp)
            _http.Dispose();
    }
}
