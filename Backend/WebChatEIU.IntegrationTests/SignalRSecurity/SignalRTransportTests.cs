using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.WebSockets;
using FirebaseAdmin.Auth;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;

namespace WebChatEIU.IntegrationTests.SignalRSecurity;

[Collection("Auth workflows")]
[Trait("Category", "SignalRSecurity")]
public sealed class SignalRTransportTests
{
    [Theory]
    [InlineData("Anonymous", HttpStatusCode.Unauthorized)]
    [InlineData("Invalid", HttpStatusCode.Unauthorized)]
    [InlineData("Expired", HttpStatusCode.Unauthorized)]
    [InlineData("Disabled", HttpStatusCode.Unauthorized)]
    [InlineData("Deleted", HttpStatusCode.Unauthorized)]
    [InlineData("Revoked", HttpStatusCode.Unauthorized)]
    [InlineData("AuthUnavailable", HttpStatusCode.ServiceUnavailable)]
    public async Task NewConnectionUsesSharedBearerGuard(string state, HttpStatusCode expected)
    {
        await using var scenario = new SignalRScenario();
        await scenario.StartAsync();
        var (uid, unsigned) = await scenario.Host.SignInAsync();
        var validToken = scenario.Host.Sign(unsigned);
        var token = state switch
        {
            "Anonymous" => null,
            "Invalid" => "not-a-jwt",
            "Expired" => scenario.Host.Sign(unsigned, payload =>
            {
                payload["exp"] = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds();
                payload["iat"] = DateTimeOffset.UtcNow.AddHours(-2).ToUnixTimeSeconds();
            }),
            _ => validToken
        };

        if (state is "Disabled" or "Deleted" or "Revoked" or "AuthUnavailable")
            await ChangeAuthStateAsync(scenario, uid, state, unsigned);

        await AssertConnectionRejectedAsync(scenario, token, expected);
    }

    [Fact]
    public async Task QueryTokenAuthenticatesDirectWebSocketUpgradeOnlyOnHub()
    {
        await using var scenario = new SignalRScenario();
        await scenario.StartAsync();
        var (_, unsigned) = await scenario.Host.SignInAsync();
        var token = scenario.Host.Sign(unsigned);
        var connection = scenario.CreateConnection(token, queryToken: true);
        await SignalRScenario.StartConnectionAsync(connection);
        Assert.Equal("pong", await connection.InvokeAsync<string>("Ping").WaitAsync(TimeSpan.FromSeconds(10)));

        using var rest = await scenario.Host.SendAsync(HttpMethod.Get,
            "/api/firebase-auth-test/me?access_token=" + Uri.EscapeDataString(token));
        Assert.Equal(HttpStatusCode.Unauthorized, rest.StatusCode);
    }

