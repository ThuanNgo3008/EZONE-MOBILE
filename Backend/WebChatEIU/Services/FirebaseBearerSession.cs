using FirebaseAdmin.Auth;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.JsonWebTokens;

namespace WebChatEIU.Services;

internal static class FirebaseBearerSession
{
    private static readonly object AuthUnavailable = new();

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

        var auth = context.HttpContext.RequestServices.GetRequiredService<FirebaseAuth>();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.HttpContext.RequestAborted);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var user = await auth.GetUserAsync(uid, timeout.Token);
            // The Admin SDK exposes validSince as a UTC DateTime.
            var authenticatedAt = DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
            if (user.Disabled || authenticatedAt < user.TokensValidAfterTimestamp)
                context.Fail("Firebase session is unavailable.");
        }
        catch (FirebaseAuthException ex) when (ex.AuthErrorCode == AuthErrorCode.UserNotFound)
        {
            context.Fail("Firebase session is unavailable.");
        }
        catch (Exception ex) when (ex is FirebaseAuthException or HttpRequestException ||
                                   ex is OperationCanceledException && !context.HttpContext.RequestAborted.IsCancellationRequested)
        {
            context.HttpContext.Items[AuthUnavailable] = true;
            context.Fail("Firebase account lookup failed.");
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
