// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

namespace Galaxus.RecommendationAgent.Evals.Controls;

/// <summary>
/// Negative control #6 — the constraint-blind recommender: a uniform draw from the catalogue that
/// reads neither the need nor the customer. The chance floor, EXECUTED rather than declared.
/// </summary>
/// <remarks>
/// <para>
/// <b>What it must score.</b> Exactly the floor, in expectation. A stated-need case's floor is
/// <c>|S| / N</c> — the share of the catalogue that satisfies the need — and that is what a draw
/// that ignores every constraint scores on average, whatever k it draws. Eval 02b runs this arm
/// many times per case and checks the executed mean against the closed form within a stated
/// band. Above the band, the grader is crediting things it should not; below it, the grader is
/// rejecting true satisfiers. Either is a wiring fault, and only one direction is the flattering
/// one.
/// </para>
/// <para>
/// <b>Deterministic.</b> The seed is a stable hash of the customer id and the repetition index —
/// not <c>string.GetHashCode</c>, which .NET randomises per process — so a run reproduces its
/// draws byte for byte and a "floor" cannot drift between two invocations of the same command.
/// </para>
/// <para>
/// <b>The optional ownership exclusion is for Eval 02c only.</b> A next-purchase floor is
/// <c>k / pool</c> where the pool excludes what the customer already owns, because no discovery
/// arm re-recommends owned items; with <c>excludeOwned</c> the draw is from that pool and the
/// control reads exactly one thing about the customer — their owned SKUs — and says so in its
/// label. On Eval 02b it reads nothing at all.
/// </para>
/// </remarks>
public sealed class Broken06_ConstraintBlindRecommender : IEvaluableAgent
{
    /// <summary>How many items a draw presents. The suite's declared degenerate budget.</summary>
    public const int DrawSize = ChanceFloors.DegenerateDrawSize;

    private readonly int _rep;
    private readonly bool _excludeOwned;

    /// <summary>Creates one draw.</summary>
    /// <param name="rep">Repetition index — part of the seed, so each rep is a different draw.</param>
    /// <param name="excludeOwned">Draw from the catalogue minus the customer's owned SKUs (Eval 02c's pool).</param>
    public Broken06_ConstraintBlindRecommender(int rep, bool excludeOwned = false)
    {
        _rep = rep;
        _excludeOwned = excludeOwned;
    }

    /// <inheritdoc/>
    public string Name => nameof(Broken06_ConstraintBlindRecommender);

    /// <summary>The size of the pool the most recent draw came from. Eval 02c checks it against the expected pool.</summary>
    public int LastPoolSize { get; private set; }

    /// <inheritdoc/>
    public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default)
    {
        var catalogue = Catalogue.Default;
        string userId = ScriptedTrace.PersonaIdFrom(prompt) ?? "no-customer";

        IReadOnlyList<Product> pool = Pool(userId, _excludeOwned);
        LastPoolSize = pool.Count;

        var trace = new ScriptedTrace();
        foreach (Product product in Draw(pool, userId, _rep, DrawSize))
        {
            string? citation = Broken03_SingleShotWorkflow.FirstResolvingCitation(product);
            if (citation is null) continue;

            trace.Present(product.Id,
                $"Drawn at random — {product.Name}.",
                citation,
                outOfStock: product.StockUnits == 0);
        }

        trace.Say(_excludeOwned
            ? "Uniform draw from the catalogue minus what you own. No constraint, no need, no model."
            : "Uniform draw from the whole catalogue. No constraint, no need, no customer, no model.");
        return Task.FromResult(trace.ToResponse());
    }

    /// <summary>The pool a draw is taken from, in a fixed order so the seed alone decides the draw.</summary>
    /// <param name="userId">The customer, read only when <paramref name="excludeOwned"/> is true.</param>
    /// <param name="excludeOwned">Remove the customer's owned SKUs from the pool.</param>
    public static IReadOnlyList<Product> Pool(string userId, bool excludeOwned)
    {
        var catalogue = Catalogue.Default;
        IEnumerable<Product> pool = catalogue.All;

        if (excludeOwned && UserProfiles.Find(userId) is { } profile)
        {
            var owned = profile.Purchases.Select(p => p.ProductId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            pool = pool.Where(p => !owned.Contains(p.Id));
        }

        return [.. pool.OrderBy(p => p.Id, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Draws <paramref name="k"/> distinct products uniformly from <paramref name="pool"/> — a
    /// partial Fisher-Yates over a copy, seeded by <see cref="StableSeed"/>.
    /// </summary>
    /// <param name="pool">The pool, in a fixed order.</param>
    /// <param name="salt">Seed salt — the customer id.</param>
    /// <param name="rep">Repetition index.</param>
    /// <param name="k">Draw size, clamped to the pool.</param>
    public static IReadOnlyList<Product> Draw(IReadOnlyList<Product> pool, string salt, int rep, int k)
    {
        ArgumentNullException.ThrowIfNull(pool);

        var copy = pool.ToArray();
        var rng = new Random(StableSeed(salt, rep));
        int take = Math.Min(k, copy.Length);

        for (int i = 0; i < take; i++)
        {
            int j = i + rng.Next(copy.Length - i);
            (copy[i], copy[j]) = (copy[j], copy[i]);
        }

        return copy[..take];
    }

    /// <summary>FNV-1a over the salt and the rep. Stable across processes, unlike <c>string.GetHashCode</c>.</summary>
    /// <param name="salt">Seed salt.</param>
    /// <param name="rep">Repetition index.</param>
    public static int StableSeed(string salt, int rep)
    {
        const uint offset = 2166136261;
        const uint prime = 16777619;

        uint hash = offset;
        foreach (char c in salt ?? string.Empty)
        {
            hash ^= c;
            hash *= prime;
        }
        hash ^= (uint)rep;
        hash *= prime;

        return unchecked((int)hash);
    }
}
