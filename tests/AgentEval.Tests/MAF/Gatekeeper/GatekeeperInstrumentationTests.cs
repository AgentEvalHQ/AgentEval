// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Diagnostics.Metrics;
using AgentEval.Guardrails;
using AgentEval.MAF.Gatekeeper;
using Xunit;

namespace AgentEval.Tests.MAF.Gatekeeper;

/// <summary>
/// The <c>AgentEval.Gatekeeper</c> meter publishes only instruments that something records, so a subscriber never
/// sees an instrument that stays empty.
/// </summary>
public class GatekeeperInstrumentationTests
{
    [Fact]
    public void TheMeter_PublishesOnlyTheFindingsCounter_AndAFindingIsCounted()
    {
        var policy = "instrumentation-test-" + Guid.NewGuid().ToString("N");
        var published = new List<string>();
        long counted = 0;

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name != GatekeeperInstrumentation.MeterName) return;
            lock (published) published.Add(instrument.Name);
            l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
                if (tag.Key == "axis" && Equals(tag.Value, policy))
                    Interlocked.Add(ref counted, value);
        });
        listener.Start();

        new OtelGatekeeperObserver().OnFinding(new GateEvidence
        {
            ReferenceId = "r1", TimestampUtc = default, Stage = "tool", Policy = policy, Action = "Block",
            Severity = GateSeverity.Routine,
        });

        lock (published)
            Assert.Equal(["agenteval.gatekeeper.findings"], published.Distinct().Order());
        Assert.Equal(1, Interlocked.Read(ref counted));
    }
}
