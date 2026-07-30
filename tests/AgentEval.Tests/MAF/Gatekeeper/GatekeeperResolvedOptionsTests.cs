// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Reflection;
using System.Runtime.ExceptionServices;
using AgentEval.MAF.Gatekeeper;
using AgentEval.Testing;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentEval.Tests.MAF.Gatekeeper;

/// <summary>Phase 3, Task 3.0 — one-time validation and immutable resolution of containment defaults.</summary>
public class GatekeeperResolvedOptionsTests
{
    [Fact]
    public void Resolve_UnsetOptionsPreserveExistingDefaults()
    {
        var resolved = Resolve(new GatekeeperOptions());

        Assert.Equal(5, Property<int>(resolved, "ContainmentRetryThreshold"));
        Assert.Equal(GatekeeperRefusalStyle.Structured, Property<GatekeeperRefusalStyle>(resolved, "RefusalStyle"));
        Assert.Empty(Property<IReadOnlyList<string>>(resolved, "CamouflagedRefusalMessages"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1_000)]
    public void Resolve_ContainmentThresholdBoundsAreAccepted(int threshold)
    {
        var resolved = Resolve(new GatekeeperOptions { ContainmentRetryThreshold = threshold });

        Assert.Equal(threshold, Property<int>(resolved, "ContainmentRetryThreshold"));
    }

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1_001)]
    [InlineData(int.MaxValue)]
    public void Resolve_ContainmentThresholdOutsideBoundsFails(int threshold)
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => Resolve(new GatekeeperOptions { ContainmentRetryThreshold = threshold }));

    [Fact]
    public void Resolve_UndefinedRefusalStyleFails()
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => Resolve(new GatekeeperOptions { RefusalStyle = (GatekeeperRefusalStyle)int.MaxValue }));

    [Fact]
    public void Resolve_StructuredModeRejectsCamouflagePool()
        => Assert.Throws<InvalidOperationException>(
            () => Resolve(new GatekeeperOptions
            {
                CamouflagedRefusalMessages = ["The operation could not be completed."],
            }));

    [Fact]
    public void Resolve_CamouflagedModeRequiresPool()
        => Assert.Throws<InvalidOperationException>(
            () => Resolve(new GatekeeperOptions { RefusalStyle = GatekeeperRefusalStyle.Camouflaged }));

    [Fact]
    public void Resolve_CamouflagePoolIsDefensivelyCopiedAndReadOnly()
    {
        var source = new List<string> { "The operation could not be completed." };
        var resolved = Resolve(new GatekeeperOptions
        {
            RefusalStyle = GatekeeperRefusalStyle.Camouflaged,
            CamouflagedRefusalMessages = source,
        });
        var frozen = Property<IReadOnlyList<string>>(resolved, "CamouflagedRefusalMessages");

        source[0] = "A later caller mutation.";

        Assert.Equal("The operation could not be completed.", frozen[0]);
        Assert.Throws<NotSupportedException>(
            () => ((IList<string>)frozen).Add("Another message."));
    }

    [Fact]
    public void Resolve_InvalidCamouflageMessagesFailWithoutEchoingConfiguredValue()
    {
        var invalidPools = new IReadOnlyList<string>[]
        {
            new string[] { null! },
            [string.Empty],
            [" "],
            [" leading whitespace"],
            ["trailing whitespace "],
            ["Cafe\u0301 could not be processed."],
            [new string('X', 513)],
            ["The operation\ncould not be completed."],
            ["The operation\u2028could not be completed."],
            ["The operation {code} could not be completed."],
            ["The security operation could not be completed."],
            ["The Gatekeeper operation could not be completed."],
            ["Resolve the reference with support."],
            ["The target is unavailable."],
            ["Try another attempt."],
            ["Use a bypass procedure."],
        };

        foreach (var pool in invalidPools)
        {
            var exception = Assert.Throws<InvalidOperationException>(
                () => Resolve(new GatekeeperOptions
                {
                    RefusalStyle = GatekeeperRefusalStyle.Camouflaged,
                    CamouflagedRefusalMessages = pool,
                }));

            var configured = pool[0];
            if (!string.IsNullOrWhiteSpace(configured))
            {
                Assert.DoesNotContain(configured, exception.ToString(), StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void Resolve_DuplicateCamouflageMessageFailsWithoutEchoingIt()
    {
        const string message = "The operation could not be completed.";
        var exception = Assert.Throws<InvalidOperationException>(
            () => Resolve(new GatekeeperOptions
            {
                RefusalStyle = GatekeeperRefusalStyle.Camouflaged,
                CamouflagedRefusalMessages = [message, message],
            }));

        Assert.DoesNotContain(message, exception.ToString(), StringComparison.Ordinal);
    }
    [Theory]
    [InlineData(GatekeeperRefusalStyle.Structured)]
    [InlineData(GatekeeperRefusalStyle.Camouflaged)]
    public void Resolve_ThrowingMessageCountFailsWithoutLeakingInnerException(GatekeeperRefusalStyle style)
    {
        const string secret = "SENSITIVE-COLLECTION-FAILURE";
        var exception = Assert.Throws<InvalidOperationException>(
            () => Resolve(new GatekeeperOptions
            {
                RefusalStyle = style,
                CamouflagedRefusalMessages = new ThrowingCountList(secret),
            }));

        Assert.DoesNotContain(secret, exception.ToString(), StringComparison.Ordinal);
    }


    [Fact]
    public void UseGatekeeper_InvalidResolvedDefaultsFailBeforeObserveBanner()
    {
        using var writer = new StringWriter();
        var agent = BuildAgent();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => agent.AsBuilder().UseGatekeeper(
                GatekeeperEnforcement.Observe,
                options =>
                {
                    options.ContainmentRetryThreshold = 0;
                    options.BannerWriter = writer;
                }));

        Assert.Equal(string.Empty, writer.ToString());
    }

    [Fact]
    public void UseGatekeeper_UnconfiguredPhase3DefaultsRemainBackwardCompatible()
    {
        var agent = BuildAgent();

        var builder = agent.AsBuilder().UseGatekeeper(GatekeeperEnforcement.Terminate, _ => { });

        Assert.NotNull(builder.Build());
    }

    private static object Resolve(GatekeeperOptions options)
    {
        var resolver = typeof(GatekeeperOptions).Assembly
            .GetType("AgentEval.MAF.Gatekeeper.GatekeeperOptionsResolver", throwOnError: true)!;
        var method = resolver.GetMethod("Resolve", BindingFlags.Static | BindingFlags.NonPublic)!;
        try
        {
            return method.Invoke(null, [options])!;
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }

    private static T Property<T>(object instance, string name)
        => (T)instance.GetType().GetProperty(name)!.GetValue(instance)!;

    private sealed class ThrowingCountList(string secret) : IReadOnlyList<string>
    {
        public int Count => throw new InvalidOperationException(secret);
        public string this[int index] => "unused";

        public IEnumerator<string> GetEnumerator() => Enumerable.Empty<string>().GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static ChatClientAgent BuildAgent()
    {
        var tool = AIFunctionFactory.Create((string value) => value, "echo");
        var scripted = new ScriptedChatClient().AddText("done");
        return new ChatClientAgent(
            scripted,
            new ChatClientAgentOptions
            {
                Name = "resolved-options-test",
                ChatOptions = new ChatOptions { Tools = [tool] },
            });
    }
}
