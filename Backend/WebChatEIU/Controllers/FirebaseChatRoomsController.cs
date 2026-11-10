using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WebChatEIU.Services;
using WebChatEIU.DTOs;

namespace WebChatEIU.Controllers
{
    [ApiController]
    [Route("api/firebase-chat-rooms")]
    [Authorize(Policy = "FirebaseUser")]
    public class FirebaseChatRoomsController : ControllerBase
    {
        private readonly FirebaseChatRoomService _chatRoomService;
        private readonly FirebaseUserService _userService;
        private readonly FirebaseReportService _reportService;
        private readonly FirebaseTemporaryMessageCleanupService _cleanupService;
        private readonly IWebHostEnvironment _environment;

        public FirebaseChatRoomsController(
            FirebaseChatRoomService chatRoomService,
            FirebaseUserService userService,
            FirebaseReportService reportService,
            FirebaseTemporaryMessageCleanupService cleanupService,
            IWebHostEnvironment environment)
        {
            _chatRoomService = chatRoomService;
            _userService = userService;
            _reportService = reportService;
            _cleanupService = cleanupService;
            _environment = environment;
        }

        [HttpPost("test-create")]
        public async Task<IActionResult> TestCreateRoom(
            [FromBody] TestCreateRoomRequest request)
        {
            if (!_environment.IsDevelopment())
            {
                return NotFound();
            }

            string? currentUserId =
                User.FindFirst("user_id")?.Value
                ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                ?? User.FindFirst("sub")?.Value;

            if (string.IsNullOrWhiteSpace(currentUserId))
            {
                return Unauthorized();
            }

            if (string.IsNullOrWhiteSpace(request.PartnerUserId) ||
                currentUserId == request.PartnerUserId)
            {
                return BadRequest("Invalid partner.");
            }

            var currentUser = await _userService.GetByIdAsync(currentUserId);
            var partner = await _userService.GetByIdAsync(request.PartnerUserId);

            if (currentUser == null || partner == null ||
                !currentUser.IsActive || currentUser.IsBanned || currentUser.IsDeleted ||
                !partner.IsActive || partner.IsBanned || partner.IsDeleted)
            {
                return BadRequest("Users must be active.");
            }


            var room = await _chatRoomService.CreateRoomAsync(
                    currentUserId,
                    request.PartnerUserId,
                    "Anonymous Fox",
                    "Anonymous Panda"
                );


            return Ok(new
            {
                room.RoomId,
                room.User1Id,
                room.User2Id,
                room.Status,
                room.MatchMode
            });
        }

        [HttpGet("{roomId}")]
        public async Task<IActionResult> GetRoomById(string roomId)
        {
            if (string.IsNullOrWhiteSpace(roomId))
            {
                return BadRequest("Room ID is required.");
            }

            string? currentUserId =
                User.FindFirst("user_id")?.Value
                ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                ?? User.FindFirst("sub")?.Value;

            if (string.IsNullOrWhiteSpace(currentUserId))
            {
                return Unauthorized();
            }

            var currentUser = await _userService.GetByIdAsync(currentUserId);

            if (currentUser == null ||
                !currentUser.IsActive ||
                currentUser.IsBanned ||
                currentUser.IsDeleted)
            {
                return Forbid();
            }

            var room = await _chatRoomService.GetRoomByIdAsync(roomId);

            if (room == null)
            {
                return NotFound("Room not found.");
            }

            if (room.User1Id != currentUserId &&
                room.User2Id != currentUserId)
            {
                return Forbid();
            }

            return Ok(new
            {
                room.RoomId,
                room.Status,
                room.MatchMode,
                room.Purpose,
                room.AffinityScore,
                room.IsRevealed,

                MyRole = room.User1Id == currentUserId ? "User1" : "User2",

                MyNickname = room.User1Id == currentUserId ? room.User1Nickname : room.User2Nickname,

                PartnerNickname = room.User1Id == currentUserId ? room.User2Nickname : room.User1Nickname,

                room.TemporaryEndsAt,
                room.DecisionEndsAt
            });
        }

