using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WebChatEIU.Services;

namespace WebChatEIU.Controllers
{
    [ApiController]
    [Authorize(Policy = "FirebaseUser")]
    [Route("api/firebase-random-match")]
    public class FirebaseRandomMatchController : ControllerBase
    {
        private readonly FirebaseRandomMatchQueueService _queueService;

        public FirebaseRandomMatchController(
            FirebaseRandomMatchQueueService queueService)
        {
            _queueService = queueService;
        }

        [HttpPost("find")]
        public async Task<IActionResult> FindMatch()
        {
            var userId = User.FindFirst("user_id")?.Value
                ?? User.FindFirst("sub")?.Value
                ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

            if (string.IsNullOrWhiteSpace(userId))
            {
                return Unauthorized();
            }

            try
            {
                var result = await _queueService
                    .FindMatchAsync(userId);

                return Ok(new
                {
                    result.Status,
                    result.ReservationId
                });
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { Error = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return Conflict(new { Error = ex.Message });
            }
        }


        [HttpPost("heartbeat")]
        public async Task<IActionResult> Heartbeat()
        {
            var userId = User.FindFirst("user_id")?.Value
                ?? User.FindFirst("sub")?.Value
                ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

            if (string.IsNullOrWhiteSpace(userId))
            {
                return Unauthorized();
            }

            try
            {
                var result = await _queueService
                    .FindMatchAsync(userId, joinIfNeeded: false);


                return Ok(new
                {
                    result.Status,
                    result.ReservationId
                });
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { Error = ex.Message });
            }
        }


        [HttpPost("cancel")]
        public async Task<IActionResult> CancelMatch()
        {
            var userId = User.FindFirst("user_id")?.Value
                ?? User.FindFirst("sub")?.Value
                ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

            if (string.IsNullOrWhiteSpace(userId))
            {
                return Unauthorized();
            }

            try
            {
                var status = await _queueService
                    .CancelWaitingAsync(userId);

                return Ok(new { Status = status });
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { Error = ex.Message });
            }
        }


    }
}
