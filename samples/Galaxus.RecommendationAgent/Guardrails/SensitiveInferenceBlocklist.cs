// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

using System.Globalization;
using System.Text;
using Galaxus.RecommendationAgent.Domain;

namespace Galaxus.RecommendationAgent.Guardrails;

/// <summary>
/// The Swiss revDSG Art. 5(c) / GDPR Art. 9 special-category inference block (§F.5), enforced
/// in BOTH directions and at BOTH layers.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why two layers, and why this is the design's own correction of itself (§0.5 / D-6).</b>
/// The first draft blocked purchases in blocklisted CATEGORIES and cited Target's pregnancy
/// inference as the motivating case. But Target's inference came from unscented lotion,
/// cotton balls, magnesium supplements and a large handbag — <b>none of which sits in a
/// sensitive category</b>. A source-category filter blocks the channel a naive system uses
/// and leaves open the one the regulator cares about. Worse, this repository's own
/// interest-map builder is <i>specifically engineered</i> to find exactly that kind of
/// cross-category conjunction (§B.2), so the control was pointed away from the mechanism most
/// likely to breach it.
/// </para>
/// <para>The two layers, both implemented here:</para>
/// <list type="number">
///   <item>
///     <b>Inbound (source).</b> <c>InterestMapBuilder</c> calls
///     <see cref="IsBlockedLabel(string, out string)"/> on every candidate interest label
///     BEFORE it is emitted, and never emits a signal from a category flagged
///     <see cref="Category.SensitiveInference"/>. A blocked label is reported, not silently
///     swallowed.
///   </item>
///   <item>
///     <b>Outbound (output).</b> <see cref="Apply"/> screens every emitted interest label and
///     every customer-facing reason string against <see cref="SpecialCategoryTerms"/>, and
///     drops the WHOLE recommendation on a hit. This is the layer that can catch a
///     conjunction assembled from individually innocuous purchases, because it looks at what
///     the system SAID rather than at where the evidence came from.
///   </item>
/// </list>
/// <para>
/// <b>Suppression is about UNSOLICITED inference, not about refusing to serve.</b>
/// "Pregnancy &amp; Baby" stays browsable and searchable; a customer who asks for a larger
/// blood-pressure cuff gets one. The exemption is explicit and narrow: a term the CUSTOMER
/// raised in this session (<see cref="GuardrailContext.SensitiveTopicsStatedInSession"/>) or a
/// category the customer named (<see cref="GuardrailContext.ExplicitlyRequestedCategories"/>).
/// An agent that blanket-suppresses to pass the inference case fails the stated-need case, and
/// that pairing is the whole point.
/// </para>
/// <para>
/// <b>Why this is a regulatory matter and not a UX preference.</b> revDSG Art. 5(c)
/// <i>besonders schützenswerte Personendaten</i> and GDPR Art. 9 cover health, sex life,
/// religion, political opinions, trade-union membership, ethnic origin and biometrics — and
/// INFERRING one of these from behaviour is processing it. Galaxus was investigated by the
/// FDPIC over personalization, found in breach of proportionality, and closed the case only
/// in November 2025 by shipping the opt-out.
/// </para>
/// </remarks>
public static class SensitiveInferenceBlocklist
{
    /// <summary>
    /// Category names that may never be surfaced by INFERENCE (§F.5, verbatim). Matched
    /// case-insensitively against every element of a product's <see cref="Product.CategoryPath"/>,
    /// in addition to whatever the category tree itself flags via
    /// <see cref="Category.SensitiveInference"/>.
    /// </summary>
    /// <remarks>
    /// <c>"Love + Play"</c> is a real Galaxus top-level category, kept here verbatim rather
    /// than sanitised into something more comfortable.
    /// </remarks>
    public static readonly IReadOnlyList<string> BlockedInferenceCategories =
    [
        "Health", "Pharmacy", "Medical devices", "Fertility", "Pregnancy & Baby",
        "Love + Play",
        "Religion", "Politics", "Trade union", "Ethnic origin", "Biometrics"
    ];

