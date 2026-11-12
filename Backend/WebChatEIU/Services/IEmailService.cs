namespace WebChatEIU.Services
{
    public interface IEmailService
    {
        // Xác thực email mới (Firebase)
        Task SendVerificationLinkAsync(
            string toEmail,
            string fullname,
            string verificationLink);
    }
}
