// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace AgentEval.MAF.Gatekeeper;

/// <summary>Immutable internal projection from a decided refusal to its model-visible presentation.</summary>
internal sealed class GatekeeperRefusalPresenter
{
    internal static readonly GatekeeperRefusalPresenter Structured =
        new(GatekeeperRefusalStyle.Structured, Array.Empty<string>());

    private readonly GatekeeperRefusalStyle _style;
    private readonly IReadOnlyList<string> _camouflagedMessages;

    private GatekeeperRefusalPresenter(
        GatekeeperRefusalStyle style,
        IReadOnlyList<string> camouflagedMessages)
    {
        _style = style;
        _camouflagedMessages = camouflagedMessages;
    }

    internal static GatekeeperRefusalPresenter FromResolved(ResolvedGatekeeperOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.RefusalStyle switch
        {
            GatekeeperRefusalStyle.Structured => Structured,
            GatekeeperRefusalStyle.Camouflaged when options.CamouflagedRefusalMessages.Count > 0 =>
                new(options.RefusalStyle, options.CamouflagedRefusalMessages),
            _ => throw new InvalidOperationException("Resolved Gatekeeper refusal presentation is invalid."),
        };
    }

    internal string Present(
        string referenceId,
        RefusalDisposition disposition = RefusalDisposition.Denied,
        int? attempts = null)
    {
        if (_style == GatekeeperRefusalStyle.Structured)
        {
            return GateReferenceId.RefusalBody(referenceId, disposition, attempts);
        }

        try
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(referenceId));
            var index = BinaryPrimitives.ReadUInt32LittleEndian(hash) % (uint)_camouflagedMessages.Count;
            return _camouflagedMessages[(int)index];
        }
        catch (Exception exception) when
            (exception is not OutOfMemoryException and not StackOverflowException)
        {
            return _camouflagedMessages[0];
        }
    }
}
