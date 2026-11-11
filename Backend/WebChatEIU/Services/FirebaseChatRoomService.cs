using Google.Cloud.Firestore;
using WebChatEIU.Models.Firestore;
using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;

namespace WebChatEIU.Services
{
    public class FirebaseChatRoomService
    {
        private const string CollectionName = "chatRooms";

        private readonly CollectionReference _rooms;

        private readonly FirestoreDb _firestoreDb;

        public FirebaseChatRoomService(FirestoreDb firestoreDb)
        {
            _rooms = firestoreDb.Collection(CollectionName);
            _firestoreDb = firestoreDb;
        }


        public async Task<FirebaseChatRoom> CreateRoomAsync(
            string user1Id,
            string user2Id,
            string user1Nickname,
            string user2Nickname)
        {
            if (string.IsNullOrWhiteSpace(user1Id) ||
                string.IsNullOrWhiteSpace(user2Id))
            {
                throw new ArgumentException("User ID is required.");
            }

            if (user1Id == user2Id)
            {
                throw new ArgumentException(
                    "Cannot match a user with themselves.");
            }

            var now = Timestamp.FromDateTime(DateTime.UtcNow);

            var room = new FirebaseChatRoom
            {
                User1Id = user1Id,
                User2Id = user2Id,
                User1Nickname = user1Nickname,
                User2Nickname = user2Nickname,
                Status = "Active",
                MatchMode = "Random",
                AffinityScore = 0,
                BecamePersistentAt = now,
                CreatedAt = now,
                UpdatedAt = now
            };

            var document = await _rooms.AddAsync(room);

            room.RoomId = document.Id;

            return room;
        }

        public async Task<FirebaseChatRoom?> GetRoomByIdAsync(
            string roomId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(roomId))
            {
                return null;
            }

            var snapshot = await _rooms
                .Document(roomId)
                .GetSnapshotAsync(cancellationToken);

            if (!snapshot.Exists)
            {
                return null;
            }

            return snapshot.ConvertTo<FirebaseChatRoom>();
        }


        public async Task<(IReadOnlyList<FirebaseChatRoom> Rooms, string? NextCursor)> ListForMemberAsync(
            string userId, int pageSize, string? cursor, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(userId) || pageSize is < 1 or > 50)
                throw new ArgumentException("Invalid room listing parameters.");

            var query = _rooms.Where(Filter.Or(
                    Filter.EqualTo("User1Id", userId),
                    Filter.EqualTo("User2Id", userId)))
                .OrderByDescending("CreatedAt")
                .OrderByDescending(FieldPath.DocumentId)
                .Limit(pageSize + 1);

            if (cursor is not null)
            {
                var (createdAt, roomId) = DecodeListCursor(cursor);
                query = query.StartAfter(createdAt, _rooms.Document(roomId));
            }

            var snapshot = await query.GetSnapshotAsync(cancellationToken);
            var rooms = snapshot.Documents.Take(pageSize).Select(doc =>
            {
                var room = doc.ConvertTo<FirebaseChatRoom>();
                if (!doc.ContainsField("UpdatedAt"))
                    room.UpdatedAt = room.CreatedAt;
                return room;
            }).ToArray();
            if (snapshot.Count <= pageSize)
                return (rooms, null);

