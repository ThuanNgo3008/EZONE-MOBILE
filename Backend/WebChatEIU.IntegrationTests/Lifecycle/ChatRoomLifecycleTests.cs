using Google.Cloud.Firestore;
using WebChatEIU.Models.Firestore;

namespace WebChatEIU.IntegrationTests.Lifecycle;

[Collection("Firestore emulator")]
[Trait("Category", "Lifecycle")]
public sealed class ChatRoomLifecycleTests(FirestoreEmulatorFixture fixture)
{
    [Fact]
    public async Task MatchedPair_ReceivesOneFiveMinuteTemporaryRoom()
    {
        await using var data = new LifecycleTestData(fixture.Database);
        var pair = await data.MatchedPairAsync();
        await data.Matches.AssertMatchedAsync(pair.User1, pair.User2, pair.ReservationId, pair.RoomId);
        Assert.False(await data.Rooms.TryMoveToAwaitingDecisionAsync(pair.RoomId));
    }

    [Fact]
    public async Task ExpiredTemporaryRoom_TransitionsOnceWithTwoMinuteDecisionWindow()
    {
        await using var data = new LifecycleTestData(fixture.Database);
        var pair = await data.MatchedPairAsync();
        await data.AwaitDecisionAsync(pair);
        var room = await data.ReadRoomAsync(pair);
        Assert.Equal("AwaitingDecision", room.Status);
        Assert.Equal(TimeSpan.FromMinutes(2), room.DecisionEndsAt!.Value.ToDateTime() - room.TemporaryEndsAt!.Value.ToDateTime());
        Assert.False(await data.Rooms.TryMoveToAwaitingDecisionAsync(pair.RoomId));
        Assert.Equal(room.DecisionEndsAt, (await data.ReadRoomAsync(pair)).DecisionEndsAt);
    }

