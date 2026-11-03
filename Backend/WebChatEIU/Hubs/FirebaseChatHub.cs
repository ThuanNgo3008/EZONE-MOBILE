
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using WebChatEIU.Services;

namespace WebChatEIU.Hubs
{
    [Authorize(Policy = "FirebaseUser")]
    public class FirebaseChatHub : Hub
    {
        private readonly FirebaseUserService _userService;
        private readonly FirebaseChatRoomService _roomService;
        private readonly FirebaseChatMessageService _messageService;
        private readonly ModerationService _moderationService;

        public FirebaseChatHub(
            FirebaseUserService userService,
            FirebaseChatRoomService roomService,
            FirebaseChatMessageService messageService,
            ModerationService moderationService)
        {
            _userService = userService;
            _roomService = roomService;
            _messageService = messageService;
            _moderationService = moderationService;
        }


        public override async Task OnConnectedAsync()
        {
            string? userId = Context.User?
                .FindFirst("user_id")?.Value;

            if (string.IsNullOrWhiteSpace(userId))
            {
                Context.Abort();
                return;
            }

            var user = await _userService.GetByIdAsync(userId);

            if (user == null ||
                !user.IsActive ||
                user.IsBanned ||
                user.IsDeleted)
            {
                Context.Abort();
                return;
            }

            await base.OnConnectedAsync();
        }

        public Task<string> Ping()
        {
            return Task.FromResult("pong");
        }


        public async Task JoinRoom(string roomId)
        {
            string? userId = Context.User?
                .FindFirst("user_id")?.Value;

            if (string.IsNullOrWhiteSpace(userId))
            {
                throw new HubException("Unauthorized.");
            }

            if (string.IsNullOrWhiteSpace(roomId) ||
                roomId.Contains('/'))
            {
                throw new HubException("Invalid room ID.");
            }

            var user = await _userService.GetByIdAsync(userId);

            if (user == null ||
                !user.IsActive ||
                user.IsBanned ||
                user.IsDeleted)
            {
                throw new HubException("Account is not allowed.");
            }

            var room = await _roomService.GetRoomByIdAsync(roomId);

            if (room == null)
            {
                throw new HubException("Room not found.");
            }

            var now = DateTime.UtcNow;

            bool canJoin = room.Status switch
            {
                "Active" => true,

                "Temporary" =>
                    room.TemporaryStartedAt.HasValue &&
                    room.TemporaryEndsAt.HasValue &&
                    now >= room.TemporaryStartedAt.Value.ToDateTime() &&
                    now < room.TemporaryEndsAt.Value.ToDateTime(),

                "AwaitingDecision" =>
                    room.DecisionEndsAt.HasValue &&
                    now < room.DecisionEndsAt.Value.ToDateTime(),

                _ => false
            };

            if (!canJoin)
            {
                throw new HubException("Room is unavailable.");
            }

            if (room.User1Id != userId &&
                room.User2Id != userId)
            {
                throw new HubException("Access denied.");
            }

            await Groups.AddToGroupAsync(
                Context.ConnectionId,
                $"firebase-room:{roomId}"
            );

            await Clients.Caller.SendAsync("JoinedRoom", roomId);
        }


        public async Task SendMessage(string roomId, string content)
        {
            string? userId = Context.User?
                .FindFirst("user_id")?.Value;

            if (string.IsNullOrWhiteSpace(userId))
            {
                throw new HubException("Unauthorized.");
            }

            if (string.IsNullOrWhiteSpace(roomId) ||
                roomId.Contains('/') ||
                string.IsNullOrWhiteSpace(content) ||
                content.Length > 2000)
            {
                throw new HubException("Invalid message.");
            }

            var user = await _userService.GetByIdAsync(userId);

            if (user == null ||
                !user.IsActive ||
                user.IsBanned ||
                user.IsDeleted)
            {
                throw new HubException("Account is not allowed.");
            }

            if (_moderationService.IsSensitive(content))
            {
                throw new HubException(
                    "Message contains prohibited content.");
            }

            try
            {

                var message = await _messageService.SendMessageAsync(
                    roomId,
                    userId,
                    content);

                var room = await _roomService.GetRoomByIdAsync(roomId);

                if (room == null)
                {
                    throw new HubException("Room not found.");
                }

                var senderRole = room.User1Id == userId
                    ? "User1"
                    : "User2";

                await Clients.Group($"firebase-room:{roomId}")
                    .SendAsync("ReceiveMessage", new
                    {
                        message.MessageId,
                        SenderRole = senderRole,
                        message.Content,
                        CreatedAt = message.CreatedAt.ToDateTime()
                    });

            }
            catch (ArgumentException)
            {
                throw new HubException("Invalid message.");
            }
            catch (UnauthorizedAccessException)
            {
                throw new HubException("Access denied.");
            }
            catch (InvalidOperationException)
            {
                throw new HubException("Room is unavailable.");
            }
        }


        public async Task LeaveRoom(string roomId)
        {
            if (string.IsNullOrWhiteSpace(roomId) ||
                roomId.Contains('/'))
            {
                throw new HubException("Invalid room ID.");
            }

            await Groups.RemoveFromGroupAsync(
                Context.ConnectionId,
                $"firebase-room:{roomId}"
            );
        }



    }
}