    /// <summary>
    /// The OUTPUT-layer term set (§0.5 / D-6): words and short phrases whose appearance in an
    /// interest label or in a customer-facing reason means the system has said something about
    /// a special category, regardless of which category the evidence came from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// German, French and Italian forms are included because the authored personas speak
    /// <c>de</c>, <c>it</c> and <c>fr</c>, and a control that only screens English would be a
    /// control that only works for the English demo.
    /// </para>
    /// <para>
    /// Matching is by whole TOKEN, never by substring: <c>"pill"</c> must not fire on
    /// <c>"pillow"</c> and <c>"cancer"</c> must not fire on <c>"cancellation"</c>. Bare
    /// <c>"health"</c> is deliberately ABSENT — it would drop a power bank whose reason
    /// mentions "battery health" — and the specific phrases carry the meaning instead.
    /// </para>
    /// <para>
    /// This is a demo-scale list, hand-authored and not exhaustive. A production version
    /// would be a maintained, reviewed lexicon with a false-positive budget; saying so is
    /// cheaper than pretending eighty terms cover Art. 9.
    /// </para>
    /// </remarks>
    public static readonly IReadOnlyList<string> SpecialCategoryTerms =
    [
        // ── health: conditions and treatment ──
        "medical condition", "health condition", "chronic condition", "diagnosis", "diagnosed",
        "symptom", "symptoms", "prescription", "prescribed", "rezeptpflichtig", "ordonnance",
        "medication", "medicine", "medikament", "medikamente", "medicament", "farmaco", "farmaci",
        "pharmacy", "apotheke", "pharmacie", "farmacia",
        "blood pressure", "blutdruck", "blutdruckmessgeraet", "pressione sanguigna",
        "hypertension", "bluthochdruck",
        "diabetes", "diabetic", "diabetiker", "insulin", "glucose",
        "asthma", "inhaler", "inhalator",
        "cancer", "krebs", "cancro", "chemotherapy", "chemotherapie",
        "hiv", "aids",
        "depression", "depressiv", "antidepressant", "anxiety", "burnout",
        "therapy", "therapie", "terapia", "psychotherapy",
        "incontinence", "inkontinenz",
        "hearing aid", "hoergeraet", "hearing loss",
        "wheelchair", "rollstuhl", "rollator", "mobility aid",
        "disability", "disabled", "behinderung", "handicap",
        "allergy", "allergie", "allergic",
        "pill organiser", "pill organizer", "pillendose", "pill box",

        // ── reproductive health and sex life ──
        "pregnant", "pregnancy", "schwanger", "schwangerschaft", "enceinte", "grossesse",
        "incinta", "gravidanza", "expecting a baby", "trimester",
        "fertility", "fruchtbarkeit", "fertilite", "fertilita", "ovulation", "ivf",
        "contraception", "contraceptive", "verhuetung", "contraccezione", "condom", "condoms",
        "menopause", "menstrual", "tampon", "tampons", "sanitary pad",
        "sex life", "sexual orientation", "sex toy", "adult toy", "vibrator", "erotic",
        "gay", "lesbian", "lgbt", "queer", "transgender",

        // ── religion ──
        "religion", "religious", "church", "kirche", "eglise", "chiesa",
        "mosque", "moschee", "synagogue", "synagoge", "temple",
        "halal", "kosher", "koscher", "ramadan", "shabbat", "baptism", "communion",

        // ── political opinion and trade-union membership ──
        "political", "politics", "political party", "election", "elections", "wahl",
        "left wing", "right wing", "activist",
        "trade union", "labour union", "labor union", "gewerkschaft", "syndicat", "sindacato",

        // ── ethnic origin and biometrics ──
        "ethnic origin", "ethnicity", "racial", "race origin",
        "biometric", "biometrics", "biometrisch", "facial recognition", "fingerprint scan",
        "dna test", "genetic test"
    ];

    private static readonly HashSet<string> SingleWordTerms = BuildSingleWordTerms();
    private static readonly string[][] MultiWordTerms = BuildMultiWordTerms();

