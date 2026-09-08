// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Output;

using System.Globalization;
using AgentEval.Evals.Meta;

/// <summary>
/// The facts a run must carry for two run directories to be shown comparable without any third
/// artefact. ADR-031 finding <b>V1</b>.
/// </summary>
/// <param name="EvalKey">The eval's key — <c>EvalMetadata.Key</c>.</param>
/// <param name="EvalVersion">The eval's version — <c>EvalMetadata.Version</c>.</param>
/// <remarks>
/// <para>
/// <b>Why this exists, and what it is NOT.</b> V1 names six facts a <c>compare</c> needs: the
/// stimulus, the eval's key, its version, the effective bar, the chance floor and the judge
/// fingerprint. ADR-031 S2 landed the <b>stimulus</b> (<see cref="ScenarioResult.StimulusHash"/>).
/// This record is the remaining five. It is <b>not</b> <c>compare</c> (S5) and does not decide
/// comparability — it records what a decision would have to read. A <c>compare</c> built before
/// these existed had exactly one reachable outcome, which is what refuted S5 as specified
/// (ADR-031 §0.1, Wave 7).
/// </para>
/// <para>
/// ⚠ <b>The runner knows all five at execution time.</b> That is V1's whole point: comparability
/// data belongs on the RUN, computable without a manifest, so nothing here may require a producer
/// to look anything up. <c>EvalResultPersistence.ToScenarioResult</c> derives every field from the
/// <c>EvalResult</c> it already holds — except <see cref="JudgeFingerprint.SubjectRelation"/>,
/// which needs the subject's own model and therefore has an explicit
/// <see cref="JudgeSubjectRelation.Unknown"/> state rather than a default.
/// </para>
/// <para>
/// ⚠ <b>Absent is never zero, on every field here.</b> <see cref="EffectiveBar"/> is null when the
/// eval declared no threshold — not 0.0, which would read as "everything passes".
/// <see cref="ChanceFloor"/> is null when nobody derived one, and carries
/// <see cref="RecordedChanceFloor.State"/> when somebody tried and could not. An absent floor read
/// as a zero floor is how a metric gets condemned at p = 0.70.
/// </para>
/// </remarks>
public sealed record ComparabilityFacts(string EvalKey, string EvalVersion)
{
    /// <summary>Dimension key carrying the chance floor's bar. ADR-030 §3.2.</summary>
    public const string ChanceFloorDimension = "chance_floor";

    /// <summary>Evidence source marking the chance floor's derivation. ADR-030 §3.2.</summary>
    public const string ChanceFloorEvidenceSource = "chance-floor";

    private readonly string _evalKey = Require(EvalKey, nameof(EvalKey));
    private readonly string _evalVersion = Require(EvalVersion, nameof(EvalVersion));

    /// <inheritdoc cref="ComparabilityFacts" />
    public string EvalKey
    {
        get => _evalKey;
        init => _evalKey = Require(value, nameof(EvalKey));
    }

    /// <inheritdoc cref="ComparabilityFacts" />
    public string EvalVersion
    {
        get => _evalVersion;
        init => _evalVersion = Require(value, nameof(EvalVersion));
    }

    /// <summary>
    /// The bar the eval actually applied — <c>EvalScore.Threshold</c>. <see langword="null"/> means
    /// the eval declared none, never 0.0.
    /// </summary>
    public double? EffectiveBar { get; init; }

    /// <summary>
    /// What an arm that understands nothing would have scored, as the run recorded it.
    /// <see langword="null"/> means nobody derived a floor at all — which is a different fact from
    /// <see cref="FloorState.NotDerivable"/>, where somebody tried and said why not.
    /// </summary>
    public RecordedChanceFloor? ChanceFloor { get; init; }

    /// <summary>
    /// Which judge produced the verdict, and whether it is the subject's own model.
    /// <see langword="null"/> for an eval with no judge — a deterministic eval, not an unknown one.
    /// </summary>
    public JudgeFingerprint? Judge { get; init; }

    private static string Require(string value, string name) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException(
                $"{name} is part of a comparability claim and cannot be blank: a run that cannot say which eval "
                + "produced it cannot be compared with anything.", name)
            : value;
}

