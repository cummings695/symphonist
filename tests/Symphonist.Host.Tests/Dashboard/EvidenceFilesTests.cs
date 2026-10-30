using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Symphonist.Core;
using Symphonist.Core.Agents;
using Symphonist.Core.Integrations;
using Symphonist.Core.Storage;
using Symphonist.Host.Orchestration;
using Symphonist.Host.Tests.Fakes;
using Symphonist.Integrations;
using Symphonist.Integrations.Processes;

namespace Symphonist.Host.Tests.Dashboard;

[Collection(SymphonistHomeCollection.Name)]
public sealed class EvidenceFilesTests : IDisposable
{
    private const string Origin = "http://127.0.0.1:5199";
    private const string Stamp = "20261021T120000Z";

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private sealed class NoSecrets : ISecretStore
    {
        public string? Get(string name) => null;
        public void Set(string name, string value) => throw new NotSupportedException();
        public void Remove(string name) => throw new NotSupportedException();
    }

    private readonly TestHome home = new();
    private readonly WebApplicationFactory<Program> factory;
    private readonly HttpClient client;

    public EvidenceFilesTests()
    {
        home.WriteProject("orders.yaml", TestHome.ProjectYaml("orders"));
        var integrations = new FakeIntegrationFactory(
            new ProjectIntegrations(new FakeTracker(), new FakeSourceControl(), new FakeBuildWatcher()));
        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.AddSingleton<ITicketStore>(new FakeTicketStore());
            s.AddSingleton<IIntegrationFactory>(integrations);
            s.AddSingleton<IGitWorkspace>(new FakeGitWorkspace());
            s.AddSingleton<IAgentRunner>(new FakeAgentRunner());
            s.AddSingleton<IProcessRunner>(new FakeProcessRunner());
            s.AddSingleton<ISecretStore>(new NoSecrets());
            s.AddSingleton(new OrchestratorOptions(TimeSpan.FromMinutes(2), 1, TickAfterCommands: false));
            s.Remove(s.Single(d => d.ImplementationType == typeof(OrchestratorService)));
        }));
        client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri(Origin), AllowAutoRedirect = false, HandleCookies = true,
        });
    }

    public void Dispose()
    {
        client.Dispose();
        factory.Dispose();
        home.Dispose();
    }

    private static string Write(string file, byte[]? bytes = null, string key = "ORD-1", string stamp = Stamp)
    {
        var path = SymphonistPaths.Resolve("evidence", "orders", key, stamp, file);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes ?? Png);
        return path;
    }

    private async Task SignInAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/dashboard/login-code");
        request.Headers.Add(ApiToken.Header, File.ReadAllText(SymphonistPaths.Resolve("api-token")).Trim());
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var url = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("url").GetString()!;
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync(url)).StatusCode);
    }

    [Theory]
    [InlineData("home.png", "image/png")]
    [InlineData("cart.JPG", "image/jpeg")]
    [InlineData("pay.jpeg", "image/jpeg")]
    [InlineData("done.webp", "image/webp")]
    public async Task Evidence_file_served_with_image_type_and_nosniff(string file, string type)
    {
        Write(file);
        await SignInAsync();

        var response = await client.GetAsync($"/evidence/orders/ORD-1/{Stamp}/{file}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(type, response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal(Png, await response.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData("/evidence/orders/ORD-1/" + Stamp + "/missing.png")]
    [InlineData("/evidence/orders/ORD-1/%2e%2e/home.png")]
    [InlineData("/evidence/orders/%2e%2e/" + Stamp + "/home.png")]
    [InlineData("/evidence/%2e%2e/ORD-1/" + Stamp + "/home.png")]
    [InlineData("/evidence/orders/ORD-1/" + Stamp + "/..")]
    [InlineData("/evidence/orders/ORD-1/" + Stamp + "/%2e%2e")]
    [InlineData("/evidence/orders/ORD-1/" + Stamp + "/..%5Csecret.png")]
    [InlineData("/evidence/orders/ORD-1/..%5C" + Stamp + "/home.png")]
    [InlineData("/evidence/orders/ORD-1/" + Stamp + "/x.svg")]
    [InlineData("/evidence/orders/ORD-1/" + Stamp + "/x.html")]
    [InlineData("/evidence/orders/ORD-1/" + Stamp + "/.hidden.png")]
    [InlineData("/evidence/orders/ORD-1/not-a-stamp/home.png")]
    [InlineData("/evidence/orders/ORD-1/" + Stamp + "/home.png:stream.png")]
    public async Task Evidence_file_rejects_traversal_and_unknown_types(string url)
    {
        Write("home.png");
        Write("x.svg", "<svg xmlns='http://www.w3.org/2000/svg'><script>alert(1)</script></svg>"u8.ToArray());
        Write("x.html", "<script>alert(1)</script>"u8.ToArray());
        Write(".hidden.png");
        Write("secret.png", key: "ORD-1", stamp: ".");
        await SignInAsync();

        var response = await client.GetAsync(url);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.NotEqual(Png, await response.Content.ReadAsByteArrayAsync());
    }
}