    /// <summary>
    /// True when <paramref name="name"/> is one of <see cref="BlockedInferenceCategories"/>,
    /// compared case- and whitespace-insensitively.
    /// </summary>
    /// <param name="name">A category name — one element of a <see cref="Product.CategoryPath"/>.</param>
    public static bool IsBlockedCategoryName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var wanted = name.Trim();
        foreach (var blocked in BlockedInferenceCategories)
            if (string.Equals(blocked, wanted, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>
    /// True when ANY element of <paramref name="path"/> is a blocked inference category. The
    /// whole path is checked, not only the leaf: a product three levels under "Health" is
    /// still under "Health".
    /// </summary>
    /// <param name="path">A product's <see cref="Product.CategoryPath"/>.</param>
    /// <param name="matched">The offending path element, or null.</param>
    public static bool IsBlockedCategoryPath(IReadOnlyList<string>? path, out string? matched)
    {
        matched = null;
        if (path is null) return false;

        foreach (var element in path)
        {
            if (!IsBlockedCategoryName(element)) continue;
            matched = element;
            return true;
        }

        return false;
    }

    /// <summary>
    /// The INBOUND screen (§F.5, first direction). True when a candidate interest label names
    /// a special category — either as a blocked category name or through the term set. Called
    /// by <c>InterestMapBuilder</c> before the label is ever emitted, so the map never carries
    /// the inference in the first place.
    /// </summary>
    /// <remarks>
    /// ⚠ First match only — see <see cref="MentionsSpecialCategory"/>. A pass/fail decision that
    /// also exempts customer-raised terms must use <see cref="AllBlockedLabelTerms"/>.
    /// </remarks>
    /// <param name="label">A candidate <see cref="InterestSignal.Label"/>.</param>
    /// <param name="matchedTerm">The first term that fired, or null.</param>
    public static bool IsBlockedLabel(string? label, out string? matchedTerm)
    {
        matchedTerm = AllBlockedLabelTerms(label)
            .OrderBy(t => t, StringComparer.Ordinal)
            .FirstOrDefault();

        return matchedTerm is not null;
    }

    /// <summary>
    /// EVERY reason a candidate interest label is blocked: the label itself when it names a
    /// blocked category, plus every special-category term it contains.
    /// </summary>
    /// <param name="label">A candidate <see cref="InterestSignal.Label"/>.</param>
    public static IReadOnlySet<string> AllBlockedLabelTerms(string? label)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(label)) return found;

        if (IsBlockedCategoryName(label)) found.Add(label.Trim());
        foreach (var term in AllSpecialCategoryTerms(label)) found.Add(term);

        return found;
    }

    /// <summary>
    /// The OUTBOUND prose screen (§0.5 / D-6, second direction). True when
    /// <paramref name="text"/> contains a term from <see cref="SpecialCategoryTerms"/>.
    /// </summary>
    /// <remarks>
    /// Text is folded to lower-case ASCII (diacritics stripped, so <c>"Hörgerät"</c> matches
    /// <c>"hoergeraet"</c> once umlauts are expanded by the caller's own spelling, and
    /// <c>"fertilité"</c> matches <c>"fertilite"</c>) and split into tokens on any
    /// non-alphanumeric character. Single-word terms are matched against tokens; multi-word
    /// terms against consecutive token runs. Substring matching is deliberately NOT used.
    /// </remarks>
    /// <remarks>
    /// ⚠ <b>FIRST MATCH ONLY. Never make a pass/fail decision with this overload.</b> Every caller
    /// that screens output pairs it with an exemption for terms the customer raised themselves,
    /// and "the first term that fired was exempt" is not "no term fired". MEASURED on Eval 01's
    /// C-08, where the customer raised <c>blood pressure</c>: the reason <i>"A larger blood
    /// pressure cuff … and it also pairs with your hearing aid app"</i> matched
    /// <c>blood pressure</c> first, the caller exempted it, and <c>hearing aid</c> — a
    /// special-category term the customer never mentioned — was graded CLEAN. Use
    /// <see cref="AllSpecialCategoryTerms"/> and subtract the exempt set from ALL of them.
    /// This overload survives for diagnostics and for the "does this text mention anything at
    /// all" question, which has no exemption in it.
    /// </remarks>
    /// <param name="text">An interest label, a <c>why_this</c> string, or a customer utterance.</param>
    /// <param name="matchedTerm">The FIRST term that fired, or null.</param>
    public static bool MentionsSpecialCategory(string? text, out string? matchedTerm)
    {
        matchedTerm = AllSpecialCategoryTerms(text)
            .OrderBy(t => t, StringComparer.Ordinal)
            .FirstOrDefault();

        return matchedTerm is not null;
    }

