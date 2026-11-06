// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Microsoft365.Auth;

/// <summary>
/// A token provider that can start an interactive sign-in (the device-code flow) and says when one starts and how long
/// the person has to finish it. The readiness check uses it to bound the sign-in by the sign-in code's own expiry
/// (MSAL's device code lives about 15 minutes) instead of a stage timeout meant for network calls.
/// </summary>
internal interface IWorkIqSignInWaitSource
{
    /// <summary>
    /// Invoked when an interactive sign-in starts, with the instant its code expires; set by the host that bounds the
    /// wait. Null means nobody listens. Invoked synchronously on the acquiring thread; it must not throw.
    /// </summary>
    Action<DateTimeOffset>? SignInPrompted { get; set; }
}
