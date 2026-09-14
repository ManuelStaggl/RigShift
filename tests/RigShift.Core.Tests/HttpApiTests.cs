using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using NSubstitute;
using RigShift.Core.Api;
using RigShift.Core.Cli;
using RigShift.Core.Profiles;
using RigShift.Core.Tests.Fakes;
using RigShift.Core.Topology;
using Shouldly;
using Xunit;
using static RigShift.Core.Tests.TestDisplays;

#pragma warning disable xUnit1051 // NSubstitute matchers stand in for cancellation tokens.

namespace RigShift.Core.Tests;

public sealed class HttpApiTests
{
    private const string Token = "secret-token";

    private readonly InMemoryProfileStore _store = new();
    private readonly IProfileSwitcher _switcher = Substitute.For<IProfileSwitcher>();
    private readonly Profile _desk = Profile("Desk", DeskModes);
    private readonly Profile _rig = Rig();

    public HttpApiTests()
    {
        _store.Profiles.AddRange([_desk, _rig]);
        _switcher.SwitchAsync(Arg.Any<Profile>(), Arg.Any<SwitchRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => new SwitchResult
            {
                Outcome = SwitchOutcome.Applied,
                Plan = new TopologyPlan { Profile = call.Arg<Profile>(), Resolved = [], Missing = [], Warnings = [] },
                Attempts = 1,
                Duration = TimeSpan.FromSeconds(2.34),
            });
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ReadRequest_ParsesMethodPathQueryAndHeaders()
    {
        ApiRequest? request = await Read("post /api/profiles/My%20Rig/apply?dryRun=true&noConfirm HTTP/1.1\r\nHost: 127.0.0.1:47800\r\nauthorization: Bearer x\r\nContent-Length: 2\r\n\r\n{}");

        request.ShouldNotBeNull();
        request.Method.ShouldBe("POST");
        request.Path.ShouldBe("/api/profiles/My%20Rig/apply");
        request.Flag("dryRun").ShouldBeTrue();
        request.Flag("noConfirm").ShouldBeTrue();
        request.Flag("other").ShouldBeFalse();
        request.Header("Authorization").ShouldBe("Bearer x");
    }

    [Fact]
    public async Task ReadRequest_EmptyConnection_ReturnsNull() => (await Read(string.Empty)).ShouldBeNull();

    [Theory]
    [InlineData("GET\r\n\r\n")]
    [InlineData("GET /api/status HTTP/1.1\r\nbroken header\r\n\r\n")]
    [InlineData("GET /api/status HTTP/1.1\r\nContent-Length: 999999\r\n\r\n")]
    public async Task ReadRequest_Malformed_Throws(string raw) =>
        await Should.ThrowAsync<InvalidDataException>(() => Read(raw));

    [Fact]
    public async Task ReadRequest_OversizedHeader_Throws() =>
        await Should.ThrowAsync<InvalidDataException>(() => Read("GET / HTTP/1.1\r\nX: " + new string('a', HttpMessages.MaxHeaderBytes)));

    [Theory]
    [InlineData(null)]
    [InlineData("Bearer wrong")]
    [InlineData("Basic secret-token")]
    public async Task Handle_WithoutValidToken_Returns401(string? authorization)
    {
        ApiResponse response = await Handler().HandleAsync(Get("/api/profiles", authorization), Ct);

        response.StatusCode.ShouldBe(401);
        await _switcher.DidNotReceiveWithAnyArgs().SwitchAsync(default!, default!, default);
    }

    [Fact]
    public async Task Handle_NoTokenConfigured_RejectsEvenAnEmptyBearer()
    {
        var handler = new ApiHandler(_store, new FakeDisplayConfigurator(DeskActive()), Matcher(), _switcher, () => null, Serilog.Core.Logger.None);

        (await handler.HandleAsync(Get("/api/profiles", "Bearer "), Ct)).StatusCode.ShouldBe(401);
    }

    [Fact]
    public async Task Handle_ForeignHost_Returns403()
    {
        ApiRequest request = Get("/api/profiles") with
        {
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Host"] = "evil.example:47800", ["Authorization"] = "Bearer " + Token },
        };

        (await Handler().HandleAsync(request, Ct)).StatusCode.ShouldBe(403);
    }

    [Fact]
    public async Task Profiles_MarksTheActiveOne()
    {
        ApiResponse response = await Handler().HandleAsync(Get("/api/profiles"), Ct);

        response.StatusCode.ShouldBe(200);
        using JsonDocument json = JsonDocument.Parse(response.Body);
        json.RootElement.GetArrayLength().ShouldBe(2);
        json.RootElement[0].GetProperty("name").GetString().ShouldBe("Desk");
        json.RootElement[0].GetProperty("isActive").GetBoolean().ShouldBeTrue();
        json.RootElement[1].GetProperty("isActive").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task Status_NamesActiveProfileAndDisplays()
    {
        ApiResponse response = await Handler().HandleAsync(Get("/api/status"), Ct);

        using JsonDocument json = JsonDocument.Parse(response.Body);
        json.RootElement.GetProperty("activeProfile").GetProperty("id").GetGuid().ShouldBe(_desk.Id);
        JsonElement primary = json.RootElement.GetProperty("displays").EnumerateArray().Single(d => d.GetProperty("isPrimary").GetBoolean());
        primary.GetProperty("name").GetString().ShouldBe("Desk 4K");
        primary.GetProperty("refreshHz").GetDouble().ShouldBe(165);
    }

    [Fact]
    public async Task Apply_ByNameWithFlags_SwitchesAndReportsOutcome()
    {
        ApiResponse response = await Handler().HandleAsync(Post("/api/profiles/RIG/apply", "dryRun", "noConfirm"), Ct);

        response.StatusCode.ShouldBe(200);
        using JsonDocument json = JsonDocument.Parse(response.Body);
        json.RootElement.GetProperty("outcome").GetString().ShouldBe("Applied");
        json.RootElement.GetProperty("exitCode").GetInt32().ShouldBe(CliExitCodes.Applied);
        json.RootElement.GetProperty("durationSeconds").GetDouble().ShouldBe(2.3);
        await _switcher.Received(1).SwitchAsync(
            Arg.Is<Profile>(p => p.Id == _rig.Id),
            Arg.Is<SwitchRequest>(r => r.DryRun && r.SkipConfirmation),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Apply_ByEscapedNameOrId_FindsTheProfile()
    {
        _store.Profiles.Add(Profile("Sim Rig", DeskModes));

        (await Handler().HandleAsync(Post("/api/profiles/Sim%20Rig/apply"), Ct)).StatusCode.ShouldBe(200);
        (await Handler().HandleAsync(Post($"/api/profiles/{_rig.Id}/apply"), Ct)).StatusCode.ShouldBe(200);
        await _switcher.Received(1).SwitchAsync(Arg.Is<Profile>(p => p.Name == "Sim Rig"), Arg.Is<SwitchRequest>(r => !r.DryRun && !r.SkipConfirmation), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Apply_UnknownProfile_Returns404()
    {
        ApiResponse response = await Handler().HandleAsync(Post("/api/profiles/Couch/apply"), Ct);

        response.StatusCode.ShouldBe(404);
        response.Body.ShouldContain("'Couch' not found. Available: Desk, Rig");
    }

    [Fact]
    public async Task Apply_WhileBusy_Returns409()
    {
        _switcher.SwitchAsync(Arg.Any<Profile>(), Arg.Any<SwitchRequest>(), Arg.Any<CancellationToken>()).Returns((SwitchResult?)null);

        (await Handler().HandleAsync(Post("/api/profiles/Rig/apply"), Ct)).StatusCode.ShouldBe(409);
    }

    [Fact]
    public async Task Handle_WrongMethodOrPath_Returns405Or404()
    {
        (await Handler().HandleAsync(Get("/api/profiles/Rig/apply"), Ct)).StatusCode.ShouldBe(405);
        (await Handler().HandleAsync(Post("/api/status"), Ct)).StatusCode.ShouldBe(405);
        (await Handler().HandleAsync(Get("/api/unknown"), Ct)).StatusCode.ShouldBe(404);
    }

    [Fact]
    public void CreateToken_IsLongAndRandom()
    {
        string token = ApiHandler.CreateToken();

        token.Length.ShouldBe(64);
        token.ShouldNotBe(ApiHandler.CreateToken());
    }

    [Fact]
    public async Task Server_ServesRealHttpClients()
    {
        ApiHandler handler = Handler();
        await using var server = new HttpApiServer(0, handler.HandleAsync, Serilog.Core.Logger.None);
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.Port}/") };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);

        using HttpResponseMessage profiles = await client.GetAsync("api/profiles", Ct);
        using HttpResponseMessage apply = await client.PostAsync("api/profiles/Rig/apply?noConfirm=true", new StringContent("{\"ignored\":true}", Encoding.UTF8, "application/json"), Ct);
        client.DefaultRequestHeaders.Authorization = null;
        using HttpResponseMessage unauthorized = await client.GetAsync("api/status", Ct);

        profiles.StatusCode.ShouldBe(HttpStatusCode.OK);
        profiles.Content.Headers.ContentType?.MediaType.ShouldBe("application/json");
        (await profiles.Content.ReadAsStringAsync(Ct)).ShouldContain("\"Desk\"");
        apply.StatusCode.ShouldBe(HttpStatusCode.OK);
        unauthorized.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Server_MalformedRequest_Answers400()
    {
        await using var server = new HttpApiServer(0, Handler().HandleAsync, Serilog.Core.Logger.None);
        using var tcp = new System.Net.Sockets.TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, server.Port, Ct);
        await using System.Net.Sockets.NetworkStream stream = tcp.GetStream();

        await stream.WriteAsync("NONSENSE\r\n\r\n"u8.ToArray(), Ct);
        using var reader = new StreamReader(stream);
        string answer = await reader.ReadToEndAsync(Ct);

        answer.ShouldStartWith("HTTP/1.1 400 ");
    }

    private static async Task<ApiRequest?> Read(string raw)
    {
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes(raw));
        return await HttpMessages.ReadRequestAsync(stream, Ct);
    }

    private static ApiRequest Get(string path, string? authorization = "Bearer " + Token) => Request("GET", path, authorization);

    private static ApiRequest Post(string path, params string[] flags) =>
        Request("POST", path, "Bearer " + Token) with { Query = flags.ToDictionary(f => f, _ => "true", StringComparer.OrdinalIgnoreCase) };

    private static ApiRequest Request(string method, string path, string? authorization)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Host"] = "127.0.0.1:47800" };
        if (authorization is not null)
        {
            headers["Authorization"] = authorization;
        }

        return new ApiRequest(method, path, new Dictionary<string, string>(), headers);
    }

    private static ActiveProfileMatcher Matcher() => new(new TopologyPlanner(new TopologyPlannerOptions()));

    private ApiHandler Handler() =>
        new(_store, new FakeDisplayConfigurator(DeskActive()), Matcher(), _switcher, () => Token, Serilog.Core.Logger.None);
}
