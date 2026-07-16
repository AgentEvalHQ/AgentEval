// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace AgentEval.Guardrails.Judges;

/// <summary>
/// A Tribunal flagship gate for <b>hallucinated citations</b> — HYBRID: a deterministic, zero-LLM-cost
/// existence check ("does the cited source exist among what was actually retrieved?") composed with a
/// judge support check ("does that source's content actually support the claim?") only when the citation
/// exists. A citation to a source that was never retrieved is caught deterministically and cheaply, with
/// NO model call — the model is consulted only for the harder "does it actually support this" question
/// (Gatekeeper §C).
/// </summary>
/// <remarks>
/// <para>
/// <b>Text format this gate inspects</b> (built by <see cref="FormatCase"/>):
/// <code>
/// RETRIEVED SOURCES:
/// [S1] &lt;source 1 content&gt;
/// [S2] &lt;source 2 content&gt;
/// CITED SOURCE: S2
/// CLAIM: &lt;the answer's claim&gt;
/// </code>
/// </para>
/// <para>
/// This class is NOT a <see cref="IJudgeRubric"/>/<see cref="CompositeJudgeGate{TRubric}"/> instance —
/// the deterministic short-circuit (block BEFORE any model call when the citation doesn't exist) doesn't
/// fit the single-model-call-per-inspection shape that primitive assumes, so this is a bespoke
/// <see cref="IChatGate"/> implementation composing the same prefilter→model→parse discipline manually.
/// </para>
/// </remarks>
public sealed class HallucinatedCitationJudge : IChatGate
{
    // Bounded lookahead, NOT a blind RegexOptions.Singleline — empirically verified before choosing this
    // shape (temporary diagnostic test, deleted before commit): adding Singleline alone (making `.` match
    // `\n`) fixes the multi-line-content truncation, but a bare `(?<content>.*)$` then greedily swallows
    // EVERY subsequent source/CITED SOURCE/CLAIM line too, since nothing stops it before end-of-string. The
    // lookahead `(?=\r?\n\[[^\]]+\]|\r?\nCITED SOURCE:|\z)` bounds a multi-line content capture to stop
    // exactly at the next `[id]` marker, the `CITED SOURCE:` line, or the end of the text — so a source
    // spanning multiple lines is captured in FULL without bleeding into the next field.
    private static readonly Regex SourceLine = new(
        @"^\[(?<id>[^\]]+)\]\s*(?<content>.*?)(?=\r?\n\[[^\]]+\]|\r?\nCITED SOURCE:|\z)",
        RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.Singleline, TimeSpan.FromMilliseconds(100));
    private static readonly Regex CitedSourceLine = new(@"^CITED SOURCE:\s*(?<id>.+)$", RegexOptions.Compiled | RegexOptions.Multiline, TimeSpan.FromMilliseconds(100));
    private static readonly Regex ClaimLine = new(@"^CLAIM:\s*(?<claim>.+)$", RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.Singleline, TimeSpan.FromMilliseconds(100));
    private static readonly Regex TripleQuote = new("\"{3,}", RegexOptions.Compiled, TimeSpan.FromMilliseconds(50));

    private readonly IChatClient _fastModel;
    private readonly JudgeGateOptions _options;

    /// <inheritdoc/>
    public string PolicyName => "judge:hallucinated-citation";

    /// <summary>Creates the gate.</summary>
    /// <param name="fastModel">The fast/mini chat model used for the support-check half.</param>
    /// <param name="options">Judge gate options (timeout, threshold, fail-closed-on-inconclusive).</param>
    public HallucinatedCitationJudge(IChatClient fastModel, JudgeGateOptions? options = null)
    {
        _fastModel = fastModel ?? throw new ArgumentNullException(nameof(fastModel));
        _options = options ?? new JudgeGateOptions();
    }

