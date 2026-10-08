// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using AgentEval.Results.Runs;
using AgentEval.Results.Signatures;

namespace AgentEval.Results.Integrity;

/// <summary>A result whose effective state an <c>override</c> or <c>adjudicate</c> sets ([OVL-7]): both states are shown.</summary>
/// <param name="ResultId">The result.</param>
/// <param name="SealedState">Its state in the sealed results.ndjson.</param>
/// <param name="EffectiveState">The <c>state</c> of the last verified <c>override</c> or <c>adjudicate</c> targeting it.</param>
/// <param name="Event">That event's <c>eventId</c>.</param>
public sealed record EffectiveResult(string ResultId, string? SealedState, string? EffectiveState, string Event);

/// <summary>A target's review status ([OVL-8]): the kind of the last verified <c>approve</c> or <c>reject</c> targeting it.</summary>
/// <param name="Target"><c>run</c>, or the result id.</param>
/// <param name="Status"><c>approve</c> or <c>reject</c>.</param>
/// <param name="Event">That event's <c>eventId</c>.</param>
public sealed record EffectiveReview(string Target, string Status, string Event);

/// <summary>A verified <c>waive</c> ([OVL-9]): what it waives, until when, and whether it holds at the view's time.</summary>
/// <param name="Target">The event's target without <c>run</c> and <c>runHash</c>.</param>
/// <param name="Expires">The event's <c>expires</c>, as written.</param>
/// <param name="Active">Its <c>at</c> ≤ the view's time &lt; its <c>expires</c>; an expired waiver is shown as expired.</param>
/// <param name="Event">The event's <c>eventId</c>.</param>
public sealed record EffectiveWaiver(JsonObject Target, string? Expires, bool Active, string Event);

/// <summary>
/// The effective view of a run with its overlays (contracts/aef/1/spec/04-integrity.md, §4.3; the shape of spec 09
/// §9.2.1), computed from the events of the verified batches (<see cref="OverlayChain.VerifiedEvents"/>), in file order
/// ([OVL-6]: <c>at</c> is shown, never used to reorder). Parents, summaries, gate decisions and lanes are not
/// recomputed: they describe the sealed run ([OVL-7]). An overlay never changes a sealed file.
/// </summary>
public sealed class EffectiveView
{
    private EffectiveView(
        IReadOnlyList<EffectiveResult> results,
        IReadOnlyList<EffectiveReview> reviews,
        IReadOnlyList<EffectiveWaiver> waivers,
        IReadOnlyList<string> withheld,
        int unsealedEvents)
    {
        Results = results;
        Reviews = reviews;
        Waivers = waivers;
        Withheld = withheld;
        UnsealedEvents = unsealedEvents;
    }

    /// <summary>One entry per result a verified <c>override</c> or <c>adjudicate</c> targets, in results.ndjson order.</summary>
    public IReadOnlyList<EffectiveResult> Results { get; }

    /// <summary>One entry per target of a verified <c>approve</c> or <c>reject</c>: the run first, then results in results.ndjson order.</summary>
    public IReadOnlyList<EffectiveReview> Reviews { get; }

    /// <summary>Every verified <c>waive</c>, in file order.</summary>
    public IReadOnlyList<EffectiveWaiver> Waivers { get; }

    /// <summary>
    /// The blobs (SHA-256, lower-case hex) authorized redactions name ([OVL-10]), in file order, each once: redacted,
    /// whether or not they are gone yet (spec 09 §9.2.1). §4.5's withheld count is narrower: the sealed ones that are gone.
    /// </summary>
    public IReadOnlyList<string> Withheld { get; }

    /// <summary>The number of events after the last verified batch: shown as unsealed, with no effect.</summary>
    public int UnsealedEvents { get; }

    /// <summary>Computes the effective view of the run in <paramref name="directory"/> at <paramref name="at"/>.</summary>
    /// <param name="directory">The run folder.</param>
    /// <param name="at">The time the view is computed at ([OVL-9] compares waivers with it).</param>
    /// <param name="policy">The caller's trust policy, which decides which redactions are authorized ([OVL-10]); null for none.</param>
    /// <exception cref="IOException">The folder or a file cannot be read.</exception>
    public static EffectiveView Compute(string directory, AefTime at, TrustPolicy? policy)
    {
        var folder = AefRunFolder.Open(directory);
        var documents = AefRunDocuments.Read(folder);
        return Compute(folder, documents, OverlayChain.Verify(folder, documents), at, policy);
    }

    /// <summary>Computes the effective view from a run already read and its verified chain.</summary>
    /// <exception cref="IOException">A file cannot be read.</exception>
    public static EffectiveView Compute(AefRunFolder folder, AefRunDocuments documents, OverlayChain chain, AefTime at, TrustPolicy? policy)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(chain);

        // The results in results.ndjson order (the first line of an id when one appears twice) and their sealed states.
        var order = new Dictionary<string, (int Position, string? State)>(StringComparer.Ordinal);
        foreach (var (_, line) in documents.Results.Objects)
        {
            if (AefNode.String(line["resultId"]) is { } id)
            {
                order.TryAdd(id, (order.Count, AefNode.String(line["state"])));
            }
        }

