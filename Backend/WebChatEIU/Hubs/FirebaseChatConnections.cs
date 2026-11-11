using FirebaseAdmin.Auth;
using Grpc.Core;
using Microsoft.AspNetCore.SignalR;
using WebChatEIU.Models.Firestore;
using WebChatEIU.Services;

namespace WebChatEIU.Hubs;

public sealed class FirebaseChatConnections(
    FirebaseAuth auth, IServiceScopeFactory scopes, ILogger<FirebaseChatConnections> logger) : BackgroundService
{
    internal sealed record Connection(
        string Id, FirebaseBearerSession.Session Session, Action Abort, CancellationToken Closed)
    {
        public string Uid => Session.Uid;
        public bool IsOpen => !Closed.IsCancellationRequested && Session.ExpiresAt > DateTime.UtcNow;
    }

    private readonly object sync = new();
    private readonly Dictionary<string, Connection> connections = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<Connection>> byUser = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<Connection>> byRoom = new(StringComparer.Ordinal);
    private readonly Dictionary<Connection, HashSet<string>> subscriptions = new();
    private readonly SemaphoreSlim authSlots = new(8);
    private readonly SemaphoreSlim[] roomGates = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1)).ToArray();

    internal void Add(Connection connection)
    {
        lock (sync)
        {
            connections.Add(connection.Id, connection);
            if (!byUser.TryGetValue(connection.Uid, out var sockets))
                byUser.Add(connection.Uid, sockets = []);
            sockets.Add(connection);
            subscriptions.Add(connection, new(StringComparer.Ordinal));
        }
    }

    internal void Remove(string id)
    {
        lock (sync)
            RemoveConnection(id);
    }

    // Called under sync. Exactly one cleanup path owns a removed connection.
    private bool RemoveConnection(string id)
    {
        if (!connections.Remove(id, out var connection))
            return false;

        var sockets = byUser[connection.Uid];
        sockets.Remove(connection);
        if (sockets.Count == 0)
            byUser.Remove(connection.Uid);
        foreach (var roomId in subscriptions[connection].ToArray())
            Leave(connection, roomId);
        subscriptions.Remove(connection);
        return true;
    }

    private void Invalidate(IEnumerable<Connection> sockets)
    {
        Connection[] removed;
        lock (sync)
        {
            removed = sockets.Where(socket => RemoveConnection(socket.Id)).ToArray();
        }
        foreach (var socket in removed)
            socket.Abort();
    }

    internal bool IsAuthorized(Connection connection)
    {
        lock (sync)
            return connection.IsOpen && subscriptions.ContainsKey(connection);
    }

    internal async Task<Connection> AuthorizeAsync(string id, CancellationToken cancellationToken)
    {
        Connection? connection;
        lock (sync)
            connections.TryGetValue(id, out connection);
        if (connection is not null)
        {
            await RevalidateAsync(connection.Uid, cancellationToken);
            if (IsAuthorized(connection))
                return connection;
        }

        throw new HubException("Unauthorized.");
    }

    private async ValueTask RevalidateAsync(string uid, CancellationToken cancellationToken)
    {
        Connection[] sockets;
        lock (sync)
            sockets = byUser.TryGetValue(uid, out var current) ? current.ToArray() : [];
        if (sockets.Length == 0)
            return;

        FirebaseBearerSession.AccountStatus account;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await authSlots.WaitAsync(budget.Token);
            try
            {
                account = await FirebaseBearerSession.GetAccountStatusAsync(auth, uid, budget.Token);
                // Apply Auth denials before awaiting Firestore. A disconnected caller
                // can cancel the profile read but cannot undo an observed revocation.
                var rejected = sockets.Where(socket => !socket.IsOpen || !account.Allows(socket.Session)).ToArray();
                var eligible = sockets.Except(rejected).ToArray();
                Invalidate(rejected);
                if (eligible.Length != 0 && !await IsEligibleProfileAsync(uid, budget.Token))
                    Invalidate(eligible);
            }
            finally
            {
                authSlots.Release();
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            account = new(null, Unavailable: true);
            Invalidate(sockets);
        }

        if (account.Unavailable)
            logger.LogWarning("SignalR Firebase account revalidation unavailable.");

    }

    internal async Task<bool> IsEligibleProfileAsync(string uid, CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(5));
        using var scope = scopes.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<FirebaseUserService>();
        try
        {
            var user = await users.GetByIdAsync(uid, budget.Token);
            return user is { IsActive: true, IsBanned: false, IsDeleted: false };
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested &&
            ex is RpcException or OperationCanceledException)
        {
            logger.LogWarning("SignalR Firestore profile revalidation unavailable.");
            return false;
        }
    }

    // Serialize room reads and subscription changes that can add recipients.
    // The fixed stripe count avoids retaining a lock for every historical room.
    public SemaphoreSlim RoomGate(string roomId) =>
        roomGates[(uint)StringComparer.Ordinal.GetHashCode(roomId) % (uint)roomGates.Length];

    public async Task<FirebaseChatRoom?> ReadRoomAndEvictAsync(string roomId, CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(5));
        using var scope = scopes.CreateScope();
        var rooms = scope.ServiceProvider.GetRequiredService<FirebaseChatRoomService>();
        var room = await rooms.GetRoomByIdAsync(roomId, budget.Token);
        lock (sync)
        {
            if (byRoom.TryGetValue(roomId, out var members))
            {
                foreach (var member in members.Where(member => !CanReceive(room, member.Uid)).ToArray())
                    Leave(member, roomId);
            }
        }
        return room;
    }

    public static bool IsAvailable(FirebaseChatRoom? room)
    {
        var now = DateTime.UtcNow;
        return room?.Status switch
        {
            "Active" => true,
            "Temporary" => room.TemporaryStartedAt?.ToDateTime() <= now && room.TemporaryEndsAt?.ToDateTime() > now,
            "AwaitingDecision" => room.DecisionEndsAt?.ToDateTime() > now,
            _ => false
        };
    }

    private static bool CanReceive(FirebaseChatRoom? room, string uid) =>
        IsAvailable(room) && (room!.User1Id == uid || room.User2Id == uid);

    internal void Join(Connection connection, string roomId)
    {
        lock (sync)
        {
            if (!IsAuthorized(connection))
                throw new HubException("Unauthorized.");
            if (!byRoom.TryGetValue(roomId, out var members))
                byRoom.Add(roomId, members = []);
            members.Add(connection);
            subscriptions[connection].Add(roomId);
        }
    }

    internal void Leave(Connection connection, string roomId)
    {
        lock (sync)
        {
            if (subscriptions.TryGetValue(connection, out var roomIds) && roomIds.Remove(roomId))
            {
                var members = byRoom[roomId];
                members.Remove(connection);
                if (members.Count == 0)
                    byRoom.Remove(roomId);
            }
        }
    }

    internal async Task BroadcastAsync(
        string roomId, Connection sender, Func<string, IReadOnlyList<string>, Task> send)
    {
        var gate = RoomGate(roomId);
        await gate.WaitAsync(sender.Closed);
        Task delivery;
        try
        {
            Connection[] candidates;
            lock (sync)
                candidates = byRoom.TryGetValue(roomId, out var members) ? members.ToArray() : [];
            if (candidates.Length == 0)
                return;

            // One lookup per UID for this delivery, with per-socket auth_time and exp.
            // A sender disconnect does not cancel checks for other users.
            await Task.WhenAll(candidates.Select(member => member.Uid).Append(sender.Uid).Distinct(StringComparer.Ordinal)
                .Select(uid => RevalidateAsync(uid, CancellationToken.None).AsTask()));

            var room = await ReadRoomAndEvictAsync(roomId, sender.Closed);
            lock (sync)
            {
                if (!IsAuthorized(sender) || !CanReceive(room, sender.Uid) ||
                    !byRoom.TryGetValue(roomId, out var current))
                    return;

                var recipients = candidates.Where(member => current.Contains(member) && IsAuthorized(member))
                    .Select(member => member.Id).ToArray();
                if (recipients.Length == 0)
                    return;

                // Queue writes before an observed invalidation can remove these subscriptions.
                delivery = send(room!.User1Id == sender.Uid ? "User1" : "User2", recipients);
            }
        }
        catch (Exception ex) when (!sender.Closed.IsCancellationRequested &&
            ex is RpcException or OperationCanceledException)
        {
            // Persistence already succeeded. Suppress delivery if room access cannot be read.
            logger.LogWarning("SignalR message delivery suppressed because room access was unavailable.");
            return;
        }
        finally
        {
            gate.Release();
        }

        await delivery;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            string[] users;
            string[] roomIds;
            lock (sync)
            {
                users = byUser.Keys.ToArray();
                roomIds = byRoom.Keys.ToArray();
            }

            var options = new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = stoppingToken };
            await Parallel.ForEachAsync(users, options, RevalidateAsync);
            await Parallel.ForEachAsync(roomIds, options, SweepRoomAsync);
        }
    }

    private async ValueTask SweepRoomAsync(string roomId, CancellationToken cancellationToken)
    {
        var gate = RoomGate(roomId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            await ReadRoomAndEvictAsync(roomId, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested &&
            ex is RpcException or OperationCanceledException)
        {
            logger.LogWarning("SignalR room revalidation unavailable.");
        }
        finally
        {
            gate.Release();
        }
    }
}
