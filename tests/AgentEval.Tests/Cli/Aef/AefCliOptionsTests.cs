// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.CommandLine;
using System.Security.Cryptography;
using AgentEval.Cli.Commands;
using Xunit;

namespace AgentEval.Tests.Cli.Aef;

[Collection("ConsoleTests")]
public sealed class AefCliOptionsTests
{
    private static (Command Command, AefCliOptions Controls) Create()
    {
        var command = new Command("native");
        command.Options.Add(new Option<DirectoryInfo?>("--aef"));
        command.Options.Add(new Option<bool>("--no-aef"));
        return (command, new AefCliOptions(command));
    }

    [Theory]
    [InlineData("--no-aef --aef-unsealed")]
    [InlineData("--no-aef --aef-key missing.pem")]
    [InlineData("--aef-unsealed --aef-key missing.pem")]
    [InlineData("--aef-key missing.pem")]
    public async Task Invoke_InvalidControls_RejectsBeforeInvokingCommand(string args)
    {
        var (command, controls) = Create();
        var invoked = false;
        var exit = await controls.InvokeAsync(command.Parse(args), () => { invoked = true; return Task.FromResult(0); });
        Assert.Equal(2, exit);
        Assert.False(invoked);
        Assert.True(AefCliOptions.Seal);
        Assert.Null(AefCliOptions.Signer);
    }

    [Fact]
    public async Task Invoke_UnsealedScope_RestoresDefaultAfterException()
    {
        var (command, controls) = Create();
        await Assert.ThrowsAsync<InvalidOperationException>(() => controls.InvokeAsync(command.Parse("--aef-unsealed"), async () =>
        {
            await Task.Yield();
            Assert.False(AefCliOptions.Seal);
            throw new InvalidOperationException("test");
        }));
        Assert.True(AefCliOptions.Seal);
        Assert.Null(AefCliOptions.Signer);
    }

    [Fact]
    public async Task Invoke_SigningKey_OnlyExposesSignerDuringInvocation()
    {
        var (command, controls) = Create();
        var keyFile = Path.GetTempFileName();
        try
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            File.WriteAllText(keyFile, key.ExportPkcs8PrivateKeyPem());
            var exit = await controls.InvokeAsync(command.Parse(["--aef-key", keyFile]), async () =>
            {
                await Task.Yield();
                Assert.True(AefCliOptions.Seal);
                Assert.NotNull(AefCliOptions.Signer);
                Assert.NotEmpty(AefCliOptions.Signer.Sign("message"u8));
                return 17;
            });
            Assert.Equal(17, exit);
            Assert.Null(AefCliOptions.Signer);
        }
        finally { File.Delete(keyFile); }
    }
}