    [Fact]
    public async Task SingleYesAndDuplicate_KeepRoomWaitingAndDoNotPermitDecisionChange()
    {
        await using var data = new LifecycleTestData(fixture.Database);
        var pair = await data.MatchedPairAsync();
        await data.AwaitDecisionAsync(pair);
        Assert.Equal("Waiting", await data.Decisions.SubmitDecisionAsync(pair.RoomId, pair.User1, true));
        Assert.Equal("Waiting", await data.Decisions.SubmitDecisionAsync(pair.RoomId, pair.User1, true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => data.Decisions.SubmitDecisionAsync(pair.RoomId, pair.User1, false));
        var room = await data.ReadRoomAsync(pair);
        Assert.Equal("AwaitingDecision", room.Status);
        Assert.Null(room.BecamePersistentAt);
        var decision = await data.Matches.ReadAsync<FirebaseRoomDecision>("roomDecisions", pair.RoomId);
        Assert.True(decision.User1Continue);
        Assert.Null(decision.User2Continue);
        Assert.Equal(pair.RoomId, (await data.Matches.ReadAsync<FirebaseMatchSession>("matchSessions", pair.User1)).RoomId);
        Assert.Equal(pair.RoomId, (await data.Matches.ReadAsync<FirebaseMatchSession>("matchSessions", pair.User2)).RoomId);
    }

    [Fact]
    public async Task BothYes_MakePersistentRoomAndReleaseSessions_TerminalRetryIsRejected()
    {
        await using var data = new LifecycleTestData(fixture.Database);
        var pair = await data.MatchedPairAsync();
        await data.MakePersistentAsync(pair);
        var before = await data.ReadRoomAsync(pair);
        Assert.Equal("Active", before.Status);
        Assert.NotNull(before.BecamePersistentAt);
        Assert.False(before.CleanupPending);
        Assert.Null(before.ClosedAt);
        await data.AssertSessionsReleasedAsync(pair);
        await Assert.ThrowsAsync<InvalidOperationException>(() => data.Decisions.SubmitDecisionAsync(pair.RoomId, pair.User2, true));
        Assert.Equal(before.BecamePersistentAt, (await data.ReadRoomAsync(pair)).BecamePersistentAt);
        Assert.Single(await data.Matches.OwnedAsync("chatRooms"));
        Assert.Single(await data.Matches.OwnedAsync("matchHistory"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EitherParticipantNo_ClosesRoomAndReleasesSessions(bool firstUser)
    {
        await using var data = new LifecycleTestData(fixture.Database);
        var pair = await data.MatchedPairAsync();
        await data.AwaitDecisionAsync(pair);
        Assert.Equal("Closed", await data.Decisions.SubmitDecisionAsync(pair.RoomId, firstUser ? pair.User1 : pair.User2, false));
        await data.AssertClosedAsync(pair);
        await Assert.ThrowsAsync<InvalidOperationException>(() => data.Decisions.SubmitDecisionAsync(pair.RoomId, pair.User1, false));
        Assert.Single(await data.Matches.OwnedAsync("matchHistory"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DecisionTimeout_ClosesViaExpirationOrLateSubmission(bool lateSubmission)
    {
        await using var data = new LifecycleTestData(fixture.Database);
        var pair = await data.MatchedPairAsync();
        await data.AwaitDecisionAsync(pair);
        await data.Room(pair.RoomId).UpdateAsync("DecisionEndsAt", Timestamp.FromDateTime(DateTime.UtcNow.AddMinutes(-2)));
        if (lateSubmission) Assert.Equal("Closed", await data.Decisions.SubmitDecisionAsync(pair.RoomId, pair.User1, true));
        else Assert.True(await data.Rooms.TryCloseExpiredDecisionAsync(pair.RoomId));
        await data.AssertClosedAsync(pair);
        Assert.False(await data.Rooms.TryCloseExpiredDecisionAsync(pair.RoomId));
    }

    [Fact]
    public async Task EndingTemporaryRoom_PreservesUnrelatedRoomAndReplacementSession()
    {
        await using var data = new LifecycleTestData(fixture.Database);
        var pair = await data.MatchedPairAsync();
        var unrelated = await data.MatchedPairAsync();
        var otherBefore = await data.ReadRoomAsync(unrelated);
        await data.Matches.Document("matchSessions", pair.User1).UpdateAsync("RoomId", unrelated.RoomId);
        Assert.True(await data.Rooms.EndTemporaryRoomAsync(pair.RoomId, pair.User1));
        Assert.False(await data.Rooms.EndTemporaryRoomAsync(pair.RoomId, pair.User1));
        var closed = await data.ReadRoomAsync(pair);
        Assert.Equal("Closed", closed.Status);
        Assert.True(closed.CleanupPending);
        Assert.Equal(unrelated.RoomId, (await data.Matches.ReadAsync<FirebaseMatchSession>("matchSessions", pair.User1)).RoomId);
        await data.Matches.AssertMissingAsync("matchSessions", pair.User2);
        var otherAfter = await data.ReadRoomAsync(unrelated);
        Assert.Equal(otherBefore.UpdatedAt, otherAfter.UpdatedAt);
        Assert.Equal("Temporary", otherAfter.Status);
        foreach (var user in new[] { unrelated.User1, unrelated.User2 })
        {
            var session = await data.Matches.ReadAsync<FirebaseMatchSession>("matchSessions", user);
            Assert.Equal("Temporary", session.Status);
            Assert.Equal(unrelated.RoomId, session.RoomId);
            Assert.Equal(unrelated.ReservationId, session.ReservationId);
        }
    }

    [Fact]
    public async Task UnmatchPersistentRoom_PreservesReadOnlyMessagesAndUnrelatedSession()
    {
        await using var data = new LifecycleTestData(fixture.Database);
        var pair = await data.MatchedPairAsync();
        var message = await data.Messages.SendMessageAsync(pair.RoomId, pair.User1, "Retained history");
        await data.MakePersistentAsync(pair);
        var other = await data.MatchedPairAsync();
        Assert.True(await data.Rooms.UnmatchRoomAsync(pair.RoomId, pair.User1));
        Assert.False(await data.Rooms.UnmatchRoomAsync(pair.RoomId, pair.User2));
        await data.AssertClosedAsync(pair, cleanupPending: false);
        var history = await data.Messages.GetMessagesAsync(pair.RoomId, pair.User2);
        Assert.Equal(message.MessageId, Assert.Single(history.Messages).MessageId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => data.Messages.SendMessageAsync(pair.RoomId, pair.User1, "No longer writable"));
        Assert.Equal("Temporary", (await data.ReadRoomAsync(other)).Status);
        Assert.Equal(other.RoomId, (await data.Matches.ReadAsync<FirebaseMatchSession>("matchSessions", other.User1)).RoomId);
        Assert.Equal(other.RoomId, (await data.Matches.ReadAsync<FirebaseMatchSession>("matchSessions", other.User2)).RoomId);
    }

    [Fact]
    public async Task Outsider_CannotSubmitDecisionOrUnmatch()
    {
        await using var data = new LifecycleTestData(fixture.Database);
        var pair = await data.MatchedPairAsync();
        var outsider = await data.Matches.NewUserAsync();
        await data.AwaitDecisionAsync(pair);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => data.Decisions.SubmitDecisionAsync(pair.RoomId, outsider, true));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => data.Rooms.UnmatchRoomAsync(pair.RoomId, outsider));
        Assert.Equal("AwaitingDecision", (await data.ReadRoomAsync(pair)).Status);
    }
}
