using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace WebChatEIU.Controllers
{
    [ApiController]
    [Route("api/firebase-auth-test")]
    public class FirebaseAuthTestController : ControllerBase
    {
        [Authorize(Policy = "FirebaseUser")]
        [HttpGet("me")]
        public IActionResult GetMe()
        {
            var uid = User.FindFirst("user_id")?.Value
                      ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

            return Ok(new
            {
                success = true,
                uid
            });
        }
    }
}