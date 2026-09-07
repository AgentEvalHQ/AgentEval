// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Models;

namespace AgentEval.Benchmarks;

/// <summary>
/// What a benchmark IS, with nothing about how it runs: a set of cases and a set of floored checks.
/// </summary>
/// <remarks>
/// <para>
/// <b>Data only.</b> No subject, no judge, no store, no client. Everything that could reach the
/// network or hold a credential belongs to a <see cref="BenchmarkArm"/> or to the runner; keeping it
/// out of here is what lets a definition be written down, reviewed, diffed and re-used against arms
/// nobody had in mind when it was authored.
/// </para>
/// <para>
/// 🔴 <b><see cref="TestCase.Id"/> is REQUIRED here, though it is optional on the type.</b> The unit
/// of analysis for a chance floor, a paired comparison or a rep collapse is the CASE (ADR-030 §4.5,
/// defect D11), and none of them is implementable without a key that is allowed to be ugly and
/// forbidden to change. <see cref="TestCase.Name"/> is a display string — the recorded defect is a
/// flagship sample joining on <c>$"{id} — {group}"</c>, a join that silently re-points the moment
/// anyone edits a label. A definition whose cases have no ids cannot be scored, so it is refused at
/// construction rather than at the point where the join has already gone wrong.
/// </para>
/// <para>
/// <b>What this deliberately does NOT hold.</b> No content hash: nothing reads one today, and a
/// digest nobody verifies is a claim of integrity without the integrity (ADR-031 V2). No controls
/// slot: whether the definition funds its own negative controls is an owner question, still open. No
/// judge slot: a judged benchmark is a different shape and naming it here would be a type standing
/// in for a decision nobody has made.
/// </para>
/// <para>
/// <b><see cref="Version"/> is a re-baseline marker, not decoration.</b> Bump it whenever
/// <see cref="Cases"/> or <see cref="Checks"/> change: numbers from two versions are not comparable,
/// and the version is the only thing that says so out loud. Nothing here enforces the bump — a
/// content hash would, which is exactly the open question above.
/// </para>
/// </remarks>
/// <param name="Key">Human identity for this benchmark.</param>
/// <param name="Version">Bump when <paramref name="Cases"/> or <paramref name="Checks"/> change.</param>
/// <param name="Cases">The cases. Non-empty; every <see cref="TestCase.Id"/> non-blank and unique.</param>
/// <param name="Checks">The floored checks. Non-empty; every eval key unique.</param>
public sealed record BenchmarkDefinition(
    string Key,
    string Version,
    IReadOnlyList<TestCase> Cases,
    IReadOnlyList<AdmittedCheck> Checks)
{
    private readonly string _key = Require(Key, nameof(Key));
    private readonly string _version = Require(Version, nameof(Version));
    private readonly IReadOnlyList<TestCase> _cases = ValidateCases(Cases);
    private readonly IReadOnlyList<AdmittedCheck> _checks = ValidateChecks(Checks);

    /// <inheritdoc cref="BenchmarkDefinition(string, string, IReadOnlyList{TestCase}, IReadOnlyList{AdmittedCheck})"/>
    public string Key
    {
        get => _key;
        init => _key = Require(value, nameof(Key));
    }

    /// <inheritdoc cref="BenchmarkDefinition(string, string, IReadOnlyList{TestCase}, IReadOnlyList{AdmittedCheck})"/>
    public string Version
    {
        get => _version;
        init => _version = Require(value, nameof(Version));
    }

    /// <inheritdoc cref="BenchmarkDefinition(string, string, IReadOnlyList{TestCase}, IReadOnlyList{AdmittedCheck})"/>
    public IReadOnlyList<TestCase> Cases
    {
        get => _cases;
        init => _cases = ValidateCases(value);
    }

    /// <inheritdoc cref="BenchmarkDefinition(string, string, IReadOnlyList{TestCase}, IReadOnlyList{AdmittedCheck})"/>
    public IReadOnlyList<AdmittedCheck> Checks
    {
        get => _checks;
        init => _checks = ValidateChecks(value);
    }

    // The AE-01 / AE-08 pattern, on all four members. A validating INITIALIZER runs on the
    // constructor path; a `with` copy invokes the init ACCESSOR instead. Declaring BOTH is what makes
    // the two paths share one guard, and the shortcut is measured rather than assumed: an
    // auto-property with a validating initializer (`public string Key { get; init; } = Require(...)`)
    // throws on `new(...)` and does NOT throw on `x with { Key = "  " }` — the copy goes through the
    // compiler-generated accessor, which has no guard in it. That object is one the constructor would
    // have refused.
    private static string Require(string value, string member) =>
        !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"{member} is required.", member);

    private static IReadOnlyList<TestCase> ValidateCases(IReadOnlyList<TestCase> cases)
    {
        ArgumentNullException.ThrowIfNull(cases);

        if (cases.Count == 0)
        {
            throw new ArgumentException(
                "A benchmark definition needs at least one case. An empty case list makes every check "
                + "vacuously green — a control that passes on an empty set is not a control.",
                nameof(Cases));
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < cases.Count; i++)
        {
            var id = cases[i]?.Id;
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new ArgumentException(
                    $"Case at index {i} ('{cases[i]?.Name ?? "<null>"}') has no Id. TestCase.Id is optional on the "
                    + "type and REQUIRED here: the case is the unit of analysis for every floor, pairing and rep "
                    + "collapse, and Name is a display string that re-points the join the moment anyone edits it.",
                    nameof(Cases));
            }

            if (!seen.Add(id))
            {
                throw new ArgumentException(
                    $"Duplicate case Id '{id}' at index {i}. Two cases sharing an id collapse into one "
                    + "observation per arm, so one of them silently stops being measured while the census "
                    + "still counts the definition as complete.",
                    nameof(Cases));
            }
        }

        return cases;
    }

    private static IReadOnlyList<AdmittedCheck> ValidateChecks(IReadOnlyList<AdmittedCheck> checks)
    {
        ArgumentNullException.ThrowIfNull(checks);

        if (checks.Count == 0)
        {
            throw new ArgumentException(
                "A benchmark definition needs at least one check. A definition with cases and no checks "
                + "runs an agent and measures nothing, which reports as a clean run.",
                nameof(Checks));
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < checks.Count; i++)
        {
            var key = checks[i]?.Eval?.Key;
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new ArgumentException(
                    $"Check at index {i} has no eval key. Results are addressed by key — an unkeyed check "
                    + "cannot be scored, compared or reported.",
                    nameof(Checks));
            }

            if (!seen.Add(key))
            {
                throw new ArgumentException(
                    $"Duplicate check key '{key}' at index {i}. Two checks sharing a key produce two results "
                    + "under one name, and whichever the reader reads is arbitrary.",
                    nameof(Checks));
            }
        }

        return checks;
    }
}
