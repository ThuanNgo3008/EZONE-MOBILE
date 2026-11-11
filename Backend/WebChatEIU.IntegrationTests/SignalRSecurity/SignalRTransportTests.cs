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
            "Revoked" => scenario.Host.Sign(unsigned, payload =>
                payload["auth_time"] = DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeSeconds()),
            _ => validToken
        };

        if (state is "Disabled" or "Deleted" or "Revoked" or "AuthUnavailable")
            await ChangeAuthStateAsync(scenario, uid, state);

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
    public async Task KnownGap_EstablishedWebSocketKeepsSendingAndReceivingAfterAuthChanges(
        string state, HttpStatusCode newConnectionStatus)
    {
        await using var scenario = new SignalRScenario();
        await scenario.StartAsync();
        var (uid, unsigned) = await scenario.Host.SignInAsync();
        var (partnerId, partnerUnsigned) = await scenario.Host.SignInAsync();
        var token = scenario.Host.Sign(unsigned);
        var peer = await scenario.ConnectAsync(token);
        var partner = await scenario.ConnectAsync(scenario.Host.Sign(partnerUnsigned));
        var room = await scenario.CreateRoomAsync(uid, partnerId);
        await peer.JoinAsync(room);
        await partner.JoinAsync(room);

        if (state == "Revoked")
        {
            var seconds = long.Parse(new JwtSecurityTokenHandler().ReadJwtToken(unsigned).Claims
                .Single(claim => claim.Type == "auth_time").Value, CultureInfo.InvariantCulture);
            // Ensure the emulator's whole-second revocation cutoff is strictly later.
            await WaitUntilAsync(DateTimeOffset.FromUnixTimeSeconds(seconds + 1));
        }
        await ChangeAuthStateAsync(scenario, uid, state);
        await AssertConnectionRejectedAsync(scenario, token, newConnectionStatus);

        // Expected security behavior is to block this send and remove the subscription.
        await AssertEstablishedMessageAccessAsync(peer, partner, room);
    }

    [Fact]
    public async Task KnownGap_EstablishedWebSocketContinuesPastTokenExpiration()
    {
        await using var scenario = new SignalRScenario();
        // Zero skew is test-only and makes new connections reject immediately after exp.
        await scenario.StartAsync(strictExpiry: true);
        var (uid, unsigned) = await scenario.Host.SignInAsync();
        var (partnerId, partnerUnsigned) = await scenario.Host.SignInAsync();
        var partner = await scenario.ConnectAsync(scenario.Host.Sign(partnerUnsigned));
        var room = await scenario.CreateRoomAsync(uid, partnerId);
        await partner.JoinAsync(room);
        var expires = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.AddSeconds(6).ToUnixTimeSeconds());
        var token = scenario.Host.Sign(unsigned, payload => payload["exp"] = expires.ToUnixTimeSeconds());
        var peer = await scenario.ConnectAsync(token);
        await peer.JoinAsync(room);
        await WaitUntilAsync(expires.AddMilliseconds(100));
        await AssertConnectionRejectedAsync(scenario, token, HttpStatusCode.Unauthorized);
        await AssertEstablishedMessageAccessAsync(peer, partner, room);
    }

    [Fact]
    public async Task KnownGap_RemovedRoomMemberStillReceivesOnBothExistingConnections()
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
        await secondPeer.JoinAsync(unaffectedRoom);
        await partner.JoinAsync(room);
        await replacement.JoinAsync(unaffectedRoom);
        await scenario.Data.Database.Collection("chatRooms").Document(room).UpdateAsync("User1Id", replacementId);

        foreach (var formerMember in new[] { peer, secondPeer })
        {
            var denied = await Assert.ThrowsAsync<HubException>(() => formerMember.SendAsync(room, "Former member send"));
            Assert.Contains("Access denied.", denied.Message);
            denied = await Assert.ThrowsAsync<HubException>(() => formerMember.JoinAsync(room));
            Assert.Contains("Access denied.", denied.Message);
        }
        await partner.SendAsync(room, "Current member send");
        var message = await partner.ReceiveAsync();
        Assert.Equal("Current member send", message.Content);
        // Expected security behavior is to evict both old subscriptions for this room.
        Assert.Equal(message, await peer.ReceiveAsync());
        Assert.Equal(message, await secondPeer.ReceiveAsync());

        await replacement.SendAsync(unaffectedRoom, "Other room stays available");
        Assert.Equal("Other room stays available", (await secondPeer.ReceiveAsync()).Content);
        await peer.AssertNoQueuedMessageAsync();
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

    private static async Task AssertEstablishedMessageAccessAsync(SignalRPeer peer, SignalRPeer partner, string room)
    {
        await peer.SendAsync(room, "Send after security state changed");
        Assert.Equal("Send after security state changed", (await partner.ReceiveAsync()).Content);
        Assert.Equal("Send after security state changed", (await peer.ReceiveAsync()).Content);
        await partner.SendAsync(room, "Receive after security state changed");
        Assert.Equal("Receive after security state changed", (await peer.ReceiveAsync()).Content);
    }

    private static async Task AssertConnectionRejectedAsync(SignalRScenario scenario, string? token, HttpStatusCode expected)
    {
        var connection = scenario.CreateConnection(token);
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => SignalRScenario.StartConnectionAsync(connection));
        Assert.Equal(expected, error.StatusCode);
        Assert.Equal(HubConnectionState.Disconnected, connection.State);
    }

    private static async Task ChangeAuthStateAsync(SignalRScenario scenario, string uid, string state)
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
                await scenario.Data.Auth.RevokeRefreshTokensAsync(uid);
                break;
            case "AuthUnavailable":
                scenario.Data.LookupFailureStatus = HttpStatusCode.Forbidden;
                break;
        }
    }

    private static Task WaitUntilAsync(DateTimeOffset instant)
    {
        var delay = instant - DateTimeOffset.UtcNow;
        return delay > TimeSpan.Zero ? Task.Delay(delay) : Task.CompletedTask;
    }
}
