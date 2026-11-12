using Google.Cloud.Firestore;
using Grpc.Core;
using WebChatEIU.IntegrationTests.Lifecycle;
using WebChatEIU.IntegrationTests.Matching;
using WebChatEIU.Models.Firestore;

namespace WebChatEIU.IntegrationTests.Workers;

[Collection("Firestore emulator")]
[Trait("Category", "BackgroundWorker")]
public sealed class BackgroundWorkerTests(FirestoreEmulatorFixture fixture)
{
    [Fact]
    public async Task StopsDuringAnInFlightScanWithoutProcessingItsResults()
    {
        await using var data = new MatchTestData(fixture.Database);
        var pair = await data.ReservedPairAsync();
        await SetExpiredDeadlineAsync(data, pair.Reservation);
        var watched = new[]
        {
            data.Document("matchReservations", pair.Reservation),
            data.Document("matchSessions", pair.User1),
            data.Document("matchSessions", pair.User2),
            data.Document("randomMatchQueue", pair.User1),
            data.Document("randomMatchQueue", pair.User2)
        };
        var before = await Task.WhenAll(watched.Select(document => document.GetSnapshotAsync()));
        Assert.All(before, snapshot => Assert.True(snapshot.Exists));
        var scan = new ScanGate("matchReservations");
        await using var worker = new HostedCleanupWorker(data.Auth, scan.AfterScanAsync);

        await worker.StartAsync();
        await scan.WaitAsync();
        await worker.StopAsync();

        Assert.True(scan.Canceled);
        Assert.Empty(worker.Errors);
        var after = await Task.WhenAll(watched.Select(document => document.GetSnapshotAsync()));
        Assert.Equal(before.Select(snapshot => snapshot.UpdateTime), after.Select(snapshot => snapshot.UpdateTime));
        Assert.Equal("PendingReady", (await ReadReservationAsync(data, pair.Reservation)).Status);
        await data.AssertPendingSessionAsync(pair.User1, pair.Reservation);
        await data.AssertPendingSessionAsync(pair.User2, pair.Reservation);
    }

    [Fact]
    public async Task RecoversOnNextCycleAfterUnavailableScanWithoutLosingCompletedWork()
    {
        await using var data = new MatchTestData(fixture.Database);
        var pair = await data.ReservedPairAsync();
        await SetExpiredDeadlineAsync(data, pair.Reservation);
        var waiting = await data.WaitingUserAsync();
        await SetStaleAsync(data, waiting);
        var attempted = new ScanGate("randomMatchQueue");
        var failed = false;
        async Task FailOnceAsync(string collection, CancellationToken token)
        {
            await attempted.AfterScanAsync(collection, token);
            if (collection == "randomMatchQueue" && !failed)
            {
                failed = true;
                throw new RpcException(new Status(StatusCode.Unavailable, "Injected local worker transport failure."));
            }
        }
        await using var worker = new HostedCleanupWorker(data.Auth, FailOnceAsync);

        await worker.StartAsync();
        await attempted.WaitAsync();
        await worker.ExpectStagesAsync("reservations");
        Assert.Equal("Expired", (await ReadReservationAsync(data, pair.Reservation)).Status);
        await data.AssertMissingAsync("matchSessions", pair.User1);
        await data.AssertMissingAsync("matchSessions", pair.User2);
        Assert.Equal("Waiting", (await ReadWaitingAsync(data, waiting)).Status);
        attempted.Release();

        // The failed waiting scan aborts the rest of its cycle. The next cycle
        // re-runs the reservation scan before completing both remaining stages.
        await worker.ExpectStagesAsync("reservations", "waiting", "messages");
        Assert.Equal(StatusCode.Unavailable, Assert.IsType<RpcException>(Assert.Single(worker.Errors)).StatusCode);
        await data.AssertMissingAsync("randomMatchQueue", waiting);
        Assert.Equal("Expired", (await ReadReservationAsync(data, pair.Reservation)).Status);
        Assert.Empty(await data.OwnedAsync("chatRooms"));
        await worker.StopAsync();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExpirationAndLateMutualReadyCannotReviveAnExpiredReservation(bool readyHandlesTimeoutFirst)
    {
        await using var data = new MatchTestData(fixture.Database);
        var pair = await data.ReservedPairAsync();
        var original = await ReadReservationAsync(data, pair.Reservation);
        Assert.Equal(TimeSpan.FromSeconds(30), original.ReadyExpiresAt.ToDateTime() - original.CreatedAt.ToDateTime());
        Assert.Equal("Waiting", (await data.Reservations.SubmitReadyAsync(pair.Reservation, pair.User1)).Status);
        await SetExpiredDeadlineAsync(data, pair.Reservation);

        var scan = new ScanGate("matchReservations");
        await using var worker = new HostedCleanupWorker(data.Auth, scan.AfterScanAsync);
        await worker.StartAsync();
        await scan.WaitAsync();
        if (readyHandlesTimeoutFirst)
            Assert.Equal(("Expired", (string?)null), await data.Reservations.SubmitReadyAsync(pair.Reservation, pair.User2));
        scan.Release();
        await worker.ExpectStagesAsync("reservations", "waiting", "messages");

        var expired = await ReadReservationAsync(data, pair.Reservation);
        Assert.Equal("Expired", expired.Status);
        Assert.True(expired.User1Ready);
        Assert.False(expired.User2Ready);
        Assert.Null(expired.RoomId);
        await data.AssertMissingAsync("matchSessions", pair.User1);
        await data.AssertMissingAsync("matchSessions", pair.User2);
        var readyUser = await ReadWaitingAsync(data, pair.User1);
        Assert.Equal("Waiting", readyUser.Status);
        Assert.Null(readyUser.ReservationId);
        await data.AssertMissingAsync("randomMatchQueue", pair.User2);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => data.Reservations.SubmitReadyAsync(pair.Reservation, pair.User2));
        Assert.Equal("Reservation is no longer available.", error.Message);
        Assert.Empty(await data.OwnedAsync("chatRooms"));
        Assert.Empty(await data.OwnedAsync("matchHistory"));
        Assert.Empty(worker.Errors);
        await worker.StopAsync();
    }

