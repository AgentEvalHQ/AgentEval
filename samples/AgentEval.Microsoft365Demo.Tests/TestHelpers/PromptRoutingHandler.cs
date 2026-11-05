// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.Tests.Microsoft365.TestHelpers;

namespace AgentEval.Microsoft365Demo.Tests.TestHelpers;

/// <summary>
/// The innermost handler the demo receives through <c>DemoHostOverrides.InnerHandler</c>: a <c>SendMessage</c> POST whose
/// body contains a registered prompt marker is answered by that marker's own scripted <see cref="FakeWorkIqHandler"/>;
/// every other request goes to <see cref="Default"/>. So one case of a run can pause for confirmation while the prompt
/// turn and the other cases complete, without depending on the order in which the demo sends them. Disposal is a no-op:
/// the demo may build more than one <see cref="HttpClient"/> over the same handler (readiness, then the live agent) and
/// each one disposes its chain.
/// </summary>
internal sealed class PromptRoutingHandler : HttpMessageHandler
{
    private readonly List<(string Marker, FakeWorkIqHandler Handler, HttpMessageInvoker Invoker)> _routes = [];
    private readonly HttpMessageInvoker _default;

    /// <summary>Creates the router over <paramref name="defaultHandler"/>.</summary>
    /// <param name="defaultHandler">Answers every request no route claims.</param>
    public PromptRoutingHandler(FakeWorkIqHandler defaultHandler)
    {
        Default = defaultHandler ?? throw new ArgumentNullException(nameof(defaultHandler));
        _default = new HttpMessageInvoker(defaultHandler, disposeHandler: false);
    }

    /// <summary>The handler that answers every request no route claims.</summary>
    public FakeWorkIqHandler Default { get; }

    /// <summary>
    /// Answers every <c>SendMessage</c> whose body contains <paramref name="promptMarker"/> with
    /// <paramref name="fixtureOrJson"/>; returns the route's own handler so a test can count what it received.
    /// </summary>
    /// <param name="promptMarker">Plain ASCII text without quotes or backslashes (it is searched for in the JSON body).</param>
    /// <param name="fixtureOrJson">An embedded fixture name or literal JSON.</param>
    public FakeWorkIqHandler RouteSendMessage(string promptMarker, string fixtureOrJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(promptMarker);
        var handler = new FakeWorkIqHandler().OnRpc("SendMessage", fixtureOrJson);
        _routes.Add((promptMarker, handler, new HttpMessageInvoker(handler, disposeHandler: false)));
        return handler;
    }

    /// <summary>Every request any handler of this router received, the default's first.</summary>
    public IReadOnlyList<RecordedRequest> AllRequests =>
        Default.Requests.Concat(_routes.SelectMany(r => r.Handler.Requests)).ToArray();

    /// <inheritdoc/>
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Content is not null)
        {
            // Buffer the body once and hand the next handler a re-readable copy with the same content headers.
            var original = request.Content;
            var body = await original.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var copy = new StringContent(body);
            copy.Headers.Clear();
            foreach (var header in original.Headers)
            {
                // The length is the copy's own; every other content header (Content-Type above all) is carried over.
                if (!string.Equals(header.Key, "Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            request.Content = copy;
            if (IsSendMessage(body))
            {
                foreach (var (marker, _, invoker) in _routes)
                {
                    if (body.Contains(marker, StringComparison.Ordinal))
                    {
                        return await invoker.SendAsync(request, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }

        return await _default.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        // Deliberately nothing: every HttpClient the demo builds disposes its chain, and the next one reuses this handler.
    }

    private static bool IsSendMessage(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("method", out var method)
                && method.ValueKind == JsonValueKind.String
                && string.Equals(method.GetString(), "SendMessage", StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
