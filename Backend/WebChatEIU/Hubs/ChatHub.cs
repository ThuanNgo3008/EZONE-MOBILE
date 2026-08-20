using Microsoft.AspNetCore.SignalR;
using WebChatEIU.Data;
using WebChatEIU.Models;
using WebChatEIU.Services;

namespace WebChatEIU.Hubs
{
    public class ChatHub : Hub
    {
        private readonly ApplicationDbContext _context;
        private readonly MatchmakingService _matchmakingService;
        private readonly ModerationService _moderationService;

        public ChatHub(MatchmakingService matchmakingService, ApplicationDbContext context, ModerationService moderationService)
        {
            _context = context;
            _matchmakingService = matchmakingService;
            _moderationService = moderationService;
        }

        public override async Task OnConnectedAsync()
        {
            var httpContext = Context.GetHttpContext();

            string userIdString = httpContext.Request.Query["userId"];

            Console.WriteLine($"Connected: {Context.ConnectionId}, userId = {userIdString}");

            if (!string.IsNullOrEmpty(userIdString))
            {
                if (!int.TryParse(userIdString, out int userId))
                {
                    await Clients.Caller.SendAsync(
                        "ViolationDetected",
                        "Invalid userId!"
                    );

                    Context.Abort();
                    return;
                }

                var currentUser = await _context.Users.FindAsync(userId);

                if (currentUser == null || currentUser.IsBanned)
                {
                    await Clients.Caller.SendAsync(
                        "ViolationDetected",
                        "Your account has been locked!"
                    );

                    Context.Abort();
                    return;
                }

                _matchmakingService.RegisterUser(Context.ConnectionId, userId);

                //var activeRoom = _context.ChatRooms.FirstOrDefault(r =>(r.User1Id == userId || r.User2Id == userId) && r.Status == ChatRooms.RoomStatus.Active);

                //if (activeRoom != null)
                //{
                //    _matchmakingService.RegisterRoom(
                //        Context.ConnectionId,
                //        activeRoom.RoomId
                //    );

                //    await Groups.AddToGroupAsync(
                //        Context.ConnectionId,
                //        activeRoom.RoomId.ToString()
                //    );

                //    await Clients.Client(Context.ConnectionId)
                //        .SendAsync("Matched", activeRoom.RoomId);

                //    Console.WriteLine($"Reconnected to room {activeRoom.RoomId}");

                //    await base.OnConnectedAsync();
                //    return;
                //}

                
            }

            await base.OnConnectedAsync();
        }

        public async Task FindMatch()
        {
            try
            {
                string connectionId = Context.ConnectionId;

                var (partnerConnectionId, roomId) =
                    _matchmakingService.FindMatch(connectionId);

                if (partnerConnectionId == null)
                {
                    await Clients.Caller.SendAsync("WaitingForMatch");
                    return;
                }

                if (roomId == 0)
                {
                    await Clients.Caller.SendAsync("MatchError", "Room not found");
                    return;
                }

                await Clients.Client(connectionId)
                    .SendAsync("Matched", roomId);

                await Clients.Client(partnerConnectionId)
                    .SendAsync("Matched", roomId);
            }
            catch (Exception ex)
            {
                Console.WriteLine("FindMatch error: " + ex.Message);
                await Clients.Caller.SendAsync("MatchError", ex.Message);
            }
        }

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            int roomId = _matchmakingService.GetRoomIdOrDefault(Context.ConnectionId);

            Console.WriteLine($"Disconnected: {Context.ConnectionId}, roomId = {roomId}");

            if (roomId != 0)
            {
                await Clients.GroupExcept(roomId.ToString(), Context.ConnectionId)
                    .SendAsync("PartnerDisconnected");

                // Rớt kết nối (tắt app, mất mạng, logout giữa chừng...) mà chưa
                // từng bấm LEAVE tường minh -> đóng room luôn, tránh nó bị kẹt
                // vĩnh viễn ở Active khiến ChatHistoryScreen thiếu sót các cuộc
                // chat kết thúc "đột ngột" thay vì kết thúc sạch qua LeaveRoom().
                await CloseRoomAsync(roomId);
            }

            _matchmakingService.Disconnect(Context.ConnectionId);

            await base.OnDisconnectedAsync(exception);
        }

