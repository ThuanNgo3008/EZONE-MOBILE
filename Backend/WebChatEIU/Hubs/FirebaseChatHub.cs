using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using WebChatEIU.Services;

namespace WebChatEIU.Hubs;

[Authorize(Policy = "FirebaseUser")]
public class FirebaseChatHub(
    FirebaseChatMessageService messageService,
    ModerationService moderationService,
    FirebaseChatConnections connections) : Hub
{
    internal const string Route = "/firebaseChatHub";

    public override async Task OnConnectedAsync()
    {
        if (Context.GetHttpContext()?.Items[FirebaseBearerSession.ValidatedSessionKey]
                is not FirebaseBearerSession.Session session ||
            session.ExpiresAt <= DateTime.UtcNow ||
            !await connections.IsEligibleProfileAsync(session.Uid, Context.ConnectionAborted))
        {
            Context.Abort();
            return;
        }

        connections.Add(new(Context.ConnectionId, session, Context.Abort, Context.ConnectionAborted));
        await base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        connections.Remove(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    public Task<string> Ping() => Task.FromResult("pong");

    public async Task JoinRoom(string roomId)
    {
        if (InvalidRoomId(roomId))
            throw new HubException("Invalid room ID.");

        var connection = await connections.AuthorizeAsync(Context.ConnectionId, Context.ConnectionAborted);
        var gate = connections.RoomGate(roomId);
        await gate.WaitAsync(Context.ConnectionAborted);
        try
        {
            var room = await connections.ReadRoomAndEvictAsync(roomId, Context.ConnectionAborted);
            if (room is null)
                throw new HubException("Room not found.");
            if (!FirebaseChatConnections.IsAvailable(room))
                throw new HubException("Room is unavailable.");
            if (room.User1Id != connection.Uid && room.User2Id != connection.Uid)
                throw new HubException("Access denied.");

            connections.Join(connection, roomId);
        }
        finally
        {
            gate.Release();
        }

        await Clients.Caller.SendAsync("JoinedRoom", roomId);
    }

    public async Task SendMessage(string roomId, string content)
    {
        if (InvalidRoomId(roomId) || string.IsNullOrWhiteSpace(content) || content.Length > 2000)
            throw new HubException("Invalid message.");

        var connection = await connections.AuthorizeAsync(Context.ConnectionId, Context.ConnectionAborted);
        if (moderationService.IsSensitive(content))
            throw new HubException("Message contains prohibited content.");
        if (!connections.IsAuthorized(connection))
            throw new HubException("Unauthorized.");

        try
        {
            // The message service transaction independently checks current room access.
            var message = await messageService.SendMessageAsync(roomId, connection.Uid, content, connection.Closed);
            await connections.BroadcastAsync(roomId, connection, (senderRole, recipients) =>
                Clients.Clients(recipients).SendAsync("ReceiveMessage", new
                {
                    message.MessageId,
                    SenderRole = senderRole,
                    message.Content,
                    CreatedAt = message.CreatedAt.ToDateTime()
                }));
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
        if (InvalidRoomId(roomId))
            throw new HubException("Invalid room ID.");

        var connection = await connections.AuthorizeAsync(Context.ConnectionId, Context.ConnectionAborted);
        connections.Leave(connection, roomId);
    }

    private static bool InvalidRoomId(string roomId) =>
        string.IsNullOrWhiteSpace(roomId) || roomId.Contains('/');
}
