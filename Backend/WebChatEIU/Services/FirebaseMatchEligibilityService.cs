using FirebaseAdmin.Auth;
using WebChatEIU.Models.Firestore;

namespace WebChatEIU.Services;

// Auth is checked outside Firestore transactions; profile eligibility is checked inside.
public sealed class FirebaseMatchEligibilityService(FirebaseAuth auth)
{
    public static bool HasEligibleAuthAccount(UserRecord user) =>
        user.EmailVerified && !user.Disabled;

    public static bool HasEligibleProfile(FirebaseUser? user) =>
        user is { IsActive: true, IsBanned: false, IsDeleted: false };

    public async Task<bool> IsAuthEligibleAsync(string userId)
    {
        try
        {
            return HasEligibleAuthAccount(await auth.GetUserAsync(userId));
        }
        catch (FirebaseAuthException ex) when (ex.AuthErrorCode == AuthErrorCode.UserNotFound)
        {
            return false;
        }
        catch (Exception ex) when (ex is FirebaseAuthException or HttpRequestException or TaskCanceledException)
        {
            // An unavailable Auth service is never interpreted as an eligible account.
            throw new InvalidOperationException("Unable to verify matching eligibility.", ex);
        }
    }

    public async Task EnsureAuthEligibleAsync(string userId)
    {
        if (!await IsAuthEligibleAsync(userId))
            throw new InvalidOperationException("User is not eligible for matching.");
    }
}
