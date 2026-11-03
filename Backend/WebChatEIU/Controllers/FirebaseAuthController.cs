using Microsoft.AspNetCore.Mvc;
using FirebaseAdmin.Auth;
using WebChatEIU.DTOs;
using WebChatEIU.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;

namespace WebChatEIU.Controllers
{
    [ApiController]
    [Route("api/firebase-auth")]
    public class FirebaseAuthController : ControllerBase
    {
        private readonly FirebaseAuthService _authService;

        public FirebaseAuthController(
            FirebaseAuthService authService)
        {
            _authService = authService;
        }

        [HttpPost("register")]
        public async Task<IActionResult> Register([FromBody] RegisterDto dto)
        {
            try
            {
                var uid = await _authService.RegisterAsync(dto);

                return Ok(new
                {
                    message = "Account created successfully.",
                    userId = uid,
                    emailVerificationRequired = true,
                    verificationEmailSent = true
                });
            }
            catch (VerificationEmailDeliveryException ex)
            {
                return Ok(new
                {
                    message = "Account created, but verification email could not be sent.",
                    userId = ex.UserId,
                    emailVerificationRequired = true,
                    verificationEmailSent = false
                });
            }
            catch (FirebaseAuthException ex)
            {
                return BadRequest(new
                {
                    message = "Registration failed.",
                    error = ex.AuthErrorCode.ToString()
                });
            }
        }


        [Authorize(Policy = "FirebaseUser")]
        [HttpPost("sync-verification")]
        public async Task<IActionResult> SyncVerification()
        {
            // Lấy UID từ Firebase ID Token đã xác thực
            var uid = User.FindFirst("user_id")?.Value;

            if (string.IsNullOrEmpty(uid))
            {
                return Unauthorized(new
                {
                    message = "Invalid Firebase token."
                });
            }

            // Kiểm tra email và đồng bộ IsActive
            bool isVerified = await _authService
                .SyncEmailVerificationAsync(uid);

            if (!isVerified)
            {
                return BadRequest(new
                {
                    success = false,
                    message = "Email is not verified or account is unavailable."
                });
            }

            return Ok(new
            {
                success = true,
                message = "Account activated successfully."
            });
        }

        [HttpPost("resend-verification")]
        [EnableRateLimiting("ResendVerification")]
        public async Task<IActionResult> ResendVerification([FromBody] ResendVerificationEmailDto dto)
        {
            try
            {
                await _authService.ResendVerificationEmailAsync(dto.Email);
            }
            catch (FirebaseAuthException)
            {
                // Không tiết lộ email có tồn tại hay không.
            }

            return Ok(new
            {
                message = "If the account exists and is not verified, a verification email will be sent."
            });
        }

    }
}
