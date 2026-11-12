using Google.Cloud.Firestore;
using WebChatEIU.IntegrationTests.Matching;
using WebChatEIU.Models.Firestore;

namespace WebChatEIU.IntegrationTests.Lifecycle;

[Collection("Firestore emulator")]
[Trait("Category", "Lifecycle")]
public sealed class ChatRoomConcurrencyTests(FirestoreEmulatorFixture fixture)
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task ConcurrentReadyRetries_ReturnOnePersistentRoomWithoutReacquiringSessions(int trial)
    {
        Assert.InRange(trial, 1, 3);
        await using var data = new LifecycleTestData(fixture.Database);
        var pair = await data.MatchedPairAsync();
        var results = await MatchTestData.TogetherAsync(
            () => data.Matches.Reservations.SubmitReadyAsync(pair.ReservationId, pair.User1),
            () => data.Matches.Reservations.SubmitReadyAsync(pair.ReservationId, pair.User2));
        Assert.All(results, result => Assert.Equal(("Matched", pair.RoomId), result));
        await data.Matches.AssertMatchedAsync(pair.User1, pair.User2, pair.ReservationId, pair.RoomId);
        await data.Matches.AssertMissingAsync("roomDecisions", pair.RoomId);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(false, 3)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    [InlineData(true, 3)]
    public async Task ReadyVersusReservationExpiration_RespectsDeadlineAndCannotCloseActiveRoom(bool expired, int trial)
    {
        Assert.InRange(trial, 1, 3);
        await using var data = new MatchTestData(fixture.Database);
        var (first, second, id) = await data.ReservedPairAsync();
        await data.Reservations.SubmitReadyAsync(id, first);
        var deadline = Timestamp.FromDateTime(DateTime.UtcNow.AddMinutes(expired ? -1 : 5));
        await data.Document("matchReservations", id).UpdateAsync("ReadyExpiresAt", deadline);
        foreach (var user in new[] { first, second })
            await data.Document("matchSessions", user).UpdateAsync("ExpiresAt", deadline);
        async Task<string> Ready()
        {
            try { return (await data.Reservations.SubmitReadyAsync(id, second)).Status; }
            catch (InvalidOperationException ex)
            {
                Assert.True(expired);
                Assert.Equal("Reservation is no longer available.", ex.Message);
                return "AlreadyExpired";
            }
        }
        var results = await MatchTestData.TogetherAsync(Ready, async () =>
            await data.Reservations.TryExpireReservationAsync(id) ? "Expired" : "NoChange");
        if (expired)
        {
            Assert.Contains(results[0], new[] { "Expired", "AlreadyExpired" });
            Assert.Contains(results[1], new[] { "Expired", "NoChange" });
            Assert.Equal("Expired", (await data.ReadAsync<FirebaseMatchReservation>("matchReservations", id)).Status);
            Assert.Empty(await data.OwnedAsync("chatRooms"));
            Assert.Empty(await data.OwnedAsync("matchHistory"));
            Assert.Equal("Waiting", (await data.ReadAsync<FirebaseRandomMatchQueueEntry>("randomMatchQueue", first)).Status);
            await data.AssertMissingAsync("randomMatchQueue", second);
            await data.AssertMissingAsync("matchSessions", first);
            await data.AssertMissingAsync("matchSessions", second);
        }
        else
        {
            Assert.Equal(new[] { "Matched", "NoChange" }, results);
            var reservation = await data.ReadAsync<FirebaseMatchReservation>("matchReservations", id);
            Assert.NotNull(reservation.RoomId);
            await data.AssertMatchedAsync(first, second, id, reservation.RoomId);
            // A stale expiration request after matching must not close the room.
            await data.Document("matchReservations", id).UpdateAsync("ReadyExpiresAt",
                Timestamp.FromDateTime(DateTime.UtcNow.AddMinutes(-1)));
            Assert.False(await data.Reservations.TryExpireReservationAsync(id));
            Assert.Equal("Active", (await data.ReadAsync<FirebaseChatRoom>("chatRooms", reservation.RoomId)).Status);
        }
    }
}