    [Fact]
    public async Task ExpirationDoesNotReleaseSessionsOrQueueEntriesOwnedByAnotherReservation()
    {
        await using var data = new MatchTestData(fixture.Database);
        var pair = await data.ReservedPairAsync();
        var live = await ReadReservationAsync(data, pair.Reservation);
        var stale = data.Document("matchReservations", $"older-{pair.Reservation}");
        await stale.CreateAsync(new FirebaseMatchReservation
        {
            ReservationId = stale.Id, Status = "PendingReady",
            User1Id = pair.User1, User2Id = pair.User2,
            CreatedAt = Ago(100), UpdatedAt = Ago(100), ReadyExpiresAt = Ago(70)
        });

        await using var worker = new HostedCleanupWorker(data.Auth);
        await worker.StartAsync();
        await worker.ExpectStagesAsync("reservations", "waiting", "messages");

        Assert.Equal("Expired", (await ReadReservationAsync(data, stale.Id)).Status);
        var remaining = await ReadReservationAsync(data, pair.Reservation);
        Assert.Equal("PendingReady", remaining.Status);
        Assert.Equal(live.ReadyExpiresAt, remaining.ReadyExpiresAt);
        await data.AssertPendingSessionAsync(pair.User1, pair.Reservation);
        await data.AssertPendingSessionAsync(pair.User2, pair.Reservation);
        Assert.Empty(worker.Errors);
        await worker.StopAsync();
    }

