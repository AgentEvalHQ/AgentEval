// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;

// The whole adapter is a preview: it binds to the alpha ResponsibleAI.AgentHooks contract (AGENT-HOOKS-0.1), which
// may change before 1.0, and AEVP is a draft profile. Marking the assembly gives every consumer the diagnostic on
// first use, so nobody takes a dependency on it believing it is covered by Gatekeeper v1's stability promise.
[assembly: Experimental("AGENTEVAL_AGENTHOOKS_PREVIEW001")]
