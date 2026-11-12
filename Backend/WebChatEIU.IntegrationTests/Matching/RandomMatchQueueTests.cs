using Google.Cloud.Firestore;
using WebChatEIU.Models.Firestore;
using WebChatEIU.Services;

namespace WebChatEIU.IntegrationTests.Matching;

[Collection("Firestore emulator")]
[Trait("Category", "Matching")]
public sealed class RandomMatchQueueTests(FirestoreEmulatorFixture fixture)
{
    [Fact]
    public async Task Join_CreatesOneWaitingEntry()
    {
        await using var data = new MatchTestData(fixture.Database);
        var user = await data.NewUserAsync();
        var before = Timestamp.FromDateTime(DateTime.UtcNow);
        Assert.Equal("Waiting", await data.Queue.JoinQueueAsync(user));
        var entry = await data.ReadAsync<FirebaseRandomMatchQueueEntry>("randomMatchQueue", user);
        Assert.Equal(user, entry.UserId);
        Assert.Equal("Waiting", entry.Status);
        Assert.Null(entry.ReservationId);
        Assert.True(entry.LastSeenAt >= before);
        Assert.Equal(entry.JoinedAt, entry.LastSeenAt);
        await data.AssertMissingAsync("matchSessions", user);
    }

    [Fact]
    public async Task Rejoin_RefreshesPresenceAndPreservesOriginalQueuePosition()
    {
        await using var data = new MatchTestData(fixture.Database);
        var user = await data.WaitingUserAsync();
        // Whole-second seed avoids comparing .NET ticks with Firestore's stored precision.
        var old = Timestamp.FromDateTime(new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        await data.Document("randomMatchQueue", user).UpdateAsync(new Dictionary<string, object>
            { ["JoinedAt"] = old, ["LastSeenAt"] = old });
        var before = Timestamp.FromDateTime(DateTime.UtcNow);
        Assert.Equal("Waiting", await data.Queue.JoinQueueAsync(user));
        var entry = await data.ReadAsync<FirebaseRandomMatchQueueEntry>("randomMatchQueue", user);
        Assert.Equal(old, entry.JoinedAt);
        Assert.True(entry.LastSeenAt >= before);
        Assert.Equal(entry.LastSeenAt, entry.UpdatedAt);
        var entries = await fixture.Database.Collection("randomMatchQueue").GetSnapshotAsync();
        Assert.Single(entries.Documents, d => d.Id == user);
    }

    [Fact]
    public async Task Heartbeat_RefreshesLastSeenAt()
    {
        await using var data = new MatchTestData(fixture.Database);
        var user = await data.WaitingUserAsync();
        var old = Timestamp.FromDateTime(DateTime.UtcNow.AddMinutes(-2));
        await data.Document("randomMatchQueue", user).UpdateAsync("LastSeenAt", old);
        var before = Timestamp.FromDateTime(DateTime.UtcNow);
        var result = await data.Queue.HeartbeatAsync(user);
        Assert.Equal("Waiting", result.Status);
        Assert.Null(result.ReservationId);
        Assert.True((await data.ReadAsync<FirebaseRandomMatchQueueEntry>("randomMatchQueue", user)).LastSeenAt >= before);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Heartbeat_DoesNotRejoinMissingOrCancelledEntry(bool previouslyJoined)
    {
        await using var data = new MatchTestData(fixture.Database);
        var user = await data.NewUserAsync();
        if (previouslyJoined)
        {
            await data.Queue.JoinQueueAsync(user);
            Assert.Equal("Cancelled", await data.Queue.CancelWaitingAsync(user));
        }
        Assert.Equal(("NotSearching", (string?)null), await data.Queue.HeartbeatAsync(user));
        Assert.Equal(("NotSearching", (string?)null), await data.Queue.FindMatchAsync(user, joinIfNeeded: false));
        await data.AssertMissingAsync("randomMatchQueue", user);
    }

    [Fact]
    public async Task CancelWaiting_RemovesEntryAndIsSafeToRepeat()
    {
        await using var data = new MatchTestData(fixture.Database);
        var user = await data.WaitingUserAsync();
        Assert.Equal("Cancelled", await data.Queue.CancelWaitingAsync(user));
        await data.AssertMissingAsync("randomMatchQueue", user);
        Assert.Equal("NotSearching", await data.Queue.CancelWaitingAsync(user));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public async Task IneligibleUser_CannotJoin(bool active, bool banned, bool deleted)
    {
        await using var data = new MatchTestData(fixture.Database);
        var user = await data.NewUserAsync(active, banned, deleted);
        await Assert.ThrowsAsync<InvalidOperationException>(() => data.Queue.JoinQueueAsync(user));
        await data.AssertMissingAsync("randomMatchQueue", user);
        await data.AssertMissingAsync("matchSessions", user);
    }

    [Fact]
    public async Task StaleWaitingEntry_IsRemovedWithoutWaitingSixtySeconds()
    {
        await using var data = new MatchTestData(fixture.Database);
        var user = await data.WaitingUserAsync();
        await data.Document("randomMatchQueue", user).UpdateAsync("LastSeenAt",
            Timestamp.FromDateTime(DateTime.UtcNow.AddSeconds(-120)));
        Assert.True(await data.Queue.TryRemoveStaleWaitingAsync(user));
        await data.AssertMissingAsync("randomMatchQueue", user);
        Assert.False(await data.Queue.TryRemoveStaleWaitingAsync(user));
    }

    [Fact]
    public async Task RecentHeartbeat_PreventsStaleRemoval()
    {
        await using var data = new MatchTestData(fixture.Database);
        var user = await data.WaitingUserAsync();
        await data.Document("randomMatchQueue", user).UpdateAsync("LastSeenAt",
            Timestamp.FromDateTime(DateTime.UtcNow.AddSeconds(-120)));
        await data.Queue.HeartbeatAsync(user);
        Assert.False(await data.Queue.TryRemoveStaleWaitingAsync(user));
        Assert.Equal("Waiting", (await data.ReadAsync<FirebaseRandomMatchQueueEntry>("randomMatchQueue", user)).Status);
    }

    [Fact]
    public async Task FindMatch_DiscoversEligibleCandidateBeyondFirstTwenty()
    {
        await using var data = new MatchTestData(fixture.Database);
        var seeker = await data.NewUserAsync();
        var joined = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        // Twenty oldest candidates are excluded by real pair-history documents.
        for (var i = 0; i < 20; i++)
        {
            var blocked = await data.WaitingUserAsync();
            await data.Document("randomMatchQueue", blocked).UpdateAsync("JoinedAt", Timestamp.FromDateTime(joined.AddSeconds(i)));
            await new FirebaseMatchHistoryService(fixture.Database).RecordMatchAsync(seeker, blocked, "prior-room");
        }
        var eligible = await data.WaitingUserAsync();
        await data.Document("randomMatchQueue", eligible).UpdateAsync("JoinedAt", Timestamp.FromDateTime(joined.AddSeconds(20)));
        var result = await data.Queue.FindMatchAsync(seeker);
        Assert.Equal("Reserved", result.Status);
        Assert.NotNull(result.ReservationId);
        var reservation = await data.ReadAsync<FirebaseMatchReservation>("matchReservations", result.ReservationId);
        Assert.Equal(seeker, reservation.User1Id);
        Assert.Equal(eligible, reservation.User2Id);
        Assert.Single(await data.OwnedAsync("matchReservations"));
    }
}
