using Google.Cloud.Firestore;
using WebChatEIU.Models.Firestore;
using WebChatEIU.Services;

namespace WebChatEIU.IntegrationTests.Matching;

// Owns only uniquely named users and the documents referring to those users.
internal sealed class MatchTestData(FirestoreDb database) : IAsyncDisposable
{
    private readonly string prefix = "regression-" + Guid.NewGuid().ToString("N");
    private readonly List<string> users = new();
    public FirestoreDb Database { get; } = database;
    public FirebaseRandomMatchQueueService Queue { get; } = new(database);
    public FirebaseMatchReservationService Reservations { get; } = new(database);
    public DocumentReference Document(string collection, string id) => Database.Collection(collection).Document(id);

    public async Task<string> NewUserAsync(bool active = true, bool banned = false, bool deleted = false)
    {
        var id = $"{prefix}-{users.Count}";
        users.Add(id);
        await Document("users", id).CreateAsync(new FirebaseUser
        {
            UserId = id, Email = "fixture@eiu.edu.vn", Fullname = "Regression fixture",
            IsActive = active, IsBanned = banned, IsDeleted = deleted
        });
        return id;
    }

    public async Task<string> WaitingUserAsync()
    {
        var id = await NewUserAsync();
        Assert.Equal("Waiting", await Queue.JoinQueueAsync(id));
        return id;
    }

    public async Task<(string User1, string User2, string Reservation)> ReservedPairAsync()
    {
        var first = await WaitingUserAsync();
        var second = await WaitingUserAsync();
        var reservation = await Queue.TryReserveCandidateAsync(first, second);
        Assert.NotNull(reservation);
        return (first, second, reservation);
    }

    public async Task<T> ReadAsync<T>(string collection, string id)
    {
        var snapshot = await Document(collection, id).GetSnapshotAsync();
        Assert.True(snapshot.Exists, $"Expected {collection}/{id} to exist.");
        return snapshot.ConvertTo<T>();
    }

    public async Task AssertMissingAsync(string collection, string id) =>
        Assert.False((await Document(collection, id).GetSnapshotAsync()).Exists);

    public async Task<List<DocumentSnapshot>> OwnedAsync(string collection)
    {
        var snapshot = await Database.Collection(collection).GetSnapshotAsync();
        return snapshot.Documents.Where(d =>
            (d.TryGetValue<string>("User1Id", out var first) && users.Contains(first)) ||
            (d.TryGetValue<string>("User2Id", out var second) && users.Contains(second))).ToList();
    }

    public async Task AssertPendingSessionAsync(string user, string reservation)
    {
        var session = await ReadAsync<FirebaseMatchSession>("matchSessions", user);
        Assert.Equal("PendingReady", session.Status);
        Assert.Equal(reservation, session.ReservationId);
        Assert.Null(session.RoomId);
        var entry = await ReadAsync<FirebaseRandomMatchQueueEntry>("randomMatchQueue", user);
        Assert.Equal("Reserved", entry.Status);
        Assert.Equal(reservation, entry.ReservationId);
    }

    public async Task AssertMatchedAsync(string first, string second, string reservationId, string roomId)
    {
        var reservation = await ReadAsync<FirebaseMatchReservation>("matchReservations", reservationId);
        Assert.Equal("Matched", reservation.Status);
        Assert.True(reservation.User1Ready && reservation.User2Ready);
        Assert.Equal(roomId, reservation.RoomId);
        var room = await ReadAsync<FirebaseChatRoom>("chatRooms", roomId);
        Assert.Equal("Active", room.Status);
        Assert.Equal(first, room.User1Id);
        Assert.Equal(second, room.User2Id);
        Assert.NotNull(room.BecamePersistentAt);
        Assert.Null(room.TemporaryStartedAt);
        Assert.Null(room.TemporaryEndsAt);
        Assert.Null(room.DecisionEndsAt);
        Assert.False(room.IsRevealed);
        Assert.False(room.CleanupPending);
        Assert.Single(await OwnedAsync("chatRooms"));
        var history = Assert.Single(await OwnedAsync("matchHistory")).ConvertTo<FirebaseMatchHistory>();
        Assert.Equal(roomId, history.RoomId);
        Assert.Equal(new[] { first, second }.OrderBy(x => x, StringComparer.Ordinal),
            new[] { history.User1Id, history.User2Id });
        foreach (var user in new[] { first, second })
        {
            await AssertMissingAsync("matchSessions", user);
            await AssertMissingAsync("randomMatchQueue", user);
        }
    }

    public static async Task<T[]> TogetherAsync<T>(params Func<Task<T>>[] operations)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = operations.Select(async operation => { await gate.Task; return await operation(); }).ToArray();
        gate.SetResult();
        // Firestore transaction retry/lock behavior is real, not mocked. No timing sleeps.
        return await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(90));
    }

    public async ValueTask DisposeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var owned = new List<DocumentReference>();
        foreach (var collection in new[] { "chatRooms", "matchReservations", "matchHistory" })
        {
            var snapshot = await Database.Collection(collection).GetSnapshotAsync(timeout.Token);
            owned.AddRange(snapshot.Documents.Where(d =>
                (d.TryGetValue<string>("User1Id", out var a) && users.Contains(a)) ||
                (d.TryGetValue<string>("User2Id", out var b) && users.Contains(b))).Select(d => d.Reference));
        }
        foreach (var user in users)
            foreach (var collection in new[] { "users", "randomMatchQueue", "matchSessions" })
                owned.Add(Document(collection, user));
        // Current tests create fewer than 100 documents, with no message/evidence subcollections.
        if (owned.Count == 0) return;
        var batch = Database.StartBatch();
        foreach (var document in owned) batch.Delete(document);
        await batch.CommitAsync(timeout.Token);
    }
}