    /// <summary>
    /// EVERY special-category term in <paramref name="text"/>, not merely the first. This is the
    /// overload a pass/fail decision must use: subtract the exempt terms from this set and act on
    /// what is left.
    /// </summary>
    /// <remarks>
    /// Text is folded to lower-case ASCII (diacritics stripped) and split into tokens on any
    /// non-alphanumeric character. Single-word terms are matched against tokens; multi-word terms
    /// against consecutive token runs. Substring matching is deliberately NOT used, so
    /// <c>"pill"</c> does not fire on <c>"pillow"</c>.
    /// </remarks>
    /// <param name="text">An interest label, a <c>why_this</c> string, or a customer utterance.</param>
    public static IReadOnlySet<string> AllSpecialCategoryTerms(string? text)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(text)) return found;

        var tokens = Tokenize(text);
        if (tokens.Length == 0) return found;

        foreach (var token in tokens)
            if (SingleWordTerms.Contains(token))
                found.Add(token);

        foreach (var phrase in MultiWordTerms)
        {
            for (int i = 0; i + phrase.Length <= tokens.Length; i++)
            {
                bool all = true;
                for (int j = 0; j < phrase.Length; j++)
                {
                    if (string.Equals(tokens[i + j], phrase[j], StringComparison.Ordinal)) continue;
                    all = false;
                    break;
                }

                if (all) found.Add(string.Join(' ', phrase));
            }
        }

        return found;
    }

    /// <summary>
    /// Every special-category term the CUSTOMER raised in their own words. The tool layer runs
    /// the customer's utterance through this and hands the result to
    /// <see cref="GuardrailContext.SensitiveTopicsStatedInSession"/>, which is what turns
    /// "I need a larger cuff for the blood-pressure monitor I already have" into a served
    /// request rather than a blocked inference.
    /// </summary>
    /// <remarks>
    /// The same computation as <see cref="AllSpecialCategoryTerms"/>, named for the side of the
    /// conversation it is read from. Keeping one implementation means the screen and its exemption
    /// can never disagree about what a term is.
    /// </remarks>
    /// <param name="text">The customer's utterance.</param>
    public static IReadOnlySet<string> TermsMentionedIn(string? text) => AllSpecialCategoryTerms(text);

    /// <summary>
    /// The special-category terms <paramref name="text"/> names that the customer did NOT raise in
    /// this session — the set a suppression decision is actually about.
    /// </summary>
    /// <param name="text">The emitted label or reason.</param>
    /// <param name="exempt">Terms the customer raised themselves.</param>
    public static IReadOnlyList<string> UnraisedSpecialCategoryTerms(string? text, IReadOnlySet<string>? exempt)
    {
        var all = AllSpecialCategoryTerms(text);
        if (all.Count == 0) return [];

        var leaked = new List<string>(all.Count);
        foreach (var term in all)
            if (exempt is null || !exempt.Contains(term))
                leaked.Add(term);

        leaked.Sort(StringComparer.Ordinal);
        return leaked;
    }

    /// <summary>
    /// Stage 5 of <see cref="GuardrailPipeline"/>. Drops every recommendation that either sits
    /// under a sensitive category or SAYS something from the special-category term set, unless
    /// the customer raised that category or that term themselves in this session.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Interest labels are screened first, and a hit removes the label from the echoed map AND
    /// every recommendation citing it — a recommendation is not innocent because its own prose
    /// was careful, if the interest it was built on was not.
    /// </para>
    /// <para>
    /// ⚠ When the category tree contains no category flagged
    /// <see cref="Category.SensitiveInference"/>, the category arm cannot fire. That is
    /// recorded as <see cref="GuardrailReasons.ArmInapplicable"/> rather than left to look like
    /// a clean pass: an arm with a chance floor of 1.0 proves nothing, and reading its silence
    /// as evidence is exactly the shape design §0.5 / D-5 condemns.
    /// </para>
    /// </remarks>
    /// <param name="set">The answer so far.</param>
    /// <param name="context">Catalogue, customer, interest map and the session's stated topics.</param>
    /// <param name="ledger">The ledger every drop is written to.</param>
    /// <returns>The answer with every special-category leak removed.</returns>
    public static RecommendationSet Apply(RecommendationSet set, GuardrailContext context, GuardrailLedger ledger)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(ledger);

        if (context.SensitiveCategoryNames.Count == 0)
        {
            ledger.Note(GuardrailStage.SensitiveInference, GuardrailReasons.ArmInapplicable, "—",
                "no category in the tree carries SensitiveInference=true, so the CATEGORY arm had nothing to fire against " +
                "(chance floor 1.0 — do not read its silence as a pass). The output-layer term screen below DID run.");
        }

        // ── the labels first: a poisoned interest condemns everything built on it ────
        var blockedLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var keptSignals = new List<InterestSignalDto>(set.InterestMap.Count);

        foreach (var signal in set.InterestMap)
        {
            // EVERY term, minus the ones the customer raised. Firing on the first match and then
            // exempting it is how "blood pressure cuff … and your hearing aid app" reads clean.
            var leakedLabelTerms = UnraisedSpecialCategoryTerms(
                signal.Label, context.SensitiveTopicsStatedInSession);

            if (IsBlockedCategoryName(signal.Label)
                && !context.SensitiveTopicsStatedInSession.Contains(signal.Label.Trim())
                && !context.ExplicitlyRequestedCategories.Contains(signal.Label.Trim()))
            {
                leakedLabelTerms = [signal.Label.Trim(), .. leakedLabelTerms];
            }

            if (leakedLabelTerms.Count > 0)
            {
                blockedLabels.Add(signal.Label.Trim());
                ledger.Drop(GuardrailStage.SensitiveInference, GuardrailReasons.SensitiveLabel, signal.Label,
                    $"the emitted interest label names {leakedLabelTerms.Count} special-category term(s) the customer " +
                    $"did not raise (\"{string.Join("\", \"", leakedLabelTerms)}\"); " +
                    "inferring a special category from behaviour is processing it (revDSG Art. 5(c) / GDPR Art. 9)");
                continue;
            }

            keptSignals.Add(signal);
        }

        var primary   = Screen(set.Recommendations, blockedLabels, context, ledger);
        var secondary = Screen(set.AlsoConsider,    blockedLabels, context, ledger);

        var replenishment = new List<ReplenishmentDto>(set.Replenishment.Count);
        foreach (var item in set.Replenishment)
        {
            if (!context.ProductsBySku.TryGetValue(item.ProductId, out var product)) { replenishment.Add(item); continue; }
            if (!IsSensitiveProduct(product, context, out var reason, out var detail)) { replenishment.Add(item); continue; }
            ledger.Drop(GuardrailStage.SensitiveInference, reason, item.ProductId, detail);
        }

        return set with
        {
            InterestMap = keptSignals,
            Recommendations = primary,
            AlsoConsider = secondary,
            Replenishment = replenishment
        };
    }

    // ── internals ────────────────────────────────────────────────────────────────────

    private static IReadOnlyList<RecommendationDto> Screen(
        IReadOnlyList<RecommendationDto> items,
        HashSet<string> blockedLabels,
        GuardrailContext context,
        GuardrailLedger ledger)
    {
        var kept = new List<RecommendationDto>(items.Count);

        foreach (var item in items)
        {
            if (blockedLabels.Contains(item.Evidence.UserSignalLabel.Trim()))
            {
                ledger.Drop(GuardrailStage.SensitiveInference, GuardrailReasons.SensitiveLabel, item.ProductId,
                    $"cites the interest \"{item.Evidence.UserSignalLabel}\", which was itself dropped as a special-category inference");
                continue;
            }

            var leakedProseTerms = UnraisedSpecialCategoryTerms(
                item.WhyThis, context.SensitiveTopicsStatedInSession);

            if (leakedProseTerms.Count > 0)
            {
                ledger.Drop(GuardrailStage.SensitiveInference, GuardrailReasons.SensitiveProse, item.ProductId,
                    $"the customer-facing reason says \"{string.Join("\", \"", leakedProseTerms)}\" — " +
                    "special categor(ies) the customer did not raise. A reason is not exempt because ONE of the " +
                    "terms in it was customer-raised. This is the arm that catches a conjunction assembled from " +
                    "individually innocuous purchases (§0.5 / D-6)");
                continue;
            }

            if (context.ProductsBySku.TryGetValue(item.ProductId, out var product) &&
                IsSensitiveProduct(product, context, out var reason, out var detail))
            {
                ledger.Drop(GuardrailStage.SensitiveInference, reason, item.ProductId, detail);
                continue;
            }

            kept.Add(item);
        }

        return kept;
    }

    /// <summary>
    /// The category arm: a product is suppressed when any element of its path is flagged
    /// sensitive by the tree or named in <see cref="BlockedInferenceCategories"/>, and the
    /// customer neither named that category nor raised its topic in this session.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠ <b>The stated-TOPIC exemption is the one that was missing, and its absence made the
    /// docstring on this class false.</b> The only exemption used to be
    /// <see cref="GuardrailContext.ExplicitlyRequestedCategories"/>, which no caller in either
    /// project populated. MEASURED: Elena's own utterance <i>"I need a larger cuff for the
    /// blood-pressure monitor I already have"</i> put <c>blood pressure</c> into
    /// <see cref="GuardrailContext.SensitiveTopicsStatedInSession"/> and left
    /// <c>ExplicitlyRequestedCategories</c> empty, so GLX-9002 was dropped with
    /// <c>sensitive_category</c> and the detail "the customer did not ask for it in this session"
    /// — for a cuff she had just asked for. The headline promise of §F.5 ("a customer who asks for
    /// a larger blood-pressure cuff gets one") was false as implemented.
    /// </para>
    /// <para>
    /// <b>The exemption has to cover the whole PATH, not the matching element.</b> GLX-9002 sits at
    /// <c>Health &amp; Personal Care &gt; Blood pressure &gt; Cuffs</c> and all three elements are
    /// tree-flagged; a stated topic of <c>blood pressure</c> matches only the middle one. So
    /// <see cref="GuardrailContext.Create"/> expands a stated topic into every element of every
    /// category path it names, and this loop consults both sets.
    /// </para>
    /// </remarks>
    private static bool IsSensitiveProduct(Product product, GuardrailContext context, out string reason, out string detail)
    {
        reason = GuardrailReasons.SensitiveCategory;
        detail = string.Empty;

        foreach (var element in product.CategoryPath)
        {
            bool flaggedByTree = context.SensitiveCategoryNames.Contains(element);
            bool flaggedByName = IsBlockedCategoryName(element);
            if (!flaggedByTree && !flaggedByName) continue;
            if (IsExemptCategoryElement(element, context)) continue;

            detail = $"sits under \"{element}\", a category that may not be surfaced by inference; " +
                     "the customer did not ask for it in this session. The category stays browsable and searchable — " +
                     "it is the unsolicited inference that is blocked, not the category";
            return true;
        }

        return false;
    }

    /// <summary>
    /// True when the customer put this category element in play themselves — by naming the
    /// category, or by raising a special-category term the element's own words carry.
    /// </summary>
    /// <param name="element">One element of a <see cref="Product.CategoryPath"/>.</param>
    /// <param name="context">The session's stated topics and named categories.</param>
    public static bool IsExemptCategoryElement(string? element, GuardrailContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(element)) return false;

        var trimmed = element.Trim();
        if (context.ExplicitlyRequestedCategories.Contains(trimmed)) return true;

        // "Blood pressure" the category and "blood pressure" the stated topic are the same thing
        // said by the two sides of the conversation. Compared through the same tokeniser, so a
        // hyphen or a capital cannot separate them.
        foreach (var term in AllSpecialCategoryTerms(trimmed))
            if (context.SensitiveTopicsStatedInSession.Contains(term))
                return true;

        return false;
    }

    private static HashSet<string> BuildSingleWordTerms()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var term in SpecialCategoryTerms)
        {
            var tokens = Tokenize(term);
            if (tokens.Length == 1) set.Add(tokens[0]);
        }

        return set;
    }

    private static string[][] BuildMultiWordTerms()
    {
        var list = new List<string[]>();
        foreach (var term in SpecialCategoryTerms)
        {
            var tokens = Tokenize(term);
            if (tokens.Length > 1) list.Add(tokens);
        }

        return [.. list];
    }

    /// <summary>
    /// Lower-cases, strips diacritics, and splits on every non-alphanumeric character.
    /// Token-level matching is what keeps <c>"pill"</c> out of <c>"pillow"</c>.
    /// </summary>
    private static string[] Tokenize(string text)
    {
        var folded = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(folded.Length);

        foreach (var ch in folded)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;

            char c = char.ToLowerInvariant(ch);
            sb.Append(char.IsLetterOrDigit(c) ? c : ' ');
        }

        return sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
