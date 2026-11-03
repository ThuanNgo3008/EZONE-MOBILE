namespace WebChatEIU.Services
{
    public interface IEmailService
    {
        // Xác thực OTP cũ (SQL Server)
        Task SendActivationEmailAsync(
            string toEmail,
            string fullname,
            string activationCode);

        // Xác thực email mới (Firebase)
        Task SendVerificationLinkAsync(
            string toEmail,
            string fullname,
            string verificationLink);
    }
}