    [Fact]
    public async Task NegotiatedWebSocketsEnforceMembershipAndKeepTwoRoomsIndependentAfterUnmatch()
    {
        await using var scenario = new SignalRScenario();
        await scenario.StartAsync();
        var (aId, aUnsigned) = await scenario.Host.SignInAsync();
        var (bId, bUnsigned) = await scenario.Host.SignInAsync();
        var (cId, cUnsigned) = await scenario.Host.SignInAsync();
        var aToken = scenario.Host.Sign(aUnsigned);
        var a = await scenario.ConnectAsync(aToken);
        var b = await scenario.ConnectAsync(scenario.Host.Sign(bUnsigned));
        var c = await scenario.ConnectAsync(scenario.Host.Sign(cUnsigned));
        var ab = await scenario.CreateRoomAsync(aId, bId);
        var ac = await scenario.CreateRoomAsync(aId, cId);
        await a.JoinAsync(ab);
        await a.JoinAsync(ab);
        await a.JoinAsync(ac);
        await b.JoinAsync(ab);
        await c.JoinAsync(ac);

        var denied = await Assert.ThrowsAsync<HubException>(() => c.JoinAsync(ab));
        Assert.Contains("Access denied.", denied.Message);
        denied = await Assert.ThrowsAsync<HubException>(() => c.SendAsync(ab, "Forbidden content"));
        Assert.Contains("Access denied.", denied.Message);
        Assert.Empty((await scenario.Data.Database.Collection("chatRooms").Document(ab)
            .Collection("messages").GetSnapshotAsync()).Documents);

        await a.SendAsync(ab, "Hello in the first room");
        var first = await b.ReceiveAsync();
        Assert.Equal("Hello in the first room", first.Content);
        Assert.Equal("User1", first.SenderRole);
        Assert.Equal(first, await a.ReceiveAsync());
        await a.AssertNoQueuedMessageAsync();
        Assert.True((await scenario.Data.Database.Collection("chatRooms").Document(ab)
            .Collection("messages").Document(first.MessageId).GetSnapshotAsync()).Exists);
        await c.AssertNoQueuedMessageAsync();

        using var unmatch = await scenario.Host.SendAsync(HttpMethod.Post, $"/api/firebase-chat-rooms/{ab}/unmatch", aToken);
        Assert.Equal(HttpStatusCode.OK, unmatch.StatusCode);
        denied = await Assert.ThrowsAsync<HubException>(() => b.SendAsync(ab, "Closed room content"));
        Assert.Contains("Room is unavailable.", denied.Message);
        denied = await Assert.ThrowsAsync<HubException>(() => b.JoinAsync(ab));
        Assert.Contains("Room is unavailable.", denied.Message);

        await c.SendAsync(ac, "Second room stays open");
        var second = await a.ReceiveAsync();
        Assert.Equal("Second room stays open", second.Content);
        Assert.Equal("User2", second.SenderRole);
        Assert.Equal(second, await c.ReceiveAsync());
        await b.AssertNoQueuedMessageAsync();
        await a.Connection.InvokeAsync("LeaveRoom", ac).WaitAsync(TimeSpan.FromSeconds(10));
        await c.SendAsync(ac, "After leaving the room");
        Assert.Equal("After leaving the room", (await c.ReceiveAsync()).Content);
        await a.AssertNoQueuedMessageAsync();
    }

    [Theory]
    [InlineData("Disabled", HttpStatusCode.Unauthorized)]
    [InlineData("Deleted", HttpStatusCode.Unauthorized)]
    [InlineData("Revoked", HttpStatusCode.Unauthorized)]
    [InlineData("AuthUnavailable", HttpStatusCode.ServiceUnavailable)]
    public async Task EstablishedWebSocketStopsReceivingAfterAuthChanges(
        string state, HttpStatusCode newConnectionStatus)
    {
        await using var scenario = new SignalRScenario();
        await scenario.StartAsync();
        var (uid, unsigned) = await scenario.Host.SignInAsync();
        var (partnerId, partnerUnsigned) = await scenario.Host.SignInAsync();
        var token = scenario.Host.Sign(unsigned);
        var peer = await scenario.ConnectAsync(token);
        var secondPeer = await scenario.ConnectAsync(token);
        var partner = await scenario.ConnectAsync(scenario.Host.Sign(partnerUnsigned));
        var room = await scenario.CreateRoomAsync(uid, partnerId);
        await peer.JoinAsync(room);
        await secondPeer.JoinAsync(room);
        await partner.JoinAsync(room);

        await ChangeAuthStateAsync(scenario, uid, state, unsigned);
        await AssertConnectionRejectedAsync(scenario, token, newConnectionStatus);

        if (state == "AuthUnavailable")
        {
            // An outage makes both accounts unverifiable, including the attempted sender.
            await AssertSendDeniedAsync(partner, room);
            await partner.AssertClosedWithoutMessagesAsync();
            await AssertStoredMessagesAsync(scenario, room);
        }
        else
        {
            await partner.SendAsync(room, "Only the authorized partner receives this");
            Assert.Equal("Only the authorized partner receives this", (await partner.ReceiveAsync()).Content);
            await AssertStoredMessagesAsync(scenario, room, "Only the authorized partner receives this");
        }

        // During a lookup outage the passive peer is checked by the idle sweep.
        var closeTimeout = state == "AuthUnavailable" ? TimeSpan.FromSeconds(25) : (TimeSpan?)null;
        await Task.WhenAll(peer.AssertClosedWithoutMessagesAsync(closeTimeout),
            secondPeer.AssertClosedWithoutMessagesAsync(closeTimeout));
        await AssertSendDeniedAsync(peer, room);
        await AssertSendDeniedAsync(secondPeer, room);
    }

