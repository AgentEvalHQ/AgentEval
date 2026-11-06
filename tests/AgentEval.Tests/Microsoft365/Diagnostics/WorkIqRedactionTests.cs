// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Net.Http.Headers;
using System.Text;
using AgentEval.Microsoft365.Diagnostics;
using Xunit;

namespace AgentEval.Tests.Microsoft365.Diagnostics;

/// <summary>
/// Every redaction pattern of <see cref="WorkIqRedaction"/>, and the one thing that must NOT be redacted: the
/// device-code sign-in message the user has to read.
/// </summary>
public sealed class WorkIqRedactionTests
{
    private const string Jws = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIiwibmFtZSI6IkpvaG4ifQ.SflKxwRJSMeKKF2QT4fwpMeJf36POk6yJV_adQssw5c";
    private const string Jwe = "eyJhbGciOiJSU0EtT0FFUCIsImVuYyI6IkEyNTZHQ00ifQ.OKOawDo13gRp2ojaHV7LFpZcgV7T6DVZKTyKOMTYUmKoTCVJRgckCL9kiMT03JGe.48V1_ALb6US04U3b.5eym8TW_c8SuK0ltJ3rpYIzOeDQz7TALvtu6UG9oMo4vpzs9tX_EFShS8iB7j6jiSdiwkIr3ajwQzaBtQD_A.XFBoMYUZodetZdvTiFvSkQ";
    private const string DeviceCodeMessage = "To sign in, use a web browser to open the page https://microsoft.com/devicelogin and enter the code ABCD1234 to authenticate.";

    public static TheoryData<string, string> Patterns => new()
    {
        { "Authorization: Bearer abc.def-ghi_123", "Authorization: Bearer [redacted]" },
        { "bearer xyz123 was sent", "bearer [redacted] was sent" },
        { "a Bearer [redacted] stays", "a Bearer [redacted] stays" },
        { $"token {Jws} end", "token [jwt redacted] end" },
        { $"jwe {Jwe}", "jwe [jwt redacted]" },
        { "eyJhbGciOiJub25lIn0.eyJzdWIiOiJ4In0.", "[jwt redacted]" },
        { "https://login.example.invalid/callback?code=AbC123xyz&state=s1", "https://login.example.invalid/callback?code=[redacted]&state=s1" },
        { "grant_type=device_code&device_code=DAQABAAEAAAD&client_id=1", "grant_type=device_code&device_code=[redacted]&client_id=1" },
        { "client_secret=s3cr3t&access_token=tok&refresh_token=ref&id_token=idt", "client_secret=[redacted]&access_token=[redacted]&refresh_token=[redacted]&id_token=[redacted]" },
        { "error_code=42 http_code=401", "error_code=42 http_code=401" },
        { "code= nothing", "code= nothing" },
        { "Set-Cookie: session=abc; Path=/; HttpOnly", "Set-Cookie: [redacted]" }, // DevSkim: ignore DS610001 - test input for the Set-Cookie redaction, not a cookie this code sets
        { "Cookie: a=1; b=2", "Cookie: [redacted]" },
        { "HTTP/1.1 200 OK\r\nSet-Cookie: a=b\r\nrequest-id: 123", "HTTP/1.1 200 OK\r\nSet-Cookie: [redacted]\r\nrequest-id: 123" }, // DevSkim: ignore DS610001 - test input for the Set-Cookie redaction, not a cookie this code sets
        { "{\"access_token\":\"opaque.token.value\",\"refresh_token\": \"0.AXoA-refresh\",\"token_type\":\"Bearer\"}", "{\"access_token\":\"[redacted]\",\"refresh_token\": \"[redacted]\",\"token_type\":\"Bearer\"}" },
        { "{\"Authorization\":\"Bearer abc\",\"Cookie\":\"a=1\",\"A2A-Version\":\"1.0\"}", "{\"Authorization\":\"[redacted]\",\"Cookie\":\"[redacted]\",\"A2A-Version\":\"1.0\"}" },
        { "{\"error\":{\"code\":\"TooManyRequests\",\"message\":\"slow down\"}}", "{\"error\":{\"code\":\"TooManyRequests\",\"message\":\"slow down\"}}" },
        { "{\"error\":{\"code\":-32001}}", "{\"error\":{\"code\":-32001}}" },
        { DeviceCodeMessage, DeviceCodeMessage },
        { "", "" },
    };

