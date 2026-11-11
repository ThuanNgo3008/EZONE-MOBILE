using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FirebaseAdmin.Auth;
using Google.Cloud.Firestore;
using WebChatEIU.IntegrationTests.AuthWorkflows;
using WebChatEIU.Models.Firestore;
using WebChatEIU.Services;

namespace WebChatEIU.IntegrationTests.HttpSecurity;

[Collection("Auth workflows")]
[Trait("Category", "ChatRoomListing")]
public sealed class ChatRoomListingTests
{
    private const string Route = "/api/firebase-chat-rooms";
    private static readonly DateTime CreatedAt = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task RequiresAuthenticationAndReturnsEmptyPageForMemberWithoutRooms()
    {
        await using var scenario = new Scenario();
        await scenario.Host.StartAsync();
        await ExpectStatusAsync(scenario, HttpStatusCode.Unauthorized);
        var member = await scenario.SignInAsync();
        var page = await ReadPageAsync(scenario, member.Token);
        Assert.Empty(page.GetProperty("rooms").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, page.GetProperty("nextCursor").ValueKind);
    }

    [Fact]
    public async Task ListsOnlyMemberRoomsAndOnlyAnonymousSummaryFields()
    {
        await using var scenario = new Scenario();
        await scenario.Host.StartAsync();
        var member = await scenario.SignInAsync();
        var partner = await scenario.SignInAsync();
        var outsider = await scenario.SignInAsync();
        var own = await scenario.SeedRoomAsync(partner.Uid, member.Uid, "own");
        await scenario.SeedRoomAsync(partner.Uid, outsider.Uid, "other");
        await own.UpdateAsync("User1Nickname", "Private Person <private@example.invalid>");
        await own.UpdateAsync("UpdatedAt", FieldValue.Delete);

        var page = await ReadPageAsync(scenario, member.Token, "?userId=" + outsider.Uid);
        var room = Assert.Single(page.GetProperty("rooms").EnumerateArray());
        Assert.Equal(own.Id, room.GetProperty("roomId").GetString());
        Assert.Equal("Active", room.GetProperty("status").GetString());
        Assert.Equal("User2", room.GetProperty("myRole").GetString());
        Assert.Equal("Anonymous Fox", room.GetProperty("partnerNickname").GetString());
        Assert.Equal(CreatedAt, room.GetProperty("createdAt").GetDateTime());
        Assert.Equal(CreatedAt, room.GetProperty("lastActivityAt").GetDateTime());
        Assert.Equal(new[] { "createdAt", "lastActivityAt", "myRole", "partnerNickname", "roomId", "status" },
            room.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal));
        Assert.DoesNotContain(partner.Uid, room.GetRawText());
        Assert.DoesNotContain("Private Person", room.GetRawText());
        Assert.DoesNotContain("private@example.invalid", room.GetRawText());
        Assert.DoesNotContain("private purpose", room.GetRawText());
        Assert.Equal(JsonValueKind.Null, page.GetProperty("nextCursor").ValueKind);
    }

    [Fact]
    public async Task PagesAcrossBothRolesWithTimestampTiesAndDeletedCursorDocument()
    {
        await using var scenario = new Scenario();
        await scenario.Host.StartAsync();
        var member = await scenario.SignInAsync();
        var partner = await scenario.SignInAsync();
        var outsider = await scenario.SignInAsync();
        var older = await scenario.SeedRoomAsync(member.Uid, partner.Uid, "old");
        var tiedA = await scenario.SeedRoomAsync(member.Uid, partner.Uid, "tie-a", CreatedAt.AddDays(1));
        var tiedB = await scenario.SeedRoomAsync(partner.Uid, member.Uid, "tie-b", CreatedAt.AddDays(1));
        var newest = await scenario.SeedRoomAsync(partner.Uid, member.Uid, "new", CreatedAt.AddDays(2));
        var other = await scenario.SeedRoomAsync(outsider.Uid, partner.Uid, "tie-0", CreatedAt.AddDays(1));
        // A newer stored activity value must survive an older send timestamp.
        var laterActivity = Timestamp.FromDateTime(new DateTime(2090, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        await older.UpdateAsync("UpdatedAt", laterActivity);
        using var send = await scenario.Host.SendAsync(HttpMethod.Post, "/api/firebase-chat-messages", member.Token,
            new { roomId = older.Id, content = "Older activity sample" });
        Assert.Equal(HttpStatusCode.OK, send.StatusCode);
        Assert.Equal(laterActivity, (await older.GetSnapshotAsync()).GetValue<Timestamp>("UpdatedAt"));

        var first = await ReadPageAsync(scenario, member.Token, "?pageSize=2");
        Assert.Equal(new[] { newest.Id, tiedB.Id }, RoomIds(first));
        var cursor = Assert.IsType<string>(first.GetProperty("nextCursor").GetString());
        // The cursor stores an ordering position rather than looking up a room.
        await tiedB.DeleteAsync();
        var added = await scenario.SeedRoomAsync(member.Uid, partner.Uid, "added", CreatedAt.AddDays(3));
        var query = "?pageSize=2&cursor=" + Uri.EscapeDataString(cursor);
        var second = await ReadPageAsync(scenario, member.Token, query);
        Assert.Equal(new[] { tiedA.Id, older.Id }, RoomIds(second));
        Assert.Equal(JsonValueKind.Null, second.GetProperty("nextCursor").ValueKind);
        Assert.Equal(new[] { other.Id }, RoomIds(await ReadPageAsync(scenario, outsider.Token, query)));
        Assert.Equal(new[] { added.Id }, RoomIds(await ReadPageAsync(scenario, member.Token, "?pageSize=1")));
    }

    [Theory]
    [InlineData("?pageSize=0")]
    [InlineData("?pageSize=51")]
    [InlineData("?pageSize=not-a-number")]
    [InlineData("?cursor=%25%25%25")]
    [InlineData("?cursor=a")]
    [InlineData("oversized")]
    public async Task InvalidPaginationReturnsBadRequest(string query)
    {
        await using var scenario = new Scenario();
        await scenario.Host.StartAsync();
        var member = await scenario.SignInAsync();
        if (query == "oversized")
            query = "?cursor=" + new string('a', 2049);
        await ExpectStatusAsync(scenario, HttpStatusCode.BadRequest, member.Token, query);
    }

    [Theory]
    [InlineData("Disabled", HttpStatusCode.Unauthorized)]
    [InlineData("Deleted", HttpStatusCode.Unauthorized)]
    [InlineData("Revoked", HttpStatusCode.Unauthorized)]
    [InlineData("ProfileBanned", HttpStatusCode.Forbidden)]
    [InlineData("ProfileDeleted", HttpStatusCode.Forbidden)]
    [InlineData("AuthUnavailable", HttpStatusCode.ServiceUnavailable)]
    public async Task DeniesIneligibleAccountsBeforeReturningRoomData(string state, HttpStatusCode expected)
    {
        await using var scenario = new Scenario();
        await scenario.Host.StartAsync();
        var member = await scenario.SignInAsync();
        var partner = await scenario.SignInAsync();
        var room = await scenario.SeedRoomAsync(member.Uid, partner.Uid, "protected");
        var token = member.Token;
        Assert.Equal(new[] { room.Id }, RoomIds(await ReadPageAsync(scenario, token)));
        switch (state)
        {
            case "Disabled":
                await scenario.Data.Auth.UpdateUserAsync(new UserRecordArgs { Uid = member.Uid, Disabled = true });
                break;
            case "Deleted":
                await scenario.Data.Auth.DeleteUserAsync(member.Uid);
                break;
            case "Revoked":
                await scenario.Data.Auth.RevokeRefreshTokensAsync(member.Uid);
                var account = await scenario.Data.Auth.GetUserAsync(member.Uid);
                var cutoff = new DateTimeOffset(account.TokensValidAfterTimestamp).ToUnixTimeSeconds();
                token = scenario.Host.Sign(member.Unsigned, payload => payload["auth_time"] = cutoff - 1);
                break;
            case "AuthUnavailable":
                scenario.Data.LookupFailureStatus = HttpStatusCode.ServiceUnavailable;
                break;
            default:
                await scenario.Data.Database.Collection("users").Document(member.Uid)
                    .UpdateAsync(state == "ProfileBanned" ? "IsBanned" : "IsDeleted", true);
                break;
        }

        using var response = await scenario.Host.SendAsync(HttpMethod.Get, Route, token);
        Assert.Equal(expected, response.StatusCode);
        Assert.DoesNotContain(room.Id, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task UnmatchedRoomRemainsVisibleToMembersWithReadOnlyHistory()
    {
        await using var scenario = new Scenario();
        await scenario.Host.StartAsync();
        var member = await scenario.SignInAsync();
        var partner = await scenario.SignInAsync();
        var outsider = await scenario.SignInAsync();
        var room = await scenario.SeedRoomAsync(member.Uid, partner.Uid, "unmatched");
        var active = await scenario.SeedRoomAsync(outsider.Uid, member.Uid, "active");
        var messages = "/api/firebase-chat-messages";
        using var send = await scenario.Host.SendAsync(HttpMethod.Post, messages, member.Token,
            new { roomId = room.Id, content = "Private message" });
        Assert.Equal(HttpStatusCode.OK, send.StatusCode);
        var sent = await send.Content.ReadFromJsonAsync<JsonElement>();
        var messageId = Assert.IsType<string>(sent.GetProperty("messageId").GetString());
        var persisted = await room.Collection("messages").Document(messageId).GetSnapshotAsync();
        var roomBeforeClose = (await ReadPageAsync(scenario, member.Token)).GetProperty("rooms").EnumerateArray()
            .Single(item => item.GetProperty("roomId").GetString() == room.Id);
        Assert.Equal(persisted.GetValue<Timestamp>("CreatedAt").ToDateTime(),
            roomBeforeClose.GetProperty("lastActivityAt").GetDateTime());
        Assert.DoesNotContain("Private message", roomBeforeClose.GetRawText());

        using var unmatch = await scenario.Host.SendAsync(HttpMethod.Post, $"{Route}/{room.Id}/unmatch", member.Token);
        Assert.Equal(HttpStatusCode.OK, unmatch.StatusCode);
        var closed = Assert.Single((await ReadPageAsync(scenario, partner.Token)).GetProperty("rooms").EnumerateArray());
        Assert.Equal(room.Id, closed.GetProperty("roomId").GetString());
        Assert.Equal("Closed", closed.GetProperty("status").GetString());
        Assert.Equal(new[] { active.Id }, RoomIds(await ReadPageAsync(scenario, outsider.Token)));

        using var history = await scenario.Host.SendAsync(HttpMethod.Get, $"{messages}/{room.Id}", partner.Token);
        Assert.Equal(HttpStatusCode.OK, history.StatusCode);
        var historyPage = await history.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Private message", Assert.Single(historyPage.GetProperty("messages").EnumerateArray())
            .GetProperty("content").GetString());
        var closedActivity = (await room.GetSnapshotAsync()).GetValue<Timestamp>("UpdatedAt");
        using var rejected = await scenario.Host.SendAsync(HttpMethod.Post, messages, partner.Token,
            new { roomId = room.Id, content = "No longer writable" });
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        using var unauthorized = await scenario.Host.SendAsync(HttpMethod.Post, messages, outsider.Token,
            new { roomId = room.Id, content = "Outside this room" });
        Assert.Equal(HttpStatusCode.Forbidden, unauthorized.StatusCode);
        Assert.Equal(closedActivity, (await room.GetSnapshotAsync()).GetValue<Timestamp>("UpdatedAt"));
        Assert.Single((await room.Collection("messages").GetSnapshotAsync()).Documents);
    }

    [Fact]
    public async Task MutualReadyCreatesRoomDiscoverableByBothParticipants()
    {
        await using var scenario = new Scenario();
        await scenario.Host.StartAsync();
        var first = await scenario.SignInAsync();
        var second = await scenario.SignInAsync();
        var reservations = new FirebaseMatchReservationService(scenario.Data.Database,
            new FirebaseMatchEligibilityService(scenario.Data.Auth));
        var id = await reservations.CreateReservationAsync(first.Uid, second.Uid, "Random");
        scenario.ReservationIds.Add(id);
        var path = $"/api/firebase-match-reservations/{id}/ready";
        using var waiting = await scenario.Host.SendAsync(HttpMethod.Post, path, first.Token);
        Assert.Equal(HttpStatusCode.OK, waiting.StatusCode);
        Assert.Empty(RoomIds(await ReadPageAsync(scenario, first.Token)));
        using var matched = await scenario.Host.SendAsync(HttpMethod.Post, path, second.Token);
        Assert.Equal(HttpStatusCode.OK, matched.StatusCode);
        var result = await matched.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Matched", result.GetProperty("status").GetString());
        var roomId = Assert.IsType<string>(result.GetProperty("roomId").GetString());
        foreach (var member in new[] { first, second })
            Assert.Equal(new[] { roomId }, RoomIds(await ReadPageAsync(scenario, member.Token)));
    }

    private static async Task<JsonElement> ReadPageAsync(Scenario scenario, string token, string query = "")
    {
        using var response = await scenario.Host.SendAsync(HttpMethod.Get, Route + query, token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task ExpectStatusAsync(Scenario scenario, HttpStatusCode status, string? token = null, string query = "")
    {
        using var response = await scenario.Host.SendAsync(HttpMethod.Get, Route + query, token);
        Assert.Equal(status, response.StatusCode);
    }

    private static string?[] RoomIds(JsonElement page) =>
        page.GetProperty("rooms").EnumerateArray().Select(room => room.GetProperty("roomId").GetString()).ToArray();

    private sealed class Scenario : IAsyncDisposable
    {
        private readonly string prefix = Guid.NewGuid().ToString("N") + "-";
        private readonly List<string> users = [];
        private readonly HashSet<DocumentReference> rooms = [];
        public AuthWorkflowTestData Data { get; } = new();
        public HttpSecurityHost Host { get; }
        public List<string> ReservationIds { get; } = [];

        public Scenario() => Host = new HttpSecurityHost(Data);

        public async Task<(string Uid, string Unsigned, string Token)> SignInAsync()
        {
            var (uid, unsigned) = await Host.SignInAsync();
            users.Add(uid);
            return (uid, unsigned, Host.Sign(unsigned));
        }

        public async Task<DocumentReference> SeedRoomAsync(string first, string second, string suffix, DateTime? created = null)
        {
            var timestamp = Timestamp.FromDateTime(created ?? CreatedAt);
            var reference = Data.Database.Collection("chatRooms").Document(prefix + suffix);
            rooms.Add(reference);
            await reference.CreateAsync(new FirebaseChatRoom
            {
                User1Id = first, User2Id = second, User1Nickname = "Anonymous Fox", User2Nickname = "Anonymous Owl",
                CreatedAt = timestamp, UpdatedAt = timestamp, BecamePersistentAt = timestamp,
                Purpose = "private purpose", AffinityScore = 73
            });
            return reference;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Host.DisposeAsync();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var database = Data.Database;
                var batch = database.StartBatch();
                foreach (var id in ReservationIds)
                {
                    var reference = database.Collection("matchReservations").Document(id);
                    var snapshot = await reference.GetSnapshotAsync(timeout.Token);
                    if (snapshot.TryGetValue<string>("RoomId", out var roomId) && !string.IsNullOrEmpty(roomId))
                        rooms.Add(database.Collection("chatRooms").Document(roomId));
                    batch.Delete(reference);
                }
                foreach (var room in rooms)
                {
                    var messages = await room.Collection("messages").GetSnapshotAsync(timeout.Token);
                    foreach (var message in messages.Documents)
                        batch.Delete(message.Reference);
                    var history = await database.Collection("matchHistory").WhereEqualTo("RoomId", room.Id)
                        .GetSnapshotAsync(timeout.Token);
                    foreach (var match in history.Documents)
                        batch.Delete(match.Reference);
                    batch.Delete(room);
                }
                foreach (var user in users)
                    batch.Delete(database.Collection("matchSessions").Document(user));
                await batch.CommitAsync(timeout.Token);
            }
            finally
            {
                await Data.DisposeAsync();
            }
        }
    }
}
