// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using AgentEval.Testing;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentEval.Tests.MAF.Gatekeeper;

/// <summary>
/// Phase 1.7 executable seam spike. These tests intentionally prove capability boundaries; they are not a
/// production "opaque tool gate" and must not be presented as per-invocation interception.
/// </summary>
public sealed class OpaqueHostedToolSeamSpikeTests
{
    [Fact]
    public async Task DelegatingClient_HostedWebSearchRequest_SeesMarkerAndCanRemoveCapabilityPerRequest()
    {
        var inner = new HostedSearchEvidenceClient();
        using var policy = new HostedToolRequestProbeClient(inner, removeHostedWebSearch: true);
        var hosted = new HostedWebSearchTool(
            new Dictionary<string, object?> { ["providerRegion"] = "probe-region" });
        var local = AIFunctionFactory.Create(() => "local", "local_lookup");
        var callerOptions = new ChatOptions { Tools = [hosted, local] };

        var response = await policy.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Find the current source.")],
            callerOptions);

        var observation = Assert.Single(policy.RequestTools);
        Assert.Equal(nameof(HostedWebSearchTool), observation.RuntimeType);
        Assert.Equal("web_search", observation.Name);
        Assert.Contains("providerRegion", observation.AdditionalPropertyNames);

        // The policy clones options. It can disable the hosted capability for this whole model request without
        // mutating caller-owned options or stripping unrelated local functions.
        Assert.Equal(2, callerOptions.Tools!.Count);
        Assert.IsType<HostedWebSearchTool>(callerOptions.Tools[0]);
        var forwarded = Assert.Single(inner.ReceivedOptions);
        var forwardedTool = Assert.Single(forwarded!.Tools!);
        Assert.Same(local, forwardedTool);
        Assert.Single(response.Messages);
    }

    [Fact]
    public async Task DelegatingClient_HostedWebSearchResponse_SeesPostResponseEvidenceButNoLocalFunctionCall()
    {
        var inner = new HostedSearchEvidenceClient();
        using var observer = new HostedToolRequestProbeClient(inner, removeHostedWebSearch: false);
        var options = new ChatOptions { Tools = [new HostedWebSearchTool()] };

        var response = await observer.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Find the current source.")],
            options);

        Assert.Equal(1, inner.CallCount);
        Assert.Empty(response.Messages.SelectMany(static message => message.Contents).OfType<FunctionCallContent>());
        var citation = Assert.Single(observer.Citations);
        Assert.Equal("web_search", citation.ToolName);
        Assert.Equal(new Uri("https://example.test/source"), citation.Url);
        var uri = Assert.Single(observer.Uris);
        Assert.Equal(new Uri("https://example.test/source"), uri);
    }

    [Fact]
    public async Task DelegatingClient_StreamingHostedWebSearch_SeesSameRequestAndPostResponseBoundaries()
    {
        var inner = new HostedSearchEvidenceClient();
        using var observer = new HostedToolRequestProbeClient(inner, removeHostedWebSearch: false);
        var options = new ChatOptions { Tools = [new HostedWebSearchTool()] };
        var updates = new List<ChatResponseUpdate>();

        await foreach (var update in observer.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "Find the current source.")],
            options))
        {
            updates.Add(update);
        }

        Assert.Single(observer.RequestTools);
        Assert.Single(observer.Citations);
        Assert.Single(observer.Uris);
        Assert.Empty(updates.SelectMany(static update => update.Contents).OfType<FunctionCallContent>());
    }

    [Fact]
    public async Task DelegatingClient_HostedCapabilityDenied_CanOnlyRejectWholeRequestBeforeProviderCall()
    {
        var inner = new ScriptedChatClient().AddText("must not be reached");
        using var policy = new HostedToolRequestProbeClient(
            inner,
            removeHostedWebSearch: false,
            rejectRequestWhenHostedWebSearchPresent: true);
        var options = new ChatOptions { Tools = [new HostedWebSearchTool()] };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => policy.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "Search once.")],
                options));

        Assert.Contains("entire model request", error.Message);
        Assert.Equal(0, inner.CallCount);
    }

    private sealed record HostedToolObservation(
        string RuntimeType,
        string Name,
        IReadOnlyList<string> AdditionalPropertyNames);

    private sealed class HostedToolRequestProbeClient : DelegatingChatClient
    {
        private readonly bool _removeHostedWebSearch;
        private readonly bool _rejectRequestWhenHostedWebSearchPresent;

        public HostedToolRequestProbeClient(
            IChatClient inner,
            bool removeHostedWebSearch,
            bool rejectRequestWhenHostedWebSearchPresent = false)
            : base(inner)
        {
            _removeHostedWebSearch = removeHostedWebSearch;
            _rejectRequestWhenHostedWebSearchPresent = rejectRequestWhenHostedWebSearchPresent;
        }

        public List<HostedToolObservation> RequestTools { get; } = [];
        public List<CitationAnnotation> Citations { get; } = [];
        public List<Uri> Uris { get; } = [];

        public override async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var forwardedOptions = PrepareOptions(options);
            var response = await base.GetResponseAsync(
                messages,
                forwardedOptions,
                cancellationToken).ConfigureAwait(false);
            Observe(response.Messages.SelectMany(static message => message.Contents));
            return response;
        }

        public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var forwardedOptions = PrepareOptions(options);
            await foreach (var update in base.GetStreamingResponseAsync(
                messages,
                forwardedOptions,
                cancellationToken).ConfigureAwait(false))
            {
                Observe(update.Contents);
                yield return update;
            }
        }

        private ChatOptions? PrepareOptions(ChatOptions? options)
        {
            var hostedTools = options?.Tools?.OfType<HostedWebSearchTool>().ToArray() ?? [];
            foreach (var tool in hostedTools)
            {
                RequestTools.Add(new HostedToolObservation(
                    tool.GetType().Name,
                    tool.Name,
                    tool.AdditionalProperties?.Keys.Order(StringComparer.Ordinal).ToArray() ?? []));
            }

            if (_rejectRequestWhenHostedWebSearchPresent && hostedTools.Length > 0)
            {
                throw new InvalidOperationException(
                    "Opaque hosted execution has no per-invocation pre-execution callback; " +
                    "the policy can only reject the entire model request.");
            }

            var forwardedOptions = options?.Clone();
            if (_removeHostedWebSearch && forwardedOptions?.Tools is not null)
            {
                forwardedOptions.Tools = forwardedOptions.Tools
                    .Where(static tool => tool is not HostedWebSearchTool)
                    .ToList();
            }

            return forwardedOptions;
        }

        private void Observe(IEnumerable<AIContent> contents)
        {
            foreach (var content in contents)
            {
                if (content.Annotations is not null)
                {
                    Citations.AddRange(content.Annotations.OfType<CitationAnnotation>());
                }

                if (content is UriContent uri)
                {
                    Uris.Add(uri.Uri);
                }
            }
        }
    }

    private sealed class HostedSearchEvidenceClient : IChatClient
    {
        public List<ChatOptions?> ReceivedOptions { get; } = [];
        public int CallCount => ReceivedOptions.Count;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            ReceivedOptions.Add(options);
            var citation = new CitationAnnotation
            {
                ToolName = "web_search",
                Url = new Uri("https://example.test/source"),
                Title = "Probe source",
                Snippet = "Sanitized evidence.",
            };
            var text = new TextContent("Provider-composed answer.")
            {
                Annotations = [citation],
            };
            var source = new UriContent(new Uri("https://example.test/source"), "text/html");
            return Task.FromResult(new ChatResponse(
                new ChatMessage(ChatRole.Assistant, [text, source])));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            foreach (var message in response.Messages)
            {
                yield return new ChatResponseUpdate(message.Role, message.Contents);
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
