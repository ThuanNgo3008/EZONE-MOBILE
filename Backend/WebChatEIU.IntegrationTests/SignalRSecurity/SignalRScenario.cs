using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using WebChatEIU.IntegrationTests.AuthWorkflows;
using WebChatEIU.IntegrationTests.HttpSecurity;
using WebChatEIU.Services;

namespace WebChatEIU.IntegrationTests.SignalRSecurity;

internal sealed class SignalRScenario : IAsyncDisposable
{
    private readonly List<HubConnection> connections = [];
    private readonly List<string> roomIds = [];
    public AuthWorkflowTestData Data { get; } = new();
    public HttpSecurityHost Host { get; }

    public SignalRScenario() => Host = new HttpSecurityHost(Data);

    public Task StartAsync() => Host.StartAsync(useKestrel: true);

    public async Task<string> CreateRoomAsync(string first, string second)
    {
        var room = await new FirebaseChatRoomService(Data.Database)
            .CreateRoomAsync(first, second, "Fixture first", "Fixture second");
        roomIds.Add(room.RoomId);
        return room.RoomId;
    }

    public HubConnection CreateConnection(string? token, bool queryToken = false,
        Action<ClientWebSocket>? observeSocket = null)
    {
        var address = new Uri(Host.LoopbackAddress, "/firebaseChatHub");
        if (queryToken)
            address = new Uri(address + "?access_token=" + Uri.EscapeDataString(token!));

        var builder = new HubConnectionBuilder().WithUrl(address, options =>
        {
            options.Transports = HttpTransportType.WebSockets;
            options.SkipNegotiation = queryToken;
            options.AccessTokenProvider = () => Task.FromResult(queryToken ? null : token);
            options.HttpMessageHandlerFactory = handler =>
            {
                if (handler is HttpClientHandler http)
                {
                    http.UseProxy = false;
                    http.AllowAutoRedirect = false;
                }
                return handler;
            };
            if (observeSocket is not null)
            {
                options.WebSocketFactory = async (context, cancellation) =>
                {
                    if (context.Uri.Host != "127.0.0.1" || context.Uri.Port != address.Port ||
                        context.Uri.Scheme != "ws" || context.Uri.AbsolutePath != "/firebaseChatHub")
                        throw new InvalidOperationException("Unexpected WebSocket destination.");
                    var socket = new ClientWebSocket();
                    try
                    {
                        socket.Options.SetRequestHeader("Authorization", new AuthenticationHeaderValue("Bearer", token).ToString());
                        await socket.ConnectAsync(context.Uri, cancellation);
                        observeSocket(socket);
                        return socket;
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                };
            }
        });
        if (observeSocket is not null)
            builder.WithAutomaticReconnect([TimeSpan.Zero]);
        var connection = builder.Build();
        connections.Add(connection);
        return connection;
    }

    public async Task<SignalRPeer> ConnectAsync(string token)
    {
        var peer = new SignalRPeer(CreateConnection(token));
        await StartConnectionAsync(peer.Connection);
        return peer;
    }

    public static async Task StartConnectionAsync(HubConnection connection)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await connection.StartAsync(timeout.Token);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            foreach (var connection in connections)
                await connection.DisposeAsync();
        }
        finally
        {
            try
            {
                await Host.DisposeAsync();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                foreach (var id in roomIds)
                {
                    var room = Data.Database.Collection("chatRooms").Document(id);
                    var messages = await room.Collection("messages").GetSnapshotAsync(timeout.Token);
                    var history = await Data.Database.Collection("matchHistory").WhereEqualTo("RoomId", id)
                        .GetSnapshotAsync(timeout.Token);
                    var reports = await Data.Database.Collection("reports").WhereEqualTo("RoomId", id)
                        .GetSnapshotAsync(timeout.Token);
                    var batch = Data.Database.StartBatch();
                    foreach (var report in reports.Documents)
                    {
                        var evidence = await report.Reference.Collection("evidence").GetSnapshotAsync(timeout.Token);
                        foreach (var entry in evidence.Documents)
                            batch.Delete(entry.Reference);
                    }
                    foreach (var entry in messages.Documents.Concat(history.Documents).Concat(reports.Documents))
                        batch.Delete(entry.Reference);
                    batch.Delete(room);
                    await batch.CommitAsync(timeout.Token);
                }
            }
            finally
            {
                await Data.DisposeAsync();
            }
        }
    }
}

internal sealed class SignalRPeer
{
    private readonly Channel<string> joined = Channel.CreateUnbounded<string>();
    private readonly Channel<RoomMessage> received = Channel.CreateUnbounded<RoomMessage>();
    private readonly TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public HubConnection Connection { get; }

    public SignalRPeer(HubConnection connection)
    {
        Connection = connection;
        connection.On<string>("JoinedRoom", room => joined.Writer.TryWrite(room));
        connection.On<RoomMessage>("ReceiveMessage", message => received.Writer.TryWrite(message));
        connection.Closed += _ =>
        {
            closed.TrySetResult();
            return Task.CompletedTask;
        };
    }

    public async Task JoinAsync(string room)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Connection.InvokeAsync("JoinRoom", room, timeout.Token);
        Assert.Equal(room, await joined.Reader.ReadAsync(timeout.Token));
    }

    public async Task SendAsync(string room, string content)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Connection.InvokeAsync("SendMessage", room, content, timeout.Token);
    }

    public async Task<RoomMessage> ReceiveAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        return await received.Reader.ReadAsync(timeout.Token);
    }

    public async Task AssertNoQueuedMessageAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        // Ping's reply follows any previously enqueued sends on this connection.
        Assert.Equal("pong", await Connection.InvokeAsync<string>("Ping", timeout.Token));
        Assert.False(received.Reader.TryRead(out _));
    }

    public async Task AssertClosedWithoutMessagesAsync(TimeSpan? timeout = null)
    {
        await closed.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(12));
        Assert.Equal(HubConnectionState.Disconnected, Connection.State);
        Assert.False(received.Reader.TryRead(out _));
    }
}

internal sealed record RoomMessage(string MessageId, string SenderRole, string Content, DateTime CreatedAt);
