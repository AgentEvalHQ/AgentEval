// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Reflection;

namespace AgentEval.Tests.MAF.AgentHooks.Ctk;

/// <summary>
/// Binding to the AGENT-HOOKS-0.1 reference core — the Rust engine that defines what the spec actually means,
/// used here as an independent oracle over the verdicts and contexts our adapter produces.
/// </summary>
/// <remarks>
/// <para><b>Why reflection rather than P/Invoke.</b> An earlier attempt bound directly to the native
/// <c>ah_*</c> exports and read the returned pointers with <see cref="System.Runtime.InteropServices.Marshal.PtrToStringUTF8"/>,
/// which produced heap garbage. The conclusion drawn at the time — "the exports do not return NUL-terminated
/// UTF-8" — was <b>wrong</b>. <c>ResponsibleAI.AgentHooks</c> ships managed wrappers over those same exports on
/// an <c>internal static class Native</c>, and they return correct strings (<c>SpecVersion()</c> yields
/// <c>"agent-hooks/0.1"</c>). The defect was in our marshalling, not the vendor ABI, so the right fix is to
/// stop re-implementing a layer the vendor already ships correctly.</para>
///
/// <para>The wrapper is <c>internal</c> and <c>InternalsVisibleTo</c> names only <c>AgentHooks.Conformance</c>
/// and <c>AgentHooks.Tests</c>. Naming one of our assemblies <c>AgentHooks.Conformance</c> to borrow that grant
/// would squat the vendor's assembly identity and collide when the package ships, so this reflects instead.
/// Reflection over another package's internals is a real coupling risk and is accepted <b>only</b> because this
/// is test-only code whose entire purpose is to detect drift: if the vendor renames these members, these tests
/// fail loudly via <see cref="MissingMethodException"/> rather than degrading into a silent pass.</para>
///
/// <para>The native library ships as <c>runtimes/{rid}/native/agent_hooks_ffi.*</c> for win-x64, linux-x64,
/// osx-x64 and osx-arm64. On an unsupported RID the call throws <see cref="DllNotFoundException"/>, which
/// <see cref="IsAvailable"/> converts into an explicit skip — an unrunnable conformance check is a coverage
/// gap, not a green result.</para>
/// </remarks>
internal static class CtkNative
{
    private static readonly Type Native =
        typeof(global::AgentHooks.Verdict).Assembly.GetType("AgentHooks.Native")
        ?? throw new InvalidOperationException("AgentHooks.Native not found — the vendor package changed shape.");

    private static string Invoke(string method, params object?[] args)
    {
        var m = Native.GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new MissingMethodException("AgentHooks.Native", method);
        try
        {
            return (string)m.Invoke(null, args)!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            // Surface the core's own error rather than a reflection wrapper, so an assertion failure names the
            // spec clause that was violated.
            throw ex.InnerException;
        }
    }

    /// <summary>The spec version the native core implements.</summary>
    internal static string SpecVersion() => Invoke(nameof(SpecVersion));

    /// <summary>
    /// Validates a verdict against §5. Returns normally when conformant; throws with the violated clause
    /// otherwise. This is the same call the vendor's own emitter makes before dispatching a verdict.
    /// </summary>
    internal static string ValidateVerdict(string verdictJson) => Invoke(nameof(ValidateVerdict), verdictJson);

    /// <summary>Validates a full §4 context envelope. Throws with the violated clause when non-conformant.</summary>
    internal static string ValidateEnvelope(string contextJson) => Invoke(nameof(ValidateEnvelope), contextJson);

    /// <summary>The §10.1 <c>jcs-sha256</c> identity of a context.</summary>
    internal static string ContextIdentity(string contextJson) => Invoke(nameof(ContextIdentity), contextJson);

    /// <summary>RFC 8785 canonical JSON, per §10.2.</summary>
    internal static string CanonicalJson(string valueJson) => Invoke(nameof(CanonicalJson), valueJson);

    /// <summary>Capability gate (§3.2): a skip reason, or JSON <c>null</c> when the vector applies.</summary>
    internal static string CtkShouldSkip(string vectorJson, string harnessCapabilitiesJson)
        => Invoke(nameof(CtkShouldSkip), vectorJson, harnessCapabilitiesJson);

    /// <summary>Evaluates a vector's <c>interceptor_script</c> against a context, returning a verdict.</summary>
    internal static string CtkScriptedIntercept(string rulesJson, string contextJson)
        => Invoke(nameof(CtkScriptedIntercept), rulesJson, contextJson);

    /// <summary>
    /// Whether the native core is loadable on this RID. False ⇒ tests skip with a stated reason instead of
    /// reporting a pass they never measured.
    /// </summary>
    internal static bool IsAvailable
    {
        get
        {
            try
            {
                return SpecVersion().StartsWith("agent-hooks/", StringComparison.Ordinal);
            }
            catch (DllNotFoundException)
            {
                return false;
            }
            catch (BadImageFormatException)
            {
                return false;
            }
        }
    }
}
