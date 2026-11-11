using System.Collections.Concurrent;
using System.Threading.Channels;
using FirebaseAdmin.Auth;
using Google.Cloud.Firestore;
using Google.Cloud.Firestore.V1;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Grpc.Net.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WebChatEIU.Services;

namespace WebChatEIU.IntegrationTests.Workers;

// Runs only the production cleanup worker. The channel belongs to this test host.
internal sealed class HostedCleanupWorker : IAsyncDisposable
{
    private readonly GrpcChannel channel;
    private readonly IHost host;
    private readonly FirebaseRoomExpirationWorker worker;
    private readonly Channel<string> completedStages = Channel.CreateUnbounded<string>();
    private int scopesOpened;
    private int scopesDisposed;

    public ConcurrentQueue<Exception> Errors { get; } = new();

    public HostedCleanupWorker(FirebaseAuth auth, Func<string, CancellationToken, Task>? afterScan = null)
    {
        // The interceptor needs an owned channel. Resolve only the guarded literal
        // loopback address and use no ADC, proxy, redirect or production endpoint.
        var settings = FirestoreEmulatorSettings.FromEnvironment();
        var http = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false });
        // The emulator recognizes this fixed value as its local Admin marker.
        http.DefaultRequestHeaders.Authorization = new("Bearer", "owner");
        channel = GrpcChannel.ForAddress($"http://{settings.Host}", new GrpcChannelOptions
        {
            HttpClient = http,
            DisposeHttpClient = true
        });
        var invoker = channel.CreateCallInvoker();
        if (afterScan is not null)
            invoker = invoker.Intercept(new ScanInterceptor(afterScan));
        var client = new FirestoreClientBuilder { CallInvoker = invoker }.Build();
        var database = FirestoreDb.Create(settings.ProjectId, client);

        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<ILogger<FirebaseRoomExpirationWorker>>(new WorkerLogger(Errors));
        builder.Services.AddSingleton(database);
        builder.Services.AddSingleton(auth);
        builder.Services.AddScoped<FirebaseMatchEligibilityService>();
        builder.Services.AddScoped<StageScope>();
        builder.Services.AddScoped(services => CreateStage(services, "reservations", () =>
            ActivatorUtilities.CreateInstance<FirebaseMatchReservationService>(services)));
        builder.Services.AddScoped(services => CreateStage(services, "waiting", () =>
            ActivatorUtilities.CreateInstance<FirebaseRandomMatchQueueService>(services)));
        builder.Services.AddScoped(services => CreateStage(services, "messages", () =>
            ActivatorUtilities.CreateInstance<FirebaseTemporaryMessageCleanupService>(services)));
        builder.Services.AddHostedService<FirebaseRoomExpirationWorker>();
        host = builder.Build();
        worker = Assert.IsType<FirebaseRoomExpirationWorker>(Assert.Single(host.Services.GetServices<IHostedService>()));
    }

    private T CreateStage<T>(IServiceProvider services, string stage, Func<T> create)
    {
        var scope = services.GetRequiredService<StageScope>();
        scope.OnDispose = () =>
        {
            Interlocked.Increment(ref scopesDisposed);
            completedStages.Writer.TryWrite(stage);
        };
        Interlocked.Increment(ref scopesOpened);
        return create();
    }

    public async Task StartAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await host.StartAsync(timeout.Token);
    }

    public async Task ExpectStagesAsync(params string[] expected)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        foreach (var stage in expected)
            Assert.Equal(stage, await completedStages.Reader.ReadAsync(timeout.Token));
    }

    public async Task StopAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await host.StopAsync(timeout.Token);
        Assert.True(worker.ExecuteTask?.IsCompletedSuccessfully);
        Assert.Equal(Volatile.Read(ref scopesOpened), Volatile.Read(ref scopesDisposed));
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (worker.ExecuteTask is { IsCompleted: false })
                await StopAsync();
        }
        finally
        {
            try { host.Dispose(); }
            finally { channel.Dispose(); }
        }
    }

    private sealed class StageScope : IDisposable
    {
        public Action? OnDispose { get; set; }
        public void Dispose() => OnDispose?.Invoke();
    }

    private sealed class WorkerLogger(ConcurrentQueue<Exception> errors) : ILogger<FirebaseRoomExpirationWorker>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel) && exception is not null)
                errors.Enqueue(exception);
        }
    }

    private sealed class ScanInterceptor(Func<string, CancellationToken, Task> afterScan) : Interceptor
    {
        public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(
            TRequest request, ClientInterceptorContext<TRequest, TResponse> context,
            AsyncServerStreamingCallContinuation<TRequest, TResponse> continuation)
        {
            var call = continuation(request, context);
            if (request is not RunQueryRequest query || !query.Parent.EndsWith("/documents", StringComparison.Ordinal))
                return call;

            var collection = query.StructuredQuery.From.Single().CollectionId;
            return new AsyncServerStreamingCall<TResponse>(
                new ScanReader<TResponse>(call.ResponseStream, () => afterScan(collection, context.Options.CancellationToken)),
                call.ResponseHeadersAsync, call.GetStatus, call.GetTrailers, call.Dispose);
        }
    }

    // The real scan has completed, but the worker has not acted on its results.
    private sealed class ScanReader<T>(IAsyncStreamReader<T> source, Func<Task> afterScan) : IAsyncStreamReader<T>
    {
        public T Current => source.Current;
        public async Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            if (await source.MoveNext(cancellationToken))
                return true;
            await afterScan();
            return false;
        }
    }
}
