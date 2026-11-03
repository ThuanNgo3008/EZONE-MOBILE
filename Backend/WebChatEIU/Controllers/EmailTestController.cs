
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using WebChatEIU.Services;

namespace WebChatEIU.Controllers
{
    [ApiController]
    [Route("api/email-test")]
    [Authorize(Policy = "AdminOnly")]
    public class EmailTestController : ControllerBase
    {
        private readonly IEmailService _emailService;
        private readonly IConfiguration _configuration;

        public EmailTestController(
            IEmailService emailService,
            IConfiguration configuration)
        {
            _emailService = emailService;
            _configuration = configuration;
        }

        [HttpPost("send")]
        public async Task<IActionResult> Send()
        {
            var email = _configuration["Gmail:Email"];

            if (string.IsNullOrWhiteSpace(email))
                return StatusCode(500, "Gmail is not configured.");

            await _emailService.SendActivationEmailAsync(
                email,
                "EZone Test",
                "TEST123"
            );

            return Ok(new
            {
                success = true,
                message = "Test email sent successfully."
            });
        }
    }
}
