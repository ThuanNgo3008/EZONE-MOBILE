using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FirebaseAdmin.Auth;
using Google.Cloud.Firestore;
using WebChatEIU.IntegrationTests.AuthWorkflows;
using WebChatEIU.Models.Firestore;
using WebChatEIU.Services;

namespace WebChatEIU.IntegrationTests.HttpSecurity;

[Collection("Auth workflows")]
[Trait("Category", "HttpSecurity")]
public sealed class HttpJwtSecurityTests
{
    private const string Identity = "/api/firebase-auth-test/me";
    private const string Session = "/api/firebase-session/me";
    private const string Find = "/api/firebase-random-match/find";
    private const string Sync = "/api/firebase-auth/sync-verification";
    private const string Rooms = "/api/firebase-chat-rooms/";
    private const string Messages = "/api/firebase-chat-messages/";
    private static readonly object Report = new { reportType = "User", reason = "Fixture report" };

    [Theory]
    [InlineData("GET", Identity)]
    [InlineData("GET", Session)]
    [InlineData("POST", Sync)]
    [InlineData("GET", Rooms + "fixture")]
    [InlineData("GET", Messages + "fixture")]
    [InlineData("POST", Find)]
    [InlineData("POST", "/api/firebase-random-match/heartbeat")]
    [InlineData("POST", "/api/firebase-random-match/cancel")]
    [InlineData("POST", "/api/firebase-match-reservations/fixture/ready")]
    [InlineData("POST", Rooms + "fixture/unmatch")]
    [InlineData("POST", Rooms + "fixture/reports")]
    public async Task ProtectedEndpoints_ChallengeAnonymousRequests(string method, string path)
    {
        await using var data = new AuthWorkflowTestData();
        await using var host = new HttpSecurityHost(data);
        await host.StartAsync();
        using var response = await host.SendAsync(new HttpMethod(method), path);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(response.Headers.WwwAuthenticate, value => value.Scheme == "Bearer");
    }

    [Theory]
    [InlineData("Malformed")]
    [InlineData("Unsigned")]
    [InlineData("Signature")]
    [InlineData("Expired")]
    [InlineData("Issuer")]
    [InlineData("Audience")]
    public async Task StrictBearerHandler_RejectsInvalidTokens(string failure)
    {
        await using var data = new AuthWorkflowTestData();
        await using var host = new HttpSecurityHost(data);
        await host.StartAsync();
        var (_, unsigned) = await host.SignInAsync();
        var token = failure switch
        {
            "Malformed" => "not-a-jwt",
            "Unsigned" => unsigned,
            "Signature" => CorruptSignature(host.Sign(unsigned)),
            "Expired" => host.Sign(unsigned, payload =>
            {
                payload["exp"] = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds();
                payload["iat"] = DateTimeOffset.UtcNow.AddHours(-2).ToUnixTimeSeconds();
            }),
            "Issuer" => host.Sign(unsigned, payload => payload["iss"] = "https://wrong-issuer.invalid"),
            "Audience" => host.Sign(unsigned, payload => payload["aud"] = "wrong-audience"),
            _ => throw new ArgumentOutOfRangeException(nameof(failure))
        };
        // A premature account lookup would fail with 503 instead of the JWT challenge.
        data.RejectLookups = true;
        using var rejected = await host.SendAsync(HttpMethod.Get, Identity, token);
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        data.RejectLookups = false;
        await ExpectAsync(host, HttpMethod.Get, Identity, HttpStatusCode.OK, host.Sign(unsigned));
    }

