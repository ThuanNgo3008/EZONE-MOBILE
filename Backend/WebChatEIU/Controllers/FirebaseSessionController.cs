
using FirebaseAdmin.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WebChatEIU.Services;

namespace WebChatEIU.Controllers
{
    [ApiController]
    [Route("api/firebase-session")]
    [Authorize(Policy = "FirebaseUser")]
    public class FirebaseSessionController : ControllerBase
    {
        private readonly FirebaseUserService _userService;

        public FirebaseSessionController(
            FirebaseUserService userService)
        {
            _userService = userService;
        }

        [HttpGet("me")]
        public async Task<IActionResult> GetMe()
        {
            // UID lấy từ Firebase ID Token
            var uid = User.FindFirst("user_id")?.Value;

            if (string.IsNullOrEmpty(uid))
                return Unauthorized();

            // Kiểm tra tài khoản Firebase Authentication
            UserRecord authUser;

            try
            {
                authUser = await FirebaseAuth.DefaultInstance
                    .GetUserAsync(uid);
            }
            catch (FirebaseAuthException ex) when (ex.AuthErrorCode == AuthErrorCode.UserNotFound)
            {
                return Unauthorized();
            }

            if (!FirebaseMatchEligibilityService.HasEligibleAuthAccount(authUser))
            {
                return StatusCode(403, new
                {
                    message = "Email is not verified or account is disabled."
                });
            }

            // Kiểm tra hồ sơ Firestore
            var user = await _userService.GetByIdAsync(uid);

            if (user == null)
                return NotFound(new
                {
                    message = "User profile not found."
                });

            if (!FirebaseMatchEligibilityService.HasEligibleProfile(user))
            {
                return StatusCode(403, new
                {
                    message = "Account is inactive, banned or deleted."
                });
            }

            return Ok(new
            {
                success = true,
                userId = uid,
                fullname = user.Fullname,
                email = user.Email
            });
        }
    }
}