        [HttpPost("{roomId}/reports")]
        public async Task<IActionResult> CreateReport(
            string roomId,
            [FromBody] CreateFirebaseReportDto dto)
        {
            var userId = User.FindFirst("user_id")?.Value;

            if (string.IsNullOrWhiteSpace(userId))
            {
                return Unauthorized();
            }

            var user = await _userService.GetByIdAsync(userId);

            if (user == null || !user.IsActive || user.IsDeleted)
            {
                return Forbid();
            }

            try
            {
                var reportId = await _reportService.CreateReportAsync(
                    roomId,
                    userId,
                    dto.ReportType,
                    dto.Reason,
                    dto.Description,
                    dto.ReportedMessageId
                );

                return Ok(new
                {
                    reportId,
                    message = "Report submitted successfully.",
                    roomStatus = "Closed"
                });
            }
            catch (ArgumentException)
            {
                return BadRequest(new
                {
                    message = "Invalid report information."
                });
            }
            catch (UnauthorizedAccessException)
            {
                return Forbid();
            }
            catch (InvalidOperationException ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[FirebaseReport Error] {ex}");

                return Conflict(new
                {
                    message = "Room or reported message is unavailable."
                });
            }
        }


        [HttpPost("{roomId}/unmatch")]
        public async Task<IActionResult> UnmatchRoom(string roomId)
        {
            var userId = User.FindFirst("user_id")?.Value;

            if (string.IsNullOrWhiteSpace(userId))
            {
                return Unauthorized();
            }

            var user = await _userService.GetByIdAsync(userId);

            if (user == null ||
                !user.IsActive ||
                user.IsBanned ||
                user.IsDeleted)
            {
                return Forbid();
            }

            try
            {
                bool unmatched = await _chatRoomService
                    .UnmatchRoomAsync(roomId, userId);

                return Ok(new
                {
                    status = "Closed",
                    unmatched,
                    message = unmatched
                        ? "Connection ended successfully."
                        : "Connection was already closed."
                });
            }
            catch (ArgumentException)
            {
                return BadRequest(new
                {
                    message = "Invalid room ID."
                });
            }
            catch (UnauthorizedAccessException)
            {
                return Forbid();
            }
            catch (InvalidOperationException)
            {
                return Conflict(new
                {
                    message = "Room is unavailable for unmatching."
                });
            }
        }


        [HttpPost("{roomId}/end-chat")]
        public async Task<IActionResult> EndTemporaryChat(string roomId)
        {
            var userId = User.FindFirst("user_id")?.Value;

            if (string.IsNullOrWhiteSpace(userId))
            {
                return Unauthorized();
            }

            var user = await _userService.GetByIdAsync(userId);

            if (user == null ||
                !user.IsActive ||
                user.IsBanned ||
                user.IsDeleted)
            {
                return Forbid();
            }

            try
            {
                bool ended = await _chatRoomService
                    .EndTemporaryRoomAsync(roomId, userId);

                return Ok(new
                {
                    status = "Closed",
                    ended,
                    message = ended
                        ? "Temporary chat ended successfully."
                        : "Chat was already closed."
                });
            }
            catch (ArgumentException)
            {
                return BadRequest(new
                {
                    message = "Invalid room ID."
                });
            }
            catch (UnauthorizedAccessException)
            {
                return Forbid();
            }
            catch (InvalidOperationException)
            {
                return Conflict(new
                {
                    message = "Room is not available for ending temporary chat."
                });
            }
        }


        [HttpPost("{roomId}/test-cleanup")]
        public async Task<IActionResult> TestCleanup(string roomId)
        {
            if (!_environment.IsDevelopment())
            {
                return NotFound();
            }

            var userId = User.FindFirst("user_id")?.Value;

            if (string.IsNullOrWhiteSpace(userId))
            {
                return Unauthorized();
            }

            var user = await _userService.GetByIdAsync(userId);

            if (user == null || !user.IsActive ||
                user.IsBanned || user.IsDeleted)
            {
                return Forbid();
            }

            var room = await _chatRoomService.GetRoomByIdAsync(roomId);

            if (room == null)
            {
                return NotFound();
            }

            if (room.User1Id != userId && room.User2Id != userId)
            {
                return Forbid();
            }

            try
            {
                var deletedCount = await _cleanupService
                    .DeleteTemporaryMessagesAsync(roomId);

                return Ok(new
                {
                    roomId,
                    deletedCount,
                    message = "Temporary messages cleaned up."
                });
            }
            catch (InvalidOperationException ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[Cleanup Error] {ex}");

                return Conflict(new
                {
                    message = "Room is not eligible for cleanup."
                });
            }
        }



    }

    public class TestCreateRoomRequest
    {
        public string PartnerUserId { get; set; } = string.Empty;

    }

}
