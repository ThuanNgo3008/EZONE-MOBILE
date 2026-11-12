using FirebaseAdmin;
using FirebaseAdmin.Auth;

namespace WebChatEIU.IntegrationTests.Auth;

[Collection("Auth emulator")]
[Trait("Category", "AuthEmulator")]
public sealed class AuthEmulatorSmokeTests(AuthEmulatorFixture fixture)
{
    [Fact]
    public async Task DisposableAccount_IsCreatedReadAndDeleted_WithoutDefaultApp()
    {
        var uid = "auth-smoke-" + Guid.NewGuid().ToString("N");
        var email = uid + "@example.invalid";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            Assert.Null(FirebaseApp.DefaultInstance);
            Assert.Equal("demo-ezone-regression", fixture.ProjectId);
            var created = await fixture.Auth.CreateUserAsync(new UserRecordArgs
            {
                Uid = uid, Email = email, DisplayName = "Disposable emulator account",
                EmailVerified = false, Disabled = false
            }, timeout.Token);
            Assert.Equal(uid, created.Uid);
            var read = await fixture.Auth.GetUserAsync(uid, timeout.Token);
            Assert.Equal(uid, read.Uid);
            Assert.Equal(email, read.Email);
            Assert.False(read.EmailVerified);
            Assert.False(read.Disabled);
            await fixture.Auth.DeleteUserAsync(uid, timeout.Token);
            var error = await Assert.ThrowsAsync<FirebaseAuthException>(() => fixture.Auth.GetUserAsync(uid, timeout.Token));
            Assert.Equal(AuthErrorCode.UserNotFound, error.AuthErrorCode);
            Assert.Null(FirebaseApp.DefaultInstance);
            Assert.NotEmpty(fixture.Requests);
            Assert.All(fixture.Requests, uri =>
            {
                Assert.Equal("http", uri.Scheme);
                Assert.Equal("127.0.0.1:9099", uri.Authority);
                Assert.StartsWith("/identitytoolkit.googleapis.com/v1/projects/demo-ezone-regression/", uri.AbsolutePath);
            });
        }
        finally
        {
            // Deterministic UID enables cleanup even if creation times out after committing.
            using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await fixture.Auth.DeleteUserAsync(uid, cleanupTimeout.Token); }
            catch (FirebaseAuthException ex) when (ex.AuthErrorCode == AuthErrorCode.UserNotFound) { }
        }
    }
}
