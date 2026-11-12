using WebChatEIU.Models.Firestore;

namespace WebChatEIU.IntegrationTests.Lifecycle;

[Collection("Firestore emulator")]
[Trait("Category", "Lifecycle")]
public sealed class ReportingTests(FirestoreEmulatorFixture fixture)
{
    [Fact]
    public async Task MessageReport_CopiesTargetAndFourMessagesOnEachSide()
    {
        await using var data = new LifecycleTestData(fixture.Database);
        var pair = await data.MatchedPairAsync();
        var messages = await data.SeedMessagesAsync(pair, 13);
        var id = await data.Reports.CreateReportAsync(pair.RoomId, pair.User1, "Message", " Harassment ",
            " Details ", messages[6].MessageId);
        var report = await data.Matches.ReadAsync<FirebaseReport>("reports", id);
        Assert.Equal(pair.RoomId, report.RoomId);
        Assert.Equal(pair.User1, report.ReporterId);
        Assert.Equal(pair.User2, report.ReportedUserId);
        Assert.Equal("Message", report.ReportType);
        Assert.Equal(messages[6].MessageId, report.ReportedMessageId);
        Assert.Equal("Harassment", report.Reason);
        Assert.Equal("Details", report.Description);
        Assert.Equal("Pending", report.Status);
        Assert.True(report.EvidenceCaptured);
        await data.AssertEvidenceAsync(id, messages.Skip(2).Take(9));
        await data.AssertClosedAsync(pair);
        Assert.Equal(13, (await data.Room(pair.RoomId).Collection("messages").GetSnapshotAsync()).Count);
    }

    [Theory]
    [InlineData("Temporary")]
    [InlineData("AwaitingDecision")]
    [InlineData("Active")]
    public async Task ParticipantReport_CapturesTenRecentMessagesAndClosesEligibleRoom(string state)
    {
        await using var data = new LifecycleTestData(fixture.Database);
        var pair = await data.MatchedPairAsync();
        var messages = await data.SeedMessagesAsync(pair, 13);
        if (state != "Active") await data.SeedHistoricalRoomAsync(pair, state);
        var id = await data.Reports.CreateReportAsync(pair.RoomId, pair.User1, "User", "Abuse");
        var report = await data.Matches.ReadAsync<FirebaseReport>("reports", id);
        Assert.Equal("User", report.ReportType);
        Assert.Equal(pair.User2, report.ReportedUserId);
        Assert.True(report.EvidenceCaptured);
        await data.AssertEvidenceAsync(id, messages.Skip(3));
        await data.AssertClosedAsync(pair, cleanupPending: state != "Active");
        await Assert.ThrowsAsync<InvalidOperationException>(() => data.Reports.CreateReportAsync(pair.RoomId, pair.User1, "User", "Retry"));
        Assert.Single((await fixture.Database.Collection("reports").WhereEqualTo("RoomId", pair.RoomId).GetSnapshotAsync()).Documents);
    }

