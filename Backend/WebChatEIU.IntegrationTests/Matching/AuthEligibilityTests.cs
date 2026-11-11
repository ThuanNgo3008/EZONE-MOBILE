using FirebaseAdmin.Auth;
using Google.Cloud.Firestore;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using WebChatEIU.Controllers;
using WebChatEIU.Models.Firestore;

namespace WebChatEIU.IntegrationTests.Matching;

[Collection("Firestore emulator")]
[Trait("Category", "Matching")]
public sealed class AuthEligibilityTests(FirestoreEmulatorFixture fixture)
{
    [Theory]
    [InlineData("Eligible", true)]
    [InlineData("Unverified", false)]
    [InlineData("Disabled", false)]
    [InlineData("MissingAuth", false)]
    [InlineData("Inactive", false)]
    [InlineData("Banned", false)]
    [InlineData("Deleted", false)]
    [InlineData("MissingProfile", false)]
    public async Task Join_RequiresEligibleAuthAndProfile(string state, bool eligible)
    {
        await using var data = new MatchTestData(fixture.Database);
        var user = await data.NewUserAsync(active: state != "Inactive", banned: state == "Banned", deleted: state == "Deleted");
        if (state == "MissingProfile") await data.Document("users", user).DeleteAsync();
        if (state is "Unverified" or "Disabled" or "MissingAuth") await MakeIneligibleAsync(data, user, state);
        if (eligible)
        {
            Assert.Equal("Waiting", await data.Queue.JoinQueueAsync(user));
            Assert.Equal("Waiting", (await data.ReadAsync<FirebaseRandomMatchQueueEntry>("randomMatchQueue", user)).Status);
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => data.Queue.JoinQueueAsync(user));
            await data.AssertMissingAsync("randomMatchQueue", user);
        }
        await data.AssertMissingAsync("matchSessions", user);
        Assert.Empty(await data.OwnedAsync("matchReservations"));
    }

    [Theory]
    [InlineData("Unverified", false)]
    [InlineData("Unverified", true)]
    [InlineData("Disabled", false)]
    [InlineData("Disabled", true)]
    [InlineData("MissingAuth", false)]
    [InlineData("MissingAuth", true)]
    public async Task AlreadyWaiting_IneligibleRequesterOrCandidateCannotBeReserved(string state, bool candidate)
    {
        await using var data = new MatchTestData(fixture.Database);
        var first = await data.WaitingUserAsync();
        var second = await data.WaitingUserAsync();
        await MakeIneligibleAsync(data, candidate ? second : first, state);
        if (!candidate)
            await Assert.ThrowsAsync<InvalidOperationException>(() => data.Queue.JoinQueueAsync(first));
        Assert.Null(await data.Queue.TryReserveCandidateAsync(first, second));
        // Heartbeat-driven search also goes through the guarded candidate path.
        var result = await data.Queue.FindMatchAsync(first, joinIfNeeded: false);
        Assert.Equal("Waiting", result.Status);
        Assert.Null(result.ReservationId);
        foreach (var user in new[] { first, second })
        {
            await data.AssertMissingAsync("matchSessions", user);
            var waiting = await data.ReadAsync<FirebaseRandomMatchQueueEntry>("randomMatchQueue", user);
            Assert.Equal("Waiting", waiting.Status);
            Assert.Null(waiting.ReservationId);
        }
        Assert.Empty(await data.OwnedAsync("matchReservations"));
        Assert.Empty(await data.OwnedAsync("chatRooms"));
        Assert.Empty(await data.OwnedAsync("matchHistory"));
    }

    [Theory]
    [InlineData("Unverified", false)]
    [InlineData("Unverified", true)]
    [InlineData("Disabled", false)]
    [InlineData("Disabled", true)]
    [InlineData("MissingAuth", false)]
    [InlineData("MissingAuth", true)]
    public async Task DirectReservation_CannotBypassAuthEligibility(string state, bool secondUser)
    {
        await using var data = new MatchTestData(fixture.Database);
        var first = await data.NewUserAsync();
        var second = await data.NewUserAsync();
        await MakeIneligibleAsync(data, secondUser ? second : first, state);
        await Assert.ThrowsAsync<InvalidOperationException>(() => data.Reservations.CreateReservationAsync(first, second, "Random"));
        await data.AssertMissingAsync("matchSessions", first);
        await data.AssertMissingAsync("matchSessions", second);
        Assert.Empty(await data.OwnedAsync("matchReservations"));
        Assert.Empty(await data.OwnedAsync("chatRooms"));
    }

    [Theory]
    [InlineData("Unverified", false)]
    [InlineData("Unverified", true)]
    [InlineData("Disabled", false)]
    [InlineData("Disabled", true)]
    [InlineData("MissingAuth", false)]
    [InlineData("MissingAuth", true)]
    public async Task Ready_RechecksBothAccounts_AndLeavesOwnedReservationSafeToCancel(string state, bool secondUser)
    {
        await using var data = new MatchTestData(fixture.Database);
        var (first, second, reservation) = await data.ReservedPairAsync();
        await data.Reservations.SubmitReadyAsync(reservation, first);
        await MakeIneligibleAsync(data, secondUser ? second : first, state);
        await Assert.ThrowsAsync<InvalidOperationException>(() => data.Reservations.SubmitReadyAsync(reservation, second));
        var pending = await data.ReadAsync<FirebaseMatchReservation>("matchReservations", reservation);
        Assert.Equal("PendingReady", pending.Status);
        Assert.True(pending.User1Ready);
        Assert.False(pending.User2Ready);
        Assert.Null(pending.RoomId);
        await data.AssertPendingSessionAsync(first, reservation);
        await data.AssertPendingSessionAsync(second, reservation);
        Assert.Empty(await data.OwnedAsync("chatRooms"));
        Assert.Empty(await data.OwnedAsync("matchHistory"));
        Assert.Equal("Cancelled", await data.Queue.CancelWaitingAsync(second));
        await data.AssertMissingAsync("matchSessions", first);
        await data.AssertMissingAsync("matchSessions", second);
    }

    [Fact]
    public async Task RestoredEligibility_AllowsExistingWaitingUsersToMatchNormally()
    {
        await using var data = new MatchTestData(fixture.Database);
        var first = await data.WaitingUserAsync();
        var second = await data.WaitingUserAsync();
        await MakeIneligibleAsync(data, second, "Unverified");
        Assert.Null(await data.Queue.TryReserveCandidateAsync(first, second));
        await data.Auth.UpdateUserAsync(new UserRecordArgs { Uid = second, EmailVerified = true });
        var reservation = await data.Queue.TryReserveCandidateAsync(first, second);
        Assert.NotNull(reservation);
        await data.Reservations.SubmitReadyAsync(reservation, first);
        var matched = await data.Reservations.SubmitReadyAsync(reservation, second);
        Assert.Equal("Matched", matched.Status);
        Assert.NotNull(matched.RoomId);
        await data.AssertMatchedAsync(first, second, reservation, matched.RoomId);
    }

    private static Task MakeIneligibleAsync(MatchTestData data, string user, string state) => state switch
    {
        "MissingAuth" => data.Auth.DeleteUserAsync(user),
        "Disabled" => data.Auth.UpdateUserAsync(new UserRecordArgs { Uid = user, Disabled = true }),
        "Unverified" => data.Auth.UpdateUserAsync(new UserRecordArgs { Uid = user, EmailVerified = false }),
        _ => throw new ArgumentException("Unknown fixture state.")
    };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CandidateScan_ChecksRequesterOnlyOnceAndSkipsIneligibleCandidates(bool joinIfNeeded)
    {
        await using var data = new MatchTestData(fixture.Database);
        var requester = await data.WaitingUserAsync();
        var disabled = await data.WaitingUserAsync();
        var unverified = await data.WaitingUserAsync();
        var eligible = await data.WaitingUserAsync();
        var oldest = DateTime.UnixEpoch;
        foreach (var candidate in new[] { disabled, unverified, eligible })
        {
            await data.Document("randomMatchQueue", candidate).UpdateAsync("JoinedAt", Timestamp.FromDateTime(oldest));
            oldest = oldest.AddSeconds(1);
        }
        await MakeIneligibleAsync(data, disabled, "Disabled");
        await MakeIneligibleAsync(data, unverified, "Unverified");
        var before = data.AuthLookupCount;
        var result = await data.Queue.FindMatchAsync(requester, joinIfNeeded);
        Assert.Equal("Reserved", result.Status);
        Assert.NotNull(result.ReservationId);
        Assert.Equal(4, data.AuthLookupCount - before);
        var reservation = await data.ReadAsync<FirebaseMatchReservation>("matchReservations", result.ReservationId);
        Assert.Equal(requester, reservation.User1Id);
        Assert.Equal(eligible, reservation.User2Id);
        await data.AssertMissingAsync("matchSessions", disabled);
        await data.AssertMissingAsync("matchSessions", unverified);
    }

    [Fact]
    public async Task HeartbeatAction_ReportsLookupFailureWithoutCreatingReservation()
    {
        await using var data = new MatchTestData(fixture.Database);
        var requester = await data.WaitingUserAsync();
        var candidate = await data.WaitingUserAsync();
        var controller = new FirebaseRandomMatchController(data.Queue)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("user_id", requester)], "fixture"))
                }
            }
        };
        data.RejectAuthLookups = true;
        Assert.IsType<ConflictObjectResult>(await controller.Heartbeat());
        Assert.Empty(await data.OwnedAsync("matchReservations"));
        await data.AssertMissingAsync("matchSessions", requester);
        await data.AssertMissingAsync("matchSessions", candidate);
        Assert.Equal("Waiting", (await data.ReadAsync<FirebaseRandomMatchQueueEntry>("randomMatchQueue", requester)).Status);
    }

    [Theory]
    [InlineData("Join")]
    [InlineData("Reserve")]
    [InlineData("CreateReservation")]
    [InlineData("Ready")]
    public async Task AuthLookupFailure_FailsClosedWithoutFirestoreMutations(string operation)
    {
        await using var data = new MatchTestData(fixture.Database);
        var first = await data.WaitingUserAsync();
        var second = await data.WaitingUserAsync();
        string? reservation = null;
        if (operation == "Ready")
        {
            reservation = await data.Queue.TryReserveCandidateAsync(first, second);
            Assert.NotNull(reservation);
            await data.Reservations.SubmitReadyAsync(reservation, first);
        }
        var before = await data.ReadAsync<FirebaseRandomMatchQueueEntry>("randomMatchQueue", first);
        data.RejectAuthLookups = true;
        Func<Task> attempt = operation switch
        {
            "Join" => () => data.Queue.JoinQueueAsync(first),
            "Reserve" => () => data.Queue.TryReserveCandidateAsync(first, second),
            "CreateReservation" => () => data.Reservations.CreateReservationAsync(first, second, "Random"),
            "Ready" => () => data.Reservations.SubmitReadyAsync(reservation!, second),
            _ => throw new ArgumentException("Unknown fixture operation.")
        };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(attempt);
        Assert.Equal("Unable to verify matching eligibility.", error.Message);
        Assert.IsType<FirebaseAuthException>(error.InnerException);
        var after = await data.ReadAsync<FirebaseRandomMatchQueueEntry>("randomMatchQueue", first);
        Assert.Equal(before.Status, after.Status);
        Assert.Equal(before.ReservationId, after.ReservationId);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
        Assert.Empty(await data.OwnedAsync("chatRooms"));
        Assert.Empty(await data.OwnedAsync("matchHistory"));
        if (reservation is null)
        {
            Assert.Empty(await data.OwnedAsync("matchReservations"));
            await data.AssertMissingAsync("matchSessions", first);
            await data.AssertMissingAsync("matchSessions", second);
        }
        else
        {
            var pending = await data.ReadAsync<FirebaseMatchReservation>("matchReservations", reservation);
            Assert.Equal("PendingReady", pending.Status);
            Assert.True(pending.User1Ready);
            Assert.False(pending.User2Ready);
            await data.AssertPendingSessionAsync(first, reservation);
            await data.AssertPendingSessionAsync(second, reservation);
        }
    }
}
