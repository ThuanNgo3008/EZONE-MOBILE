using FirebaseAdmin.Auth;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.JsonWebTokens;
using WebChatEIU.Hubs;

namespace WebChatEIU.Services;

internal static class FirebaseBearerSession
{
    private static readonly object AuthUnavailable = new();
    internal static readonly object ValidatedSessionKey = new();

    internal sealed record Session(string Uid, DateTime AuthenticatedAt, DateTime ExpiresAt);

    internal readonly record struct AccountStatus(DateTime? ValidAfter, bool Unavailable = false)
    {
        public bool Allows(Session session) =>
            ValidAfter is DateTime cutoff && session.AuthenticatedAt >= cutoff;
    }

    public static async Task ValidateAsync(TokenValidatedContext context)
    {
        // Inspect original payload types. Claims enumeration can flatten singleton arrays.
        if (context.SecurityToken is not JsonWebToken token ||
            !token.TryGetPayloadValue<object>("sub", out var subject) ||
            subject is not string uid || string.IsNullOrWhiteSpace(uid) || uid.Length > 128 ||
            !token.TryGetPayloadValue<object>("user_id", out var userId) ||
            userId is not string firebaseUid || !string.Equals(uid, firebaseUid, StringComparison.Ordinal) ||
            !token.TryGetPayloadValue<object>("auth_time", out var authTime) ||
            !TryGetAuthTimeSeconds(authTime, out var seconds) ||
            seconds <= 0 || seconds > DateTimeOffset.UtcNow.ToUnixTimeSeconds())
        {
            context.Fail("Invalid Firebase session claims.");
            return;
        }

        var session = new Session(uid, DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime, token.ValidTo);
        if (context.HttpContext.Request.Path.StartsWithSegments(FirebaseChatHub.Route) &&
            session.ExpiresAt <= DateTime.UtcNow)
        {
            context.Fail("Firebase session has expired.");
            return;
        }

        var auth = context.HttpContext.RequestServices.GetRequiredService<FirebaseAuth>();
        var status = await GetAccountStatusAsync(auth, uid, context.HttpContext.RequestAborted);
        if (status.Unavailable)
        {
            context.HttpContext.Items[AuthUnavailable] = true;
            context.Fail("Firebase account lookup failed.");
        }
        else if (!status.Allows(session))
        {
            context.Fail("Firebase session is unavailable.");
        }
        else
        {
            context.HttpContext.Items[ValidatedSessionKey] = session;
        }
    }

    internal static async Task<AccountStatus> GetAccountStatusAsync(
        FirebaseAuth auth, string uid, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var user = await auth.GetUserAsync(uid, timeout.Token);
            return user.Disabled ? default : new(user.TokensValidAfterTimestamp);
        }
        catch (FirebaseAuthException ex) when (ex.AuthErrorCode == AuthErrorCode.UserNotFound)
        {
            return default;
        }
        catch (Exception ex) when (ex is FirebaseAuthException or HttpRequestException ||
                                   ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return new(null, Unavailable: true);
        }
    }

    public static Task ChallengeAsync(JwtBearerChallengeContext context)
    {
        if (context.HttpContext.Items.ContainsKey(AuthUnavailable))
        {
            context.HandleResponse();
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        }

        return Task.CompletedTask;
    }

    private static bool TryGetAuthTimeSeconds(object value, out long seconds)
    {
        seconds = value switch
        {
            int number => number,
            long number => number,
            _ => 0
        };
        return seconds > 0;
    }
}
