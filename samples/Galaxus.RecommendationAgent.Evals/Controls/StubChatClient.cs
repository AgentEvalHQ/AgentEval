// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Galaxus.RecommendationAgent.Evals.Controls;

/// <summary>
/// A scripted <see cref="IChatClient"/> that spends nothing and calls nobody, used by
/// <c>--dry-run</c> to exercise the entire evaluation path before a paid run.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is a standing protocol in this repository, not a nicety.</b> Before any paid run:
/// dry-run every case against a stub model (spends nothing, real code path, stub deliberately
/// implausible so a silent fallback is visible), then one real single-case run, then the full run.
/// The first execution of that protocol elsewhere in this repo caught a crash. Eval 01 is fifteen
/// live turns and Eval 02 is nine more; discovering a wrong argument name at turn thirteen is an
/// avoidable expense.
/// </para>
/// <para>
/// <b>Deliberately implausible.</b> The stub's prose says so in capital letters and its
/// recommendations are chosen by index, not by reasoning. If a dry-run report ever looks like a
/// plausible agent result, something is falling back to a real model and that must be visible
/// rather than pleasant.
/// </para>
/// <para>
/// <b>It goes through the REAL function-invocation loop.</b> The stub emits
/// <see cref="FunctionCallContent"/>, so MEAI's <c>FunctionInvokingChatClient</c> actually invokes
/// <c>GalaxusTools</c>, the tools actually write to the budget and the capture scope, and the
/// resulting messages are extracted by the real <c>ToolUsageExtractor</c>. That is what makes the
/// dry run informative about anything other than the stub.
/// </para>
/// </remarks>
public sealed class StubChatClient : IChatClient
{
    /// <summary>The prose the stub emits. Deliberately unmistakable.</summary>
    public const string StubText =
        "DRY RUN — THIS TEXT CAME FROM A STUB, NOT FROM A MODEL. No inference happened. "
      + "If this sentence appears in a report you meant to be real, the run did not reach Azure.";

    private readonly Func<IReadOnlyList<ChatMessage>, IReadOnlyList<AIContent>> _decide;
    private int _calls;

    /// <summary>How many times the stub was asked for a completion. Non-zero proves the path ran.</summary>
    public int CallCount => _calls;

    /// <summary>
    /// Creates a stub whose reply is decided by the INCOMING messages, exactly as a real model's is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two successive versions of this stub were wrong, and the dry run caught both.</b> The
    /// first held a script indexed by an instance-wide turn counter; because the instance is shared
    /// across cases, only the first case ever got tool calls and the other thirteen silently
    /// exercised an empty turn while the report still looked plausible. The second alternated on
    /// call parity, which fixed that but left the outcome depending on how many model calls each
    /// preceding case happened to consume — an approval-gated turn costs one call, an executed-tool
    /// turn costs two, so the parity drifted and C-12's priming turn shifted the graded turn's
    /// behaviour.
    /// </para>
    /// <para>
    /// Deciding from the incoming conversation removes the state entirely: the stub emits its tool
    /// calls when the conversation does not yet contain their results, and prose once it does. That
    /// is what a model does, and it makes every case independent of every other.
    /// </para>
    /// </remarks>
    /// <param name="decide">Given the conversation so far, the contents to reply with. Empty means prose.</param>
    public StubChatClient(Func<IReadOnlyList<ChatMessage>, IReadOnlyList<AIContent>> decide)
    {
        ArgumentNullException.ThrowIfNull(decide);
        _decide = decide;
    }

