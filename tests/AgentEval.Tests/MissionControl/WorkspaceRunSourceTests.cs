// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

#if NET10_0_OR_GREATER
using System.Reflection;
using System.Runtime.CompilerServices;
using AgentEval.MissionControl.Services;
using AgentEval.Output;
using AgentEval.Results.Adapters.Reading;

namespace AgentEval.Tests.MissionControl;

public sealed class WorkspaceRunSourceTests
{
    [Theory]
    [InlineData(32_768, 2, 1, 1)]
    [InlineData(100, 1000, 2, 100)]
    public async Task Read_OversizedSemanticInput_StopsBeforeRequestingRemainingScenarios(int inputLength, int count, int minRead, int maxRead)
    {
        var store = new InMemoryOutputStore();
        var manifest = await store.StartRunAsync(new(SubjectKind.Agent, "Bounded"), new("test", ".", "offline", null, null, "eval"));
        foreach (var index in Enumerable.Range(0, count))
            await store.WriteScenarioResultAsync(manifest.Run.RunId, new("case" + index, "case", new string('x', inputLength), "ok", true, 1,
                new Dictionary<string, double>(), [], TimeSpan.Zero, 0));
        var proxy = DispatchProxy.Create<IOutputStoreReader, StoreProbe>();
        var probe = (StoreProbe)proxy;
        probe.Inner = store;
        var source = new WorkspaceRunSource(proxy, new AefWorkspaceEvidenceReader(), maxBytes: 8192);

        await Assert.ThrowsAsync<InvalidDataException>(() => source.ReadAsync(default));

        Assert.InRange(probe.ScenariosRead, minRead, maxRead);
    }

    public class StoreProbe : DispatchProxy
    {
        public required IOutputStoreReader Inner { get; set; }
        public int ScenariosRead { get; private set; }
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name == nameof(IOutputStoreReader.GetScenarioResultsAsync)
            ? Scenarios((string)args![0]!, (CancellationToken)args[1]!) : method.Invoke(Inner, args);
        private async IAsyncEnumerable<ScenarioResult> Scenarios(string id, [EnumeratorCancellation] CancellationToken ct)
        {
            await foreach (var scenario in Inner.GetScenarioResultsAsync(id, ct))
            {
                ScenariosRead++;
                yield return scenario;
            }
        }
    }
}
#endif