/// <summary>
/// A chance floor as PERSISTED — the projection of <see cref="AgentEval.Evals.Meta.ChanceFloor"/>
/// that is safe to serialise.
/// </summary>
/// <param name="Kind">Which derivation produced it — <c>ChanceFloor.Kind*</c>.</param>
/// <param name="State">Derived, or not derivable.</param>
/// <param name="Bar">
/// The number a comparison must clear — <c>ChanceFloor.ComparisonBar</c>, so the interval's upper
/// bound when the floor was estimated. <see langword="null"/> exactly when
/// <paramref name="State"/> is <see cref="FloorState.NotDerivable"/>.
/// </param>
/// <param name="Derivation">One sentence naming the pool, the favourable set and k. Never empty.</param>
/// <remarks>
/// <para>
/// ⚠ <b>This exists because the meta type cannot be serialised.</b>
/// <see cref="AgentEval.Evals.Meta.ChanceFloor.Value"/> and
/// <see cref="AgentEval.Evals.Meta.ChanceFloor.ComparisonBar"/> are public getters that
/// <b>throw</b> when the floor was not derived — deliberately, so an absence cannot be averaged
/// into a mean. A serialiser walks public getters, so writing the meta type straight into a run
/// file would throw on exactly the floors most worth recording. The projection reads those getters
/// once, under the guard, and stores a nullable number instead.
/// </para>
/// <para>
/// ⚠ <b><paramref name="Bar"/> null and the whole record null are different facts.</b> A null
/// record on <see cref="ComparabilityFacts.ChanceFloor"/> is "nobody derived one". A record with a
/// null <paramref name="Bar"/> is "somebody tried, and <paramref name="Derivation"/> says why they
/// could not". Collapsing the two is the silent-<c>{}</c> shape ADR-030 §4.2 rejects.
/// </para>
/// </remarks>
public sealed record RecordedChanceFloor(string Kind, FloorState State, double? Bar, string Derivation)
{
    /// <summary>Projects a meta-lane floor into the persisted shape, without tripping its guards.</summary>
    /// <param name="floor">The floor to record.</param>
    /// <returns>The projection.</returns>
    public static RecordedChanceFloor From(AgentEval.Evals.Meta.ChanceFloor floor)
    {
        ArgumentNullException.ThrowIfNull(floor);

        // ComparisonBar THROWS on a NotDerivable floor. Read it only inside the guard — and store
        // null rather than 0.0 outside it, because an absent floor is not a zero floor.
        return new RecordedChanceFloor(
            Kind: floor.Kind,
            State: floor.State,
            Bar: floor.State is FloorState.Derived ? floor.ComparisonBar : null,
            Derivation: floor.Derivation);
    }

    /// <summary>True when this floor names a number a comparison can be held to.</summary>
    public bool IsUsableAsABar => State is FloorState.Derived && Bar is not null;
}

/// <summary>
/// Whether the judge that graded a run is the same model as the subject it graded.
/// </summary>
/// <remarks>
/// ⚠ <b>Three states, not a bool.</b> ADR-031 §0.1 names this follow-on <c>judgeIsSubjectModel</c>,
/// and a <c>bool</c> would answer "nobody told us" with <c>false</c> — "the judge is a different
/// model" — which is the flattering direction and the exact silent-<c>{}</c> shape ADR-030 §4.2
/// rejects. <see cref="Unknown"/> is <c>default</c> so a producer that says nothing says nothing.
/// </remarks>
public enum JudgeSubjectRelation
{
    /// <summary>The subject's model was not supplied. <b>Not "different".</b></summary>
    Unknown = 0,

    /// <summary>
    /// The judge IS the subject's model. The artifact under test is grading itself, which is the
    /// gate-self-examination failure at its purest: the thing being measured supplies the measurement.
    /// </summary>
    SameModel = 1,

    /// <summary>The judge is a different model from the subject.</summary>
    DifferentModel = 2,
}

