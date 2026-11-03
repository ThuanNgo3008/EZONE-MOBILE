
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using System.Net;

namespace WebChatEIU.Services
{
    public class EmailService : IEmailService
    {
        private readonly string _gmail;
        private readonly string _appPassword;

        public EmailService(IConfiguration configuration)
        {
            _gmail = configuration["Gmail:Email"]
                ?? throw new InvalidOperationException(
                    "Missing Gmail:Email.");

            _appPassword = configuration["Gmail:AppPassword"]
                ?.Replace(" ", "")
                ?? throw new InvalidOperationException(
                    "Missing Gmail:AppPassword.");
        }

        // Hàm gửi email chung qua Gmail
        private async Task SendEmailAsync(
            string toEmail,
            string toName,
            string subject,
            string htmlBody,
            string textBody)
        {
            var message = new MimeMessage();

            message.From.Add(
                new MailboxAddress("EZone Support", _gmail));

            message.To.Add(
                new MailboxAddress(toName, toEmail));

            message.Subject = subject;

            message.Body = new BodyBuilder
            {
                HtmlBody = htmlBody,
                TextBody = textBody
            }.ToMessageBody();

            using var client = new SmtpClient();

            try
            {
                await client.ConnectAsync(
                    "smtp.gmail.com",
                    587,
                    SecureSocketOptions.StartTls);

                await client.AuthenticateAsync(
                    _gmail,
                    _appPassword);

                await client.SendAsync(message);
            }
            finally
            {
                if (client.IsConnected)
                {
                    await client.DisconnectAsync(true);
                }
            }
        }

        // Gửi link xác thực Firebase Authentication
        public async Task SendVerificationLinkAsync(
            string toEmail,
            string fullname,
            string verificationLink)
        {
            var safeName = WebUtility.HtmlEncode(fullname);
            var safeLink = WebUtility.HtmlEncode(verificationLink);

            var html = $"""
                <div style="font-family:Arial,sans-serif;
                            max-width:550px;
                            margin:auto;
                            padding:30px;">
                    <h2>Welcome to EZone!</h2>

                    <p>Xin chào {safeName},</p>

                    <p>
                        Cảm ơn bạn đã đăng ký EZone.
                        Vui lòng xác thực email để
                        kích hoạt tài khoản.
                    </p>

                    <p style="margin:30px 0;">
                        <a href="{safeLink}"
                           style="background:#E91E63;
                                  padding:14px 24px;
                                  color:white;
                                  border-radius:8px;
                                  text-decoration:none;">
                            Xác thực tài khoản
                        </a>
                    </p>

                    <p>
                        Nếu bạn không đăng ký EZone,
                        hãy bỏ qua email này.
                    </p>
                </div>
                """;

            var text =
                $"Xin chào {fullname}!\n\n" +
                "Vui lòng xác thực tài khoản EZone:\n" +
                verificationLink;

            await SendEmailAsync(
                toEmail,
                fullname,
                "[EZone] Xác thực tài khoản",
                html,
                text
            );
        }
    }
}
