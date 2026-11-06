// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;

// The Microsoft 365 provider is a preview: its public API may change in any release, and its live path is not
// independently verified against a tenant yet. Every public type of this assembly is experimental, so a consumer
// opts in knowingly by suppressing the diagnostic: <NoWarn>$(NoWarn);AGENTEVAL_M365_PREVIEW001</NoWarn>.
[assembly: Experimental("AGENTEVAL_M365_PREVIEW001")]
