// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

using System.Text.Json;
using System.Text.Json.Nodes;
using Galaxus.RecommendationAgent.Retrieval;

namespace Galaxus.RecommendationAgent.Evals;

/// <summary>
/// What produced a stored snapshot: the embedding space it resolved in and the chat deployment the
/// process was configured with. Attached by the ONE store chokepoint, so no eval has to remember.
/// </summary>
/// <remarks>
/// <para>
/// <b>Plan item 7.1 / ADR-031 S1, second clause.</b> S1's acceptance is *"two runs coexist; the
/// model id is recorded"*. The first half has been true since <c>EvalResultStore.Write</c> started
/// archiving the previous file under its own last-write time — re-verified by execution, not by
/// reading: the store holds hundreds of dated archives beside thirteen canonical keys. The second
/// half was **not** true: measured over the canonical files, <c>eval01_integrity</c>,
/// <c>eval02_coverage_ab</c>, <c>eval03_controls</c> and <c>eval07_topology</c> carried
/// <c>Label</c>, their payload and <c>RunAt</c>, and nothing at all about what produced them.
/// </para>
/// <para>
/// ⚠ <b>Why that matters here specifically, and it is not bookkeeping.</b> This suite has TWO
/// resolved configurations that both claim to be the product — the concept default and
/// <c>--real-vectors</c> — and §20/§42 measured that the deterministic discovery loop is not
/// space-invariant: two of Eval 07's five customers swap round counts between them and one of four
/// frozen stop reasons is unreachable on the real path. Until now a snapshot did not say which of
/// the two it came from, and the canonical file holds <b>whichever space ran last</b> (§54.6). A
/// reader comparing two snapshots had no way to tell a change in the agent from a change in the
/// space.
/// </para>
/// <para>
/// ⚠ <b>What is deliberately NOT here.</b> No endpoint, no key, no host, no fingerprint or hash of
/// either — the standing rule, and it binds a provenance block harder than anything else because a
/// provenance block is exactly where somebody would put them. Only deployment NAMES, which
/// <c>Config.PrintAzureTarget</c> already prints, and a boolean for whether credentials were
/// present at all.
/// </para>
/// <para>
/// ⚠ <b><see cref="ChatDeploymentConfigured"/> is what the PROCESS was configured with, never
/// evidence that this snapshot's numbers came from a model call.</b> Evals 03, 04 and 07 call no
/// model on any path and write anyway. The field is named "configured" for that reason and
/// <see cref="Note"/> says it in the file, because the file will be read by someone who does not
/// have this class open.
/// </para>
/// </remarks>
/// <param name="EmbeddingSpace">The RESOLVED space's source name, or <c>"(unresolved)"</c> when this run never retrieved.</param>
/// <param name="EmbeddingModel">The resolved source's model id, or <c>"(unresolved)"</c>.</param>
/// <param name="EmbeddingDimensions">The resolved source's dimensionality, or 0.</param>
/// <param name="ChatDeploymentConfigured">The chat deployment name this process resolves. NOT proof a model was called.</param>
/// <param name="AzureCredentialsPresent">Whether an endpoint and key were both set. Never the values.</param>
/// <param name="Note">The sentence a reader of the raw file needs in front of the two fields above.</param>
public sealed record SnapshotProvenance(
    string EmbeddingSpace,
    string EmbeddingModel,
    int EmbeddingDimensions,
    string ChatDeploymentConfigured,
    bool AzureCredentialsPresent,
    string Note)
{
    /// <summary>The JSON member name the store writes this under.</summary>
    public const string MemberName = "Provenance";

    /// <summary>The standing caveat, written into every snapshot so it travels with the file.</summary>
    public const string StandingNote =
        "ChatDeploymentConfigured is what THIS PROCESS was configured with and is NOT evidence that a model "
      + "produced these numbers — Evals 03, 04 and 07 call none and persist anyway. EmbeddingSpace is the "
      + "RESOLVED space, never the requested one. No endpoint, key, host or digest of either is recorded here.";

    /// <summary>
    /// Reads the provenance of the current process. Cheap, side-effect free, and — deliberately —
    /// it never RESOLVES the embedding space: it reads <see cref="Retrieval.EmbeddingSpace.Current"/>,
    /// so writing a snapshot cannot cause a live space-identity probe or pin a space a run never
    /// chose.
    /// </summary>
    public static SnapshotProvenance OfThisProcess()
    {
        var resolved = Retrieval.EmbeddingSpace.Current;

        return new SnapshotProvenance(
            resolved?.Source.Name       ?? "(unresolved)",
            resolved?.Source.ModelId    ?? "(unresolved)",
            resolved?.Source.Dimensions ?? 0,
            Config.Model,
            Config.IsConfigured,
            StandingNote);
    }

    /// <summary>
    /// Splices this provenance into an already-serialised snapshot as one extra top-level member.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Done on the serialised JSON rather than on the snapshot RECORDS because there are eight
    /// record types and a ninth will be added by whoever adds the tenth eval. A property on each
    /// type is a thing to forget; a splice at the single write chokepoint is not.
    /// </para>
    /// <para>
    /// ⚠ <b>NaN has to survive this.</b> An empty denominator is represented as NaN throughout this
    /// suite — that is how "we could not score this one" is kept from rendering as 0 or 1 — and the
    /// store serialises with <c>AllowNamedFloatingPointLiterals</c>, which writes it as the STRING
    /// <c>"NaN"</c>. That is valid JSON, so the parse round trip below preserves it, and the
    /// control <c>EverySnapshotSaysWhatProducedIt</c> asserts exactly that rather than assuming it.
    /// </para>
    /// </remarks>
    /// <param name="snapshotJson">The snapshot, already serialised by the store's own options.</param>
    /// <param name="options">The store's serialiser options, so the output formats identically.</param>
    /// <returns>The same document with one extra top-level member.</returns>
    /// <exception cref="InvalidOperationException">The snapshot did not serialise to a JSON object.</exception>
    public string Attach(string snapshotJson, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(snapshotJson);
        ArgumentNullException.ThrowIfNull(options);

        if (JsonNode.Parse(snapshotJson) is not JsonObject document)
        {
            throw new InvalidOperationException(
                "A snapshot must serialise to a JSON object before provenance can be attached; this one did not.");
        }

        document[MemberName] = JsonSerializer.SerializeToNode(this, options);
        return document.ToJsonString(options);
    }
}
