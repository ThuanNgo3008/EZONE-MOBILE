using WebChatEIU.Models.Firestore;

namespace WebChatEIU.IntegrationTests.Matching;

[Collection("Firestore emulator")]
[Trait("Category", "Matching")]
public sealed class MatchingConcurrencyTests(FirestoreEmulatorFixture fixture)
{
    // Three independently seeded trials per race; no assumption about which request wins.
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task CompetingUsers_OnlyOneCanReserveSameCandidate(int trial)
    {
        Assert.InRange(trial, 1, 3);
        await using var data = new MatchTestData(fixture.Database);
        var candidate = await data.WaitingUserAsync();
        var first = await data.WaitingUserAsync();
        var second = await data.WaitingUserAsync();
        var results = await MatchTestData.TogetherAsync(
            () => data.Queue.TryReserveCandidateAsync(first, candidate),
            () => data.Queue.TryReserveCandidateAsync(second, candidate));
        var reservationId = Assert.Single(results, id => id != null);
        Assert.NotNull(reservationId);
        var reservation = Assert.Single(await data.OwnedAsync("matchReservations"))
            .ConvertTo<FirebaseMatchReservation>();
        Assert.Equal(reservationId, reservation.ReservationId);
        Assert.Equal("PendingReady", reservation.Status);
        Assert.Equal(candidate, reservation.User2Id);
        var winner = reservation.User1Id;
        Assert.Contains(winner, new[] { first, second });
        var loser = winner == first ? second : first;
        await data.AssertPendingSessionAsync(winner, reservationId);
        await data.AssertPendingSessionAsync(candidate, reservationId);
        await data.AssertMissingAsync("matchSessions", loser);
        var waiting = await data.ReadAsync<FirebaseRandomMatchQueueEntry>("randomMatchQueue", loser);
        Assert.Equal("Waiting", waiting.Status);
        Assert.Null(waiting.ReservationId);
        Assert.Empty(await data.OwnedAsync("chatRooms"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task ConcurrentReady_CreatesExactlyOneRoom(int trial)
    {
        Assert.InRange(trial, 1, 3);
        await using var data = new MatchTestData(fixture.Database);
        var (first, second, id) = await data.ReservedPairAsync();
        var results = await MatchTestData.TogetherAsync(
            () => data.Reservations.SubmitReadyAsync(id, first),
            () => data.Reservations.SubmitReadyAsync(id, second));
        Assert.Contains(results, r => r.Status == "Matched");
        Assert.All(results, r => Assert.Contains(r.Status, new[] { "Waiting", "Matched" }));
        var reservation = await data.ReadAsync<FirebaseMatchReservation>("matchReservations", id);
        Assert.NotNull(reservation.RoomId);
        Assert.All(results.Where(r => r.Status == "Matched"), r => Assert.Equal(reservation.RoomId, r.RoomId));
        Assert.Equal(("Matched", reservation.RoomId), await data.Reservations.SubmitReadyAsync(id, first));
        await data.AssertMatchedAsync(first, second, id, reservation.RoomId);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task ReadyVersusCancel_LeavesCoherentTerminalState(int trial)
    {
        Assert.InRange(trial, 1, 3);
        await using var data = new MatchTestData(fixture.Database);
        var (first, second, id) = await data.ReservedPairAsync();
        await data.Reservations.SubmitReadyAsync(id, first);
        async Task<string> Ready()
        {
            try { return (await data.Reservations.SubmitReadyAsync(id, second)).Status; }
            catch (InvalidOperationException ex)
            {
                Assert.Equal("Reservation is no longer available.", ex.Message);
                return "CancelledFirst";
            }
        }
        var results = await MatchTestData.TogetherAsync(Ready, () => data.Queue.CancelWaitingAsync(second));
        var reservation = await data.ReadAsync<FirebaseMatchReservation>("matchReservations", id);
        Assert.Single(await data.OwnedAsync("matchReservations"));
        if (reservation.Status == "Matched")
        {
            Assert.Equal("Matched", results[0]);
            // Ready removes the queue; Cancel may observe no entry, or a matched reservation.
            Assert.Contains(results[1], new[] { "NotSearching", "Matched" });
            Assert.NotNull(reservation.RoomId);
            await data.AssertMatchedAsync(first, second, id, reservation.RoomId);
        }
        else
        {
            Assert.Equal("Cancelled", reservation.Status);
            Assert.Equal("CancelledFirst", results[0]);
            Assert.Equal("Cancelled", results[1]);
            Assert.Null(reservation.RoomId);
            await data.AssertMissingAsync("matchSessions", first);
            await data.AssertMissingAsync("matchSessions", second);
            await data.AssertMissingAsync("randomMatchQueue", second);
            var requeued = await data.ReadAsync<FirebaseRandomMatchQueueEntry>("randomMatchQueue", first);
            Assert.Equal("Waiting", requeued.Status);
            Assert.Null(requeued.ReservationId);
            Assert.Empty(await data.OwnedAsync("chatRooms"));
            Assert.Empty(await data.OwnedAsync("matchHistory"));
        }
    }
}
