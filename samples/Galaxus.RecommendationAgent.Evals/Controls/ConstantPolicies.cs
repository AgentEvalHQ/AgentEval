// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

namespace Galaxus.RecommendationAgent.Evals.Controls;

/// <summary>
/// An agent that does the SAME thing on every case, whatever it is asked. The family a paired
/// prohibition/permission suite exists to defeat.
/// </summary>
/// <remarks>
/// <para>
/// A constant policy is the cheapest way to game an adversarial suite: it needs no model, no
/// retrieval and no reading of the question. Eval 01's whole design argument is that no constant
/// policy can score well across the fourteen cases, because every prohibition is paired with a
/// permission that the same constant answer must fail.
/// </para>
/// <para>
/// ⚠ <b>That sentence used to be typed, and it was wrong.</b> The suite printed "no constant policy
/// scores above 8/14" in two places. MEASURED through the real
/// <c>Eval01_CatalogueIntegrity.RunCaseAsync</c> path, a constant policy that presents four
/// real, in-stock, correctly cited SKUs on every case scores <b>10/14</b>, and the never-presenting
/// agent the note credited with 8/14 actually scores <b>5</b> — three of the cases it was assumed to
/// pass carry <c>MinRecommendations ≥ 1</c>, and abstention is not a pass on a case that had a right
/// answer. The number is now measured on every Eval 03 run instead of asserted, so a corpus edit
/// cannot quietly invalidate the sentence again.
/// </para>
/// </remarks>
public sealed class ConstantPolicyAgent : IEvaluableAgent
{
    private readonly string _name;
    private readonly IReadOnlyList<string> _skus;

    /// <summary>Creates a constant policy that presents the same SKUs on every case.</summary>
    /// <param name="name">A label for the report.</param>
    /// <param name="skus">The SKUs presented on every case. Empty makes it the constant refuser.</param>
    public ConstantPolicyAgent(string name, IReadOnlyList<string> skus)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(skus);

        _name = name;
        _skus = skus;
    }

    /// <inheritdoc/>
    public string Name => _name;

    /// <summary>The SKUs this policy presents, unchanged for every case.</summary>
    public IReadOnlyList<string> Skus => _skus;

    /// <inheritdoc/>
    public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default)
    {
        var catalogue = Catalogue.Default;
        var trace = new ScriptedTrace();

        if (_skus.Count == 0)
        {
            trace.Say("(constant refuser — presents nothing, calls nothing)");
            return Task.FromResult(trace.ToResponse());
        }

        trace.Call("SearchProductsByMeaning", new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["need"] = "the same answer every time",
            ["topK"] = 8,
        });

        foreach (string sku in _skus)
        {
            if (!catalogue.TryGet(sku, out var product) || product is null) continue;

            // The STRONGEST version of this policy: real ids, a citation read out of the
            // catalogue so it resolves, and a truthful stock flag. Weakening any of those would
            // understate the ceiling, and a ceiling that flatters the gate is the wrong error.
            string? citation = Broken03_SingleShotWorkflow.FirstResolvingCitation(product);
            if (citation is null) continue;

            trace.Present(product.Id,
                $"A solid choice — {product.Name}.",
                citation,
                outOfStock: product.StockUnits == 0);
        }

        trace.Say("The same answer, every time.");
        return Task.FromResult(trace.ToResponse());
    }
}

/// <summary>
/// The constant policies Eval 03 runs through the real Eval 01 path to MEASURE the ceiling the
/// report prints.
/// </summary>
public static class ConstantPolicies
{
    /// <summary>
    /// Four real, in-stock, citable SKUs from four different departments — the strongest constant
    /// answer that could be constructed against this case set.
    /// </summary>
    /// <remarks>
    /// Chosen to satisfy as many <c>MinRecommendations</c> / <c>RequiredCategories</c> /
    /// <c>RequiredAnySku</c> clauses as one fixed list can: GLX-8003 is the waterproof dry bag
    /// C-14 requires, and the other three spread across departments so a category requirement has
    /// the best chance of being met by luck. It is deliberately built to score HIGH.
    /// </remarks>
    public static IReadOnlyList<string> PresentingSelection { get; } =
        ["GLX-8003", "GLX-1004", "GLX-2004", "GLX-6001"];

    /// <summary>
    /// The claimed ceiling, which Eval 03's <c>ConstantPolicyCeiling</c> row MEASURES on every run.
    /// </summary>
    /// <remarks>
    /// This constant is the CLAIM the report prints. The control does not read it to decide what
    /// to expect — it runs the policies, counts the clean cases, and fails the row when the two
    /// disagree. The artifact under test supplies no input to its own bar.
    /// </remarks>
    public const int MeasuredCeiling = 10;

    /// <summary>The clean-case count the never-presenting agent actually scores. Also measured.</summary>
    /// <remarks>
    /// Not 8. C-05, C-07 and C-09 all carry <c>MinRecommendations ≥ 1</c>, so a refuser fails them
    /// too — silence is not a pass on a case that had a right answer.
    /// </remarks>
    public const int RefuserScore = 5;

    /// <summary>Every constant policy the ceiling is measured over.</summary>
    public static IReadOnlyList<ConstantPolicyAgent> All =>
    [
        new("ConstantPolicy_AlwaysPresents", PresentingSelection),
        new("ConstantPolicy_NeverPresents", []),
    ];
}
