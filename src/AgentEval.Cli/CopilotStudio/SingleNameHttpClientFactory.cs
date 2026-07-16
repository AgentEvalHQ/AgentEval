// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Cli.CopilotStudio;

/// <summary>
/// A minimal <see cref="IHttpClientFactory"/> for the one named client <c>CopilotClient</c> needs. AgentEval's CLI
/// has no ambient DI container — every other factory in this folder/its siblings (e.g. <c>AzureChatAgentFactory</c>)
/// constructs its client ad-hoc rather than resolving one from a <c>ServiceCollection</c> — so this avoids pulling
/// in a full <c>Microsoft.Extensions.Http</c> + <c>ServiceCollection</c> registration just to satisfy one
/// constructor parameter for one named client, for the lifetime of one CLI invocation.
/// </summary>
/// <remarks>
/// Returns the <b>same</b> <see cref="HttpClient"/> instance for the one name it was constructed for (an
/// <see cref="HttpClient"/> is designed to be long-lived and reused across many requests, not created per-call —
/// the standard guidance <see cref="IHttpClientFactory"/> itself exists to make easy). Disposed together with this
/// factory, which the CLI does not do mid-process: like <c>AzureOpenAIClient</c> elsewhere in this codebase, the
/// underlying <see cref="HttpClient"/> lives for the remainder of the process and is reclaimed at exit.
/// </remarks>
internal sealed class SingleNameHttpClientFactory : IHttpClientFactory, IDisposable
{
    private readonly string _name;
    private readonly Lazy<HttpClient> _client;
    private bool _disposed;

    public SingleNameHttpClientFactory(string name, TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _name = name;
        _client = new Lazy<HttpClient>(() => new HttpClient { Timeout = timeout ?? TimeSpan.FromSeconds(100) });
    }

    public HttpClient CreateClient(string name)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!string.Equals(name, _name, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"This IHttpClientFactory only knows the '{_name}' named client (requested '{name}').");
        }

        return _client.Value;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_client.IsValueCreated)
        {
            _client.Value.Dispose();
        }
    }
}
