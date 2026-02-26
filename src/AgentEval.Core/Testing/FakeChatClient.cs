// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using Microsoft.Extensions.AI;

namespace AgentEval.Testing;

/// <summary>
/// Fake IChatClient for unit testing that returns predetermined responses.
/// No external mocking library required.
/// </summary>
/// <remarks>
/// Use this client in your tests to avoid making real API calls while
/// still testing your agent evaluation logic.
/// </remarks>
/// <example>
/// <code>
/// var fake = new FakeChatClient()
///     .WithResponse("{\"score\": 95, \"reasoning\": \"Good response\"}")
///     .WithResponse("{\"score\": 50, \"reasoning\": \"Average response\"}");
///     
/// var metric = new FaithfulnessMetric(fake);
/// var result = await metric.EvaluateAsync(context);
/// </code>
/// </example>
public class FakeChatClient : IChatClient
{
    private readonly Queue<string> _responses = new();
    
    /// <summary>
    /// All prompts/messages received by this fake client.
    /// </summary>
    public List<IEnumerable<ChatMessage>> ReceivedMessages { get; } = new();
    
    /// <summary>
    /// Count of calls made to GetResponseAsync.
    /// </summary>
    public int CallCount => ReceivedMessages.Count;
    
    /// <summary>
    /// If true, throws an exception on next call.
    /// </summary>
    public bool ThrowOnNextCall { get; set; }
    
    /// <summary>
    /// Exception message to throw when ThrowOnNextCall is true.
    /// </summary>
    public string ThrowMessage { get; set; } = "Simulated API error";
    
    /// <summary>
    /// Create a fake chat client that returns the specified responses in order.
    /// </summary>
    /// <param name="responses">JSON or text responses to return.</param>
    public FakeChatClient(params string[] responses)
    {
        foreach (var r in responses)
        {
            _responses.Enqueue(r);
        }
    }
    
    /// <summary>
    /// Queue additional responses.
    /// </summary>
    public FakeChatClient WithResponse(string response)
    {
        _responses.Enqueue(response);
        return this;
    }
    
    /// <summary>
    /// Get the text of the last prompt sent.
    /// </summary>
    public string? LastPrompt => ReceivedMessages.LastOrDefault()
        ?.Select(m => m.Text)
        .Where(t => !string.IsNullOrEmpty(t))
        .LastOrDefault();
    
    /// <inheritdoc />
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (ThrowOnNextCall)
        {
            ThrowOnNextCall = false;
            throw new InvalidOperationException(ThrowMessage);
        }
        
        ReceivedMessages.Add(messages.ToList());
        
        var responseText = _responses.Count > 0 
            ? _responses.Dequeue() 
            : "{\"score\": 50, \"reasoning\": \"Default fake response\"}";
        
        var assistantMessage = new ChatMessage(ChatRole.Assistant, responseText);
        var response = new ChatResponse(assistantMessage);
        
        return Task.FromResult(response);
    }
    
    /// <inheritdoc />
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException("Streaming not implemented in FakeChatClient. Not needed for metric tests.");
    }
    
    /// <inheritdoc />
    public object? GetService(Type serviceType, object? key = null) => null;
    
    /// <inheritdoc />
    public void Dispose()
    {
        // Nothing to dispose
    }
}
