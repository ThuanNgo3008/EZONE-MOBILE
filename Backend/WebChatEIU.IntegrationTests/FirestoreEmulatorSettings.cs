using System.Text.RegularExpressions;
using Google.Api.Gax;
using Google.Cloud.Firestore;

namespace WebChatEIU.IntegrationTests;

internal sealed record FirestoreEmulatorSettings(string ProjectId, string Host)
{
    public const string DemoProjectId = "demo-ezone-regression";

    public static FirestoreEmulatorSettings FromEnvironment()
    {
        // No credentials are needed. Refuse an inherited service-account path.
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS")))
            throw new InvalidOperationException("Clear GOOGLE_APPLICATION_CREDENTIALS in the test process.");

        return Validate(Environment.GetEnvironmentVariable("EZONE_TEST_PROJECT_ID"),
            Environment.GetEnvironmentVariable("FIRESTORE_EMULATOR_HOST"));
    }

    public static FirestoreEmulatorSettings Validate(string? projectId, string? host)
    {
        if (!string.Equals(projectId, DemoProjectId, StringComparison.Ordinal))
            throw new InvalidOperationException("Only demo-ezone-regression is permitted; production and missing project IDs are rejected.");

        // Exact IP literals avoid DNS resolution, wildcard binds, URL paths and credentials.
        var match = Regex.Match(host ?? "", @"\A(?:127\.0\.0\.1|\[::1\]):([0-9]{1,5})\z");
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var port) || port < 1 || port > 65535)
            throw new InvalidOperationException("FIRESTORE_EMULATOR_HOST must be 127.0.0.1:port or [::1]:port.");

        return new FirestoreEmulatorSettings(projectId!, host!);
    }

    public FirestoreDb CreateDatabase()
    {
        // Re-check immediately before construction; the SDK reads this environment variable.
        var current = FromEnvironment();
        if (current != this)
            throw new InvalidOperationException("Emulator settings changed before client construction.");

        return new FirestoreDbBuilder
        {
            ProjectId = ProjectId,
            EmulatorDetection = EmulatorDetection.EmulatorOnly
        }.Build();
    }
}
