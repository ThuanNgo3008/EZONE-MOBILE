using System.Collections.Concurrent;
using System.Net;
using Google.Apis.Http;

namespace WebChatEIU.IntegrationTests.Auth;

// Defense against SDK routing fallback. No requests can leave the selected emulator.
internal sealed class AuthEmulatorHttpClientFactory(AuthEmulatorSettings settings)
    : Google.Apis.Http.HttpClientFactory
{
    public ConcurrentQueue<Uri> Requests { get; } = new();

    protected override HttpMessageHandler CreateHandler(CreateHttpClientArgs args) =>
        new AuthEmulatorRequestHandler(settings, Requests,
            new HttpClientHandler
            {
                AllowAutoRedirect = false,
                UseProxy = false,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            }, revalidateEnvironment: true);
}

internal sealed class AuthEmulatorRequestHandler(
    AuthEmulatorSettings settings, ConcurrentQueue<Uri> requests,
    HttpMessageHandler innerHandler, bool revalidateEnvironment) : DelegatingHandler(innerHandler)
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (revalidateEnvironment) settings.AssertEnvironmentUnchanged();
        settings.AssertRequestAllowed(request.RequestUri);
        requests.Enqueue(request.RequestUri!); // URI only: never record tokens, accounts or request bodies.
        return base.SendAsync(request, cancellationToken);
    }
}