    [Fact]
    public async Task StaleWaitingScanRechecksHeartbeatCancelAndNewReservationOwnership()
    {
        await using var data = new MatchTestData(fixture.Database);
        var stale = await data.WaitingUserAsync();
        var heartbeat = await data.WaitingUserAsync();
        var canceled = await data.WaitingUserAsync();
        var reserved = await data.WaitingUserAsync();
        var partner = await data.WaitingUserAsync();
        var recent = await data.WaitingUserAsync();
        foreach (var user in new[] { stale, heartbeat, canceled, reserved })
            await SetStaleAsync(data, user);
        var originalRecent = await ReadWaitingAsync(data, recent);
        var scan = new ScanGate("randomMatchQueue");
        await using var worker = new HostedCleanupWorker(data.Auth, scan.AfterScanAsync);

        await worker.StartAsync();
        await scan.WaitAsync();
        Assert.Equal("Waiting", (await data.Queue.HeartbeatAsync(heartbeat)).Status);
        await data.Queue.CancelWaitingAsync(canceled);
        Assert.Equal("Waiting", (await data.Queue.HeartbeatAsync(reserved)).Status);
        var reservation = await data.Queue.TryReserveCandidateAsync(reserved, partner);
        Assert.NotNull(reservation);
        scan.Release();
        await worker.ExpectStagesAsync("reservations", "waiting", "messages");

        await data.AssertMissingAsync("randomMatchQueue", stale);
        await data.AssertMissingAsync("randomMatchQueue", canceled);
        Assert.Equal("Waiting", (await ReadWaitingAsync(data, heartbeat)).Status);
        Assert.Equal(originalRecent.LastSeenAt, (await ReadWaitingAsync(data, recent)).LastSeenAt);
        await data.AssertPendingSessionAsync(reserved, reservation);
        await data.AssertPendingSessionAsync(partner, reservation);
        Assert.Single(await data.OwnedAsync("matchReservations"));
        Assert.Empty(worker.Errors);
        await worker.StopAsync();
    }

    [Fact]
    public async Task RepeatedCyclesPreserveMultipleActiveRoomsAndHistoricalOpenRooms()
    {
        await using var data = new LifecycleTestData(fixture.Database);
        var first = await data.MatchedPairAsync();
        var historicalTemporary = await data.MatchedPairAsync();
        var historicalDecision = await data.MatchedPairAsync();
        await data.SeedHistoricalRoomAsync(historicalTemporary);
        await data.SeedHistoricalRoomAsync(historicalDecision, "AwaitingDecision");

        var scan = new ScanGate("matchReservations");
        await using var worker = new HostedCleanupWorker(data.Matches.Auth, scan.AfterScanAsync);
        await worker.StartAsync();
        await scan.WaitAsync();
        var second = await data.MatchAgainAsync(first.User1);
        var rooms = new[] { first, second, historicalTemporary, historicalDecision };
        foreach (var pair in rooms)
            await data.SeedMessagesAsync(pair, 1);
        // Stale historical timers on Active records must have no worker effect.
        foreach (var pair in rooms.Take(2))
        {
            await data.Room(pair.RoomId).UpdateAsync(new Dictionary<string, object>
            {
                ["TemporaryStartedAt"] = Ago(900), ["TemporaryEndsAt"] = Ago(600),
                ["DecisionEndsAt"] = Ago(480), ["CleanupPending"] = true
            });
            await data.Matches.Document("matchReservations", pair.ReservationId).UpdateAsync("ReadyExpiresAt", Ago(30));
        }

        scan.Release();
        await worker.ExpectStagesAsync("reservations", "waiting", "messages", "reservations", "waiting", "messages");
        Assert.Equal(new[] { "Active", "Active", "Temporary", "AwaitingDecision" },
            await Task.WhenAll(rooms.Select(async pair => (await data.ReadRoomAsync(pair)).Status)));
        foreach (var pair in rooms)
        {
            var messages = await data.Room(pair.RoomId).Collection("messages").GetSnapshotAsync();
            Assert.Equal("Message 0", Assert.Single(messages.Documents).GetValue<string>("Content"));
            Assert.Equal("Matched", (await ReadReservationAsync(data.Matches, pair.ReservationId)).Status);
            await data.AssertSessionsReleasedAsync(pair);
        }
        Assert.Equal(4, (await data.Matches.OwnedAsync("chatRooms")).Count);
        Assert.True(await data.Rooms.UnmatchRoomAsync(first.RoomId, first.User1));
        Assert.Equal("Closed", (await data.ReadRoomAsync(first)).Status);
        Assert.Equal("Active", (await data.ReadRoomAsync(second)).Status);
        Assert.Empty(worker.Errors);
        await worker.StopAsync();
    }