        var effective = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var reviews = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var waivers = new List<EffectiveWaiver>();
        foreach (var line in chain.VerifiedEvents)
        {
            var e = line.Event!;
            var target = e["target"] as JsonObject;
            var result = AefNode.String(target?["result"]);

            // §7.3: a kind this version does not know is an annotation, with no effect on any state.
            var kind = AefNode.String(e["kind"]);
            if (kind is "override" or "adjudicate")
            {
                if (result is not null && order.ContainsKey(result))
                {
                    effective[result] = e;   // [OVL-7]: the last one sets the effective state
                }
            }
            else if (kind is "approve" or "reject")
            {
                if (result is null)
                {
                    reviews["run"] = e;      // [OVL-8]: the last one is the review status
                }
                else if (order.ContainsKey(result))
                {
                    reviews[result] = e;
                }
            }
            else if (kind == "waive")
            {
                var waived = new JsonObject();
                foreach (var (name, value) in target ?? new JsonObject())
                {
                    if (name is not ("run" or "runHash"))
                    {
                        waived[name] = value?.DeepClone();
                    }
                }

                // [OVL-9]: from its at until its expires, compared with the view's time.
                var active = AefNode.Time(e["at"]) is { } from && AefNode.Time(e["expires"]) is { } until && from <= at && at < until;
                waivers.Add(new EffectiveWaiver(waived, AefNode.String(e["expires"]), active, AefNode.String(e["eventId"]) ?? ""));
            }
        }

        return new EffectiveView(
            [.. effective.OrderBy(r => order[r.Key].Position).Select(r => new EffectiveResult(
                r.Key, order[r.Key].State, AefNode.String(r.Value["state"]), AefNode.String(r.Value["eventId"]) ?? ""))],
            [.. reviews.OrderBy(r => r.Key == "run" ? -1 : order[r.Key].Position).Select(r => new EffectiveReview(
                r.Key, AefNode.String(r.Value["kind"]) ?? "", AefNode.String(r.Value["eventId"]) ?? ""))],
            waivers,
            AuthorizedRedactions(folder, chain, policy),
            chain.UnsealedEvents);
    }

    /// <summary>
    /// [OVL-10]: the blobs (SHA-256, lower-case hex) that authorized redactions withhold, in file order, each once. A
    /// <c>redact</c> is authorized when it is in a verified batch whose signature
    /// (<c>overlays/seal-&lt;nnnn&gt;.dsse.json</c> over <c>overlays/seal-&lt;nnnn&gt;.json</c>, [SIG-1]) verifies for the
    /// event's <c>by.identity</c> under the caller's trust policy, and the policy lets that identity redact
    /// (<c>"may": ["redact"]</c>, [SIG-4]). Without a policy nothing is authorized: whoever can append an unsigned event
    /// cannot suppress evidence.
    /// </summary>
    /// <exception cref="IOException">A file cannot be read.</exception>
    public static IReadOnlyList<string> AuthorizedRedactions(AefRunFolder folder, OverlayChain chain, TrustPolicy? policy)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(chain);
        var withheld = new List<string>();
        if (policy is null)
        {
            return withheld;
        }

        var signers = new Dictionary<int, IReadOnlyList<string>>();
        foreach (var line in chain.VerifiedEvents)
        {
            var e = line.Event!;
            if (AefNode.String(e["kind"]) != "redact" || AefNode.String(AefNode.At(e, "target", "blob")) is not { } blob
                || AefNode.String(AefNode.At(e, "by", "identity")) is not { } identity || withheld.Contains(blob, StringComparer.Ordinal))
            {
                continue;
            }

            var batch = line.Batch!.Value;
            if (!signers.TryGetValue(batch, out var identities))
            {
                signers[batch] = identities = BatchSigners(folder, batch, policy);
            }

            if (identities.Contains(identity, StringComparer.Ordinal) && policy.Allows(identity, TrustPolicy.Redact))
            {
                withheld.Add(blob);
            }
        }

        return withheld;
    }

    /// <summary>
    /// The identities a batch's signature verifies for under <paramref name="policy"/> ([SIG-5]), in policy order; none
    /// when the batch has no signature, or its signature is above the 56 MiB [ENC-17] allows an envelope (refused).
    /// </summary>
    /// <exception cref="IOException">A file cannot be read.</exception>
    public static IReadOnlyList<string> BatchSigners(AefRunFolder folder, int batch, TrustPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(policy);
        var (signature, seal) = (OverlayChain.SignaturePath(batch), OverlayChain.SealPath(batch));
        if (!folder.Has(signature) || !folder.Has(seal) || folder.Size(signature) > AefLimits.MaxEnvelopeBytes || folder.Size(seal) > AefLimits.MaxSealBytes)
        {
            return [];
        }

        return DsseVerifier.Verify(
            folder.Read(signature, AefLimits.MaxEnvelopeBytes), folder.Read(seal, AefLimits.MaxSealBytes), Dsse.InTotoPayloadType, policy).VerifiesFor;
    }
}
