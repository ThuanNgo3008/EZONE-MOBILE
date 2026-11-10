using Google.Cloud.Firestore;
using WebChatEIU.Models.Firestore;
using WebChatEIU.Services;

namespace WebChatEIU.IntegrationTests.Matching;

[Collection("Firestore emulator")]
[Trait("Category", "Matching")]
public sealed class MatchReservationTests(FirestoreEmulatorFixture fixture)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reservation_CreatesPendingReadyAndOwnsBothSessions(bool directCreation)
    {
        await using var data = new MatchTestData(fixture.Database);
        var first = await data.WaitingUserAsync();
        var second = await data.WaitingUserAsync();
        var id = directCreation
            ? await data.Reservations.CreateReservationAsync(first, second, "Random")
            : await data.Queue.TryReserveCandidateAsync(first, second);
        Assert.NotNull(id);
        var reservation = await data.ReadAsync<FirebaseMatchReservation>("matchReservations", id);
        Assert.Equal("PendingReady", reservation.Status);
        Assert.Equal(first, reservation.User1Id);
        Assert.Equal(second, reservation.User2Id);
        Assert.Equal("Random", reservation.MatchMode);
        Assert.False(reservation.User1Ready || reservation.User2Ready);
        Assert.Null(reservation.RoomId);
        Assert.Equal(TimeSpan.FromSeconds(30), reservation.ReadyExpiresAt.ToDateTime() - reservation.CreatedAt.ToDateTime());
        foreach (var user in new[] { first, second })
        {
            var session = await data.ReadAsync<FirebaseMatchSession>("matchSessions", user);
            Assert.Equal(id, session.ReservationId);
            Assert.Equal("PendingReady", session.Status);
            Assert.Equal(reservation.ReadyExpiresAt, session.ExpiresAt);
            Assert.Null(session.RoomId);
            if (!directCreation) await data.AssertPendingSessionAsync(user, id);
        }
        Assert.Single(await data.OwnedAsync("matchReservations"));
        Assert.Empty(await data.OwnedAsync("chatRooms"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PriorPairHistory_PreventsAnotherReservation(bool directCreation)
    {
        await using var data = new MatchTestData(fixture.Database);
        var first = await data.WaitingUserAsync();
        var second = await data.WaitingUserAsync();
        Assert.True(await new FirebaseMatchHistoryService(fixture.Database).RecordMatchAsync(first, second, "prior-room"));
        if (directCreation)
            await Assert.ThrowsAsync<InvalidOperationException>(() => data.Reservations.CreateReservationAsync(second, first, "Random"));
        else
            Assert.Null(await data.Queue.TryReserveCandidateAsync(second, first));
        Assert.Empty(await data.OwnedAsync("matchReservations"));
        await data.AssertMissingAsync("matchSessions", first);
        await data.AssertMissingAsync("matchSessions", second);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BusyUser_CannotAcquireSecondActiveReservation(bool directCreation)
    {
        await using var data = new MatchTestData(fixture.Database);
        var (first, second, original) = await data.ReservedPairAsync();
        var third = await data.WaitingUserAsync();
        if (directCreation)
            await Assert.ThrowsAsync<InvalidOperationException>(() => data.Reservations.CreateReservationAsync(first, third, "Random"));
        else
        {
            // Even an inconsistent stale Waiting entry cannot bypass the session lock.
            await data.Document("randomMatchQueue", first).UpdateAsync("Status", "Waiting");
            Assert.Null(await data.Queue.TryReserveCandidateAsync(first, third));
            await data.Document("randomMatchQueue", first).UpdateAsync("Status", "Reserved");
        }
        await data.AssertPendingSessionAsync(first, original);
        await data.AssertPendingSessionAsync(second, original);
        await data.AssertMissingAsync("matchSessions", third);
        Assert.Single(await data.OwnedAsync("matchReservations"));
    }

    [Fact]
    public async Task FirstReady_DoesNotCreateRoomOrHistory()
    {
        await using var data = new MatchTestData(fixture.Database);
        var (first, second, id) = await data.ReservedPairAsync();
        Assert.Equal(("Waiting", (string?)null), await data.Reservations.SubmitReadyAsync(id, first));
        var reservation = await data.ReadAsync<FirebaseMatchReservation>("matchReservations", id);
        Assert.True(reservation.User1Ready);
        Assert.False(reservation.User2Ready);
        Assert.Equal("PendingReady", reservation.Status);
        Assert.Null(reservation.RoomId);
        Assert.Empty(await data.OwnedAsync("chatRooms"));
        Assert.Empty(await data.OwnedAsync("matchHistory"));
        await data.AssertPendingSessionAsync(first, id);
        await data.AssertPendingSessionAsync(second, id);
    }

    [Fact]
    public async Task BothReady_AndRepeatedReady_CreateOnlyOneRoomAndHistory()
    {
        await using var data = new MatchTestData(fixture.Database);
        var (first, second, id) = await data.ReservedPairAsync();
        await data.Reservations.SubmitReadyAsync(id, first);
        Assert.Equal(("Waiting", (string?)null), await data.Reservations.SubmitReadyAsync(id, first));
        var result = await data.Reservations.SubmitReadyAsync(id, second);
        Assert.Equal("Matched", result.Status);
        Assert.NotNull(result.RoomId);
        Assert.Equal(result, await data.Reservations.SubmitReadyAsync(id, first));
        Assert.Equal(result, await data.Reservations.SubmitReadyAsync(id, second));
        await data.AssertMatchedAsync(first, second, id, result.RoomId);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CancelReserved_ReleasesSessionsAndRequeuesOnlyOtherReadyUser(bool cancelFirst, bool otherReady)
    {
        await using var data = new MatchTestData(fixture.Database);
        var (first, second, id) = await data.ReservedPairAsync();
        var caller = cancelFirst ? first : second;
        var other = cancelFirst ? second : first;
        if (otherReady) await data.Reservations.SubmitReadyAsync(id, other);
        Assert.Equal("Cancelled", await data.Queue.CancelWaitingAsync(caller));
        Assert.Equal("Cancelled", (await data.ReadAsync<FirebaseMatchReservation>("matchReservations", id)).Status);
        await data.AssertMissingAsync("randomMatchQueue", caller);
        await AssertRequeueAsync(data, other, otherReady);
        await data.AssertMissingAsync("matchSessions", first);
        await data.AssertMissingAsync("matchSessions", second);
        Assert.Empty(await data.OwnedAsync("chatRooms"));
        Assert.Empty(await data.OwnedAsync("matchHistory"));
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    public async Task Expiration_RequeuesPreviouslyReadyParticipantsWithoutCreatingRooms(bool expireViaReady, int readyParticipant)
    {
        await using var data = new MatchTestData(fixture.Database);
        var (first, second, id) = await data.ReservedPairAsync();
        if (readyParticipant != 0)
            await data.Reservations.SubmitReadyAsync(id, readyParticipant == 1 ? first : second);
        await data.Document("matchReservations", id).UpdateAsync("ReadyExpiresAt",
            Timestamp.FromDateTime(DateTime.UtcNow.AddMinutes(-2)));
        if (expireViaReady)
            Assert.Equal(("Expired", (string?)null), await data.Reservations.SubmitReadyAsync(id, second));
        else
            Assert.True(await data.Reservations.TryExpireReservationAsync(id));
        Assert.Equal("Expired", (await data.ReadAsync<FirebaseMatchReservation>("matchReservations", id)).Status);
        await AssertRequeueAsync(data, first, readyParticipant == 1);
        await AssertRequeueAsync(data, second, readyParticipant == 2);
        await data.AssertMissingAsync("matchSessions", first);
        await data.AssertMissingAsync("matchSessions", second);
        Assert.Empty(await data.OwnedAsync("chatRooms"));
        Assert.Empty(await data.OwnedAsync("matchHistory"));
        Assert.False(await data.Reservations.TryExpireReservationAsync(id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => data.Reservations.SubmitReadyAsync(id, first));
    }

    [Fact]
    public async Task ExpiringOldReservation_DoesNotDeleteReplacementSession()
    {
        await using var data = new MatchTestData(fixture.Database);
        var (first, second, id) = await data.ReservedPairAsync();
        var replacement = "replacement-" + Guid.NewGuid().ToString("N");
        await data.Document("matchSessions", first).UpdateAsync("ReservationId", replacement);
        await data.Document("matchReservations", id).UpdateAsync("ReadyExpiresAt",
            Timestamp.FromDateTime(DateTime.UtcNow.AddMinutes(-2)));
        Assert.True(await data.Reservations.TryExpireReservationAsync(id));
        Assert.Equal(replacement, (await data.ReadAsync<FirebaseMatchSession>("matchSessions", first)).ReservationId);
        await data.AssertMissingAsync("matchSessions", second);
    }

    [Fact]
    public async Task UnexpiredReservation_IsNotExpiredEarly()
    {
        await using var data = new MatchTestData(fixture.Database);
        var (first, second, id) = await data.ReservedPairAsync();
        Assert.False(await data.Reservations.TryExpireReservationAsync(id));
        await data.AssertPendingSessionAsync(first, id);
        await data.AssertPendingSessionAsync(second, id);
    }

    [Fact]
    public async Task CancellingOldReservation_DoesNotDeleteReplacementSession()
    {
        await using var data = new MatchTestData(fixture.Database);
        var (first, second, id) = await data.ReservedPairAsync();
        var replacement = "replacement-" + Guid.NewGuid().ToString("N");
        await data.Document("matchSessions", first).UpdateAsync("ReservationId", replacement);
        Assert.Equal("Cancelled", await data.Queue.CancelWaitingAsync(second));
        Assert.Equal(replacement, (await data.ReadAsync<FirebaseMatchSession>("matchSessions", first)).ReservationId);
        await data.AssertMissingAsync("matchSessions", second);
        Assert.Equal("Cancelled", (await data.ReadAsync<FirebaseMatchReservation>("matchReservations", id)).Status);
        Assert.Empty(await data.OwnedAsync("chatRooms"));
    }

    [Fact]
    public async Task ReadyWithoutBothOwnedSessions_CannotCreateRoomOrDeleteReplacement()
    {
        await using var data = new MatchTestData(fixture.Database);
        var (first, second, id) = await data.ReservedPairAsync();
        await data.Reservations.SubmitReadyAsync(id, first);
        var replacement = "replacement-" + Guid.NewGuid().ToString("N");
        await data.Document("matchSessions", first).UpdateAsync("ReservationId", replacement);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => data.Reservations.SubmitReadyAsync(id, second));
        Assert.Equal("Reservation no longer owns both match sessions.", error.Message);
        Assert.Equal(replacement, (await data.ReadAsync<FirebaseMatchSession>("matchSessions", first)).ReservationId);
        await data.AssertPendingSessionAsync(second, id);
        Assert.Empty(await data.OwnedAsync("chatRooms"));
        Assert.Empty(await data.OwnedAsync("matchHistory"));
    }

    [Theory]
    [InlineData("Active", false)]
    [InlineData("Active", true)]
    [InlineData("Temporary", false)]
    [InlineData("Temporary", true)]
    public async Task HistoricalRoomSessions_DoNotBlockNewReservations(string status, bool directCreation)
    {
        await using var data = new MatchTestData(fixture.Database);
        var first = await data.NewUserAsync();
        var second = await data.NewUserAsync();
        await data.Document("matchSessions", first).CreateAsync(new FirebaseMatchSession
        {
            UserId = first, Status = status, ReservationId = "historical-reservation",
            RoomId = "historical-room", ExpiresAt = Timestamp.FromDateTime(DateTime.UtcNow.AddHours(1))
        });
        Assert.Equal("Waiting", await data.Queue.JoinQueueAsync(first));
        await data.Queue.JoinQueueAsync(second);
        var id = directCreation
            ? await data.Reservations.CreateReservationAsync(first, second, "Random")
            : await data.Queue.TryReserveCandidateAsync(first, second);
        Assert.NotNull(id);
        foreach (var user in new[] { first, second })
        {
            var session = await data.ReadAsync<FirebaseMatchSession>("matchSessions", user);
            Assert.Equal("PendingReady", session.Status);
            Assert.Equal(id, session.ReservationId);
            Assert.Null(session.RoomId);
        }
    }

    private static async Task AssertRequeueAsync(MatchTestData data, string user, bool ready)
    {
        if (!ready) { await data.AssertMissingAsync("randomMatchQueue", user); return; }
        var queue = await data.ReadAsync<FirebaseRandomMatchQueueEntry>("randomMatchQueue", user);
        Assert.Equal("Waiting", queue.Status);
        Assert.Null(queue.ReservationId);
    }
}
