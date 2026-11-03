using Google.Cloud.Firestore;
using WebChatEIU.Models.Firestore;

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
                CreatedAt = now,
                UpdatedAt = now
            };

            var document = await _rooms.AddAsync(room);

            room.RoomId = document.Id;

            return room;
        }

        public async Task<FirebaseChatRoom> CreateTemporaryRoomAsync(
            string user1Id,
            string user2Id,
            string user1Nickname,
            string user2Nickname,
            string matchMode,
            string? purpose = null)
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

            if (matchMode != "Random" && matchMode != "Smart")
            {
                throw new ArgumentException("Invalid match mode.");
            }

            if (matchMode == "Smart" &&
                string.IsNullOrWhiteSpace(purpose))
            {
                throw new ArgumentException(
                    "Purpose is required for Smart Match.");
            }

            var now = DateTime.UtcNow;

            var room = new FirebaseChatRoom
            {
                User1Id = user1Id,
                User2Id = user2Id,
                User1Nickname = user1Nickname,
                User2Nickname = user2Nickname,

                Status = "Temporary",
                MatchMode = matchMode,
                Purpose = purpose,

                AffinityScore = 0,

                TemporaryStartedAt = Timestamp.FromDateTime(now),
                TemporaryEndsAt = Timestamp.FromDateTime(
                    now.AddMinutes(5)),

                CreatedAt = Timestamp.FromDateTime(now),
                UpdatedAt = Timestamp.FromDateTime(now)
            };

            var document = await _rooms.AddAsync(room);

            room.RoomId = document.Id;

            return room;
        }

        public async Task<bool> TryMoveToAwaitingDecisionAsync(string roomId)
        {
            if (string.IsNullOrWhiteSpace(roomId) || roomId.Contains('/'))
            {
                throw new ArgumentException("Invalid room ID.");
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

                    if (room.Status != "Temporary")
                    {
                        return false;
                    }

                    if (!room.TemporaryEndsAt.HasValue)
                    {
                        throw new InvalidOperationException(
                            "Temporary end time is missing.");
                    }

                    var now = DateTime.UtcNow;
                    var temporaryEnd = room.TemporaryEndsAt.Value.ToDateTime();

                    if (now < temporaryEnd)
                    {
                        return false;
                    }

                    var decisionEnd = temporaryEnd.AddMinutes(2);

                    transaction.Update(
                        roomReference,
                        new Dictionary<string, object>
                        {
                            ["Status"] = "AwaitingDecision",
                            ["DecisionEndsAt"] =
                                Timestamp.FromDateTime(decisionEnd),
                            ["UpdatedAt"] =
                                Timestamp.FromDateTime(now)
                        });

                    return true;
                });
        }



        public async Task<FirebaseChatRoom?> GetRoomByIdAsync(
            string roomId)
        {
            if (string.IsNullOrWhiteSpace(roomId))
            {
                return null;
            }

            var snapshot = await _rooms
                .Document(roomId)
                .GetSnapshotAsync();

            if (!snapshot.Exists)
            {
                return null;
            }

            return snapshot.ConvertTo<FirebaseChatRoom>();
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

        public async Task<bool> TryCloseExpiredDecisionAsync(string roomId)
        {
            if (string.IsNullOrWhiteSpace(roomId) ||
                roomId.Contains('/'))
            {
                throw new ArgumentException("Invalid room ID.");
            }

            var roomReference = _rooms.Document(roomId);

            return await _firestoreDb.RunTransactionAsync(
                async transaction =>
                {
                    var snapshot = await transaction
                        .GetSnapshotAsync(roomReference);

                    if (!snapshot.Exists)
                    {
                        return false;
                    }

                    var room = snapshot.ConvertTo<FirebaseChatRoom>();

                    if (room.Status != "AwaitingDecision" ||
                        !room.DecisionEndsAt.HasValue)
                    {
                        return false;
                    }

                    var now = DateTime.UtcNow;

                    if (now < room.DecisionEndsAt.Value.ToDateTime())
                    {
                        return false;
                    }

                    // Kiểm tra lịch sử ghép cặp
                    var pairId =
                        FirebaseMatchHistoryService.GeneratePairId(
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


                    var timestamp = Timestamp.FromDateTime(now);

                    // Chỉ tạo lịch sử nếu cặp chưa tồn tại
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

                    // Đóng phòng và đánh dấu Cleanup
                    transaction.Update(
                        roomReference,
                        new Dictionary<string, object>
                        {
                            ["Status"] = "Closed",
                            ["ClosedAt"] = timestamp,
                            ["UpdatedAt"] = timestamp,
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
