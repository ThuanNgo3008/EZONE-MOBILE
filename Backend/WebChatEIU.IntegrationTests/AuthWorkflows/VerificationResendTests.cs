using FirebaseAdmin.Auth;
using Microsoft.AspNetCore.Mvc;
using WebChatEIU.Controllers;
using WebChatEIU.DTOs;
using WebChatEIU.Services;

namespace WebChatEIU.IntegrationTests.AuthWorkflows;

[Collection("Auth workflows")]
[Trait("Category", "AuthWorkflow")]
public sealed class VerificationResendTests
{
    [Fact]
    public async Task UnverifiedAccount_ReceivesAnotherCapturedVerificationLink()
    {
        await using var data = new AuthWorkflowTestData();
        var dto = AuthWorkflowTestData.Registration();
        var uid = await data.Service.RegisterAsync(dto);
        var before = (await data.Users.GetByIdAsync(uid))!;
        await data.Service.ResendVerificationEmailAsync(dto.Email);
        Assert.Equal(2, data.Email.Attempts.Count);
        var email = data.Email.Attempts[1];
        Assert.Equal(dto.Email, email.Address);
        Assert.Equal(dto.Fullname, email.Name);
        await data.VerifyEmailAsync(email);
        Assert.True((await data.Auth.GetUserAsync(uid)).EmailVerified);
        Assert.False((await data.Users.GetByIdAsync(uid))!.IsActive);
        Assert.Equal(before.CreatedDate, (await data.Users.GetByIdAsync(uid))!.CreatedDate);
    }

    [Fact]
    public async Task AlreadyVerifiedAccount_DoesNotReceiveResend()
    {
        await using var data = new AuthWorkflowTestData();
        var uid = await data.AccountWithProfileAsync();
        await data.Service.ResendVerificationEmailAsync((await data.Auth.GetUserAsync(uid)).Email);
        Assert.Empty(data.Email.Attempts);
        Assert.True((await data.Users.GetByIdAsync(uid))!.IsActive);
    }

    [Fact]
    public async Task MissingAccount_ServiceRejects_ControllerReturnsGenericOk()
    {
        await using var data = new AuthWorkflowTestData();
        var email = AuthWorkflowTestData.Registration().Email;
        var error = await Assert.ThrowsAsync<FirebaseAuthException>(() => data.Service.ResendVerificationEmailAsync(email));
        Assert.Equal(AuthErrorCode.UserNotFound, error.AuthErrorCode);
        var result = await new FirebaseAuthController(data.Service).ResendVerification(new ResendVerificationEmailDto { Email = email });
        Assert.IsType<OkObjectResult>(result);
        Assert.Empty(data.Email.Attempts);
        var missing = await Assert.ThrowsAsync<FirebaseAuthException>(() => data.Auth.GetUserByEmailAsync(email));
        Assert.Equal(AuthErrorCode.UserNotFound, missing.AuthErrorCode);
    }

    [Fact]
    public async Task MalformedEmail_DirectServiceRejectsItWithoutEmailAttempt()
    {
        await using var data = new AuthWorkflowTestData();
        var error = await Assert.ThrowsAsync<FirebaseAuthException>(() => data.Service.ResendVerificationEmailAsync("not-an-email"));
        Assert.Equal(AuthErrorCode.UserNotFound, error.AuthErrorCode);
        Assert.Empty(data.Email.Attempts);
    }

    [Fact]
    public async Task ResendEmailFailure_PropagatesAndPreservesAccountAndProfile()
    {
        await using var data = new AuthWorkflowTestData();
        var uid = await data.AccountWithProfileAsync(verified: false, active: false);
        var account = await data.Auth.GetUserAsync(uid);
        var before = (await data.Users.GetByIdAsync(uid))!;
        var failure = new IOException("Synthetic resend delivery failure.");
        data.Email.Failure = failure;
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => data.Service.ResendVerificationEmailAsync(account.Email)));
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() =>
            new FirebaseAuthController(data.Service).ResendVerification(new ResendVerificationEmailDto { Email = account.Email })));
        Assert.Equal(2, data.Email.Attempts.Count);
        Assert.False((await data.Auth.GetUserAsync(uid)).EmailVerified);
        Assert.Equal(before.CreatedDate, (await data.Users.GetByIdAsync(uid))!.CreatedDate);
        Assert.False((await data.Users.GetByIdAsync(uid))!.IsActive);
    }

    [Fact]
    public async Task RegistrationEmailFailure_CanRecoverThroughResendAndVerification()
    {
        await using var data = new AuthWorkflowTestData();
        var dto = AuthWorkflowTestData.Registration();
        var failure = new IOException("Synthetic initial delivery failure.");
        data.Email.Failure = failure;
        var error = await Assert.ThrowsAsync<VerificationEmailDeliveryException>(() => data.Service.RegisterAsync(dto));
        Assert.Same(failure, error.InnerException);
        Assert.Equal(error.UserId, (await data.Auth.GetUserByEmailAsync(dto.Email)).Uid);
        Assert.False((await data.Users.GetByIdAsync(error.UserId))!.IsActive);
        data.Email.Failure = null;
        await data.Service.ResendVerificationEmailAsync(dto.Email);
        Assert.Equal(2, data.Email.Attempts.Count);
        await data.VerifyEmailAsync(data.Email.Attempts[1]);
        Assert.True(await data.Service.SyncEmailVerificationAsync(error.UserId));
        Assert.True((await data.Users.GetByIdAsync(error.UserId))!.IsActive);
    }
}
