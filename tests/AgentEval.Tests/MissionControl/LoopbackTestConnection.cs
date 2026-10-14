// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

#if NET10_0_OR_GREATER
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace AgentEval.Tests.MissionControl;

internal sealed class LoopbackTestConnection : IStartupFilter
{
    internal static void Configure(IWebHostBuilder builder) =>
        builder.ConfigureServices(services => services.AddSingleton<IStartupFilter, LoopbackTestConnection>());

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        // TestServer has no socket and therefore supplies no peer address. Model an actual local connection explicitly.
        app.Use(async (context, continuation) =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Loopback;
            await continuation(context);
        });
        next(app);
    };
}
#endif
