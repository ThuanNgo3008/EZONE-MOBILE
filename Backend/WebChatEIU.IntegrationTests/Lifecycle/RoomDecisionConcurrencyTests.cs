using Google.Cloud.Firestore;
using WebChatEIU.IntegrationTests.Matching;
using WebChatEIU.Models.Firestore;

namespace WebChatEIU.IntegrationTests.Lifecycle;

[Collection("Firestore emulator")]
[Trait("Category", "Lifecycle")]
public sealed class RoomDecisionConcurrencyTests(FirestoreEmulatorFixture fixture)
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task SimultaneousYes_ActivateExactlyOneRoom(int trial)
    {
        Assert.InRange(trial, 1, 3);
        await using var data = new LifecycleTestData(fixture.Database);
        var pair = await data.MatchedPairAsync();
        await data.AwaitDecisionAsync(pair);
        var results = await MatchTestData.TogetherAsync(
            () => data.Decisions.SubmitDecisionAsync(pair.RoomId, pair.User1, true),
            () => data.Decisions.SubmitDecisionAsync(pair.RoomId, pair.User2, true));
        Assert.Equal(1, results.Count(r => r == "Active"));
        Assert.Equal(1, results.Count(r => r == "Waiting"));
        var room = await data.ReadRoomAsync(pair);
        Assert.Equal("Active", room.Status);
        Assert.NotNull(room.BecamePersistentAt);
        Assert.Null(room.ClosedAt);
        Assert.False(room.CleanupPending);
        var decision = await data.Matches.ReadAsync<FirebaseRoomDecision>("roomDecisions", pair.RoomId);
        Assert.True(decision.User1Continue);
        Assert.True(decision.User2Continue);
        await data.AssertSessionsReleasedAsync(pair);
        Assert.Single(await data.Matches.OwnedAsync("chatRooms"));
        Assert.Single(await data.Matches.OwnedAsync("matchHistory"));
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(false, 3)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    [InlineData(true, 3)]
    public async Task DecisionVersusExpiration_RespectsSeededDeadlineAndTerminalState(bool expired, int trial)
    {
        Assert.InRange(trial, 1, 3);
        await using var data = new LifecycleTestData(fixture.Database);
        var pair = await data.MatchedPairAsync();
        await data.AwaitDecisionAsync(pair);
        await data.Decisions.SubmitDecisionAsync(pair.RoomId, pair.User1, true);
        // Exercise both sides of the deadline without racing the wall clock itself.
        await data.Room(pair.RoomId).UpdateAsync("DecisionEndsAt",
            Timestamp.FromDateTime(DateTime.UtcNow.AddMinutes(expired ? -2 : 5)));
        async Task<string> Decision()
        {
            try { return await data.Decisions.SubmitDecisionAsync(pair.RoomId, pair.User2, true); }
            catch (InvalidOperationException ex)
            {
                Assert.True(expired);
                Assert.Equal("Room is not awaiting decision.", ex.Message);
                return "AlreadyClosed";
            }
        }
        var results = await MatchTestData.TogetherAsync(Decision, async () =>
            (await data.Rooms.TryCloseExpiredDecisionAsync(pair.RoomId)) ? "Expired" : "NoChange");
        if (expired)
        {
            Assert.Contains(results[0], new[] { "Closed", "AlreadyClosed" });
            Assert.Contains(results[1], new[] { "Expired", "NoChange" });
            await data.AssertClosedAsync(pair);
            Assert.Null((await data.ReadRoomAsync(pair)).BecamePersistentAt);
        }
        else
        {
            Assert.Equal(new[] { "Active", "NoChange" }, results);
            var before = await data.ReadRoomAsync(pair);
            Assert.Equal("Active", before.Status);
            Assert.NotNull(before.BecamePersistentAt);
            // Simulate the old decision deadline eventually passing after activation.
            await data.Room(pair.RoomId).UpdateAsync("DecisionEndsAt",
                Timestamp.FromDateTime(DateTime.UtcNow.AddMinutes(-2)));
            Assert.False(await data.Rooms.TryCloseExpiredDecisionAsync(pair.RoomId));
            var after = await data.ReadRoomAsync(pair);
            Assert.Equal("Active", after.Status);
            Assert.Equal(before.BecamePersistentAt, after.BecamePersistentAt);
            Assert.Null(after.ClosedAt);
            Assert.False(after.CleanupPending);
            await data.AssertSessionsReleasedAsync(pair);
        }
        Assert.Single(await data.Matches.OwnedAsync("chatRooms"));
        Assert.Single(await data.Matches.OwnedAsync("matchHistory"));
    }
}
