namespace WebChatEIU.IntegrationTests.Lifecycle;

[Collection("Firestore emulator")]
[Trait("Category", "Lifecycle")]
public sealed class TemporaryMessageCleanupTests(FirestoreEmulatorFixture fixture)
{
    [Fact]
    public async Task ClosedTemporaryRoom_CleansMultipleBatchesAndSupportsRetry()
    {
        await using var data = new LifecycleTestData(fixture.Database);
        var pair = await data.MatchedPairAsync();
        await data.SeedMessagesAsync(pair, 401);
        await data.SeedHistoricalRoomAsync(pair);
        await data.Rooms.EndTemporaryRoomAsync(pair.RoomId, pair.User1);
        Assert.Equal(401, await data.Cleanup.DeleteTemporaryMessagesAsync(pair.RoomId));
        Assert.Empty((await data.Room(pair.RoomId).Collection("messages").GetSnapshotAsync()).Documents);
        Assert.Equal(0, await data.Cleanup.DeleteTemporaryMessagesAsync(pair.RoomId));
        await data.AssertClosedAsync(pair, cleanupPending: true);
        // The worker, not this service, clears CleanupPending after successful cleanup.
        Assert.True((await data.ReadRoomAsync(pair)).CleanupPending);
    }

    [Theory]
    [InlineData("Temporary")]
    [InlineData("AwaitingDecision")]
    [InlineData("Active")]
    [InlineData("ClosedPersistent")]
    [InlineData("ClosedNotPending")]
    public async Task IneligibleRoom_MessagesAreProtected(string state)
    {
        await using var data = new LifecycleTestData(fixture.Database);
        var pair = await data.MatchedPairAsync();
        var messages = await data.SeedMessagesAsync(pair, 1);
        if (state is "Temporary" or "AwaitingDecision") await data.SeedHistoricalRoomAsync(pair, state);
        if (state == "ClosedPersistent")
        {
            await data.Rooms.UnmatchRoomAsync(pair.RoomId, pair.User1);
            // Verify persistent marker protection even with a mistakenly pending flag.
            await data.Room(pair.RoomId).UpdateAsync("CleanupPending", true);
        }
        if (state == "ClosedNotPending")
        {
            await data.SeedHistoricalRoomAsync(pair);
            await data.Rooms.EndTemporaryRoomAsync(pair.RoomId, pair.User1);
            await data.Room(pair.RoomId).UpdateAsync("CleanupPending", false);
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => data.Cleanup.DeleteTemporaryMessagesAsync(pair.RoomId));
        var snapshot = await data.Room(pair.RoomId).Collection("messages").GetSnapshotAsync();
        Assert.Equal(messages[0].MessageId, Assert.Single(snapshot.Documents).Id);
        Assert.Equal(messages[0].Content, snapshot.Documents[0].GetValue<string>("Content"));
    }

    [Fact]
    public async Task Cleanup_PreservesConfirmedReportAndEvidence()
    {
        await using var data = new LifecycleTestData(fixture.Database);
        var pair = await data.MatchedPairAsync();
        await data.SeedHistoricalRoomAsync(pair);
        var messages = await data.SeedMessagesAsync(pair, 13);
        var reportId = await data.Reports.CreateReportAsync(pair.RoomId, pair.User1, "Message", "Abuse", reportedMessageId: messages[6].MessageId);
        Assert.Equal(13, await data.Cleanup.DeleteTemporaryMessagesAsync(pair.RoomId));
        await data.AssertEvidenceAsync(reportId, messages.Skip(2).Take(9));
        Assert.True((await data.Report(reportId).GetSnapshotAsync()).Exists);
        Assert.Equal(0, await data.Cleanup.DeleteTemporaryMessagesAsync(pair.RoomId));
        await data.AssertEvidenceAsync(reportId, messages.Skip(2).Take(9));
    }

    [Fact]
    public async Task UnconfirmedEvidence_BlocksDeletionUntilConfirmed_ThenRetrySucceeds()
    {
        await using var data = new LifecycleTestData(fixture.Database);
        var pair = await data.MatchedPairAsync();
        await data.SeedHistoricalRoomAsync(pair);
        var messages = await data.SeedMessagesAsync(pair, 3);
        var reportId = await data.Reports.CreateReportAsync(pair.RoomId, pair.User1, "User", "Abuse");
        // Controlled fixture represents an outstanding evidence confirmation.
        await data.Report(reportId).UpdateAsync("EvidenceCaptured", false);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => data.Cleanup.DeleteTemporaryMessagesAsync(pair.RoomId));
        Assert.Equal("Cannot delete messages before report evidence is confirmed.", error.Message);
        Assert.Equal(3, (await data.Room(pair.RoomId).Collection("messages").GetSnapshotAsync()).Count);
        Assert.True((await data.ReadRoomAsync(pair)).CleanupPending);
        await data.AssertEvidenceAsync(reportId, messages);
        await data.Report(reportId).UpdateAsync("EvidenceCaptured", true);
        Assert.Equal(3, await data.Cleanup.DeleteTemporaryMessagesAsync(pair.RoomId));
        Assert.Equal(0, await data.Cleanup.DeleteTemporaryMessagesAsync(pair.RoomId));
        await data.AssertEvidenceAsync(reportId, messages);
    }

    [Fact]
    public async Task Cleanup_AfterPartialDeletion_RemovesOnlyRemainingMessages()
    {
        await using var data = new LifecycleTestData(fixture.Database);
        var pair = await data.MatchedPairAsync();
        var messages = await data.SeedMessagesAsync(pair, 401);
        await data.SeedHistoricalRoomAsync(pair);
        await data.Rooms.EndTemporaryRoomAsync(pair.RoomId, pair.User1);
        // Simulate an interrupted earlier cleanup after its first 200-message batch.
        var batch = fixture.Database.StartBatch();
        foreach (var message in messages.Take(200))
            batch.Delete(data.Room(pair.RoomId).Collection("messages").Document(message.MessageId));
        await batch.CommitAsync();
        Assert.Equal(201, await data.Cleanup.DeleteTemporaryMessagesAsync(pair.RoomId));
        Assert.Equal(0, await data.Cleanup.DeleteTemporaryMessagesAsync(pair.RoomId));
        Assert.Empty((await data.Room(pair.RoomId).Collection("messages").GetSnapshotAsync()).Documents);
    }
}