    [Fact]
    public async Task ClosedRoomFailureDoesNotBlockOtherCleanupAndRetriesAfterEvidenceIsConfirmed()
    {
        await using var data = new LifecycleTestData(fixture.Database);
        var blocked = await data.MatchedPairAsync();
        var eligible = await data.MatchedPairAsync();
        foreach (var pair in new[] { blocked, eligible })
            await data.SeedHistoricalRoomAsync(pair);
        var blockedMessages = await data.SeedMessagesAsync(blocked, 3);
        var eligibleMessages = await data.SeedMessagesAsync(eligible, 2);
        var blockedReport = await data.Reports.CreateReportAsync(blocked.RoomId, blocked.User1, "User", "Abuse");
        var eligibleReport = await data.Reports.CreateReportAsync(eligible.RoomId, eligible.User1, "User", "Abuse");
        await data.Report(blockedReport).UpdateAsync("EvidenceCaptured", false);

        await using var worker = new HostedCleanupWorker(data.Matches.Auth);
        await worker.StartAsync();
        await worker.ExpectStagesAsync("reservations", "waiting", "messages");
        Assert.Equal("Cannot delete messages before report evidence is confirmed.",
            Assert.IsType<InvalidOperationException>(Assert.Single(worker.Errors)).Message);
        Assert.True((await data.ReadRoomAsync(blocked)).CleanupPending);
        Assert.Equal(3, (await data.Room(blocked.RoomId).Collection("messages").GetSnapshotAsync()).Count);
        await AssertCleanedAsync(eligible);
        var cleanedAt = (await data.ReadRoomAsync(eligible)).UpdatedAt;
        await data.AssertEvidenceAsync(eligibleReport, eligibleMessages);

        await data.Report(blockedReport).UpdateAsync("EvidenceCaptured", true);
        await worker.ExpectStagesAsync("reservations", "waiting", "messages");
        await AssertCleanedAsync(blocked);
        await AssertCleanedAsync(eligible);
        Assert.Equal(cleanedAt, (await data.ReadRoomAsync(eligible)).UpdatedAt);
        await data.AssertEvidenceAsync(blockedReport, blockedMessages);
        await data.AssertEvidenceAsync(eligibleReport, eligibleMessages);
        Assert.Single(worker.Errors);
        await worker.StopAsync();

        async Task AssertCleanedAsync(ChatPair pair)
        {
            await data.AssertClosedAsync(pair);
            Assert.Empty((await data.Room(pair.RoomId).Collection("messages").GetSnapshotAsync()).Documents);
        }
    }

    private static Timestamp Ago(int seconds) => Timestamp.FromDateTime(DateTime.UtcNow.AddSeconds(-seconds));
    private static Task SetExpiredDeadlineAsync(MatchTestData data, string id)
    {
        var created = DateTime.UtcNow.AddSeconds(-31);
        return data.Document("matchReservations", id).UpdateAsync(new Dictionary<string, object>
        {
            ["CreatedAt"] = Timestamp.FromDateTime(created),
            ["ReadyExpiresAt"] = Timestamp.FromDateTime(created.AddSeconds(30))
        });
    }
    private static Task SetStaleAsync(MatchTestData data, string id) =>
        data.Document("randomMatchQueue", id).UpdateAsync("LastSeenAt", Ago(90));
    private static Task<FirebaseMatchReservation> ReadReservationAsync(MatchTestData data, string id) =>
        data.ReadAsync<FirebaseMatchReservation>("matchReservations", id);
    private static Task<FirebaseRandomMatchQueueEntry> ReadWaitingAsync(MatchTestData data, string id) =>
        data.ReadAsync<FirebaseRandomMatchQueueEntry>("randomMatchQueue", id);

    private sealed class ScanGate(string collection)
    {
        private readonly TaskCompletionSource reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Canceled { get; private set; }
        public Task WaitAsync() => reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        public void Release() => resume.TrySetResult();

        public async Task AfterScanAsync(string scanned, CancellationToken token)
        {
            if (scanned != collection || !reached.TrySetResult())
                return;
            try
            {
                await resume.Task.WaitAsync(token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                Canceled = true;
                throw;
            }
        }
    }
}
