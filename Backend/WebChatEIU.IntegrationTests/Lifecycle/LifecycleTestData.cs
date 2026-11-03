using Google.Cloud.Firestore;
using WebChatEIU.IntegrationTests.Matching;
using WebChatEIU.Models.Firestore;
using WebChatEIU.Services;

namespace WebChatEIU.IntegrationTests.Lifecycle;

internal sealed record ChatPair(string User1, string User2, string ReservationId, string RoomId);

internal sealed class LifecycleTestData(FirestoreDb database) : IAsyncDisposable
{
    private readonly HashSet<string> rooms = new();
    public MatchTestData Matches { get; } = new(database);
    public FirebaseChatRoomService Rooms { get; } = new(database);
    public FirebaseRoomDecisionService Decisions { get; } = new(database);
    public FirebaseChatMessageService Messages { get; } = new(database);
    public FirebaseReportService Reports { get; } = new(database);
    public FirebaseTemporaryMessageCleanupService Cleanup { get; } = new(database);
    public DocumentReference Room(string id) => database.Collection("chatRooms").Document(id);
    public DocumentReference Report(string id) => database.Collection("reports").Document(id);

    public async Task<ChatPair> MatchedPairAsync()
    {
        var (first, second, reservation) = await Matches.ReservedPairAsync();
        await Matches.Reservations.SubmitReadyAsync(reservation, first);
        var result = await Matches.Reservations.SubmitReadyAsync(reservation, second);
        Assert.Equal("Matched", result.Status);
        Assert.NotNull(result.RoomId);
        rooms.Add(result.RoomId);
        return new ChatPair(first, second, reservation, result.RoomId);
    }

    public async Task AwaitDecisionAsync(ChatPair pair)
    {
        // Whole seconds preserve exact comparisons after Firestore serialization.
        var now = DateTime.UtcNow;
        var end = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc).AddSeconds(-30);
        await Room(pair.RoomId).UpdateAsync(new Dictionary<string, object>
        {
            ["TemporaryStartedAt"] = Timestamp.FromDateTime(end.AddMinutes(-5)),
            ["TemporaryEndsAt"] = Timestamp.FromDateTime(end)
        });
        Assert.True(await Rooms.TryMoveToAwaitingDecisionAsync(pair.RoomId));
    }

    public async Task MakePersistentAsync(ChatPair pair)
    {
        await AwaitDecisionAsync(pair);
        Assert.Equal("Waiting", await Decisions.SubmitDecisionAsync(pair.RoomId, pair.User1, true));
        Assert.Equal("Active", await Decisions.SubmitDecisionAsync(pair.RoomId, pair.User2, true));
    }

    public Task<FirebaseChatRoom> ReadRoomAsync(ChatPair pair) =>
        Matches.ReadAsync<FirebaseChatRoom>("chatRooms", pair.RoomId);

    public async Task AssertSessionsReleasedAsync(ChatPair pair)
    {
        await Matches.AssertMissingAsync("matchSessions", pair.User1);
        await Matches.AssertMissingAsync("matchSessions", pair.User2);
    }

    public async Task AssertClosedAsync(ChatPair pair, bool cleanupPending = true)
    {
        var room = await ReadRoomAsync(pair);
        Assert.Equal("Closed", room.Status);
        Assert.NotNull(room.ClosedAt);
        Assert.Equal(cleanupPending, room.CleanupPending);
        await AssertSessionsReleasedAsync(pair);
        var history = (await Matches.OwnedAsync("matchHistory"))
            .Single(d => d.GetValue<string>("RoomId") == pair.RoomId);
        Assert.Equal(pair.RoomId, history.ConvertTo<FirebaseMatchHistory>().RoomId);
    }

    public async Task<List<FirebaseChatMessage>> SeedMessagesAsync(ChatPair pair, int count)
    {
        var messages = Enumerable.Range(0, count).Select(i => new FirebaseChatMessage
        {
            MessageId = $"message-{i:D4}", SenderId = i % 2 == 0 ? pair.User2 : pair.User1,
            Content = $"Message {i}",
            CreatedAt = Timestamp.FromDateTime(new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(i))
        }).ToList();
        foreach (var chunk in messages.Chunk(200))
        {
            var batch = database.StartBatch();
            foreach (var message in chunk)
                batch.Create(Room(pair.RoomId).Collection("messages").Document(message.MessageId), message);
            await batch.CommitAsync();
        }
        return messages;
    }

    public async Task AssertEvidenceAsync(string reportId, IEnumerable<FirebaseChatMessage> expected)
    {
        var snapshot = await Report(reportId).Collection("evidence").GetSnapshotAsync();
        var messages = expected.ToDictionary(m => m.MessageId);
        Assert.Equal(messages.Keys.OrderBy(x => x), snapshot.Documents.Select(d => d.Id).OrderBy(x => x));
        foreach (var document in snapshot.Documents)
        {
            var message = messages[document.Id];
            Assert.Equal(message.MessageId, document.GetValue<string>("MessageId"));
            Assert.Equal(message.SenderId, document.GetValue<string>("SenderId"));
            Assert.Equal(message.Content, document.GetValue<string>("Content"));
            Assert.Equal(message.CreatedAt, document.GetValue<Timestamp>("CreatedAt"));
        }
    }

    public async ValueTask DisposeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            foreach (var room in rooms)
            {
                var reports = await database.Collection("reports").WhereEqualTo("RoomId", room).GetSnapshotAsync(timeout.Token);
                foreach (var report in reports.Documents)
                {
                    await DeleteCollectionAsync(report.Reference.Collection("evidence"), timeout.Token);
                    await report.Reference.DeleteAsync(cancellationToken: timeout.Token);
                }
                await DeleteCollectionAsync(Room(room).Collection("messages"), timeout.Token);
                await database.Collection("roomDecisions").Document(room).DeleteAsync(cancellationToken: timeout.Token);
            }
        }
        finally { await Matches.DisposeAsync(); }
    }

    private async Task DeleteCollectionAsync(CollectionReference collection, CancellationToken cancellationToken)
    {
        while (true)
        {
            var snapshot = await collection.Limit(200).GetSnapshotAsync(cancellationToken);
            if (snapshot.Count == 0) return;
            var batch = database.StartBatch();
            foreach (var document in snapshot.Documents) batch.Delete(document.Reference);
            await batch.CommitAsync(cancellationToken);
        }
    }
}
