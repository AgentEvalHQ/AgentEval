// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
namespace AgentEval.RedTeam;

/// <summary>
/// Where an injected payload is delivered to the model (Wave B, Pillar 4). Distinguishes a payload <i>inlined</i>
/// into the user turn (today's lower-credibility proxy) from one delivered through a real trust boundary — a tool
/// result or a retrieved document — so reports can break results down <c>by_surface</c> and never present an
/// inlined proxy as a real-boundary result.
/// </summary>
public enum InjectionSurface
{
    /// <summary>Payload inlined into the user message (the classic proxy). Labeled, kept as a fallback for un-instrumented SUTs.</summary>
    UserMessage = 0,

    /// <summary>Payload spliced into a tool result (<c>FunctionResultContent</c>) — AgentDojo's tool-output placeholder.</summary>
    ToolOutput = 1,

    /// <summary>Payload spliced into a retrieved-document / RAG context block.</summary>
    RetrievedDocument = 2,

    /// <summary>
    /// Payload spliced into a MAF Agent Skill's <c>description</c>/instructions, which land in the SYSTEM
    /// PROMPT via the <c>{skills}</c> placeholder on <c>load_skill</c> — a HIGHER-trust position than a
    /// retrieved document (Skills Phase 3).
    /// </summary>
    SkillInstruction = 3,

    /// <summary>Payload spliced into a <c>read_skill_resource</c> tool output (Skills Phase 3).</summary>
    SkillResource = 4,
}