    /// <summary>
    /// The plan Eval 01's dry run uses: one turn that presents two real, in-stock, correctly-cited
    /// products, then prose.
    /// </summary>
    /// <remarks>
    /// Two products, not five, and always the SAME two: the dry run must not accidentally look like
    /// a good result. It exists to prove the plumbing carries arguments end to end — the sku the
    /// stub wrote is the sku the grader reads.
    /// </remarks>
    /// <param name="skus">Products to present. Citations are taken from the catalogue so they resolve.</param>
    public static StubChatClient PresentingAgent(params string[] skus)
    {
        var catalogue = Catalogue.Default;
        var chosen = skus.Length > 0 ? skus : ["GLX-8003", "GLX-2001"];
        int sequence = 0;

        return new StubChatClient(conversation =>
        {
            // Already presented in this turn? Then finish with prose and end the loop.
            if (HasToolResult(conversation, PresentedCall.ToolName)) return [];

            var contents = new List<AIContent>();
            foreach (string sku in chosen)
            {
                if (!catalogue.TryGet(sku, out var product) || product is null) continue;
                string citation = Broken03_SingleShotWorkflow.FirstResolvingCitation(product) ?? "";

                contents.Add(new FunctionCallContent(
                    $"stub-present-{sequence++}",
                    PresentedCall.ToolName,
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        [PresentRecommendationArguments.Sku] = product.Id,
                        [PresentRecommendationArguments.Reason] = StubText,
                        [PresentRecommendationArguments.Evidence] = citation,
                        [PresentRecommendationArguments.OutOfStock] = product.StockUnits == 0,
                    }));
            }
            return contents;
        });
    }

    /// <summary>
    /// The text the ask-first stub emits on its first turn: a "question" with no tool call, so
    /// the harness's second-turn path is exercised. Deliberately implausible, like everything else
    /// the stub says.
    /// </summary>
    public const string StubClarifyingQuestion =
        "DRY RUN — THIS IS A STUB ASKING A CLARIFYING QUESTION, NOT A MODEL. Which of these do you own? "
      + "What are you looking for?";

    /// <summary>
    /// The plan that exercises the SECOND-TURN path: on a turn whose conversation matches
    /// <paramref name="askFirstWhen"/> and that carries no customer reply yet, it presents nothing
    /// and asks; once the conversation carries the harness's reply
    /// (<see cref="Adapters.ClarifyingAnswer.OpeningLine"/>), it presents like
    /// <see cref="PresentingAgent"/>. Every other turn presents on turn 1.
    /// </summary>
    /// <remarks>
    /// This exists so a dry run can prove the second turn is WIRED — that a silent first turn is
    /// answered, that the reply reaches the same session, and that the merged trace carries the
    /// turn-2 presentations — for the one persona or case the trigger names, while every other cell
    /// keeps the single-turn stub behaviour the rest of the dry-run checks were written against.
    /// The decision is made from the incoming conversation, for the same reason the other stubs
    /// make theirs that way: no instance state, no parity drift.
    /// </remarks>
    /// <param name="askFirstWhen">Text the first user message must contain for the stub to ask before presenting — a persona id or an utterance.</param>
    /// <param name="skus">Products to present when it presents. Citations are taken from the catalogue so they resolve.</param>
    public static StubChatClient AskThenPresentAgent(string askFirstWhen, params string[] skus)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(askFirstWhen);

        var presenting = PresentingAgent(skus);
        return new StubChatClient(conversation =>
        {
            bool triggered = conversation
                .Where(m => m.Role == ChatRole.User)
                .Any(m => m.Text.Contains(askFirstWhen, StringComparison.Ordinal));
            bool replied = conversation
                .Where(m => m.Role == ChatRole.User)
                .Any(m => m.Text.Contains(Adapters.ClarifyingAnswer.OpeningLine, StringComparison.Ordinal));

            if (triggered && !replied)
                return [new TextContent(StubClarifyingQuestion)];

            return presenting._decide(conversation);
        });
    }

    /// <summary>
    /// The plan that probes the approval path: a single <c>PlaceOrder</c> call.
    /// </summary>
    /// <remarks>
    /// This exists to settle one load-bearing unknown. Cases C-11 and C-12 rest on
    /// <c>PlaceOrder</c> being VISIBLE in the tool trace when it is registered as an
    /// <c>ApprovalRequiredAIFunction</c> — if MAF swallowed the call and surfaced only an approval
    /// request, <c>NeverCallTool("PlaceOrder")</c> would have a chance floor of 1.0 all over again
    /// and C-12 would be unpassable. The probe answers it by measurement rather than by reading the
    /// design.
    /// </remarks>
    /// <param name="sku">The product to order.</param>
    public static StubChatClient OrderingAgent(string sku = "GLX-7001")
    {
        int sequence = 0;
        return new StubChatClient(conversation =>
        {
            // An approval-gated tool never returns a result, so "have I already asked?" is answered
            // by looking for the approval request the framework put into the conversation.
            if (HasApprovalRequest(conversation) || HasToolResult(conversation, "PlaceOrder")) return [];

            return
            [
                new FunctionCallContent($"stub-order-{sequence++}", "PlaceOrder",
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["sku"] = sku,
                        ["quantity"] = 1,
                    }),
            ];
        });
    }

    /// <summary>
    /// The two-turn stub for case C-12: it PRESENTS when nothing has been presented in this turn,
    /// and ORDERS once the conversation shows a product was already shown.
    /// </summary>
    /// <remarks>
    /// C-11 and C-12 need different stubs and that is not a convenience. C-11 must be probed by an
    /// agent that orders with no confirmation at all, or its D4 detector is never exercised; C-12
    /// must be probed by one that shows a product first, or its neutral priming turn leaves an
    /// outstanding approval request and the graded turn cannot run. One stub cannot do both, and
    /// making a single stub decide by reading the customer's wording would be building a small model
    /// rather than a probe.
    /// </remarks>
    /// <param name="sku">The product to show and then order.</param>
    public static StubChatClient PresentThenOrderAgent(string sku = "GLX-7001")
    {
        var catalogue = Catalogue.Default;
        int sequence = 0;

        return new StubChatClient(conversation =>
        {
            if (HasApprovalRequest(conversation) || HasToolResult(conversation, "PlaceOrder")) return [];

            // Turn 2 (the graded turn) starts with the customer's confirmation and no presentation
            // of its own, so "has anything been presented in THIS turn" is the wrong question. The
            // question is whether this turn has already done its one job.
            bool presentedThisTurn = HasToolResult(conversation, PresentedCall.ToolName);
            bool customerConfirmed = conversation
                .Where(m => m.Role == ChatRole.User)
                .Any(m => m.Text.Contains("confirmed", StringComparison.OrdinalIgnoreCase));

            if (customerConfirmed)
            {
                return
                [
                    new FunctionCallContent($"stub-confirmed-order-{sequence++}", "PlaceOrder",
                        new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["sku"] = sku,
                            ["quantity"] = 1,
                        }),
                ];
            }

            if (presentedThisTurn) return [];
            if (!catalogue.TryGet(sku, out var product) || product is null) return [];

            string citation = Broken03_SingleShotWorkflow.FirstResolvingCitation(product) ?? "";
            return
            [
                new FunctionCallContent($"stub-show-{sequence++}", PresentedCall.ToolName,
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        [PresentRecommendationArguments.Sku] = product.Id,
                        [PresentRecommendationArguments.Reason] = StubText,
                        [PresentRecommendationArguments.Evidence] = citation,
                        [PresentRecommendationArguments.OutOfStock] = product.StockUnits == 0,
                    }),
            ];
        });
    }

    /// <summary>True when the conversation already carries a result for the named tool.</summary>
    /// <param name="conversation">The messages handed to the model.</param>
    /// <param name="toolName">The tool to look for.</param>
    public static bool HasToolResult(IReadOnlyList<ChatMessage> conversation, string toolName)
    {
        var callIds = conversation
            .SelectMany(m => m.Contents)
            .OfType<FunctionCallContent>()
            .Where(c => string.Equals(c.Name, toolName, StringComparison.Ordinal))
            .Select(c => c.CallId)
            .ToHashSet(StringComparer.Ordinal);

        return callIds.Count > 0
            && conversation.SelectMany(m => m.Contents)
                           .OfType<FunctionResultContent>()
                           .Any(r => callIds.Contains(r.CallId));
    }

    /// <summary>True when the conversation already carries an approval request.</summary>
    /// <param name="conversation">The messages handed to the model.</param>
    public static bool HasApprovalRequest(IReadOnlyList<ChatMessage> conversation) =>
        conversation.SelectMany(m => m.Contents).Any(c => c is ToolApprovalRequestContent);

    /// <inheritdoc/>
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        _calls++;
        var conversation = messages as IReadOnlyList<ChatMessage> ?? [.. messages];
        IReadOnlyList<AIContent> contents = _decide(conversation);

        var message = contents.Count > 0
            ? new ChatMessage(ChatRole.Assistant, [.. contents])
            : new ChatMessage(ChatRole.Assistant, StubText);

        // The finish reason follows what the contents ARE, not whether there are any: the
        // ask-first plan returns prose as content, and a prose turn stamped ToolCalls would be a
        // lie about itself.
        bool hasToolCall = contents.Any(c => c is FunctionCallContent);

        return Task.FromResult(new ChatResponse(message)
        {
            ModelId = "stub",
            FinishReason = hasToolCall ? ChatFinishReason.ToolCalls : ChatFinishReason.Stop,
        });
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        foreach (var update in response.ToChatResponseUpdates()) yield return update;
    }

    /// <inheritdoc/>
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
    }

    /// <inheritdoc/>
    public void Dispose() { }
}
