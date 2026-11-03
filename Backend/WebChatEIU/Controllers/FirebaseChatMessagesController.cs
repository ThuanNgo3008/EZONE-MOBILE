using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WebChatEIU.Services;

namespace WebChatEIU.Controllers
{
    [ApiController]
    [Route("api/firebase-chat-messages")]
    [Authorize(Policy = "FirebaseUser")]
    public class FirebaseChatMessagesController : ControllerBase
    {
        private readonly FirebaseChatMessageService _messageService;
        private readonly FirebaseUserService _userService;
        private readonly ModerationService _moderationService;
        private readonly FirebaseChatRoomService _roomService;

        public FirebaseChatMessagesController(
            FirebaseChatMessageService messageService,
            FirebaseUserService userService,
            FirebaseChatRoomService roomService,
            ModerationService moderationService)
        {
            _messageService = messageService;
            _userService = userService;
            _roomService = roomService;
            _moderationService = moderationService;
        }

        [HttpPost]
        public async Task<IActionResult> SendMessage(
            [FromBody] SendFirebaseMessageRequest request)
        {
            string? currentUserId =
                User.FindFirst("user_id")?.Value
                ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                ?? User.FindFirst("sub")?.Value;

            if (string.IsNullOrWhiteSpace(currentUserId))
            {
                return Unauthorized();
            }

            var currentUser = await _userService.GetByIdAsync(
                currentUserId);

            if (currentUser == null ||
                !currentUser.IsActive ||
                currentUser.IsBanned ||
                currentUser.IsDeleted)
            {
                return Forbid();
            }

            if (string.IsNullOrWhiteSpace(request.RoomId) ||
                string.IsNullOrWhiteSpace(request.Content) ||
                request.Content.Length > 2000)
            {
                return BadRequest("Invalid message.");
            }

            if (_moderationService.IsSensitive(request.Content))
            {
                return BadRequest(
                    "Message contains prohibited content.");
            }

            try
            {
                var message = await _messageService.SendMessageAsync(
                    request.RoomId,
                    currentUserId,
                    request.Content);

                var room = await _roomService.GetRoomByIdAsync(request.RoomId);

                if (room == null)
                {
                    return NotFound("Room not found.");
                }

                var senderRole = room.User1Id == currentUserId
                    ? "User1"
                    : "User2";

                return Ok(new
                {
                    message.MessageId,
                    SenderRole = senderRole,
                    message.Content,
                    CreatedAt = message.CreatedAt.ToDateTime()
                });
            }
            catch (ArgumentException ex)
            {
                return BadRequest(ex.Message);
            }
            catch (UnauthorizedAccessException)
            {
                return Forbid();
            }
            catch (InvalidOperationException ex)
            {
                if (ex.Message == "Room not found.")
                {
                    return NotFound(ex.Message);
                }

                return Conflict(ex.Message);
            }
        }


        [HttpGet("{roomId}")]
        public async Task<IActionResult> GetMessages(
            string roomId,
            [FromQuery] int pageSize = 20,
            [FromQuery] string? cursor = null)
        {
            string? currentUserId =
                User.FindFirst("user_id")?.Value
                ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                ?? User.FindFirst("sub")?.Value;

            if (string.IsNullOrWhiteSpace(currentUserId))
            {
                return Unauthorized();
            }

            var currentUser = await _userService.GetByIdAsync(
                currentUserId);

            if (currentUser == null ||
                !currentUser.IsActive ||
                currentUser.IsBanned ||
                currentUser.IsDeleted)
            {
                return Forbid();
            }

            try
            {
                var result = await _messageService.GetMessagesAsync(
                    roomId,
                    currentUserId,
                    pageSize,
                    cursor);

                var room = await _roomService.GetRoomByIdAsync(roomId);

                if (room == null)
                {
                    return NotFound("Room not found.");
                }

                return Ok(new
                {
                    Messages = result.Messages.Select(message => new
                    {
                        message.MessageId,

                        SenderRole = message.SenderId == room.User1Id
                            ? "User1"
                            : "User2",

                        message.Content,
                        CreatedAt = message.CreatedAt.ToDateTime()
                    }),
                    result.NextCursor,
                    HasMore = result.NextCursor != null
                });
            }
            catch (ArgumentException ex)
            {
                return BadRequest(ex.Message);
            }
            catch (UnauthorizedAccessException)
            {
                return Forbid();
            }
            catch (InvalidOperationException)
            {
                return NotFound("Room not found.");
            }
        }

    }

    public class SendFirebaseMessageRequest
    {
        public string RoomId { get; set; } = string.Empty;

        public string Content { get; set; } = string.Empty;
    }
}
