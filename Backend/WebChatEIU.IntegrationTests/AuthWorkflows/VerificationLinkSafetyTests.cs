using WebChatEIU.IntegrationTests.Auth;

namespace WebChatEIU.IntegrationTests.AuthWorkflows;

[Trait("Category", "AuthWorkflowSafety")]
public sealed class VerificationLinkSafetyTests
{
    [Theory]
    [InlineData("https://example.com/emulator/action?mode=verifyEmail&apiKey=fake-api-key&oobCode=fixture")]
    [InlineData("http://192.168.1.10:9099/emulator/action?mode=verifyEmail&apiKey=fake-api-key&oobCode=fixture")]
    [InlineData("http://127.0.0.1:9999/emulator/action?mode=verifyEmail&apiKey=fake-api-key&oobCode=fixture")]
    [InlineData("http://user@127.0.0.1:9099/emulator/action?mode=verifyEmail&apiKey=fake-api-key&oobCode=fixture")]
    [InlineData("http://127.0.0.1:9099/other?mode=verifyEmail&apiKey=fake-api-key&oobCode=fixture")]
    [InlineData("http://127.0.0.1:9099/emulator/action?mode=resetPassword&apiKey=fake-api-key&oobCode=fixture")]
    [InlineData("http://127.0.0.1:9099/emulator/action?mode=verifyEmail&apiKey=fake-api-key")]
    [InlineData("http://127.0.0.1:9099/emulator/action?mode=verifyEmail&apiKey=unexpected&oobCode=fixture")]
    [InlineData("http://127.0.0.1:9099/emulator/action?mode=verifyEmail&apiKey=fake-api-key&oobCode=fixture&continueUrl=https://example.com")]
    [InlineData("http://127.0.0.1:9099/emulator/action?mode=verifyEmail&apiKey=fake-api-key&oobCode=fixture&tenantId=other")]
    public void UnsafeVerificationLink_IsRejectedBeforeAnyHttpRequest(string link)
    {
        var settings = AuthEmulatorSettings.Validate("demo-ezone-regression", "127.0.0.1:8085", "127.0.0.1:9099");
        Assert.Throws<InvalidOperationException>(() => AuthWorkflowTestData.ValidateVerificationLink(link, settings));
    }
}
