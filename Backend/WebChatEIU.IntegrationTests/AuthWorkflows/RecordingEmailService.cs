using WebChatEIU.Services;

namespace WebChatEIU.IntegrationTests.AuthWorkflows;

internal sealed record VerificationEmail(string Address, string Name, string Link);

internal sealed class RecordingEmailService : IEmailService
{
    public List<VerificationEmail> Attempts { get; } = new();
    public Exception? Failure { get; set; }

    public Task SendVerificationLinkAsync(string toEmail, string fullname, string verificationLink)
    {
        Attempts.Add(new VerificationEmail(toEmail, fullname, verificationLink));
        return Failure is null ? Task.CompletedTask : Task.FromException(Failure);
    }
}