    [Theory]
    [MemberData(nameof(Patterns))]
    public void Redact_Pattern_ProducesExpectedText(string input, string expected)
    {
        Assert.Equal(expected, WorkIqRedaction.Redact(input));
    }

    [Theory]
    [MemberData(nameof(Patterns))]
    public void Redact_Always_IsIdempotent(string input, string expected)
    {
        var once = WorkIqRedaction.Redact(input);

        Assert.Equal(expected, once);
        Assert.Equal(once, WorkIqRedaction.Redact(once));
    }

    [Fact]
    public void Redact_Null_ReturnsNull()
    {
        Assert.Null(WorkIqRedaction.Redact(null));
    }

    [Fact]
    public void Redact_DeviceCodeMessage_PassesThroughUnchanged()
    {
        // The user must see where to sign in and which code to enter; this is the one secret-looking thing kept.
        var redacted = WorkIqRedaction.Redact(DeviceCodeMessage);

        Assert.Same(DeviceCodeMessage, redacted);
        Assert.Contains("ABCD1234", redacted, StringComparison.Ordinal);
        Assert.Contains("https://microsoft.com/devicelogin", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Redact_BearerInsideJsonText_KeepsTheJsonValid()
    {
        var json = $"{{\"authorization\":\"Bearer {Jws}\",\"note\":\"x\"}}";

        var redacted = WorkIqRedaction.Redact(json);

        using var document = System.Text.Json.JsonDocument.Parse(redacted);
        Assert.Equal("[redacted]", document.RootElement.GetProperty("authorization").GetString());
        Assert.Equal("x", document.RootElement.GetProperty("note").GetString());
    }

    [Theory]
    [InlineData("Authorization", true)]
    [InlineData("authorization", true)]
    [InlineData(" Set-Cookie ", true)]
    [InlineData("cookie", true)]
    [InlineData("Proxy-Authorization", true)]
    [InlineData("request-id", false)]
    [InlineData("A2A-Version", false)]
    [InlineData("Content-Type", false)]
    public void IsSensitiveHeader_Name_ClassifiesCaseInsensitively(string name, bool expected)
    {
        Assert.Equal(expected, WorkIqRedaction.IsSensitiveHeader(name));
    }

    [Fact]
    public void RedactHeaders_Request_ReplacesSensitiveHeadersAndRedactsTheRest()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://workiq.svc.cloud.microsoft/a2a/");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "secret-token-value");
        request.Headers.TryAddWithoutValidation("Cookie", "a=1; b=2");
        request.Headers.TryAddWithoutValidation("X-Token", Jws);
        request.Headers.TryAddWithoutValidation("A2A-Version", "1.0");
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("Accept", "text/event-stream");
        request.Content = new StringContent("{}", Encoding.UTF8, "application/json");

        var headers = WorkIqRedaction.RedactHeaders(request);

        Assert.Equal("[redacted]", headers["Authorization"]);
        Assert.Equal("[redacted]", headers["cookie"]);
        Assert.Equal("[jwt redacted]", headers["X-Token"]);
        Assert.Equal("1.0", headers["A2A-Version"]);
        Assert.Equal("application/json, text/event-stream", headers["Accept"]);
        Assert.StartsWith("application/json", headers["Content-Type"], StringComparison.Ordinal);
        Assert.DoesNotContain(headers.Values, v => v.Contains("secret-token-value", StringComparison.Ordinal));
    }

    [Fact]
    public void RedactHeaders_Response_ReplacesSetCookieAndKeepsRequestId()
    {
        using var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        response.Headers.TryAddWithoutValidation("Set-Cookie", "session=abc; HttpOnly"); // DevSkim: ignore DS610001 - test input for the Set-Cookie redaction, not a cookie this code sets
        response.Headers.TryAddWithoutValidation("request-id", "4c2e9a7b");
        response.Headers.TryAddWithoutValidation("Retry-After", "7");

        var headers = WorkIqRedaction.RedactHeaders(response);

        Assert.Equal("[redacted]", headers["Set-Cookie"]);
        Assert.Equal("4c2e9a7b", headers["request-id"]);
        Assert.Equal("7", headers["Retry-After"]);
    }

    [Fact]
    public void RedactHeaders_PlainHeaders_AcceptsAnyHttpHeadersInstance()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://workiq.svc.cloud.microsoft/a2a/.well-known/agent-card.json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "abc");

        var headers = WorkIqRedaction.RedactHeaders(request.Headers);

        Assert.Single(headers);
        Assert.Equal("[redacted]", headers["Authorization"]);
    }
}