    [Fact]
    public async Task SignedEmulatorClaims_AuthenticateAndReachSessionAndMatching()
    {
        await using var data = new AuthWorkflowTestData();
        await using var host = new HttpSecurityHost(data);
        await host.StartAsync();
        var (uid, unsigned) = await host.SignInAsync();
        var token = host.Sign(unsigned);
        using var response = await host.SendAsync(HttpMethod.Get, Session, token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(uid, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("userId").GetString());
        using var find = await host.SendAsync(HttpMethod.Post, Find, token);
        Assert.Equal(HttpStatusCode.OK, find.StatusCode);
        Assert.Equal("Waiting", (await find.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
        // The SignalR token event must not authenticate an ordinary REST path from its query.
        await ExpectAsync(host, HttpMethod.Get, Identity + "?access_token=" + Uri.EscapeDataString(token), HttpStatusCode.Unauthorized);
        await ExpectAsync(host, HttpMethod.Post, "/firebaseChatHub/negotiate?negotiateVersion=1", HttpStatusCode.Unauthorized);
        await ExpectAsync(host, HttpMethod.Post, "/firebaseChatHub/negotiate?negotiateVersion=1&access_token=" +
            Uri.EscapeDataString(token), HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("Deleted", HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized)]
    [InlineData("Disabled", HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized)]
    [InlineData("Unverified", HttpStatusCode.OK, HttpStatusCode.Forbidden)]
    public async Task AccountEligibility_IsEnforcedBySessionAndMatchingAfterBearerAuthentication(
        string state, HttpStatusCode identityStatus, HttpStatusCode sessionStatus)
    {
        await using var data = new AuthWorkflowTestData();
        await using var host = new HttpSecurityHost(data);
        await host.StartAsync();
        var (uid, unsigned) = await host.SignInAsync(verified: state != "Unverified");
        var token = host.Sign(unsigned);
        if (state == "Deleted") await data.Auth.DeleteUserAsync(uid);
        if (state == "Disabled") await data.Auth.UpdateUserAsync(new UserRecordArgs { Uid = uid, Disabled = true });

        await ExpectAsync(host, HttpMethod.Get, Identity, identityStatus, token);
        await ExpectAsync(host, HttpMethod.Get, Session, sessionStatus, token);
        await ExpectAsync(host, HttpMethod.Post, Find,
            state == "Unverified" ? HttpStatusCode.Conflict : HttpStatusCode.Unauthorized, token);
        Assert.False((await data.Database.Collection("randomMatchQueue").Document(uid).GetSnapshotAsync()).Exists);
    }

    [Fact]
    public async Task SessionRejectsAuthTimeBeforeRevocationCutoffEvenWithNewerIssuedAt()
    {
        await using var data = new AuthWorkflowTestData();
        await using var host = new HttpSecurityHost(data);
        await host.StartAsync();
        var (uid, unsigned) = await host.SignInAsync();
        await data.Auth.RevokeRefreshTokensAsync(uid);
        var account = await data.Auth.GetUserAsync(uid);
        Assert.Equal(DateTimeKind.Utc, account.TokensValidAfterTimestamp.Kind);
        var cutoff = new DateTimeOffset(account.TokensValidAfterTimestamp).ToUnixTimeSeconds();
        Assert.InRange(cutoff, DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds(),
            DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        // Simulate a refreshed token whose issuance time follows revocation,
        // while its original session predates the cutoff by one second.
        var token = host.Sign(unsigned, payload =>
        {
            payload["auth_time"] = cutoff - 1;
            payload["iat"] = cutoff + 1;
        });
        await ExpectAsync(host, HttpMethod.Get, Identity, HttpStatusCode.Unauthorized, token);
        await ExpectAsync(host, HttpMethod.Get, Session, HttpStatusCode.Unauthorized, token);
        var atCutoff = host.Sign(unsigned, payload =>
        {
            payload["auth_time"] = cutoff;
            payload["iat"] = cutoff;
        });
        await ExpectAsync(host, HttpMethod.Get, Session, HttpStatusCode.OK, atCutoff);
    }

    [Fact]
    public async Task AnonymousAuthEndpoints_ReachModelValidationAndResendRateLimiter()
    {
        await using var data = new AuthWorkflowTestData();
        await using var host = new HttpSecurityHost(data);
        await host.StartAsync();
        using var invalid = await host.SendAsync(HttpMethod.Post, "/api/firebase-auth/register", body: new { });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal("application/problem+json", invalid.Content.Headers.ContentType?.MediaType);
        var email = AuthWorkflowTestData.Registration().Email;
        for (var attempt = 0; attempt < 3; attempt++)
            await ExpectAsync(host, HttpMethod.Post, "/api/firebase-auth/resend-verification", HttpStatusCode.OK, body: new { email });
        await ExpectAsync(host, HttpMethod.Post, "/api/firebase-auth/resend-verification", HttpStatusCode.TooManyRequests, body: new { email });
        Assert.Empty(data.Email.Attempts);
    }

    [Fact]
    public async Task MembershipGuards_ProtectRoomHistoryReportAndUnmatch()
    {
        await using var data = new AuthWorkflowTestData();
        await using var host = new HttpSecurityHost(data);
        await host.StartAsync();
        var (memberId, memberUnsigned) = await host.SignInAsync();
        var (partnerId, _) = await host.SignInAsync();
        var (_, outsiderUnsigned) = await host.SignInAsync();
        var member = host.Sign(memberUnsigned);
        var outsider = host.Sign(outsiderUnsigned);
        var service = new FirebaseChatRoomService(data.Database);
        var room = await service.CreateRoomAsync(memberId, partnerId, "Fixture member", "Fixture partner");
        var reference = data.Database.Collection("chatRooms").Document(room.RoomId);
        var path = Rooms + room.RoomId;
        try
        {
            await ExpectAsync(host, HttpMethod.Post, Messages.TrimEnd('/'), HttpStatusCode.OK, member,
                new { roomId = room.RoomId, content = "Hello from a fixture" });
            await ExpectAsync(host, HttpMethod.Get, path, HttpStatusCode.OK, member);
            using var history = await host.SendAsync(HttpMethod.Get, Messages + room.RoomId, member);
            Assert.Equal(HttpStatusCode.OK, history.StatusCode);
            var message = Assert.Single((await history.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("messages").EnumerateArray());
            Assert.Equal("Hello from a fixture", message.GetProperty("content").GetString());
            await ExpectAsync(host, HttpMethod.Get, path, HttpStatusCode.Forbidden, outsider);
            await ExpectAsync(host, HttpMethod.Get, Messages + room.RoomId, HttpStatusCode.Forbidden, outsider);
            await ExpectAsync(host, HttpMethod.Post, path + "/unmatch", HttpStatusCode.Forbidden, outsider);
            await ExpectAsync(host, HttpMethod.Post, path + "/reports", HttpStatusCode.Forbidden, outsider, Report);
            Assert.Equal("Active", (await service.GetRoomByIdAsync(room.RoomId))!.Status);
            Assert.Empty((await data.Database.Collection("reports").WhereEqualTo("RoomId", room.RoomId).GetSnapshotAsync()).Documents);
            await ExpectAsync(host, HttpMethod.Post, path + "/reports", HttpStatusCode.BadRequest, member, new { });
            await ExpectAsync(host, HttpMethod.Post, path + "/reports", HttpStatusCode.OK, member, Report);
            await ExpectAsync(host, HttpMethod.Post, path + "/reports", HttpStatusCode.Conflict, member, Report);

            await data.Auth.UpdateUserAsync(new UserRecordArgs { Uid = memberId, Disabled = true });
            using var disabledHistory = await host.SendAsync(HttpMethod.Get, Messages + room.RoomId, member);
            Assert.Equal(HttpStatusCode.Unauthorized, disabledHistory.StatusCode);
            Assert.DoesNotContain("Hello from a fixture", await disabledHistory.Content.ReadAsStringAsync());
        }
        finally
        {
            foreach (var report in (await data.Database.Collection("reports").WhereEqualTo("RoomId", room.RoomId).GetSnapshotAsync()).Documents)
            {
                await DeleteChildrenAsync(report.Reference.Collection("evidence"));
                await report.Reference.DeleteAsync();
            }
            await DeleteChildrenAsync(reference.Collection("messages"));
            await reference.DeleteAsync();
            var history = await data.Database.Collection("matchHistory").WhereEqualTo("RoomId", room.RoomId).GetSnapshotAsync();
            foreach (var entry in history.Documents)
                await entry.Reference.DeleteAsync();
        }
    }

    [Fact]
    public async Task MissingResourcesAndReservationOwnership_UseExistingHttpContracts()
    {
        await using var data = new AuthWorkflowTestData();
        await using var host = new HttpSecurityHost(data);
        await host.StartAsync();
        var (memberId, memberUnsigned) = await host.SignInAsync();
        var (partnerId, _) = await host.SignInAsync();
        var (_, outsiderUnsigned) = await host.SignInAsync();
        var token = host.Sign(memberUnsigned);
        var missing = Guid.NewGuid().ToString("N");
        await ExpectAsync(host, HttpMethod.Get, Rooms + missing, HttpStatusCode.NotFound, token);
        await ExpectAsync(host, HttpMethod.Get, Messages + missing, HttpStatusCode.NotFound, token);
        await ExpectAsync(host, HttpMethod.Post, "/api/firebase-match-reservations/" + missing + "/ready", HttpStatusCode.Conflict, token);

        var reservations = new FirebaseMatchReservationService(data.Database, new FirebaseMatchEligibilityService(data.Auth));
        var id = await reservations.CreateReservationAsync(memberId, partnerId, "Random");
        var reference = data.Database.Collection("matchReservations").Document(id);
        try
        {
            var path = "/api/firebase-match-reservations/" + id + "/ready";
            await ExpectAsync(host, HttpMethod.Post, path, HttpStatusCode.Forbidden, host.Sign(outsiderUnsigned));
            Assert.False((await reference.GetSnapshotAsync()).ConvertTo<FirebaseMatchReservation>().User1Ready);
            await ExpectAsync(host, HttpMethod.Post, path, HttpStatusCode.OK, token);
            Assert.True((await reference.GetSnapshotAsync()).ConvertTo<FirebaseMatchReservation>().User1Ready);
        }
        finally
        {
            await reference.DeleteAsync();
            await data.Database.Collection("matchSessions").Document(memberId).DeleteAsync();
            await data.Database.Collection("matchSessions").Document(partnerId).DeleteAsync();
        }
    }

    [Fact]
    public async Task VerificationSync_ReturnsControlled401WhenAccountDisappearsAfterAuthenticationLookup()
    {
        await using var data = new AuthWorkflowTestData();
        await using var host = new HttpSecurityHost(data);
        await host.StartAsync();
        var (uid, unsigned) = await host.SignInAsync();
        var token = host.Sign(unsigned);
        await ExpectAsync(host, HttpMethod.Get, Identity, HttpStatusCode.OK, token);
        data.AfterAccountLookup = async () =>
        {
            data.AfterAccountLookup = null;
            await data.Auth.DeleteUserAsync(uid);
        };
        using var response = await host.SendAsync(HttpMethod.Post, Sync, token);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Firebase account is unavailable.",
            (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString());
    }

    [Theory]
    [InlineData("sub", "missing")]
    [InlineData("user_id", "missing")]
    [InlineData("user_id", "other-user")]
    [InlineData("user_id", "array")]
    [InlineData("auth_time", "missing")]
    [InlineData("auth_time", "not-a-timestamp")]
    [InlineData("auth_time", "numeric-string")]
    [InlineData("auth_time", "future")]
    [InlineData("auth_time", "array")]
    [InlineData("auth_time", "milliseconds")]
    [InlineData("auth_time", "fractional")]
    public async Task InvalidSessionClaimsAreRejectedBeforeAccountLookup(string claim, string value)
    {
        await using var data = new AuthWorkflowTestData();
        await using var host = new HttpSecurityHost(data);
        await host.StartAsync();
        var (_, unsigned) = await host.SignInAsync();
        data.RejectLookups = true;
        var token = host.Sign(unsigned, payload =>
        {
            if (value == "missing") payload.Remove(claim);
            else if (value == "future") payload[claim] = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
            else if (value == "numeric-string") payload[claim] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            else if (value == "array") payload[claim] = new[] { payload[claim] };
            else if (value == "milliseconds") payload[claim] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            else if (value == "fractional") payload[claim] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 1.5;
            else payload[claim] = value;
        });
        await ExpectAsync(host, HttpMethod.Get, Identity, HttpStatusCode.Unauthorized, token);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task AccountLookupFailure_ReturnsOneEmpty503WithoutExecutingAction(HttpStatusCode upstreamStatus)
    {
        await using var data = new AuthWorkflowTestData();
        await using var host = new HttpSecurityHost(data);
        await host.StartAsync();
        var (uid, unsigned) = await host.SignInAsync();
        data.LookupFailureStatus = upstreamStatus;
        using var response = await host.SendAsync(HttpMethod.Post, Find, host.Sign(unsigned));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Empty(response.Headers.WwwAuthenticate);
        Assert.Empty(await response.Content.ReadAsStringAsync());
        Assert.False((await data.Database.Collection("randomMatchQueue").Document(uid).GetSnapshotAsync()).Exists);
    }

    [Fact]
    public async Task VerificationSync_DoesNotMapUnrelatedLookupFailureTo401()
    {
        await using var data = new AuthWorkflowTestData();
        await using var host = new HttpSecurityHost(data);
        await host.StartAsync(useKestrel: true);
        var (_, unsigned) = await host.SignInAsync();
        data.AfterAccountLookup = () =>
        {
            data.RejectLookups = true;
            return Task.CompletedTask;
        };
        await ExpectAsync(host, HttpMethod.Post, Sync, HttpStatusCode.InternalServerError, host.Sign(unsigned));
    }

    [Fact]
    public async Task UnverifiedAccountCanResendAndSynchronizeAfterVerifying()
    {
        await using var data = new AuthWorkflowTestData();
        await using var host = new HttpSecurityHost(data);
        await host.StartAsync();
        var (uid, unsigned) = await host.SignInAsync(verified: false);
        var token = host.Sign(unsigned);
        await ExpectAsync(host, HttpMethod.Post, Sync, HttpStatusCode.BadRequest, token);
        var user = await data.Auth.GetUserAsync(uid);
        await ExpectAsync(host, HttpMethod.Post, "/api/firebase-auth/resend-verification", HttpStatusCode.OK,
            body: new { email = user.Email });
        await data.VerifyEmailAsync(Assert.Single(data.Email.Attempts));
        await ExpectAsync(host, HttpMethod.Post, Sync, HttpStatusCode.OK, token);
    }

    private static string CorruptSignature(string token)
    {
        var signatureStart = token.LastIndexOf('.') + 1;
        return token[..signatureStart] + (token[signatureStart] == 'A' ? 'B' : 'A') + token[(signatureStart + 1)..];
    }

    private static async Task ExpectAsync(HttpSecurityHost host, HttpMethod method, string path, HttpStatusCode status,
        string? token = null, object? body = null)
    {
        using var response = await host.SendAsync(method, path, token, body);
        Assert.Equal(status, response.StatusCode);
    }

    private static async Task DeleteChildrenAsync(CollectionReference collection)
    {
        foreach (var document in (await collection.GetSnapshotAsync()).Documents)
            await document.Reference.DeleteAsync();
    }
}
