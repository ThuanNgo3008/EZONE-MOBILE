
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WebChatEIU.Services;

namespace WebChatEIU.Controllers
{
    [ApiController]
    [Authorize(Policy = "FirebaseUser")]
    [Route("api/firebase-match-reservations")]
    public class FirebaseMatchReservationsController : ControllerBase
    {
        private readonly FirebaseMatchReservationService _service;
        private readonly IWebHostEnvironment _environment;

        public FirebaseMatchReservationsController(
            FirebaseMatchReservationService service,
            IWebHostEnvironment environment)
        {
            _service = service;
            _environment = environment;
        }

        private string? GetUserId()
        {
            return User.FindFirst("user_id")?.Value
                ?? User.FindFirst("sub")?.Value
                ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        }

        [HttpPost("test-create")]
        public async Task<IActionResult> TestCreate(
            [FromBody] CreateReservationRequest request)
        {
            if (!_environment.IsDevelopment())
                return NotFound();

            var userId = GetUserId();

            if (string.IsNullOrWhiteSpace(userId))
                return Unauthorized();

            try
            {
                var reservationId = await _service
                    .CreateReservationAsync(
                        userId,
                        request.OtherUserId,
                        "Random");

                return Ok(new { ReservationId = reservationId });
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

        [HttpPost("{reservationId}/ready")]
        public async Task<IActionResult> Ready(string reservationId)
        {
            var userId = GetUserId();

            if (string.IsNullOrWhiteSpace(userId))
                return Unauthorized();

            try
            {
                var result = await _service
                    .SubmitReadyAsync(reservationId, userId);

                return Ok(new
                {
                    result.Status,
                    result.RoomId
                });
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { Error = ex.Message });
            }
            catch (UnauthorizedAccessException)
            {
                return Forbid();
            }
            catch (InvalidOperationException ex)
            {
                return Conflict(new { Error = ex.Message });
            }
        }
    }

    public class CreateReservationRequest
    {
        public string OtherUserId { get; set; } = string.Empty;
    }
}