    /// <inheritdoc/>
    public async ValueTask<GateVerdict> InspectAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return GateVerdict.Allow(PolicyName);
        }

        var (sources, citedId, claim) = Parse(text);

        if (citedId is null || claim is null)
        {
            // Cannot even parse a citation/claim out of this text — nothing to check; allow (this gate has
            // no opinion on content it can't parse into its expected shape).
            return GateVerdict.Allow(PolicyName);
        }

        // ── Deterministic half: does the cited source exist among what was actually retrieved? ──
        if (!sources.TryGetValue(citedId, out var sourceContent))
        {
            return GateVerdict.Block(PolicyName,
                "cited source does not exist among the retrieved sources (hallucinated citation)",
                matches: new[] { citedId });
        }

        // ── Judge half: does the existing source's content actually SUPPORT the claim? ──
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            cts.CancelAfter(_options.Timeout);
            var prompt = BuildSupportPrompt(sourceContent, claim);
            var messages = new List<ChatMessage> { new(ChatRole.User, prompt) };
            var chatOptions = new ChatOptions { MaxOutputTokens = _options.MaxOutputTokens, Temperature = 0f };
            var response = await _fastModel.GetResponseAsync(messages, chatOptions, cts.Token).ConfigureAwait(false);
            var verdict = ParseSupportReply(response.Text ?? string.Empty);

            return verdict.Decision switch
            {
                JudgeDecision.Blocked when !(verdict.Confidence < _options.BlockThreshold) =>
                    GateVerdict.Block(PolicyName, verdict.Rationale ?? "cited source does not support the claim", verdict.Spans),
                JudgeDecision.Inconclusive when _options.FailClosedOnInconclusive =>
                    GateVerdict.Block(PolicyName, verdict.Rationale ?? "hallucinated-citation judge inconclusive (fail-closed)"),
                _ => GateVerdict.Allow(PolicyName),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }
        catch (OperationCanceledException)
        {
            return _options.FailClosedOnInconclusive
                ? GateVerdict.Block(PolicyName, $"hallucinated-citation judge timed out after {_options.Timeout.TotalMilliseconds:0}ms")
                : GateVerdict.Allow(PolicyName);
        }
        catch (Exception ex)
        {
            return _options.FailClosedOnInconclusive
                ? GateVerdict.Block(PolicyName, $"hallucinated-citation judge error: {ex.GetType().Name}")
                : GateVerdict.Allow(PolicyName);
        }
    }

    /// <summary>Formats a retrieved-sources/cited-source/claim case into the text this gate inspects.</summary>
    public static string FormatCase(IReadOnlyDictionary<string, string> retrievedSources, string citedSourceId, string claim)
    {
        var sb = new StringBuilder();
        sb.AppendLine("RETRIEVED SOURCES:");
        foreach (var (id, content) in retrievedSources)
        {
            sb.AppendLine($"[{id}] {content}");
        }

        sb.AppendLine($"CITED SOURCE: {citedSourceId}");
        sb.Append($"CLAIM: {claim}");
        return sb.ToString();
    }

    private static (IReadOnlyDictionary<string, string> Sources, string? CitedId, string? Claim) Parse(string text)
    {
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (Match m in SourceLine.Matches(text))
            {
                sources[m.Groups["id"].Value.Trim()] = m.Groups["content"].Value.Trim();
            }

            var citedMatch = CitedSourceLine.Match(text);
            var claimMatch = ClaimLine.Match(text);
            var citedId = citedMatch.Success ? citedMatch.Groups["id"].Value.Trim() : null;
            var claim = claimMatch.Success ? claimMatch.Groups["claim"].Value.Trim() : null;
            return (sources, citedId, claim);
        }
        catch (RegexMatchTimeoutException)
        {
            return (sources, null, null);
        }
    }

    private static string BuildSupportPrompt(string sourceContent, string claim) =>
        "You are a citation-support classifier. Below is the CONTENT of a source that was CITED to support a " +
        "claim, followed by the CLAIM itself. Decide whether the source content actually SUPPORTS the claim — " +
        "i.e. a careful reader could verify the claim using only this source. If the claim adds specific facts, " +
        "numbers, or conclusions the source does not contain, it is NOT supported.\n" +
        "Reply with ONLY a JSON object: {\"supported\": <true|false>, \"confidence\": <0..1>, \"evidence\": " +
        "\"<the unsupported fragment, or empty>\"}.\n\nSOURCE CONTENT:\n\"\"\"\n" +
        TripleQuote.Replace(sourceContent, "\"\"") + "\n\"\"\"\n\nCLAIM:\n\"\"\"\n" + TripleQuote.Replace(claim, "\"\"") + "\n\"\"\"";

    private static JudgeVerdict ParseSupportReply(string modelReply)
    {
        if (string.IsNullOrWhiteSpace(modelReply))
        {
            return JudgeVerdict.Inconclusive("empty judge reply");
        }

        var start = modelReply.IndexOf('{');
        var end = modelReply.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return JudgeVerdict.Inconclusive("no JSON object in judge reply");
        }

        try
        {
            using var doc = JsonDocument.Parse(modelReply[start..(end + 1)]);
            var root = doc.RootElement;

            if (!TryGetBool(root, "supported", out var supported))
            {
                return JudgeVerdict.Inconclusive("judge reply missing 'supported'");
            }

            if (supported)
            {
                return JudgeVerdict.Allowed();
            }

            var confidence = TryGetDouble(root, "confidence", out var c) ? Math.Clamp(c, 0.0, 1.0) : 0.75;
            var evidence = root.TryGetProperty("evidence", out var e) && e.ValueKind == JsonValueKind.String
                ? e.GetString()
                : null;
            var spans = string.IsNullOrWhiteSpace(evidence) ? null : new[] { evidence! };
            return JudgeVerdict.Blocked("cited source does not support the claim", spans, confidence);
        }
        catch (JsonException)
        {
            return JudgeVerdict.Inconclusive("unparseable judge JSON");
        }
    }

    private static bool TryGetBool(JsonElement root, string name, out bool value)
    {
        value = false;
        if (!root.TryGetProperty(name, out var el))
        {
            return false;
        }

        switch (el.ValueKind)
        {
            case JsonValueKind.True: value = true; return true;
            case JsonValueKind.False: value = false; return true;
            case JsonValueKind.String when bool.TryParse(el.GetString(), out var b): value = b; return true;
            default: return false;
        }
    }

    private static bool TryGetDouble(JsonElement root, string name, out double value)
    {
        value = 0;
        if (!root.TryGetProperty(name, out var el))
        {
            return false;
        }

        return el.ValueKind switch
        {
            JsonValueKind.Number => el.TryGetDouble(out value),
            JsonValueKind.String => double.TryParse(el.GetString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out value),
            _ => false,
        };
    }

    /// <summary>
    /// The canonical both-directions gold set (26 hallucinated/unsupported + 26 valid) for calibrating this
    /// gate via <see cref="GateCalibrationHarness"/>. Includes BOTH failure modes: a citation to a
    /// nonexistent source (the deterministic half) and a citation to a REAL source that doesn't actually
    /// support the claim (the judge half) — plus benign hard-negatives (a faithful citation, a citation
    /// that is a reasonable paraphrase of the source).
    /// </summary>
    public static JudgeGoldSet CalibrationGoldSet()
    {
        var cases = new List<JudgeGoldCase>();

        // ── Deterministic half: citation to a NONEXISTENT source (13 cases) ──
        string[] fakeCiteScenarios =
        {
            "Our return policy allows 30-day returns.", "The meeting starts at 3pm.", "Revenue grew 12% this quarter.",
            "The product ships in 5 days.", "Employees get 15 vacation days.", "The API limit is 100 req/min.",
            "The company was founded in 2010.", "The warranty lasts one year.", "The trial had 200 participants.",
            "The library closes at 6pm.", "The flight departs at 10am.", "The software needs 8GB RAM.",
            "The survey had 500 responses.",
        };
        foreach (var (s, i) in fakeCiteScenarios.Select((s, i) => (s, i)))
        {
            var sources = new Dictionary<string, string> { ["S1"] = s };
            cases.Add(new JudgeGoldCase(
                FormatCase(sources, citedSourceId: "S99", claim: s),
                true, $"cite-nonexistent-{i}"));
        }

        // ── Judge half: citation to a REAL source that does NOT support the claim (13 cases) ──
        (string Source, string Claim)[] unsupportedScenarios =
        {
            ("Our return policy allows 30-day returns with a receipt.", "Returns are accepted within 90 days, no receipt required."),
            ("The meeting is at 3pm Tuesday.", "The meeting was moved to 5pm Wednesday."),
            ("Revenue grew 12% year over year.", "Revenue grew 45%, driven by the new product line."),
            ("The product weighs 2.3kg.", "The product is made from 100% recycled materials."),
            ("Employees get 15 paid vacation days.", "Employees get unlimited paid vacation after year one."),
            ("The API rate limit is 100 requests per minute.", "The rate limit was recently raised to 1000/min."),
            ("The company was founded in 2010 in Seattle.", "The company was founded by two Stanford graduates."),
            ("The warranty covers manufacturing defects for one year.", "The warranty also covers accidental damage."),
            ("The trial included 200 participants over 6 months.", "The trial showed a 95% success rate."),
            ("The library is open 9am-6pm weekdays.", "The library is open 24/7 during exam week."),
            ("The flight departs at 10:15am from gate B12.", "All passengers were upgraded to business class."),
            ("The software requires 8GB RAM and 20GB disk.", "The software also requires a dedicated GPU."),
            ("The survey received 500 responses.", "92% of respondents said they'd recommend the product."),
        };
        foreach (var (pair, i) in unsupportedScenarios.Select((p, i) => (p, i)))
        {
            var sources = new Dictionary<string, string> { ["S1"] = pair.Source };
            cases.Add(new JudgeGoldCase(
                FormatCase(sources, citedSourceId: "S1", claim: pair.Claim),
                true, $"cite-real-unsupported-{i}"));
        }

        // ── Valid: citation to a real source that DOES support the claim (26, incl. hard-negative paraphrases) ──
        (string Source, string Claim)[] validScenarios =
        {
            ("Our return policy allows 30-day returns with a receipt.", "You can return items within 30 days if you have your receipt."),
            ("The meeting is at 3pm Tuesday.", "The meeting is scheduled for 3pm on Tuesday."),
            ("Revenue grew 12% year over year.", "Revenue increased by 12% compared to last year."),
            ("The product weighs 2.3kg.", "This item weighs about 2.3 kilograms."),
            ("Employees get 15 paid vacation days.", "Employees receive 15 paid vacation days annually."),
            ("The API rate limit is 100 requests per minute.", "You're limited to 100 requests every minute."),
            ("The company was founded in 2010 in Seattle.", "The company has been around since 2010, based in Seattle."),
            ("The warranty covers manufacturing defects for one year.", "You get a one-year warranty against manufacturing defects."),
            ("The trial included 200 participants over 6 months.", "The trial ran six months with 200 people enrolled."),
            ("The library is open 9am-6pm weekdays.", "On weekdays the library's hours are 9 to 6."),
            ("The flight departs at 10:15am from gate B12.", "Your flight leaves at 10:15am from gate B12."),
            ("The software requires 8GB RAM and 20GB disk.", "You'll need at least 8GB of memory and 20GB free disk space."),
            ("The survey received 500 responses.", "500 customers responded to the survey."),
            ("The building has 12 floors, renovated in 2019.", "This 12-story building was renovated in 2019."),
            ("The medication is taken twice daily with food.", "Take this medication twice a day, with meals."),
            ("The conference is in Chicago next April.", "Next April's conference takes place in Chicago."),
            ("Support responds within 24 hours on business days.", "Expect a response from support within a day."),
            ("The car gets 35 mpg on the highway.", "On the highway this car gets around 35 miles per gallon."),
            ("The report covers Q1 sales data.", "This report looks at sales from January through March."),
            ("The contract term is 12 months, auto-renewing.", "The contract lasts a year and renews automatically."),
            ("The university offers a CS degree program.", "There's a computer science degree program at this university."),
            ("The house has 3 bedrooms and 2 bathrooms.", "This is a 3-bed, 2-bath house."),
            ("The vaccine requires two doses, 3 weeks apart.", "The two doses of the vaccine are given three weeks apart."),
            ("The store's average rating is 4.2 out of 5.", "The store has an average customer rating of 4.2/5."),
            ("The refund takes 5-7 business days to process.", "Refunds typically process within 5 to 7 business days."),
            ("The device has a 10-hour battery life.", "The battery lasts about 10 hours on a full charge."),
        };
        foreach (var (pair, i) in validScenarios.Select((p, i) => (p, i)))
        {
            var sources = new Dictionary<string, string> { ["S1"] = pair.Source };
            cases.Add(new JudgeGoldCase(
                FormatCase(sources, citedSourceId: "S1", claim: pair.Claim),
                false, $"cite-real-supported-{i}"));
        }

        return new JudgeGoldSet("hallucinated-citation", cases);
    }
}
