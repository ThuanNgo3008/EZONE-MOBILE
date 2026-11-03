namespace WebChatEIU.IntegrationTests;

[Trait("Category", "Safety")]
public sealed class FirestoreEmulatorSafetyTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ezone-afa80")]
    [InlineData("other-project")]
    [InlineData("demo-other")]
    public void MissingOrUnapprovedProject_IsRejected(string? project) =>
        Assert.Throws<InvalidOperationException>(() =>
            FirestoreEmulatorSettings.Validate(project, "127.0.0.1:8085"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("localhost:8085")]
    [InlineData("0.0.0.0:8085")]
    [InlineData("192.168.1.10:8085")]
    [InlineData("firestore.googleapis.com:443")]
    [InlineData("[::]:8085")]
    [InlineData("http://127.0.0.1:8085")]
    [InlineData("127.0.0.1")]
    [InlineData("127.0.0.1:0")]
    [InlineData("127.0.0.1:65536")]
    [InlineData("127.0.0.1:8085/path")]
    [InlineData("127.0.0.1:8085?host=remote")]
    [InlineData("user@127.0.0.1:8085")]
    [InlineData("127.0.0.1:8085\n")]
    public void MissingOrUnsafeEndpoint_IsRejected(string? host) =>
        Assert.Throws<InvalidOperationException>(() =>
            FirestoreEmulatorSettings.Validate(FirestoreEmulatorSettings.DemoProjectId, host));

    [Theory]
    [InlineData("127.0.0.1:8085")]
    [InlineData("[::1]:8085")]
    public void ExplicitLoopbackEndpoint_IsAccepted(string host)
    {
        var settings = FirestoreEmulatorSettings.Validate("demo-ezone-regression", host);
        Assert.Equal("demo-ezone-regression", settings.ProjectId);
        Assert.Equal(host, settings.Host);
    }
}
