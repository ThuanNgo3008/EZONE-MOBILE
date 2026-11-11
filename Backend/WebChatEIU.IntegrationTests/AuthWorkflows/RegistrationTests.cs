using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using FirebaseAdmin.Auth;
using Grpc.Core;
using Microsoft.AspNetCore.Mvc;
using WebChatEIU.Controllers;
using WebChatEIU.Models.Firestore;
using WebChatEIU.Services;

namespace WebChatEIU.IntegrationTests.AuthWorkflows;

[Collection("Auth workflows")]
[Trait("Category", "AuthWorkflow")]
public sealed class RegistrationTests
{
    [Fact]
    public async Task Registration_CreatesAuthAndProfile_PreservesInformation_AndRequiresVerification()
    {
        await using var data = new AuthWorkflowTestData();
        var dto = AuthWorkflowTestData.Registration();
        var uid = await data.Service.RegisterAsync(dto);
        var account = await data.Auth.GetUserAsync(uid);
        Assert.Equal(dto.Email, account.Email);
        Assert.Equal(dto.Fullname, account.DisplayName);
        Assert.False(account.EmailVerified);
        Assert.False(account.Disabled);
        var profile = await data.Users.GetByIdAsync(uid);
        Assert.NotNull(profile);
        Assert.Equal(uid, profile.UserId);
        Assert.Equal(dto.Email, profile.Email);
        Assert.Equal(dto.Fullname, profile.Fullname);
        Assert.Equal(dto.MajorCode, profile.MajorCode);
        Assert.Equal(dto.Cohort, profile.Cohort);
        Assert.Equal(dto.BirthYear, profile.BirthYear);
        Assert.Equal(dto.Interests, profile.Interests);
        Assert.False(profile.IsActive || profile.IsBanned || profile.IsDeleted);
        var stored = (await data.Database.Collection("users").Document(uid).GetSnapshotAsync()).ToDictionary();
        Assert.DoesNotContain("Password", stored.Keys);
        Assert.DoesNotContain("ActiveCode", stored.Keys);
        var email = Assert.Single(data.Email.Attempts);
        Assert.Equal(dto.Email, email.Address);
        Assert.Equal(dto.Fullname, email.Name);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            data.Queue.JoinQueueAsync(uid));
        Assert.False((await data.Database.Collection("randomMatchQueue").Document(uid).GetSnapshotAsync()).Exists);
    }

    [Fact]
    public async Task DuplicateEmail_IsRejectedWithoutReplacingOriginalProfile()
    {
        await using var data = new AuthWorkflowTestData();
        var dto = AuthWorkflowTestData.Registration();
        var uid = await data.Service.RegisterAsync(dto);
        var before = (await data.Users.GetByIdAsync(uid))!;
        var error = await Assert.ThrowsAsync<FirebaseAuthException>(() => data.Service.RegisterAsync(dto));
        Assert.Equal(AuthErrorCode.EmailAlreadyExists, error.AuthErrorCode);
        Assert.Equal(uid, (await data.Auth.GetUserByEmailAsync(dto.Email)).Uid);
        Assert.Equal(before.CreatedDate, (await data.Users.GetByIdAsync(uid))!.CreatedDate);
        Assert.Single(data.Email.Attempts);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SdkInvalidEmailOrShortPassword_IsRejectedBeforeProfileCreation(bool invalidEmail)
    {
        await using var data = new AuthWorkflowTestData();
        var dto = AuthWorkflowTestData.Registration();
        if (invalidEmail) dto.Email = "not-an-email";
        else dto.Password = "12345";
        await Assert.ThrowsAnyAsync<ArgumentException>(() => data.Service.RegisterAsync(dto));
        Assert.Empty(data.Email.Attempts);
        Assert.Empty((await data.Database.Collection("users").WhereEqualTo("Email", dto.Email).GetSnapshotAsync()).Documents);
    }

    [Fact]
    public async Task DtoOnlySchoolEmailRule_IsNotAppliedByDirectServiceCalls()
    {
        await using var data = new AuthWorkflowTestData();
        var dto = AuthWorkflowTestData.Registration();
        dto.Email = "workflow-" + Guid.NewGuid().ToString("N") + "@example.invalid";
        var errors = new List<ValidationResult>();
        Assert.False(Validator.TryValidateObject(dto, new ValidationContext(dto), errors, true));
        Assert.Contains(errors, e => e.MemberNames.Contains(nameof(dto.Email)));
        // Characterize the service boundary; HTTP ApiController validation is not invoked here.
        var uid = await data.Service.RegisterAsync(dto);
        Assert.Equal(dto.Email, (await data.Auth.GetUserAsync(uid)).Email);
        Assert.Equal(dto.Email, (await data.Users.GetByIdAsync(uid))!.Email);
    }

    [Fact]
    public async Task EmailFailure_PreservesCreatedRecords_AndControllerReportsDeliveryFailure()
    {
        await using var data = new AuthWorkflowTestData();
        var dto = AuthWorkflowTestData.Registration();
        data.Email.Failure = new IOException("Synthetic email delivery failure.");
        var result = Assert.IsType<OkObjectResult>(await new FirebaseAuthController(data.Service).Register(dto));
        var body = JsonSerializer.SerializeToElement(result.Value);
        Assert.True(body.GetProperty("emailVerificationRequired").GetBoolean());
        Assert.False(body.GetProperty("verificationEmailSent").GetBoolean());
        var uid = body.GetProperty("userId").GetString()!;
        Assert.Equal(dto.Email, (await data.Auth.GetUserAsync(uid)).Email);
        Assert.False((await data.Users.GetByIdAsync(uid))!.IsActive);
        Assert.Single(data.Email.Attempts);
    }

    [Fact]
    public async Task ProfileCreateConflict_RollsBackNewAuthAccount_PreservesExistingDocument()
    {
        await using var data = new AuthWorkflowTestData();
        var dto = AuthWorkflowTestData.Registration();
        string? createdUid = null;
        data.AfterAccountCreated = async uid =>
        {
            createdUid = uid;
            await data.Users.CreateAsync(new FirebaseUser
            {
                UserId = uid, Email = dto.Email, Fullname = "Pre-existing fixture profile"
            });
        };
        var error = await Assert.ThrowsAsync<RpcException>(() => data.Service.RegisterAsync(dto));
        Assert.Equal(StatusCode.AlreadyExists, error.StatusCode);
        Assert.NotNull(createdUid);
        var missing = await Assert.ThrowsAsync<FirebaseAuthException>(() => data.Auth.GetUserAsync(createdUid));
        Assert.Equal(AuthErrorCode.UserNotFound, missing.AuthErrorCode);
        Assert.Equal("Pre-existing fixture profile", (await data.Users.GetByIdAsync(createdUid))!.Fullname);
        Assert.Empty(data.Email.Attempts);
    }

    [Fact]
    public async Task DuplicateProfileCreate_DoesNotOverwriteExistingProfile()
    {
        await using var data = new AuthWorkflowTestData();
        var uid = await data.AccountWithProfileAsync();
        var before = (await data.Users.GetByIdAsync(uid))!;
        var error = await Assert.ThrowsAsync<RpcException>(() => data.Users.CreateAsync(new FirebaseUser { UserId = uid, Fullname = "Replacement" }));
        Assert.Equal(StatusCode.AlreadyExists, error.StatusCode);
        Assert.Equal(before.Fullname, (await data.Users.GetByIdAsync(uid))!.Fullname);
    }

    [Fact]
    public async Task MissingProfile_ReadReturnsNull_ActivationDoesNotCreateIt()
    {
        await using var data = new AuthWorkflowTestData();
        var uid = "missing-" + Guid.NewGuid().ToString("N");
        Assert.Null(await data.Users.GetByIdAsync(uid));
        var error = await Assert.ThrowsAsync<RpcException>(() => data.Users.ActivateUserAsync(uid));
        Assert.Equal(StatusCode.NotFound, error.StatusCode);
        Assert.Null(await data.Users.GetByIdAsync(uid));
    }

    [Fact]
    public async Task ProfileCreate_RequiresUid()
    {
        await using var data = new AuthWorkflowTestData();
        await Assert.ThrowsAsync<ArgumentException>(() => data.Users.CreateAsync(new FirebaseUser()));
    }
}
