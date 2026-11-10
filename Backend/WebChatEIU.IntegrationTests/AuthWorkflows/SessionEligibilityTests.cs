using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using WebChatEIU.Controllers;
using WebChatEIU.Services;

namespace WebChatEIU.IntegrationTests.AuthWorkflows;

[Collection("Auth workflows")]
[Trait("Category", "AuthWorkflow")]
public sealed class SessionEligibilityTests
{
    // Invoke only the action body. The principal is a test fixture, not a verified JWT.
    private static FirebaseSessionController Session(AuthWorkflowTestData data, string? uid) => new(data.Users)
    {
        ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    uid is null ? Array.Empty<Claim>() : new[] { new Claim("user_id", uid) }, "FixturePrincipal"))
            }
        }
    };

    private static int Status(IActionResult result) => result switch
    {
        StatusCodeResult status => status.StatusCode,
        ObjectResult body => body.StatusCode ?? 200,
        _ => throw new InvalidOperationException("Unexpected session result type.")
    };

    [Theory]
    [InlineData("VerifiedActive", 200)]
    [InlineData("Unverified", 403)]
    [InlineData("Inactive", 403)]
    [InlineData("Banned", 403)]
    [InlineData("Deleted", 403)]
    [InlineData("Disabled", 403)]
    [InlineData("MissingAuth", 401)]
    [InlineData("MissingProfile", 404)]
    [InlineData("MissingClaim", 401)]
    public async Task SessionAction_EnforcesCurrentAccountAndProfileEligibility(string state, int expectedStatus)
    {
        await using var data = new AuthWorkflowTestData();
        string? uid;
        if (state == "MissingClaim") uid = null;
        else if (state == "MissingAuth") uid = await data.MissingAccountWithProfileAsync();
        else
        {
            uid = await data.AccountWithProfileAsync(verified: state != "Unverified", active: state != "Inactive",
                banned: state == "Banned", deleted: state == "Deleted", disabled: state == "Disabled");
            if (state == "MissingProfile") await data.Database.Collection("users").Document(uid).DeleteAsync();
        }
        var result = await Session(data, uid).GetMe();
        Assert.Equal(expectedStatus, Status(result));
        if (expectedStatus == 200)
        {
            var body = System.Text.Json.JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(result).Value);
            Assert.True(body.GetProperty("success").GetBoolean());
            Assert.Equal(uid, body.GetProperty("userId").GetString());
        }
    }

    [Theory]
    [InlineData("Inactive")]
    [InlineData("Banned")]
    [InlineData("Deleted")]
    [InlineData("MissingProfile")]
    public async Task IneligibleFirestoreProfile_CannotJoinQueue(string state)
    {
        await using var data = new AuthWorkflowTestData();
        var uid = await data.AccountWithProfileAsync(active: state != "Inactive",
            banned: state == "Banned", deleted: state == "Deleted");
        if (state == "MissingProfile") await data.Database.Collection("users").Document(uid).DeleteAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new FirebaseRandomMatchQueueService(data.Database).JoinQueueAsync(uid));
        Assert.False((await data.Database.Collection("randomMatchQueue").Document(uid).GetSnapshotAsync()).Exists);
    }

    [Theory]
    [InlineData("Unverified")]
    [InlineData("Disabled")]
    public async Task EligibilityGap_ActiveProfileCanJoinQueueWhenAuthAccountIsUnavailable(string state)
    {
        await using var data = new AuthWorkflowTestData();
        var uid = await data.AccountWithProfileAsync(verified: state != "Unverified", disabled: state == "Disabled");
        Assert.Equal(403, Status(await Session(data, uid).GetMe()));
        Assert.False(await data.Service.SyncEmailVerificationAsync(uid));
        // Characterization of a cross-workflow eligibility gap, not an endorsed new rule.
        Assert.Equal("Waiting", await new FirebaseRandomMatchQueueService(data.Database).JoinQueueAsync(uid));
        var entry = await data.Database.Collection("randomMatchQueue").Document(uid).GetSnapshotAsync();
        Assert.Equal("Waiting", entry.GetValue<string>("Status"));
    }
}