            var last = rooms[^1];
            var position = last.CreatedAt.ToDateTime().Ticks.ToString(CultureInfo.InvariantCulture) + ":" + last.RoomId;
            return (rooms, WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(position)));
        }

        private static (Timestamp CreatedAt, string RoomId) DecodeListCursor(string cursor)
        {
            if (cursor.Length is 0 or > 2048 ||
                cursor.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
                throw new ArgumentException("Invalid room cursor.");

            string position;
            try
            {
                position = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(cursor));
            }
            catch (FormatException)
            {
                throw new ArgumentException("Invalid room cursor.");
            }

            var parts = position.Split(':', 2);
            if (parts.Length != 2 ||
                !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) ||
                ticks > DateTime.MaxValue.Ticks ||
                string.IsNullOrWhiteSpace(parts[1]) || parts[1] is "." or ".." ||
                parts[1].Contains('/') || Encoding.UTF8.GetByteCount(parts[1]) > 1500 ||
                (parts[1].Length >= 4 && parts[1].StartsWith("__", StringComparison.Ordinal) &&
                    parts[1].EndsWith("__", StringComparison.Ordinal)))
            {
                throw new ArgumentException("Invalid room cursor.");
            }

            return (Timestamp.FromDateTime(new DateTime(ticks, DateTimeKind.Utc)), parts[1]);
        }

        public async Task<bool> CloseRoomAsync(
            string roomId,
            string userId)
        {
            if (string.IsNullOrWhiteSpace(roomId) ||
                roomId.Contains('/') ||
                string.IsNullOrWhiteSpace(userId))
            {
                throw new ArgumentException("Invalid room or user.");
            }

            var roomReference = _rooms.Document(roomId);

            return await _firestoreDb.RunTransactionAsync(
                async transaction =>
                {
                    var snapshot = await transaction
                        .GetSnapshotAsync(roomReference);

                    if (!snapshot.Exists)
                    {
                        throw new InvalidOperationException(
                            "Room not found.");
                    }

                    var room = snapshot.ConvertTo<FirebaseChatRoom>();

                    if (room.User1Id != userId &&
                        room.User2Id != userId)
                    {
                        throw new UnauthorizedAccessException(
                            "Access denied.");
                    }

                    if (room.Status == "Closed")
                    {
                        return false;
                    }

                    if (room.Status != "Active")
                    {
                        throw new InvalidOperationException(
                            "Room is not active.");
                    }

                    transaction.Update(
                        roomReference,
                        new Dictionary<string, object>
                        {
                            ["Status"] = "Closed",
                            ["UpdatedAt"] =
                                Timestamp.FromDateTime(DateTime.UtcNow)
                        });

                    return true;
                });
        }


        public async Task<bool> UnmatchRoomAsync(
            string roomId,
            string userId)
        {
            if (string.IsNullOrWhiteSpace(roomId) ||
                roomId.Contains('/') ||
                string.IsNullOrWhiteSpace(userId))
            {
                throw new ArgumentException("Invalid room or user.");
            }

            var roomReference = _rooms.Document(roomId);

            return await _firestoreDb.RunTransactionAsync(
                async transaction =>
                {
                    var roomSnapshot = await transaction
                        .GetSnapshotAsync(roomReference);

                    if (!roomSnapshot.Exists)
                    {
                        throw new InvalidOperationException(
                            "Room not found.");
                    }

                    var room = roomSnapshot.ConvertTo<FirebaseChatRoom>();

                    if (room.User1Id != userId &&
                        room.User2Id != userId)
                    {
                        throw new UnauthorizedAccessException(
                            "Access denied.");
                    }

                    if (room.Status == "Closed")
                    {
                        return false;
                    }

                    if (room.Status != "Active" ||
                        !room.BecamePersistentAt.HasValue)
                    {
                        throw new InvalidOperationException(
                            "Room is not a persistent chat.");
                    }

                    var pairId = FirebaseMatchHistoryService.GeneratePairId(
                        room.User1Id,
                        room.User2Id);

                    var historyReference = _firestoreDb
                        .Collection("matchHistory")
                        .Document(pairId);

                    var historySnapshot = await transaction
                        .GetSnapshotAsync(historyReference);


                    var session1Reference = _firestoreDb
                        .Collection("matchSessions")
                        .Document(room.User1Id);

                    var session2Reference = _firestoreDb
                        .Collection("matchSessions")
                        .Document(room.User2Id);

                    var session1Snapshot = await transaction
                        .GetSnapshotAsync(session1Reference);

                    var session2Snapshot = await transaction
                        .GetSnapshotAsync(session2Reference);


                    var timestamp = Timestamp.FromDateTime(
                        DateTime.UtcNow);

                    if (!historySnapshot.Exists)
                    {
                        var users = new[]
                        {
                    room.User1Id,
                    room.User2Id
                        };

                        Array.Sort(users, StringComparer.Ordinal);

                        transaction.Create(
                            historyReference,
                            new FirebaseMatchHistory
                            {
                                PairId = pairId,
                                User1Id = users[0],
                                User2Id = users[1],
                                RoomId = roomId,
                                CreatedAt = timestamp
                            });
                    }

                    transaction.Update(
                        roomReference,
                        new Dictionary<string, object>
                        {
                            ["Status"] = "Closed",
                            ["ClosedAt"] = timestamp,
                            ["UpdatedAt"] = timestamp,
                            ["CleanupPending"] = false
                        });


                    if (session1Snapshot.Exists)
                    {
                        var session1 = session1Snapshot
                            .ConvertTo<FirebaseMatchSession>();

                        if (session1.RoomId == roomId &&
                            session1.Status == "Temporary")
                        {
                            transaction.Delete(session1Reference);
                        }
                    }

                    if (session2Snapshot.Exists)
                    {
                        var session2 = session2Snapshot
                            .ConvertTo<FirebaseMatchSession>();

                        if (session2.RoomId == roomId &&
                            session2.Status == "Temporary")
                        {
                            transaction.Delete(session2Reference);
                        }
                    }


                    return true;
                });
        }


        public async Task<bool> EndTemporaryRoomAsync(
            string roomId,
            string userId)
        {
            if (string.IsNullOrWhiteSpace(roomId) ||
                roomId.Contains('/') ||
                string.IsNullOrWhiteSpace(userId))
            {
                throw new ArgumentException("Invalid room or user.");
            }

            var roomReference = _rooms.Document(roomId);

            return await _firestoreDb.RunTransactionAsync(
                async transaction =>
                {
                    var snapshot = await transaction
                        .GetSnapshotAsync(roomReference);

                    if (!snapshot.Exists)
                    {
                        throw new InvalidOperationException(
                            "Room not found.");
                    }

                    var room = snapshot.ConvertTo<FirebaseChatRoom>();

                    if (room.User1Id != userId &&
                        room.User2Id != userId)
                    {
                        throw new UnauthorizedAccessException(
                            "Access denied.");
                    }

                    if (room.Status == "Closed")
                    {
                        return false;
                    }

                    if ((room.Status != "Temporary" &&
                         room.Status != "AwaitingDecision") ||
                        !room.TemporaryStartedAt.HasValue ||
                        room.BecamePersistentAt.HasValue)
                    {
                        throw new InvalidOperationException(
                            "Room is not a temporary chat.");
                    }

                    var pairId =
                        FirebaseMatchHistoryService.GeneratePairId(
                            room.User1Id,
                            room.User2Id);

                    var historyReference = _firestoreDb
                        .Collection("matchHistory")
                        .Document(pairId);

                    var historySnapshot = await transaction
                        .GetSnapshotAsync(historyReference);

                    var session1Reference = _firestoreDb.Collection("matchSessions").Document(room.User1Id);

                    var session2Reference = _firestoreDb
                        .Collection("matchSessions")
                        .Document(room.User2Id);

                    var session1Snapshot = await transaction
                        .GetSnapshotAsync(session1Reference);

                    var session2Snapshot = await transaction
                        .GetSnapshotAsync(session2Reference);

                    var now = Timestamp.FromDateTime(DateTime.UtcNow);

                    if (!historySnapshot.Exists)
                    {
                        var users = new[]
                        {
                    room.User1Id,
                    room.User2Id
                        };

                        Array.Sort(users, StringComparer.Ordinal);

                        transaction.Create(
                            historyReference,
                            new FirebaseMatchHistory
                            {
                                PairId = pairId,
                                User1Id = users[0],
                                User2Id = users[1],
                                RoomId = roomId,
                                CreatedAt = now
                            });
                    }

                    transaction.Update(
                        roomReference,
                        new Dictionary<string, object>
                        {
                            ["Status"] = "Closed",
                            ["ClosedAt"] = now,
                            ["UpdatedAt"] = now,
                            ["CleanupPending"] = true
                        });

                    if (session1Snapshot.Exists)
                    {
                        var session1 = session1Snapshot
                            .ConvertTo<FirebaseMatchSession>();

                        if (session1.RoomId == roomId &&
                            session1.Status == "Temporary")
                        {
                            transaction.Delete(session1Reference);
                        }
                    }

                    if (session2Snapshot.Exists)
                    {
                        var session2 = session2Snapshot
                            .ConvertTo<FirebaseMatchSession>();

                        if (session2.RoomId == roomId &&
                            session2.Status == "Temporary")
                        {
                            transaction.Delete(session2Reference);
                        }
                    }

                    return true;
                });
        }

    }
}
