using Google.Cloud.Firestore;
using WebChatEIU.Controllers;
using WebChatEIU.Models.Firestore;
using WebChatEIU.Services;

namespace WebChatEIU.IntegrationTests.Lifecycle;

[Collection("Firestore emulator")]
[Trait("Category", "Lifecycle")]
public sealed class ChatRoomLifecycleTests(FirestoreEmulatorFixture fixture)
{
    [Fact]
    public async Task MatchedPair_ImmediatelyReceivesOnePersistentAnonymousRoom()
    {
        await using var data = new LifecycleTestData(fixture.Database);
        var pair = await data.MatchedPairAsync();
        await data.Matches.AssertMatchedAsync(pair.User1, pair.User2, pair.ReservationId, pair.RoomId);
        await data.Matches.AssertMissingAsync("roomDecisions", pair.RoomId);
        var room = await data.ReadRoomAsync(pair);
        Assert.Equal(room.CreatedAt, room.BecamePersistentAt);
        Assert.Equal("Anonymous Fox", room.User1Nickname);
        Assert.Equal("Anonymous Panda", room.User2Nickname);
        await Assert.ThrowsAsync<InvalidOperationException>(() => data.Rooms.EndTemporaryRoomAsync(pair.RoomId, pair.User1));
        Assert.Equal("Active", (await data.ReadRoomAsync(pair)).Status);
    }

    [Theory]
    [InlineData("TemporaryEndsAt")]
    [InlineData("DecisionEndsAt")]
    public async Task HistoricalDeadlineFields_DoNotRestrictActiveMessagingOrEnableCleanup(string field)
    {
        await using var data = new LifecycleTestData(fixture.Database);
        var pair = await data.MatchedPairAsync();
        await data.Room(pair.RoomId).UpdateAsync(new Dictionary<string, object>
        {
            [field] = Timestamp.FromDateTime(DateTime.UtcNow.AddDays(-1)),
            ["TemporaryStartedAt"] = Timestamp.FromDateTime(DateTime.UtcNow.AddDays(-2)),
            ["CleanupPending"] = true
        });
        var message = await data.Messages.SendMessageAsync(pair.RoomId, pair.User1, "Still active");
        await Assert.ThrowsAsync<InvalidOperationException>(() => data.Cleanup.DeleteTemporaryMessagesAsync(pair.RoomId));
        Assert.Equal(message.MessageId, Assert.Single((await data.Messages.GetMessagesAsync(pair.RoomId, pair.User2)).Messages).MessageId);
        Assert.Equal("Active", (await data.ReadRoomAsync(pair)).Status);
        await data.AssertSessionsReleasedAsync(pair);
    }

    [Fact]
    public void ObsoleteDecisionAndTimerEntryPoints_AreRemoved()
    {
        Assert.Null(typeof(FirebaseChatRoomsController).GetMethod("SubmitDecision"));
        Assert.Null(typeof(FirebaseChatRoomService).GetMethod("TryMoveToAwaitingDecisionAsync"));
        Assert.Null(typeof(FirebaseChatRoomService).GetMethod("TryCloseExpiredDecisionAsync"));
        Assert.Null(typeof(FirebaseChatRoomService).GetMethod("CreateTemporaryRoomAsync"));
        Assert.DoesNotContain(typeof(FirebaseRoomExpirationWorker).GetMethods(
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance),
            method => method.Name == "ProcessExpiredRoomsAsync");
    }

