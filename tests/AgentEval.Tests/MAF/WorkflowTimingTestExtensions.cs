// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.MAF;
using Microsoft.Agents.AI.Workflows;

namespace AgentEval.Tests.MAF;

internal static class WorkflowTimingTestExtensions
{
    internal static Workflow BuildWithTiming(this WorkflowBuilder builder, MAFWorkflowInstrumentation owner) => owner.Build(builder);
}