    [Theory]
    [InlineData("Disabled")]
    [InlineData("Deleted")]
    [InlineData("Revoked")]
    [InlineData("AuthUnavailable")]
    public async Task EstablishedWebSocketCannotPersistAfterAuthChanges(string state)
    {
        await using var scenario = new SignalRScenario();
        await scenario.StartAsync();
        var (uid, unsigned) = await scenario.Host.SignInAsync();
        var (partnerId, _) = await scenario.Host.SignInAsync();
        var peer = await scenario.ConnectAsync(scenario.Host.Sign(unsigned));
        var room = await scenario.CreateRoomAsync(uid, partnerId);
        await peer.JoinAsync(room);

        await ChangeAuthStateAsync(scenario, uid, state, unsigned);
        await AssertSendDeniedAsync(peer, room);
        await peer.AssertClosedWithoutMessagesAsync();
        await AssertStoredMessagesAsync(scenario, room);
    }

    [Fact]
    public async Task IdleWebSocketClosesAtTokenExpirationAndCannotReconnect()
    {
        await using var scenario = new SignalRScenario();
        // Use the same JWT clock-skew settings as production.
        await scenario.StartAsync();
        var (uid, unsigned) = await scenario.Host.SignInAsync();
        var (partnerId, partnerUnsigned) = await scenario.Host.SignInAsync();
        var partner = await scenario.ConnectAsync(scenario.Host.Sign(partnerUnsigned));
        var room = await scenario.CreateRoomAsync(uid, partnerId);
        await partner.JoinAsync(room);
        var expires = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.AddSeconds(6).ToUnixTimeSeconds());
        var token = scenario.Host.Sign(unsigned, payload => payload["exp"] = expires.ToUnixTimeSeconds());
        var peer = await scenario.ConnectAsync(token);
        await peer.JoinAsync(room);
        // No invocation or broadcast is needed to trigger closure.
        await peer.AssertClosedWithoutMessagesAsync();
        Assert.True(DateTimeOffset.UtcNow >= expires);
        await AssertConnectionRejectedAsync(scenario, token, HttpStatusCode.Unauthorized);
        await AssertSendDeniedAsync(peer, room);
        await partner.SendAsync(room, "After the peer token expired");
        Assert.Equal("After the peer token expired", (await partner.ReceiveAsync()).Content);
        await peer.AssertClosedWithoutMessagesAsync();
        await AssertStoredMessagesAsync(scenario, room, "After the peer token expired");
    }

    [Fact]
    public async Task RevocationEvictsOldSocketButKeepsNewLoginForSameAccount()
    {
        await using var scenario = new SignalRScenario();
        await scenario.StartAsync();
        var password = Guid.NewGuid().ToString("N");
        var uid = await scenario.Data.AccountWithProfileAsync(password: password);
        var firstToken = await scenario.Host.SignInAccountAsync(uid, password);
        var old = await scenario.ConnectAsync(scenario.Host.Sign(firstToken));
        var (partnerId, partnerToken) = await scenario.Host.SignInAsync();
        var partner = await scenario.ConnectAsync(scenario.Host.Sign(partnerToken));
        var room = await scenario.CreateRoomAsync(uid, partnerId);
        await old.JoinAsync(room);
        await partner.JoinAsync(room);

        await ChangeAuthStateAsync(scenario, uid, "Revoked", firstToken);
        var newToken = await scenario.Host.SignInAccountAsync(uid, password);
        var current = await scenario.ConnectAsync(scenario.Host.Sign(newToken));
        await current.JoinAsync(room);

        await partner.SendAsync(room, "The new login remains authorized");
        Assert.Equal("The new login remains authorized", (await current.ReceiveAsync()).Content);
        await current.AssertNoQueuedMessageAsync();
        await old.AssertClosedWithoutMessagesAsync();
        await AssertConnectionRejectedAsync(scenario, scenario.Host.Sign(firstToken), HttpStatusCode.Unauthorized);
        await current.SendAsync(room, "The new login can reply");
        Assert.Equal("The new login remains authorized", (await partner.ReceiveAsync()).Content);
        Assert.Equal("The new login can reply", (await partner.ReceiveAsync()).Content);
    }

    [Fact]
    public async Task RemovedMemberLosesBothSubscriptionsAndCanStillUseOtherRoom()
    {
        await using var scenario = new SignalRScenario();
        await scenario.StartAsync();
        var (uid, unsigned) = await scenario.Host.SignInAsync();
        var (partnerId, partnerUnsigned) = await scenario.Host.SignInAsync();
        var (replacementId, replacementUnsigned) = await scenario.Host.SignInAsync();
        var token = scenario.Host.Sign(unsigned);
        var peer = await scenario.ConnectAsync(token);
        var secondPeer = await scenario.ConnectAsync(token);
        Assert.NotEqual(peer.Connection.ConnectionId, secondPeer.Connection.ConnectionId);
        var partner = await scenario.ConnectAsync(scenario.Host.Sign(partnerUnsigned));
        var replacement = await scenario.ConnectAsync(scenario.Host.Sign(replacementUnsigned));
        var room = await scenario.CreateRoomAsync(uid, partnerId);
        var unaffectedRoom = await scenario.CreateRoomAsync(uid, replacementId);
        await peer.JoinAsync(room);
        await secondPeer.JoinAsync(room);
        await peer.JoinAsync(unaffectedRoom);
        await secondPeer.JoinAsync(unaffectedRoom);
        await partner.JoinAsync(room);
        await replacement.JoinAsync(unaffectedRoom);
        await scenario.Data.Database.Collection("chatRooms").Document(room).UpdateAsync("User1Id", replacementId);

        // Exercise passive delivery before either former member makes another request.
        await partner.SendAsync(room, "Current member send");
        Assert.Equal("Current member send", (await partner.ReceiveAsync()).Content);
        await peer.AssertNoQueuedMessageAsync();
        await secondPeer.AssertNoQueuedMessageAsync();
        foreach (var formerMember in new[] { peer, secondPeer })
        {
            var denied = await Assert.ThrowsAsync<HubException>(() => formerMember.SendAsync(room, "Former member send"));
            Assert.Contains("Access denied.", denied.Message);
            denied = await Assert.ThrowsAsync<HubException>(() => formerMember.JoinAsync(room));
            Assert.Contains("Access denied.", denied.Message);
        }
        await replacement.SendAsync(unaffectedRoom, "Other room stays available");
        Assert.Equal("Other room stays available", (await secondPeer.ReceiveAsync()).Content);
        Assert.Equal("Other room stays available", (await peer.ReceiveAsync()).Content);
        Assert.Equal("Other room stays available", (await replacement.ReceiveAsync()).Content);
        await partner.AssertNoQueuedMessageAsync();

        await peer.Connection.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var reconnected = await scenario.ConnectAsync(token);
        var reconnectDenied = await Assert.ThrowsAsync<HubException>(() => reconnected.JoinAsync(room));
        Assert.Contains("Access denied.", reconnectDenied.Message);
        await reconnected.JoinAsync(unaffectedRoom);
        await partner.SendAsync(room, "After former member reconnect");
        Assert.Equal("After former member reconnect", (await partner.ReceiveAsync()).Content);
        await reconnected.AssertNoQueuedMessageAsync();
        await secondPeer.AssertNoQueuedMessageAsync();
        await replacement.AssertNoQueuedMessageAsync();

        // Restoring membership in the disposable fixture must not resurrect the old subscription.
        await scenario.Data.Database.Collection("chatRooms").Document(room).UpdateAsync("User1Id", uid);
        await partner.SendAsync(room, "Restored membership still requires rejoining");
        Assert.Equal("Restored membership still requires rejoining", (await partner.ReceiveAsync()).Content);
        await secondPeer.AssertNoQueuedMessageAsync();
        await reconnected.AssertNoQueuedMessageAsync();
    }

    [Fact]
    public async Task ReportClosesOnlyReportedRoomAndPreservesMessageEvidence()
    {
        await using var scenario = new SignalRScenario();
        await scenario.StartAsync();
        var (uid, unsigned) = await scenario.Host.SignInAsync();
        var (partnerId, partnerToken) = await scenario.Host.SignInAsync();
        var (otherId, otherToken) = await scenario.Host.SignInAsync();
        var token = scenario.Host.Sign(unsigned);
        var peer = await scenario.ConnectAsync(token);
        var partner = await scenario.ConnectAsync(scenario.Host.Sign(partnerToken));
        var other = await scenario.ConnectAsync(scenario.Host.Sign(otherToken));
        var room = await scenario.CreateRoomAsync(uid, partnerId);
        var otherRoom = await scenario.CreateRoomAsync(uid, otherId);
        await peer.JoinAsync(room);
        await partner.JoinAsync(room);
        await peer.JoinAsync(otherRoom);
        await other.JoinAsync(otherRoom);

        const string content = "This message is evidence";
        await partner.SendAsync(room, content);
        var reported = await peer.ReceiveAsync();
        Assert.Equal(content, reported.Content);
        Assert.Equal(reported, await partner.ReceiveAsync());
        using var response = await scenario.Host.SendAsync(HttpMethod.Post, $"/api/firebase-chat-rooms/{room}/reports",
            token, new { ReportType = "Message", Reason = "Test report", ReportedMessageId = reported.MessageId });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var denied = await Assert.ThrowsAsync<HubException>(() => partner.SendAsync(room, "Closed after report"));
        Assert.Contains("Room is unavailable.", denied.Message);
        denied = await Assert.ThrowsAsync<HubException>(() => peer.JoinAsync(room));
        Assert.Contains("Room is unavailable.", denied.Message);
        await other.SendAsync(otherRoom, "The unreported room stays open");
        Assert.Equal("The unreported room stays open", (await peer.ReceiveAsync()).Content);
        await partner.AssertNoQueuedMessageAsync();
        await AssertStoredMessagesAsync(scenario, room, content);
        var reports = await scenario.Data.Database.Collection("reports").WhereEqualTo("RoomId", room).GetSnapshotAsync();
        var report = Assert.Single(reports.Documents);
        var evidence = await report.Reference.Collection("evidence").GetSnapshotAsync();
        Assert.Contains(evidence.Documents, entry => entry.GetValue<string>("Content") == content);
    }

    [Fact]
    public async Task ConcurrentJoinAndUnmatchCannotRestoreClosedRoomAccess()
    {
        await using var scenario = new SignalRScenario();
        await scenario.StartAsync();
        var (uid, unsigned) = await scenario.Host.SignInAsync();
        var (partnerId, partnerUnsigned) = await scenario.Host.SignInAsync();
        var token = scenario.Host.Sign(unsigned);
        var peer = await scenario.ConnectAsync(token);
        var partner = await scenario.ConnectAsync(scenario.Host.Sign(partnerUnsigned));
        var room = await scenario.CreateRoomAsync(uid, partnerId);
        await partner.JoinAsync(room);

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var join = JoinOnStartAsync();
        var close = CloseOnStartAsync();
        start.SetResult();
        await Task.WhenAll(join, close).WaitAsync(TimeSpan.FromSeconds(15));

        foreach (var connection in new[] { peer, partner })
        {
            var error = await Assert.ThrowsAsync<HubException>(() => connection.JoinAsync(room));
            Assert.Contains("Room is unavailable.", error.Message);
            error = await Assert.ThrowsAsync<HubException>(() => connection.SendAsync(room, "Closed room send"));
            Assert.Contains("Room is unavailable.", error.Message);
            await connection.AssertNoQueuedMessageAsync();
        }
        await AssertStoredMessagesAsync(scenario, room);

        async Task JoinOnStartAsync()
        {
            await start.Task;
            try
            {
                await peer.JoinAsync(room);
            }
            catch (HubException ex)
            {
                Assert.Contains("Room is unavailable.", ex.Message);
            }
        }

        async Task CloseOnStartAsync()
        {
            await start.Task;
            using var response = await scenario.Host.SendAsync(HttpMethod.Post, $"/api/firebase-chat-rooms/{room}/unmatch", token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    [Fact]
    public async Task DisconnectDuringBroadcastLeavesSiblingSocketAuthorized()
    {
        await using var scenario = new SignalRScenario();
        await scenario.StartAsync();
        var (uid, unsigned) = await scenario.Host.SignInAsync();
        var (partnerId, partnerUnsigned) = await scenario.Host.SignInAsync();
        var token = scenario.Host.Sign(unsigned);
        var departing = await scenario.ConnectAsync(token);
        var staying = await scenario.ConnectAsync(token);
        var partner = await scenario.ConnectAsync(scenario.Host.Sign(partnerUnsigned));
        var room = await scenario.CreateRoomAsync(uid, partnerId);
        await departing.JoinAsync(room);
        await staying.JoinAsync(room);
        await partner.JoinAsync(room);

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnect = OnStartAsync(() => departing.Connection.StopAsync());
        var broadcast = OnStartAsync(() => partner.SendAsync(room, "Broadcast during disconnect"));
        start.SetResult();
        await Task.WhenAll(disconnect, broadcast).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(HubConnectionState.Disconnected, departing.Connection.State);
        var received = await staying.ReceiveAsync();
        Assert.Equal("Broadcast during disconnect", received.Content);
        Assert.Equal(received, await partner.ReceiveAsync());
        await staying.AssertNoQueuedMessageAsync();
        await AssertStoredMessagesAsync(scenario, room, received.Content);

        using var unmatch = await scenario.Host.SendAsync(HttpMethod.Post, $"/api/firebase-chat-rooms/{room}/unmatch", token);
        Assert.Equal(HttpStatusCode.OK, unmatch.StatusCode);
        var reconnected = await scenario.ConnectAsync(token);
        var denied = await Assert.ThrowsAsync<HubException>(() => reconnected.JoinAsync(room));
        Assert.Contains("Room is unavailable.", denied.Message);
        await reconnected.AssertNoQueuedMessageAsync();

        async Task OnStartAsync(Func<Task> operation)
        {
            await start.Task;
            await operation();
        }
    }

    [Fact]
    public async Task TransportAbortReconnectsWithNewMembershipAndNoPartnerDisconnectedEvent()
    {
        await using var scenario = new SignalRScenario();
        await scenario.StartAsync();
        var (uid, unsigned) = await scenario.Host.SignInAsync();
        var (partnerId, partnerUnsigned) = await scenario.Host.SignInAsync();
        var room = await scenario.CreateRoomAsync(uid, partnerId);
        var partner = await scenario.ConnectAsync(scenario.Host.Sign(partnerUnsigned));
        await partner.JoinAsync(room);
        var notification = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = partner.Connection.On("PartnerDisconnected", () => notification.TrySetResult());

        ClientWebSocket? currentSocket = null;
        var connection = scenario.CreateConnection(scenario.Host.Sign(unsigned), observeSocket: socket => currentSocket = socket);
        var peer = new SignalRPeer(connection);
        var reconnected = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Reconnected += id =>
        {
            reconnected.TrySetResult(id);
            return Task.CompletedTask;
        };
        await SignalRScenario.StartConnectionAsync(connection);
        await peer.JoinAsync(room);
        var oldId = connection.ConnectionId;
        Assert.NotNull(oldId);
        Assert.NotNull(currentSocket);
        currentSocket.Abort();
        var newId = await reconnected.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.NotNull(newId);
        Assert.NotEqual(oldId, newId);

        await partner.SendAsync(room, "Before rejoining");
        Assert.Equal("Before rejoining", (await partner.ReceiveAsync()).Content);
        await peer.AssertNoQueuedMessageAsync();
        await peer.JoinAsync(room);
        await partner.SendAsync(room, "After rejoining");
        Assert.Equal("After rejoining", (await peer.ReceiveAsync()).Content);
        // The current hub has no disconnect event producer. Observe through reconnect and rejoin.
        Assert.False(notification.Task.IsCompleted);
    }

    private static async Task AssertSendDeniedAsync(SignalRPeer peer, string room)
    {
        var error = await Record.ExceptionAsync(() => peer.SendAsync(room, "Send after security state changed"));
        Assert.True(error is HubException or InvalidOperationException or OperationCanceledException,
            $"Expected a denied or closed invocation, got {error?.GetType().Name ?? "success"}.");
    }

    private static async Task AssertStoredMessagesAsync(SignalRScenario scenario, string room, params string[] expected)
    {
        var snapshot = await scenario.Data.Database.Collection("chatRooms").Document(room)
            .Collection("messages").GetSnapshotAsync();
        Assert.Equal(expected, snapshot.Documents.Select(message => message.GetValue<string>("Content")));
    }

    private static async Task AssertConnectionRejectedAsync(SignalRScenario scenario, string? token, HttpStatusCode expected)
    {
        var connection = scenario.CreateConnection(token);
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => SignalRScenario.StartConnectionAsync(connection));
        Assert.Equal(expected, error.StatusCode);
        Assert.Equal(HubConnectionState.Disconnected, connection.State);
    }

    private static async Task ChangeAuthStateAsync(SignalRScenario scenario, string uid, string state, string unsignedToken)
    {
        switch (state)
        {
            case "Disabled":
                await scenario.Data.Auth.UpdateUserAsync(new UserRecordArgs { Uid = uid, Disabled = true });
                break;
            case "Deleted":
                await scenario.Data.Auth.DeleteUserAsync(uid);
                break;
            case "Revoked":
                var seconds = long.Parse(new JwtSecurityTokenHandler().ReadJwtToken(unsignedToken).Claims
                    .Single(claim => claim.Type == "auth_time").Value, CultureInfo.InvariantCulture);
                var authenticatedAt = DateTimeOffset.FromUnixTimeSeconds(seconds);
                // Wait until the emulator can record a strictly later whole-second cutoff.
                var delay = authenticatedAt.AddSeconds(1).AddMilliseconds(100) - DateTimeOffset.UtcNow;
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay);
                await scenario.Data.Auth.RevokeRefreshTokensAsync(uid);
                var revoked = await scenario.Data.Auth.GetUserAsync(uid);
                Assert.True(revoked.TokensValidAfterTimestamp > authenticatedAt.UtcDateTime,
                    "The emulator did not advance the revocation cutoff past the token's authentication time.");
                break;
            case "AuthUnavailable":
                scenario.Data.LookupFailureStatus = HttpStatusCode.Forbidden;
                break;
        }
    }

}
