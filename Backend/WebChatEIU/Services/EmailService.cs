
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

        // Gửi OTP cho hệ thống đăng ký cũ
        public async Task SendActivationEmailAsync(
            string toEmail,
            string fullname,
            string activationCode)
        {
            await SendEmailAsync(
                toEmail,
                fullname,
                "[EZone] Mã xác thực tài khoản",
                BuildHtmlBody(fullname, activationCode),
                BuildPlainTextBody(fullname, activationCode)
            );
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

        
        // ────────────────────────────────────────────────────────────────────
        //  HTML Email Template
        // ────────────────────────────────────────────────────────────────────
        private static string BuildHtmlBody(string fullname, string code)
        {
            var year = DateTime.UtcNow.Year;

            return $@"
<!DOCTYPE html>
<html lang=""vi"">
<head>
  <meta charset=""UTF-8"" />
  <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"" />
</head>
<body style=""margin:0; padding:0; background-color:#f4f4f7; font-family:'Segoe UI',Roboto,Arial,sans-serif;"">
  <table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" style=""background-color:#f4f4f7;"">
    <tr>
      <td align=""center"" style=""padding:40px 16px;"">
        <table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0""
               style=""max-width:520px; background-color:#ffffff; border-radius:16px;
                      box-shadow:0 4px 24px rgba(0,0,0,0.08); overflow:hidden;"">
          <tr>
            <td style=""padding:32px 32px 24px;"">
              <p style=""margin:0 0 8px; color:#1a1a2e; font-size:18px; font-weight:700;"">
                Xin chào {fullname}! 👋
              </p>
              <p style=""margin:0 0 24px; color:#555; font-size:15px; line-height:1.6;"">
                Cảm ơn bạn đã đăng ký tài khoản <strong style=""color:#E91E63;"">EZone</strong>.
                Để hoàn tất việc đăng ký, vui lòng nhập mã xác thực bên dưới vào ứng dụng:
              </p>
              <table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"">
                <tr>
                  <td align=""center"" style=""padding:8px 0 28px;"">
                    <div style=""display:inline-block; background:#FFF0F3; border:2px solid #E91E63;
                                border-radius:14px; padding:20px 40px;"">
                      <span style=""font-size:36px; font-weight:900; letter-spacing:10px;
                                   color:#E91E63; font-family:'Courier New',monospace;"">
                        {code}
                      </span>
                    </div>
                  </td>
                </tr>
              </table>
              <table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0""
                     style=""background-color:#FFF8E1; border-left:4px solid #FFA000;
                            border-radius:8px; margin-bottom:24px;"">
                <tr>
                  <td style=""padding:14px 16px;"">
                    <p style=""margin:0; color:#6D4C00; font-size:13px; line-height:1.6;"">
                      ⚠️ <strong>Lưu ý bảo mật:</strong> Mã xác thực này chỉ sử dụng được một lần.
                      Vui lòng <strong>không chia sẻ mã này</strong> với bất kỳ ai.
                      EZone sẽ không bao giờ yêu cầu bạn cung cấp mã này qua tin nhắn hay điện thoại.
                    </p>
                  </td>
                </tr>
              </table>
              <p style=""margin:0; color:#888; font-size:13px; line-height:1.5; text-align:center;"">
                Nếu bạn không đăng ký tài khoản EZone, vui lòng bỏ qua email này.
              </p>
            </td>
          </tr>
          <tr>
            <td style=""padding:0 32px;"">
              <hr style=""border:none; border-top:1px solid #eee; margin:0;"" />
            </td>
          </tr>
          <tr>
            <td style=""padding:20px 32px 28px; text-align:center;"">
              <p style=""margin:0 0 4px; color:#aaa; font-size:12px;"">
                Email này được gửi tự động từ hệ thống <strong>EZone</strong>.
                Vui lòng không trả lời email này.
              </p>
              <p style=""margin:0 0 4px; color:#aaa; font-size:12px;"">
                © {year} EZone Team — Eastern International University
              </p>
              <p style=""margin:0; color:#ccc; font-size:11px;"">
                Developed with ❤️ by EZone Team
              </p>
            </td>
          </tr>
        </table>
      </td>
    </tr>
  </table>
</body>
</html>";
        }

        // ────────────────────────────────────────────────────────────────────
        //  Plain-text fallback
        // ────────────────────────────────────────────────────────────────────
        private static string BuildPlainTextBody(string fullname, string code)
        {
            var year = DateTime.UtcNow.Year;

            return
                "[EZone] Mã xác thực tài khoản của bạn\n" +
                "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━\n\n" +
                $"Xin chào {fullname}!\n\n" +
                "Cảm ơn bạn đã đăng ký tài khoản EZone.\n" +
                "Mã xác thực của bạn là:\n\n" +
                $"    ▶  {code}\n\n" +
                "⚠️ Lưu ý bảo mật:\n" +
                "  • Mã xác thực này chỉ sử dụng được một lần.\n" +
                "  • Vui lòng không chia sẻ mã này với bất kỳ ai.\n" +
                "  • EZone sẽ không bao giờ yêu cầu bạn cung cấp mã\n" +
                "    qua tin nhắn hay điện thoại.\n\n" +
                "Nếu bạn không đăng ký tài khoản EZone,\n" +
                "vui lòng bỏ qua email này.\n\n" +
                "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━\n" +
                $"© {year} EZone Team — Eastern International University\n" +
                "Developed with ❤ by EZone Team\n";
        }
    }
}