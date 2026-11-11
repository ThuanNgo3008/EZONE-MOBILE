using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using WebChatEIU.IntegrationTests.Auth;
using WebChatEIU.IntegrationTests.AuthWorkflows;
using WebChatEIU.Services;

namespace WebChatEIU.IntegrationTests.HttpSecurity;

internal sealed class HttpSecurityHost(AuthWorkflowTestData data) : IAsyncDisposable
{
    private readonly AuthEmulatorSettings settings = AuthEmulatorSettings.FromEnvironment();
    private readonly RSA signer = RSA.Create(2048);
    private WebApplication? app;
    private HttpClient? client;

    public Uri LoopbackAddress
    {
        get
        {
            settings.AssertEnvironmentUnchanged();
            var address = client?.BaseAddress;
            if (address is null || address.Scheme != "http" || address.Host != "127.0.0.1")
                throw new InvalidOperationException("Start the loopback Kestrel host first.");
            return address;
        }
    }

    public async Task StartAsync(bool useKestrel = false, bool strictExpiry = false)
    {
        settings.AssertEnvironmentUnchanged();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(Program).Assembly.GetName().Name,
            EnvironmentName = Environments.Production,
            Args = []
        });
        builder.Logging.ClearProviders();
        if (useKestrel)
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        else
            builder.WebHost.UseTestServer();

        builder.Services.AddSingleton(data.Database);
        builder.Services.AddSingleton(data.Auth);
        builder.Services.AddSingleton<IEmailService>(data.Email);
        Program.AddApiServices(builder.Services);
        builder.Services.Configure<JwtBearerOptions>("Firebase", options =>
        {
            var issuer = $"https://securetoken.google.com/{settings.ProjectId}";
            var configuration = new OpenIdConnectConfiguration { Issuer = issuer };
            configuration.SigningKeys.Add(new RsaSecurityKey(signer.ExportParameters(false)) { KeyId = "http-test-key" });
            options.Authority = null;
            options.MetadataAddress = string.Empty;
            options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
            options.TokenValidationParameters.ValidIssuer = issuer;
            options.TokenValidationParameters.ValidAudience = settings.ProjectId;
            if (strictExpiry) options.TokenValidationParameters.ClockSkew = TimeSpan.Zero;
        });

        app = builder.Build();
        Program.ConfigureApiPipeline(app);
        await app.StartAsync();
        client = useKestrel
            ? new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
                { BaseAddress = new Uri(app.Urls.Single()) }
            : app.GetTestClient();
        // In-memory requests use HTTPS so production HTTPS redirection does not intercept them.
        if (!useKestrel) client.BaseAddress = new Uri("https://localhost");
        client.Timeout = TimeSpan.FromSeconds(15);
    }

    public async Task<(string Uid, string EmulatorToken)> SignInAsync(bool verified = true)
    {
        settings.AssertEnvironmentUnchanged();
        var password = Guid.NewGuid().ToString("N");
        var uid = await data.AccountWithProfileAsync(verified: verified, password: password);
        var account = await data.Auth.GetUserAsync(uid);
        using var authClient = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
            { Timeout = TimeSpan.FromSeconds(10) };
        // Fixed path and fake key. The only variable URL component is the guarded loopback authority.
        var uri = new Uri($"http://{settings.AuthHost}/identitytoolkit.googleapis.com/v1/accounts:signInWithPassword?key=fake-api-key");
        using var response = await authClient.PostAsJsonAsync(uri, new { email = account.Email, password, returnSecureToken = true });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(uid, body.GetProperty("localId").GetString());
        var token = body.GetProperty("idToken").GetString()!;
        var decoded = new JwtSecurityTokenHandler().ReadJwtToken(token);
        Assert.Equal("none", decoded.Header.Alg);
        Assert.Equal($"https://securetoken.google.com/{settings.ProjectId}", decoded.Issuer);
        Assert.Equal(settings.ProjectId, Assert.Single(decoded.Audiences));
        Assert.Equal(uid, decoded.Payload["user_id"]);
        return (uid, token);
    }

    public string Sign(string emulatorToken, Action<JwtPayload>? modify = null)
    {
        var handler = new JwtSecurityTokenHandler();
        var payload = handler.ReadJwtToken(emulatorToken).Payload;
        modify?.Invoke(payload);
        var key = new RsaSecurityKey(signer) { KeyId = "http-test-key" };
        return handler.WriteToken(new JwtSecurityToken(
            new JwtHeader(new SigningCredentials(key, SecurityAlgorithms.RsaSha256)), payload));
    }

    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? token = null, object? body = null)
    {
        settings.AssertEnvironmentUnchanged();
        if (!path.StartsWith('/') || path.StartsWith("//") || path.Contains('\\'))
            throw new ArgumentException("Expected a local API path.", nameof(path));
        using var request = new HttpRequestMessage(method, path);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = JsonContent.Create(body);
        return await (client ?? throw new InvalidOperationException("Start the test host first.")).SendAsync(request);
    }

    public async ValueTask DisposeAsync()
    {
        client?.Dispose();
        try
        {
            if (app is not null)
            {
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    await app.StopAsync(timeout.Token);
                }
                finally
                {
                    await app.DisposeAsync();
                }
            }
        }
        finally
        {
            signer.Dispose();
        }
    }
}
