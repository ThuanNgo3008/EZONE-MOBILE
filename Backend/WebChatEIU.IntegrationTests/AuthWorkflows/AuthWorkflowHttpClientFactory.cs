using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Google.Apis.Http;
using WebChatEIU.IntegrationTests.Auth;

namespace WebChatEIU.IntegrationTests.AuthWorkflows;

// Observe only locally created UIDs for cleanup and deterministic profile-conflict injection.
internal sealed class AuthWorkflowHttpClientFactory(AuthEmulatorSettings settings)
    : Google.Apis.Http.HttpClientFactory
{
    public ConcurrentDictionary<string, byte> OwnedAccounts { get; } = new();
    public Func<string, Task>? AfterAccountCreated { get; set; }

    protected override HttpMessageHandler CreateHandler(CreateHttpClientArgs args) =>
        new AuthEmulatorRequestHandler(settings, new ConcurrentQueue<Uri>(),
            new CreationObserver(this, new HttpClientHandler
            {
                AllowAutoRedirect = false, UseProxy = false,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            }), revalidateEnvironment: true);

    private sealed class CreationObserver(AuthWorkflowHttpClientFactory owner, HttpMessageHandler transport)
        : DelegatingHandler(transport)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode && request.Method == HttpMethod.Post &&
                request.RequestUri!.AbsolutePath.EndsWith("/accounts", StringComparison.Ordinal))
            {
                // Reading buffers content; the real Admin SDK still processes the same response.
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                var uid = json.RootElement.GetProperty("localId").GetString()
                    ?? throw new InvalidOperationException("Emulator response has no UID.");
                owner.OwnedAccounts.TryAdd(uid, 0);
                if (owner.AfterAccountCreated is { } callback) await callback(uid);
            }
            return response;
        }
    }
}
