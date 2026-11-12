using System.Text.RegularExpressions;

namespace WebChatEIU.IntegrationTests.Auth;

internal sealed record AuthEmulatorSettings(string ProjectId, string FirestoreHost, string AuthHost)
{
    public static AuthEmulatorSettings FromEnvironment() => Validate(
        Environment.GetEnvironmentVariable("EZONE_TEST_PROJECT_ID"),
        Environment.GetEnvironmentVariable("FIRESTORE_EMULATOR_HOST"),
        Environment.GetEnvironmentVariable("FIREBASE_AUTH_EMULATOR_HOST"),
        Environment.GetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS"));

    public static AuthEmulatorSettings Validate(string? projectId, string? firestoreHost,
        string? authHost, string? credentialPath = null)
    {
        if (!string.IsNullOrEmpty(credentialPath))
            throw new InvalidOperationException("Inherited GOOGLE_APPLICATION_CREDENTIALS is forbidden for Auth emulator tests.");

        // Preserve and reuse the existing demo-project and Firestore endpoint rules.
        var firestore = FirestoreEmulatorSettings.Validate(projectId, firestoreHost);
        var match = Regex.Match(authHost ?? "", @"\A(?:127\.0\.0\.1|\[::1\]):([0-9]{1,5})\z");
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var port) || port < 1 || port > 65535)
            throw new InvalidOperationException("FIREBASE_AUTH_EMULATOR_HOST must be 127.0.0.1:port or [::1]:port, without a URL scheme.");
        return new AuthEmulatorSettings(firestore.ProjectId, firestore.Host, authHost!);
    }

    public void AssertEnvironmentUnchanged()
    {
        if (FromEnvironment() != this)
            throw new InvalidOperationException("Auth emulator configuration changed during the fixture lifetime.");
    }

    public void AssertRequestAllowed(Uri? uri)
    {
        // Reject even manually constructed invalid settings before considering a request.
        Validate(ProjectId, FirestoreHost, AuthHost);
        var projectPath = $"/identitytoolkit.googleapis.com/v1/projects/{ProjectId}/";
        if (uri is null || !uri.IsAbsoluteUri || uri.Scheme != "http" ||
            uri.Authority != AuthHost || uri.UserInfo.Length != 0 ||
            !uri.AbsolutePath.StartsWith(projectPath, StringComparison.Ordinal))
            throw new InvalidOperationException("Auth request rejected: only the configured loopback demo-project endpoint is allowed.");
    }
}