/// <summary>
/// Which judge produced a verdict — by NAME and rubric digest, and by nothing else.
/// </summary>
/// <param name="ModelId">The judge's model or deployment NAME. Never an endpoint, never a key.</param>
/// <param name="RubricDigest">
/// A digest of the rubric the judge was given, or <see langword="null"/> when the producer recorded
/// none. Two runs judged by the same model against different rubrics are not comparable, and the
/// model id alone cannot say so. ⚠ Guarded against the ENDPOINT shape only — see the type's remarks
/// for why the model-name rules cannot be applied to a digest.
/// </param>
/// <param name="SubjectRelation">Whether this judge is the subject's own model.</param>
/// <remarks>
/// <para>
/// 🔴 <b>THIS TYPE MUST NEVER CARRY A CREDENTIAL OR AN ENDPOINT, AND IT REFUSES ONE AT
/// CONSTRUCTION — ON EVERY STRING IT HAS, NOT JUST ON THE OBVIOUS ONE.</b> It is written into run
/// files that get committed, attached to issues and pasted into chat. There is no field an endpoint
/// belongs in.
/// </para>
/// <para>
/// ⚠ <b>The two strings are guarded to DIFFERENT depths, and the difference is deliberate.</b>
/// <see cref="ModelId"/> — the field a careless producer would reach for, because a deployment
/// "name" and a deployment URL come off the same configuration object — gets the full check: a URL,
/// a known cloud host suffix, an <c>sk-</c> prefix, a long hex run or a long opaque token.
/// <see cref="RubricDigest"/> gets the <b>endpoint half only</b> (a URL or a known host suffix),
/// because a digest <i>is</i> 64 hex characters and the key/length rules would refuse every real
/// one — the failure that would make the guard look strict and be useless. What no digest can
/// legitimately contain is <c>://</c> or a cloud host, so those are refused there too. A type whose
/// stated job is "carries no endpoint" with one unguarded string on it states the claim for the
/// half that was easy.
/// </para>
/// <para>
/// ⚠ Both are <b>refused</b>, not redacted. Redaction would leave a producer believing it had
/// recorded something.
/// </para>
/// <para>
/// ⚠ <b>The guard is deliberately narrow and it is not a secret scanner.</b> It refuses the shapes a
/// credential and an endpoint actually take; it cannot prove a short string is not a secret. Its job
/// is to make the accident loud, not to certify the field.
/// </para>
/// </remarks>
public sealed record JudgeFingerprint(
    string ModelId,
    string? RubricDigest = null,
    JudgeSubjectRelation SubjectRelation = JudgeSubjectRelation.Unknown)
{
    private readonly string _modelId = EnsureNotASecret(ModelId, nameof(ModelId));
    private readonly string? _rubricDigest = EnsureNoEndpoint(RubricDigest, nameof(RubricDigest));

    /// <inheritdoc cref="JudgeFingerprint" />
    public string ModelId
    {
        get => _modelId;
        init => _modelId = EnsureNotASecret(value, nameof(ModelId));
    }

    /// <inheritdoc cref="JudgeFingerprint" />
    public string? RubricDigest
    {
        get => _rubricDigest;
        init => _rubricDigest = EnsureNoEndpoint(value, nameof(RubricDigest));
    }

    /// <summary>
    /// Builds a fingerprint, deciding <see cref="SubjectRelation"/> from the subject's model.
    /// </summary>
    /// <param name="judgeModel">The judge's model or deployment name.</param>
    /// <param name="rubricDigest">A digest of the rubric, when the producer has one.</param>
    /// <param name="subjectModel">
    /// The subject's model, when the producer knows it. <see langword="null"/> or blank yields
    /// <see cref="JudgeSubjectRelation.Unknown"/> — never <see cref="JudgeSubjectRelation.DifferentModel"/>.
    /// </param>
    /// <returns>The fingerprint.</returns>
    public static JudgeFingerprint For(string judgeModel, string? rubricDigest, string? subjectModel) =>
        new(judgeModel, rubricDigest, RelationTo(judgeModel, subjectModel));

    /// <summary>Compares a judge model against a subject model without constructing anything.</summary>
    /// <param name="judgeModel">The judge's model or deployment name.</param>
    /// <param name="subjectModel">The subject's model, or <see langword="null"/> when unknown.</param>
    /// <returns>The relation.</returns>
    /// <remarks>
    /// The comparison is case-insensitive and trims, and nothing else. It does not try to resolve a
    /// deployment name to the model behind it: two names that differ may still be one model, so a
    /// <see cref="JudgeSubjectRelation.DifferentModel"/> here is "the names differ", not "the models
    /// do". That under-claims, which is the safe direction — the finding this exists to raise is
    /// <see cref="JudgeSubjectRelation.SameModel"/>.
    /// </remarks>
    public static JudgeSubjectRelation RelationTo(string? judgeModel, string? subjectModel)
    {
        if (string.IsNullOrWhiteSpace(judgeModel) || string.IsNullOrWhiteSpace(subjectModel))
            return JudgeSubjectRelation.Unknown;

        return string.Equals(judgeModel.Trim(), subjectModel.Trim(), StringComparison.OrdinalIgnoreCase)
            ? JudgeSubjectRelation.SameModel
            : JudgeSubjectRelation.DifferentModel;
    }

    /// <summary>Host suffixes that mean the value is an endpoint rather than a name.</summary>
    private static readonly string[] s_endpointSuffixes =
    [
        ".azure.com", ".openai.com", ".azure-api.net", ".amazonaws.com",
        ".googleapis.com", ".microsoft.com", ".anthropic.com",
    ];

    /// <summary>
    /// Refuses a value that has the shape of an endpoint or a credential.
    /// </summary>
    /// <param name="value">The candidate model or deployment name.</param>
    /// <param name="name">The parameter name, for the exception.</param>
    /// <returns><paramref name="value"/> when it is name-shaped.</returns>
    /// <exception cref="ArgumentException">The value looks like a URL, a host or a secret.</exception>
    internal static string EnsureNotASecret(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{name} is blank — a judge with no name fingerprints nothing.", name);

        string trimmed = value.Trim();

        string? reason = ShapeOfASecret(trimmed);
        if (reason is not null)
        {
            // ⚠ The offending value is NOT echoed. An exception message goes to a log, and the whole
            // point of refusing is that this string may be the thing that must never be written.
            throw new ArgumentException(
                string.Create(CultureInfo.InvariantCulture, $"{name} looks like {reason}, so it was REFUSED rather than recorded. A judge fingerprint carries a model or deployment NAME and a rubric digest — never an endpoint and never a credential. The value is not repeated here on purpose."),
                name);
        }

        return trimmed;
    }

    /// <summary>
    /// Refuses a value that has the shape of an ENDPOINT. The half of the guard that applies to
    /// every string on this type, the rubric digest included.
    /// </summary>
    /// <param name="value">The candidate, or <see langword="null"/>.</param>
    /// <param name="name">The parameter name, for the exception.</param>
    /// <returns><paramref name="value"/> when it carries no endpoint.</returns>
    /// <exception cref="ArgumentException">The value looks like a URL or a cloud host.</exception>
    /// <remarks>
    /// ⚠ Deliberately <b>not</b> the full <see cref="ShapeOfASecret"/> check. A rubric digest is 64
    /// hex characters, so the key/length rules would refuse every real one; a digest can, however,
    /// never legitimately contain <c>://</c> or a cloud host, and those are the two shapes an
    /// endpoint actually takes.
    /// </remarks>
    internal static string? EnsureNoEndpoint(string? value, string name)
    {
        if (value is null) return null;

        if (ShapeOfAnEndpoint(value) is { } reason)
        {
            // ⚠ The offending value is NOT echoed — same rule as EnsureNotASecret.
            throw new ArgumentException(
                string.Create(CultureInfo.InvariantCulture, $"{name} looks like {reason}, so it was REFUSED rather than recorded. Nothing on a judge fingerprint may carry an endpoint — not the model name and not the rubric digest. The value is not repeated here on purpose."),
                name);
        }

        return value;
    }

    /// <summary>Names the ENDPOINT shape a value has, or null when it has none.</summary>
    /// <param name="value">A candidate.</param>
    /// <returns>A short phrase for the exception, or <see langword="null"/>.</returns>
    internal static string? ShapeOfAnEndpoint(string value)
    {
        if (value.Contains("://", StringComparison.Ordinal)) return "a URL";

        foreach (string suffix in s_endpointSuffixes)
        {
            if (value.Contains(suffix, StringComparison.OrdinalIgnoreCase)) return "an endpoint host";
        }

        return null;
    }

    /// <summary>Names the credential/endpoint shape a value has, or null when it has none.</summary>
    /// <param name="value">A trimmed candidate.</param>
    /// <returns>A short phrase for the exception, or <see langword="null"/>.</returns>
    internal static string? ShapeOfASecret(string value)
    {
        if (value.Length > 128) return "far too long for a model name";
        if (ShapeOfAnEndpoint(value) is { } endpoint) return endpoint;

        if (value.StartsWith("sk-", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("sk_", StringComparison.OrdinalIgnoreCase))
        {
            return "an API key";
        }

        if (LongestRun(value, IsHex) >= 32) return "a hex key or digest";
        if (LongestRun(value, IsTokenChar) >= 48) return "an opaque token";

        return null;
    }

    private static int LongestRun(string value, Func<char, bool> predicate)
    {
        int best = 0, run = 0;
        foreach (char c in value)
        {
            run = predicate(c) ? run + 1 : 0;
            if (run > best) best = run;
        }

        return best;
    }

    private static bool IsHex(char c) =>
        c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';

    private static bool IsTokenChar(char c) =>
        char.IsAsciiLetterOrDigit(c) || c is '+' or '/' or '=' or '_' or '-';
}
