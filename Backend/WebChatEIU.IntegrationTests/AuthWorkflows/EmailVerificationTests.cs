using FirebaseAdmin.Auth;
using WebChatEIU.IntegrationTests.Auth;

namespace WebChatEIU.IntegrationTests.AuthWorkflows;

[Collection("Auth workflows")]
[Trait("Category", "AuthWorkflow")]
public sealed class EmailVerificationTests
{
    [Fact]
    public async Task UnverifiedRegistration_CannotActivateProfile()
    {
        await using var data = new AuthWorkflowTestData();
        var uid = await data.Service.RegisterAsync(AuthWorkflowTestData.Registration());
        Assert.False(await data.Service.SyncEmailVerificationAsync(uid));
        Assert.False((await data.Users.GetByIdAsync(uid))!.IsActive);
        Assert.False((await data.Auth.GetUserAsync(uid)).EmailVerified);
    }

    [Fact]
    public async Task CapturedVerificationLink_VerifiesAuth_ThenSyncActivatesProfileIdempotently()
    {
        await using var data = new AuthWorkflowTestData();
        var dto = AuthWorkflowTestData.Registration();
        var uid = await data.Service.RegisterAsync(dto);
        var before = (await data.Users.GetByIdAsync(uid))!;
        var email = Assert.Single(data.Email.Attempts);
        var settings = AuthEmulatorSettings.FromEnvironment();
        var uri = AuthWorkflowTestData.ValidateVerificationLink(email.Link, settings);
        Assert.Equal("127.0.0.1:9099", uri.Authority);
        await data.VerifyEmailAsync(email);
        Assert.True((await data.Auth.GetUserAsync(uid)).EmailVerified);
        Assert.False((await data.Users.GetByIdAsync(uid))!.IsActive);
        Assert.True(await data.Service.SyncEmailVerificationAsync(uid));
        Assert.True(await data.Service.SyncEmailVerificationAsync(uid));
        var after = (await data.Users.GetByIdAsync(uid))!;
        Assert.True(after.IsActive);
        Assert.Equal(before.CreatedDate, after.CreatedDate);
        Assert.Equal(before.Fullname, after.Fullname);
        Assert.Equal(before.Email, after.Email);
        Assert.Equal(before.Interests, after.Interests);
    }

    [Theory]
    [InlineData("Disabled")]
    [InlineData("Banned")]
    [InlineData("Deleted")]
    [InlineData("MissingProfile")]
    public async Task UnavailableVerifiedAccount_IsNotActivated(string state)
    {
        await using var data = new AuthWorkflowTestData();
        var uid = await data.AccountWithProfileAsync(active: false, disabled: state == "Disabled",
            banned: state == "Banned", deleted: state == "Deleted");
        if (state == "MissingProfile") await data.Database.Collection("users").Document(uid).DeleteAsync();
        Assert.False(await data.Service.SyncEmailVerificationAsync(uid));
        var profile = await data.Users.GetByIdAsync(uid);
        if (state == "MissingProfile") Assert.Null(profile);
        else { Assert.NotNull(profile); Assert.False(profile.IsActive); }
    }

    [Fact]
    public async Task MissingAuthAccount_SyncThrowsWithoutActivatingProfile()
    {
        await using var data = new AuthWorkflowTestData();
        var uid = await data.MissingAccountWithProfileAsync();
        await data.Database.Collection("users").Document(uid).UpdateAsync("IsActive", false);
        var error = await Assert.ThrowsAsync<FirebaseAuthException>(() => data.Service.SyncEmailVerificationAsync(uid));
        Assert.Equal(AuthErrorCode.UserNotFound, error.AuthErrorCode);
        Assert.False((await data.Users.GetByIdAsync(uid))!.IsActive);
    }
}
