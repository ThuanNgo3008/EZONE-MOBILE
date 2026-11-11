using System.Collections.Concurrent;
using System.Net;
using Google.Apis.Http;

namespace WebChatEIU.IntegrationTests.Auth;

// Defense against SDK routing fallback. No requests can leave the selected emulator.
internal sealed class AuthEmulatorHttpClientFactory(AuthEmulatorSettings settings)
    : Google.Apis.Http.HttpClientFactory
{
    public ConcurrentQueue<Uri> Requests { get; } = new();
    public bool RejectLookups { get; set; }

    protected override HttpMessageHandler CreateHandler(CreateHttpClientArgs args) =>
        new AuthEmulatorRequestHandler(settings, Requests,
            new LookupFailureHandler(this, new HttpClientHandler
            {
                AllowAutoRedirect = false,
                UseProxy = false,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            }), revalidateEnvironment: true);

    // Controlled SDK error response after destination validation, never an external call.
    private sealed class LookupFailureHandler(AuthEmulatorHttpClientFactory owner, HttpMessageHandler inner)
        : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (owner.RejectLookups && request.RequestUri!.AbsolutePath.EndsWith("/accounts:lookup", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    Content = new StringContent("{\"error\":{\"code\":403,\"message\":\"PERMISSION_DENIED\"}}")
                });
            return base.SendAsync(request, cancellationToken);
        }
    }
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
