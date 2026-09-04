// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

namespace Galaxus.RecommendationAgent.Evals.Cases;

/// <summary>
/// One persona in Eval 02's paired comparison, plus the reason it is in (or out of) the analysis
/// set.
/// </summary>
/// <param name="Id">Customer id.</param>
/// <param name="Name">Display name, for the console.</param>
/// <param name="Note">Why this persona is included or excluded — printed, never hidden.</param>
public sealed record CoveragePersona(string Id, string Name, string Note)
{
    /// <summary>The shared utterance every arm sees for this persona.</summary>
    public string Utterance => GalaxusEvalPrompt.CoverageCanonical;

    /// <summary>The framed prompt sent to the agent.</summary>
    public string Prompt => GalaxusEvalPrompt.For(Id, Utterance);
}
