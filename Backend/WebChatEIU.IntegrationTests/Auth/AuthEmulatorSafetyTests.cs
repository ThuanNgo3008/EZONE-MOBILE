using System.Collections.Concurrent;
using System.Net;

namespace WebChatEIU.IntegrationTests.Auth;

[Trait("Category", "AuthSafety")]
public sealed class AuthEmulatorSafetyTests
{
    private static AuthEmulatorSettings ValidSettings() =>
        AuthEmulatorSettings.Validate("demo-ezone-regression", "127.0.0.1:8085", "127.0.0.1:9099");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ezone-afa80")]
    [InlineData("demo-other")]
    [InlineData("DEMO-EZONE-REGRESSION")]
    public void MissingOrUnapprovedProject_IsRejected(string? project) =>
        Assert.Throws<InvalidOperationException>(() =>
            AuthEmulatorSettings.Validate(project, "127.0.0.1:8085", "127.0.0.1:9099"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("localhost:9099")]
    [InlineData("0.0.0.0:9099")]
    [InlineData("192.168.1.10:9099")]
    [InlineData("identitytoolkit.googleapis.com:443")]
    [InlineData("[::]:9099")]
    [InlineData("http://127.0.0.1:9099")]
    [InlineData("127.0.0.1")]
    [InlineData("127.0.0.1:0")]
    [InlineData("127.0.0.1:65536")]
    [InlineData("127.0.0.1:9099/path")]
    [InlineData("127.0.0.1:9099?target=remote")]
    [InlineData("user@127.0.0.1:9099")]
    [InlineData("127.0.0.1:9099\n")]
    public void MissingOrUnsafeAuthEndpoint_IsRejected(string? host) =>
        Assert.Throws<InvalidOperationException>(() =>
            AuthEmulatorSettings.Validate("demo-ezone-regression", "127.0.0.1:8085", host));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0.0.0.0:8085")]
    [InlineData("firestore.googleapis.com:443")]
    public void MissingOrUnsafeFirestoreEndpoint_IsAlsoRejected(string? host) =>
        Assert.Throws<InvalidOperationException>(() =>
            AuthEmulatorSettings.Validate("demo-ezone-regression", host, "127.0.0.1:9099"));

    [Theory]
    [InlineData("C:/example/service-account.json")]
    [InlineData("/example/credentials.json")]
    [InlineData(" ")]
    public void InheritedCredentialPath_IsRejected(string path) =>
        Assert.Throws<InvalidOperationException>(() =>
            AuthEmulatorSettings.Validate("demo-ezone-regression", "127.0.0.1:8085", "127.0.0.1:9099", path));

    [Theory]
    [InlineData("127.0.0.1:9099")]
    [InlineData("[::1]:9099")]
    public void ExplicitLoopbackAuthEndpoint_IsAccepted(string host)
    {
        var settings = AuthEmulatorSettings.Validate("demo-ezone-regression", "127.0.0.1:8085", host);
        Assert.Equal("demo-ezone-regression", settings.ProjectId);
        Assert.Equal("127.0.0.1:8085", settings.FirestoreHost);
        Assert.Equal(host, settings.AuthHost);
    }

    [Theory]
    [InlineData("https://identitytoolkit.googleapis.com/v1/projects/ezone-afa80/accounts")]
    [InlineData("http://192.168.1.10:9099/identitytoolkit.googleapis.com/v1/projects/demo-ezone-regression/accounts")]
    [InlineData("http://127.0.0.1:9999/identitytoolkit.googleapis.com/v1/projects/demo-ezone-regression/accounts")]
    [InlineData("https://127.0.0.1:9099/identitytoolkit.googleapis.com/v1/projects/demo-ezone-regression/accounts")]
    [InlineData("http://127.0.0.1:9099/identitytoolkit.googleapis.com/v1/projects/ezone-afa80/accounts")]
    [InlineData("http://127.0.0.1:9099/identitytoolkit.googleapis.com/v1/projects/demo-ezone-regression-other/accounts")]
    [InlineData("http://127.0.0.1:9099/other-api")]
    [InlineData("http://user@127.0.0.1:9099/identitytoolkit.googleapis.com/v1/projects/demo-ezone-regression/accounts")]
    public async Task UnexpectedSdkDestination_IsBlockedBeforeTransport(string destination)
    {
        var terminal = new RecordingTransport();
        var recorded = new ConcurrentQueue<Uri>();
        using var handler = new AuthEmulatorRequestHandler(ValidSettings(), recorded, terminal, revalidateEnvironment: false);
        using var client = new HttpMessageInvoker(handler);
        using var request = new HttpRequestMessage(HttpMethod.Post, destination);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendAsync(request, CancellationToken.None));
        Assert.Equal(0, terminal.Calls);
        Assert.Empty(recorded);
    }

    [Fact]
    public async Task ApprovedSdkDestination_ReachesOnlyInjectedFakeTransport()
    {
        var terminal = new RecordingTransport();
        var recorded = new ConcurrentQueue<Uri>();
        using var handler = new AuthEmulatorRequestHandler(ValidSettings(), recorded, terminal, revalidateEnvironment: false);
        using var client = new HttpMessageInvoker(handler);
        using var request = new HttpRequestMessage(HttpMethod.Post,
            "http://127.0.0.1:9099/identitytoolkit.googleapis.com/v1/projects/demo-ezone-regression/accounts");
        using var response = await client.SendAsync(request, CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, terminal.Calls);
        Assert.Equal(request.RequestUri, Assert.Single(recorded));
    }

    // No sockets, Firebase app, environment mutation, or external service in safety cases.
    private sealed class RecordingTransport : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
