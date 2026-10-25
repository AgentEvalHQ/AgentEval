// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

#if NET10_0_OR_GREATER
using System.Net.Http.Json;
using System.Text.Json;
using AgentEval.Output;
using AgentEval.Tests.Output;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace AgentEval.Tests.MissionControl;

[Collection("ConsoleTests")]
public sealed class ScenarioScoreGraphQLTests
{
    [Fact]
    public async Task Scenario_UnmeasuredScore_IsReturnedAsNull()
    {
        var store = new InMemoryOutputStore();
        store.Initialize("test");
        var manifest = await store.StartRunAsync(new(SubjectKind.Agent, "test"), new("test", ".", "offline", null, null, "eval"));
        await store.WriteScenarioResultAsync(manifest.Run.RunId, ScenarioScoreNullableTests.Scenario(ScenarioMeasurement.NotMeasured));
        using var factory = new SeededMissionControlFactory();
        using var customized = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IOutputStoreReader>();
            services.AddSingleton<IOutputStoreReader>(store);
        }));
        using var client = customized.CreateClient();
        var response = await client.PostAsJsonAsync("/graphql", new
        {
            query = $"{{ scenario(runId: \"{manifest.Run.RunId}\", scenarioId: \"case\") {{ score measurement }} }}",
        });
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(json.RootElement.TryGetProperty("errors", out _));
        var scenario = json.RootElement.GetProperty("data").GetProperty("scenario");
        Assert.Equal(ScenarioMeasurement.NotMeasured, scenario.GetProperty("measurement").GetString());
        Assert.Equal(JsonValueKind.Null, scenario.GetProperty("score").ValueKind);
    }
}
#endif
