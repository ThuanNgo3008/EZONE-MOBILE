namespace WebChatEIU.IntegrationTests;

[Collection("Firestore emulator")]
[Trait("Category", "Emulator")]
public sealed class FirestoreEmulatorSmokeTests(FirestoreEmulatorFixture fixture)
{
    [Fact]
    public async Task DisposableDocument_CanBeCreatedReadAndDeleted()
    {
        var document = fixture.NewSmokeDocument();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            await document.CreateAsync(new Dictionary<string, object>
            {
                ["Marker"] = "local-emulator-smoke"
            }, timeout.Token);
            var snapshot = await document.GetSnapshotAsync(timeout.Token);
            Assert.True(snapshot.Exists);
            Assert.Equal("local-emulator-smoke", snapshot.GetValue<string>("Marker"));
            Assert.Equal("demo-ezone-regression", fixture.Database.ProjectId);

            await document.DeleteAsync(cancellationToken: timeout.Token);
            Assert.False((await document.GetSnapshotAsync(timeout.Token)).Exists);
        }
        finally
        {
            // A separate timeout allows cleanup even if an assertion or earlier RPC fails.
            using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await document.DeleteAsync(cancellationToken: cleanupTimeout.Token);
        }
    }
}
