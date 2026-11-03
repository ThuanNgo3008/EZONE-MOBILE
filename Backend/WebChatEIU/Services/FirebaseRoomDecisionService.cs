using Google.Cloud.Firestore;
using WebChatEIU.Models.Firestore;

namespace WebChatEIU.Services
{
    public class FirebaseRoomDecisionService
    {
        private readonly FirestoreDb _firestoreDb;

        public FirebaseRoomDecisionService(FirestoreDb firestoreDb)
        {
            _firestoreDb = firestoreDb;
        }

        public async Task<string> SubmitDecisionAsync(
            string roomId,
            string userId,
            bool continueChat)
        {
            if (string.IsNullOrWhiteSpace(roomId) ||
                roomId.Contains('/') ||
                string.IsNullOrWhiteSpace(userId))
            {
                throw new ArgumentException("Invalid room or user.");
            }

            var roomReference = _firestoreDb
                .Collection("chatRooms")
                .Document(roomId);

            var decisionReference = _firestoreDb
                .Collection("roomDecisions")
                .Document(roomId);

            return await _firestoreDb.RunTransactionAsync(
                async transaction =>
                {
                    var roomSnapshot = await transaction
                        .GetSnapshotAsync(roomReference);

                    var decisionSnapshot = await transaction
                        .GetSnapshotAsync(decisionReference);

                    if (!roomSnapshot.Exists)
                    {
                        throw new InvalidOperationException(
                            "Room not found.");
                    }

                    var room = roomSnapshot
                        .ConvertTo<FirebaseChatRoom>();


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


                    bool isUser1 = room.User1Id == userId;

                    if (!isUser1 && room.User2Id != userId)
                    {
                        throw new UnauthorizedAccessException(
                            "Access denied.");
                    }

                    if (room.Status != "AwaitingDecision")
                    {
                        throw new InvalidOperationException(
                            "Room is not awaiting decision.");
                    }

                    if (!room.DecisionEndsAt.HasValue)
                    {
                        throw new InvalidOperationException(
                            "Decision deadline is missing.");
                    }

                    var now = DateTime.UtcNow;
                    var timestamp = Timestamp.FromDateTime(now);


                    if (now >= room.DecisionEndsAt.Value.ToDateTime())
                    {
                        var pairId = FirebaseMatchHistoryService.GeneratePairId(
                            room.User1Id,
                            room.User2Id);

                        var expiredHistoryReference = _firestoreDb.Collection("matchHistory").Document(pairId);

                        var expiredHistorySnapshot = await transaction
                            .GetSnapshotAsync(expiredHistoryReference);

                        if (!expiredHistorySnapshot.Exists)
                        {
                            var users = new[]
                            {
                                room.User1Id,
                                room.User2Id
                            };

                            Array.Sort(users, StringComparer.Ordinal);

                            transaction.Create(
                                expiredHistoryReference,
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


                        return "Closed";
                    }


                    var decision = decisionSnapshot.Exists
                        ? decisionSnapshot.ConvertTo<FirebaseRoomDecision>()
                        : new FirebaseRoomDecision();

                    bool? previousDecision = isUser1
                        ? decision.User1Continue
                        : decision.User2Continue;

                    if (previousDecision.HasValue)
                    {
                        if (previousDecision.Value != continueChat)
                        {
                            throw new InvalidOperationException(
                                "Decision cannot be changed.");
                        }

                        return "Waiting";
                    }

                    bool? user1Continue = isUser1
                        ? continueChat
                        : decision.User1Continue;

                    bool? user2Continue = isUser1
                        ? decision.User2Continue
                        : continueChat;

                    string fieldName = isUser1
                        ? "User1Continue"
                        : "User2Continue";

                    DocumentReference? historyReference = null;
                    DocumentSnapshot? historySnapshot = null;

                    if (!continueChat)
                    {
                        var pairId = FirebaseMatchHistoryService.GeneratePairId(
                            room.User1Id,
                            room.User2Id);

                        historyReference = _firestoreDb
                            .Collection("matchHistory")
                            .Document(pairId);

                        historySnapshot = await transaction
                            .GetSnapshotAsync(historyReference);
                    }

                    transaction.Set(
                        decisionReference,
                        new Dictionary<string, object>
                        {
                            [fieldName] = continueChat
                        },
                        SetOptions.MergeAll);

                    if (!continueChat)
                    {
                        if (historyReference != null && historySnapshot != null && !historySnapshot.Exists)
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
                                    PairId = historyReference.Id,
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


                        return "Closed";
                    }

                    if (user1Continue == true &&
                        user2Continue == true)
                    {
                        transaction.Update(
                            roomReference,
                            new Dictionary<string, object>
                            {
                                ["Status"] = "Active",
                                ["BecamePersistentAt"] = timestamp,
                                ["UpdatedAt"] = timestamp
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


                        return "Active";
                    }

                    return "Waiting";
                });
        }
    }
}