        public async Task SendMessage(string message)
        {
            Console.WriteLine($"[CHAT] SendMessage called: {message}");

            if (_moderationService.IsSensitive(message))
            {
                int reporterId = _matchmakingService.GetUserId(Context.ConnectionId);

                var user = await _context.Users.FindAsync(reporterId);

                if (user != null)
                {
                    user.IsBanned = true;
                    user.BanSource = "SystemAuto";
                    await _context.SaveChangesAsync();
                }

                await Clients.Caller.SendAsync(
                    "ViolationDetected",
                    "Your account has been locked for posting sensitive content."
                );

                Context.Abort();
                return;
            }

            int roomId = _matchmakingService.GetRoomIdOrDefault(Context.ConnectionId);

            Console.WriteLine($"[CHAT] ConnectionId: {Context.ConnectionId}");
            Console.WriteLine($"[CHAT] RoomId: {roomId}");

            if (roomId == 0)
            {
                Console.WriteLine("[CHAT] ERROR: roomId = 0");
                return;
            }

            var room = await _context.ChatRooms.FindAsync(roomId);

            if (room == null || room.Status == ChatRooms.RoomStatus.Closed)
            {
                Console.WriteLine($"[CHAT] ERROR: Room {roomId} is closed/not found");

                await Clients.Caller.SendAsync(
                    "ViolationDetected",
                    "This conversation has ended. You cannot send messages anymore."
                );

                return;
            }

            int senderId = _matchmakingService.GetUserId(Context.ConnectionId);

            Console.WriteLine($"[CHAT] SenderId: {senderId}");
            Console.WriteLine($"[CHAT] Broadcasting to group: {roomId}");

            // Gửi realtime cho cả 2 người
            await Clients.Group(roomId.ToString())
                .SendAsync("ReceiveMessage", new
                {
                    senderId,
                    message
                });

            // Lưu DB
            Messages newMessage = new Messages
            {
                RoomId = roomId,
                SenderId = senderId,
                Content = message
            };

            _context.Messages.Add(newMessage);
            await _context.SaveChangesAsync();

            room.AffinityScore += 1;
            await _context.SaveChangesAsync();

            Console.WriteLine("[CHAT] Message sent successfully");
        }

        public int GetPartnerUserId()
        {
            return _matchmakingService.GetPartnerUserId(Context.ConnectionId);
        }

        public async Task Typing()
        {
            int roomId =
                _matchmakingService.GetRoomIdOrDefault(Context.ConnectionId);

            if (roomId == 0)
            {
                return;
            }

            int senderId =
                _matchmakingService.GetUserId(Context.ConnectionId);

            await Clients.GroupExcept(roomId.ToString(), Context.ConnectionId)
                .SendAsync("UserTyping", senderId);
        }


        public async Task JoinRoom(int roomId)
        {
            int userId = _matchmakingService.GetUserId(Context.ConnectionId);

            Console.WriteLine(
                $"[CHAT] JoinRoom: connection={Context.ConnectionId}, userId={userId}, roomId={roomId}"
            );

            var room = await _context.ChatRooms.FindAsync(roomId);

            if (room == null)
            {
                Console.WriteLine($"[CHAT] JoinRoom FAILED: room {roomId} not found");
                return;
            }

            if (room.Status == ChatRooms.RoomStatus.Closed)
            {
                Console.WriteLine($"[CHAT] JoinRoom FAILED: room {roomId} closed");
                return;
            }

            if (room.User1Id != userId && room.User2Id != userId)
            {
                Console.WriteLine(
                    $"[CHAT] JoinRoom FAILED: user {userId} does not belong to room {roomId}"
                );
                return;
            }

            _matchmakingService.RegisterRoom(Context.ConnectionId, roomId);

            await Groups.AddToGroupAsync(
                Context.ConnectionId,
                roomId.ToString()
            );

            Console.WriteLine(
                $"[CHAT] JOIN SUCCESS: user {userId} -> room {roomId}"
            );
        }



        public async Task LeaveRoom()
        {
            int roomId = _matchmakingService.GetRoomIdOrDefault(Context.ConnectionId);

            if (roomId == 0)
            {
                _matchmakingService.Disconnect(Context.ConnectionId);
                return;
            }

            // Dùng SignalR Group (giống OnDisconnectedAsync) thay vì
            // _matchmakingService.GetPartner — dictionary đó chỉ chứa
            // connectionId lúc match ở màn Waiting, đã disconnect từ khi
            // chuyển sang phòng chat (connection mới qua JoinRoom) nên
            // luôn trả về null ở đây.
            await Clients.GroupExcept(roomId.ToString(), Context.ConnectionId)
                .SendAsync("PartnerDisconnected");

            await CloseRoomAsync(roomId);

            await Groups.RemoveFromGroupAsync(
                Context.ConnectionId,
                roomId.ToString());

            _matchmakingService.Disconnect(Context.ConnectionId);
        }

        // Dùng chung cho cả LeaveRoom() (user chủ động bấm LEAVE) và
        // OnDisconnectedAsync() (rớt kết nối/tắt app không bấm LEAVE) — chỉ 1
        // nguồn duy nhất quyết định "đóng room" là gì, tránh lệch logic giữa
        // 2 chỗ như REST endpoint chết đã dọn trước đó.
        private async Task CloseRoomAsync(int roomId)
        {
            var room = await _context.ChatRooms.FindAsync(roomId);

            if (room == null || room.Status == ChatRooms.RoomStatus.Closed)
            {
                return;
            }

            var messages = _context.Messages.Where(m => m.RoomId == roomId);
            _context.Messages.RemoveRange(messages);

            room.Status = ChatRooms.RoomStatus.Closed;
            room.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();
        }
    }
}