    [Fact]
    public async Task ForeignRoomMessageId_IsRejectedWithoutChangingEitherRoom()
    {
        await using var data = new LifecycleTestData(fixture.Database);
        var pair = await data.MatchedPairAsync();
        var other = await data.MatchedPairAsync();
        var foreign = await data.Messages.SendMessageAsync(other.RoomId, other.User2, "Foreign message");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            data.Reports.CreateReportAsync(pair.RoomId, pair.User1, "Message", "Abuse", reportedMessageId: foreign.MessageId));
        Assert.Equal("Reported message not found.", error.Message);
        Assert.Equal("Active", (await data.ReadRoomAsync(pair)).Status);
        Assert.Equal("Active", (await data.ReadRoomAsync(other)).Status);
        Assert.Empty((await fixture.Database.Collection("reports").WhereEqualTo("RoomId", pair.RoomId).GetSnapshotAsync()).Documents);
        Assert.True((await data.Room(other.RoomId).Collection("messages").Document(foreign.MessageId).GetSnapshotAsync()).Exists);
    }

    [Fact]
    public async Task OwnMessage_CannotBeReportedAsPartnersMessage()
    {
        await using var data = new LifecycleTestData(fixture.Database);
        var pair = await data.MatchedPairAsync();
        var own = await data.Messages.SendMessageAsync(pair.RoomId, pair.User1, "Own message");
        await Assert.ThrowsAsync<ArgumentException>(() => data.Reports.CreateReportAsync(pair.RoomId, pair.User1,
            "Message", "Abuse", reportedMessageId: own.MessageId));
        Assert.Equal("Active", (await data.ReadRoomAsync(pair)).Status);
        Assert.Empty((await fixture.Database.Collection("reports").WhereEqualTo("RoomId", pair.RoomId).GetSnapshotAsync()).Documents);
    }

    [Fact]
    public async Task Outsider_CannotReportRoomParticipant()
    {
        await using var data = new LifecycleTestData(fixture.Database);
        var pair = await data.MatchedPairAsync();
        var outsider = await data.Matches.NewUserAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            data.Reports.CreateReportAsync(pair.RoomId, outsider, "User", "Abuse"));
        Assert.Equal("Active", (await data.ReadRoomAsync(pair)).Status);
        Assert.Empty((await fixture.Database.Collection("reports").WhereEqualTo("RoomId", pair.RoomId).GetSnapshotAsync()).Documents);
    }

    [Fact]
    public async Task MessageReport_RequiresAMessageIdAtServiceBoundary()
    {
        await using var data = new LifecycleTestData(fixture.Database);
        var pair = await data.MatchedPairAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => data.Reports.CreateReportAsync(pair.RoomId, pair.User1, "Message", "Abuse"));
        Assert.Equal("Active", (await data.ReadRoomAsync(pair)).Status);
    }

    [Fact]
    public async Task EmptyParticipantReport_CanCloseRoomWithoutEvidence()
    {
        await using var data = new LifecycleTestData(fixture.Database);
        var pair = await data.MatchedPairAsync();
        var id = await data.Reports.CreateReportAsync(pair.RoomId, pair.User1, "User", "Abuse");
        Assert.False((await data.Matches.ReadAsync<FirebaseReport>("reports", id)).EvidenceCaptured);
        Assert.Empty((await data.Report(id).Collection("evidence").GetSnapshotAsync()).Documents);
        await data.AssertClosedAsync(pair);
        // Even an empty persistent room is not eligible for temporary cleanup.
        await Assert.ThrowsAsync<InvalidOperationException>(() => data.Cleanup.DeleteTemporaryMessagesAsync(pair.RoomId));
    }

    [Fact]
    public async Task Report_ClosesOnlyTargetRoom_PreservesOtherRoomAndNewReservation()
    {
        await using var data = new LifecycleTestData(fixture.Database);
        var ab = await data.MatchedPairAsync();
        var ac = await data.MatchAgainAsync(ab.User1);
        var evidence = await data.SeedMessagesAsync(ab, 3);
        var otherMessage = await data.Messages.SendMessageAsync(ac.RoomId, ac.User2, "Unaffected");
        var partner = await data.Matches.WaitingUserAsync();
        await data.Matches.Queue.JoinQueueAsync(ab.User1);
        var next = await data.Matches.Queue.TryReserveCandidateAsync(ab.User1, partner);
        Assert.NotNull(next);
        var reportId = await data.Reports.CreateReportAsync(ab.RoomId, ab.User1, "User", "Abuse");
        var closed = await data.ReadRoomAsync(ab);
        Assert.Equal("Closed", closed.Status);
        Assert.False(closed.CleanupPending);
        await data.AssertEvidenceAsync(reportId, evidence);
        await data.Matches.AssertPendingSessionAsync(ab.User1, next);
        await data.Matches.AssertPendingSessionAsync(partner, next);
        Assert.Equal("Active", (await data.ReadRoomAsync(ac)).Status);
        Assert.Equal(otherMessage.MessageId, Assert.Single((await data.Messages.GetMessagesAsync(ac.RoomId, ac.User1)).Messages).MessageId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => data.Cleanup.DeleteTemporaryMessagesAsync(ab.RoomId));
        Assert.Equal(3, (await data.Room(ab.RoomId).Collection("messages").GetSnapshotAsync()).Count);
        await data.Messages.SendMessageAsync(ac.RoomId, ac.User1, "Still active");
    }
}
