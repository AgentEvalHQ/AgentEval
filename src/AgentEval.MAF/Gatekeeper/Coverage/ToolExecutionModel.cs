// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// How a tool exposed to the model actually executes — i.e. whether Gatekeeper's tool-gate seam
/// (<c>UseAgentEvalToolGate</c>, which plugs into MAF's function-invocation middleware) can ever see the call
/// at all. See the Gatekeeper architecture review, §4 ("The AITool exposure layer") — only <see cref="Microsoft.Extensions.AI.AIFunction"/>-typed
/// tools pass through <c>FunctionInvokingChatClient</c>; provider-hosted tools are executed by the model
/// provider itself and never reach any AgentEval code, in either direction.
/// </summary>
public enum ToolExecutionModel
{
    /// <summary>
    /// A local <see cref="Microsoft.Extensions.AI.AIFunction"/> — invoked in-process by MAF's
    /// <c>FunctionInvokingChatClient</c>, which is exactly the seam <c>UseAgentEvalToolGate</c> plugs into.
    /// This is the ONLY execution model any shipped <see cref="IToolGate"/> can ever inspect or block today.
    /// </summary>
    InterceptedLocalFunction,

    /// <summary>
    /// A provider-hosted tool (e.g. <c>HostedMcpServerTool</c>, <c>HostedCodeInterpreterTool</c>,
    /// <c>HostedWebSearchTool</c>, <c>HostedFileSearchTool</c>, <c>HostedImageGenerationTool</c>,
    /// <c>HostedToolSearchTool</c>) — the model provider (Foundry, OpenAI, etc.) executes the call itself.
    /// No Gatekeeper tool gate is ever invoked for this tool; it is <b>structurally</b>, not just weakly,
    /// unprotected by anything in <c>AgentEval.MAF.Gatekeeper</c> today.
    /// </summary>
    ProviderHostedOpaque,

    /// <summary>
    /// An <see cref="Microsoft.Extensions.AI.AITool"/> that is neither a local <see cref="Microsoft.Extensions.AI.AIFunction"/>
    /// nor a recognized hosted-tool type (e.g. a bare <see cref="Microsoft.Extensions.AI.AIFunctionDeclaration"/> with no
    /// invocation delegate, or a custom/future <c>AITool</c> subtype this analyzer does not yet recognize).
    /// Its execution path cannot be determined statically — treat as unprotected until proven otherwise.
    /// </summary>
    UnknownExecutionModel,
}