    [Fact]
    public async Task RepeatedReady_IsIdempotentAndDoesNotReleaseNewReservationSessions()
    {
        await using var data = new LifecycleTestData(fixture.Database);
        var pair = await data.MatchedPairAsync();
        var partner = await data.Matches.WaitingUserAsync();
        await data.Matches.Queue.JoinQueueAsync(pair.User1);
        var next = await data.Matches.Queue.TryReserveCandidateAsync(pair.User1, partner);
        Assert.NotNull(next);
        Assert.Equal(("Matched", pair.RoomId), await data.Matches.Reservations.SubmitReadyAsync(pair.ReservationId, pair.User1));
        await data.Matches.AssertPendingSessionAsync(pair.User1, next);
        await data.Matches.AssertPendingSessionAsync(partner, next);
        Assert.Single(await data.Matches.OwnedAsync("chatRooms"));
        await data.Matches.AssertMissingAsync("roomDecisions", pair.RoomId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EitherParticipantCanExplicitlyUnmatch_WithoutTemporaryCleanup(bool firstUser)
    {
        await using var data = new LifecycleTestData(fixture.Database);
        var pair = await data.MatchedPairAsync();
        Assert.True(await data.Rooms.UnmatchRoomAsync(pair.RoomId, firstUser ? pair.User1 : pair.User2));
        Assert.False(await data.Rooms.UnmatchRoomAsync(pair.RoomId, pair.User1));
        await data.AssertClosedAsync(pair);
        Assert.Single(await data.Matches.OwnedAsync("matchHistory"));
    }

    [Fact]
    public async Task MultipleActiveRooms_AllowImmediateRematchAndIndependentMessages()
    {
        await using var data = new LifecycleTestData(fixture.Database);
        var ab = await data.MatchedPairAsync();
        var before = await data.ReadRoomAsync(ab);
        var ac = await data.MatchAgainAsync(ab.User1);
        Assert.NotEqual(ab.RoomId, ac.RoomId);
        Assert.Equal(2, (await data.Matches.OwnedAsync("chatRooms")).Count);
        Assert.Equal(2, (await data.Matches.OwnedAsync("matchHistory")).Count);
        Assert.Equal(before.UpdatedAt, (await data.ReadRoomAsync(ab)).UpdatedAt);
        Assert.Equal("Active", (await data.ReadRoomAsync(ac)).Status);
        await data.AssertSessionsReleasedAsync(ac);
        var first = await data.Messages.SendMessageAsync(ab.RoomId, ab.User1, "For B");
        var second = await data.Messages.SendMessageAsync(ac.RoomId, ac.User1, "For C");
        Assert.Equal(first.MessageId, Assert.Single((await data.Messages.GetMessagesAsync(ab.RoomId, ab.User2)).Messages).MessageId);
        Assert.Equal(second.MessageId, Assert.Single((await data.Messages.GetMessagesAsync(ac.RoomId, ac.User2)).Messages).MessageId);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => data.Messages.GetMessagesAsync(ab.RoomId, ac.User2));
        await data.Matches.Queue.JoinQueueAsync(ab.User1);
        await data.Matches.Queue.JoinQueueAsync(ab.User2);
        Assert.Null(await data.Matches.Queue.TryReserveCandidateAsync(ab.User1, ab.User2));
        await Assert.ThrowsAsync<InvalidOperationException>(() => data.Matches.Reservations.CreateReservationAsync(ab.User1, ab.User2, "Random"));
    }

    [Fact]
    public async Task Unmatch_PreservesOtherActiveRoomAndNewPendingReservationAndReadOnlyHistory()
    {
        await using var data = new LifecycleTestData(fixture.Database);
        var ab = await data.MatchedPairAsync();
        var ac = await data.MatchAgainAsync(ab.User1);
        var message = await data.Messages.SendMessageAsync(ab.RoomId, ab.User1, "Retained history");
        var nextPartner = await data.Matches.WaitingUserAsync();
        await data.Matches.Queue.JoinQueueAsync(ab.User1);
        var next = await data.Matches.Queue.TryReserveCandidateAsync(ab.User1, nextPartner);
        Assert.NotNull(next);
        Assert.True(await data.Rooms.UnmatchRoomAsync(ab.RoomId, ab.User1));
        Assert.False(await data.Rooms.UnmatchRoomAsync(ab.RoomId, ab.User2));
        Assert.Equal("Closed", (await data.ReadRoomAsync(ab)).Status);
        Assert.False((await data.ReadRoomAsync(ab)).CleanupPending);
        await data.Matches.AssertPendingSessionAsync(ab.User1, next);
        await data.Matches.AssertPendingSessionAsync(nextPartner, next);
        Assert.Equal("Active", (await data.ReadRoomAsync(ac)).Status);
        Assert.Equal(message.MessageId, Assert.Single((await data.Messages.GetMessagesAsync(ab.RoomId, ab.User2)).Messages).MessageId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => data.Messages.SendMessageAsync(ab.RoomId, ab.User1, "Closed"));
        await data.Messages.SendMessageAsync(ac.RoomId, ac.User1, "Still writable");
    }

    [Fact]
    public async Task Outsider_CannotUnmatchOrCloseAnActiveRoom()
    {
        await using var data = new LifecycleTestData(fixture.Database);
        var pair = await data.MatchedPairAsync();
        var outsider = await data.Matches.NewUserAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => data.Rooms.UnmatchRoomAsync(pair.RoomId, outsider));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => data.Rooms.CloseRoomAsync(pair.RoomId, outsider));
        Assert.Equal("Active", (await data.ReadRoomAsync(pair)).Status);
    }

    [Fact]
    public async Task HistoricalEndChat_PreservesNewReservationAndUnrelatedActiveRoom()
    {
        await using var data = new LifecycleTestData(fixture.Database);
        var legacy = await data.MatchedPairAsync();
        await data.SeedHistoricalRoomAsync(legacy);
        var other = await data.MatchedPairAsync();
        var before = await data.ReadRoomAsync(other);
        var partner = await data.Matches.NewUserAsync();
        var next = await data.Matches.Reservations.CreateReservationAsync(legacy.User1, partner, "Random");
        await data.Matches.Document("matchSessions", legacy.User2).CreateAsync(new FirebaseMatchSession
        {
            UserId = legacy.User2, ReservationId = legacy.ReservationId,
            Status = "Temporary", RoomId = legacy.RoomId
        });
        Assert.True(await data.Rooms.EndTemporaryRoomAsync(legacy.RoomId, legacy.User1));
        Assert.False(await data.Rooms.EndTemporaryRoomAsync(legacy.RoomId, legacy.User2));
        Assert.Equal("Closed", (await data.ReadRoomAsync(legacy)).Status);
        Assert.True((await data.ReadRoomAsync(legacy)).CleanupPending);
        Assert.Equal(next, (await data.Matches.ReadAsync<FirebaseMatchSession>("matchSessions", legacy.User1)).ReservationId);
        Assert.Equal(next, (await data.Matches.ReadAsync<FirebaseMatchSession>("matchSessions", partner)).ReservationId);
        await data.Matches.AssertMissingAsync("matchSessions", legacy.User2);
        Assert.Equal("Active", (await data.ReadRoomAsync(other)).Status);
        Assert.Equal(before.UpdatedAt, (await data.ReadRoomAsync(other)).UpdatedAt);
    }
}
