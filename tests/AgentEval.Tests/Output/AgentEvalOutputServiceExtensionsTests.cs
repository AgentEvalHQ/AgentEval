// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Output;
using Microsoft.Extensions.DependencyInjection;

namespace AgentEval.Tests.Output;

public class AgentEvalOutputServiceExtensionsTests
{
    [Fact]
    public void AddAgentEvalOutputStore_NullMode_RegistersNullStore()
    {
        var provider = new ServiceCollection()
            .AddAgentEvalOutputStore(opts => opts.OutputStore = OutputStoreMode.Null)
            .BuildServiceProvider();

        var store = provider.GetRequiredService<IOutputStore>();

        Assert.IsType<NullOutputStore>(store);
    }

    [Fact]
    public void AddAgentEvalOutputStore_AutoMode_NoSolutionJson_RegistersNullStore()
    {
        using var temp = TempWorkspace.Create(solutionName: null);

        var provider = new ServiceCollection()
            .AddAgentEvalOutputStore(opts =>
            {
                opts.OutputStore = OutputStoreMode.Auto;
                opts.OutputStorePath = temp.Path;
            })
            .BuildServiceProvider();

        var store = provider.GetRequiredService<IOutputStore>();

        Assert.IsType<NullOutputStore>(store);
    }

    [Fact]
    public void AddAgentEvalOutputStore_AutoMode_WithSolutionJson_RegistersFileSystemStore()
    {
        using var temp = TempWorkspace.Create("MySolution");

        var provider = new ServiceCollection()
            .AddAgentEvalOutputStore(opts =>
            {
                opts.OutputStore = OutputStoreMode.Auto;
                opts.OutputStorePath = temp.Path;
            })
            .BuildServiceProvider();

        var store = provider.GetRequiredService<IOutputStore>();

        Assert.IsType<FileSystemOutputStore>(store);
    }
}
