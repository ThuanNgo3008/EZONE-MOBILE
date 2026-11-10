using Google.Cloud.Firestore;
using WebChatEIU.Models.Firestore;

namespace WebChatEIU.Services
{
    public class FirebaseRandomMatchQueueService
    {
        private readonly FirestoreDb _firestoreDb;

        public FirebaseRandomMatchQueueService(FirestoreDb firestoreDb)
        {
            _firestoreDb = firestoreDb;
        }

        public async Task<string> JoinQueueAsync(string userId)
        {
            if (string.IsNullOrWhiteSpace(userId) ||
                userId.Contains('/'))
            {
                throw new ArgumentException("Invalid user ID.");
            }

            var userReference = _firestoreDb
                .Collection("users")
                .Document(userId);

            var queueReference = _firestoreDb
                .Collection("randomMatchQueue")
                .Document(userId);

            var sessionReference = _firestoreDb
                .Collection("matchSessions")
                .Document(userId);

            return await _firestoreDb.RunTransactionAsync(
                async transaction =>
                {
                    var userSnapshot = await transaction
                        .GetSnapshotAsync(userReference);

                    var queueSnapshot = await transaction
                        .GetSnapshotAsync(queueReference);

                    var sessionSnapshot = await transaction
                        .GetSnapshotAsync(sessionReference);

                    if (!userSnapshot.Exists)
                    {
                        throw new InvalidOperationException(
                            "User not found.");
                    }

                    var user = userSnapshot.ConvertTo<FirebaseUser>();

                    if (!user.IsActive ||
                        user.IsBanned ||
                        user.IsDeleted)
                    {
                        throw new InvalidOperationException(
                            "User is not eligible for matching.");
                    }

                    var now = DateTime.UtcNow;
                    var timestamp = Timestamp.FromDateTime(now);

                    FirebaseMatchSession? session = sessionSnapshot.Exists
                        ? sessionSnapshot.ConvertTo<FirebaseMatchSession>()
                        : null;

                    var queue = queueSnapshot.Exists
                        ? queueSnapshot.ConvertTo<FirebaseRandomMatchQueueEntry>()
                        : null;

                    bool isBusy = session != null &&
                        session.Status == "PendingReady" &&
                        session.ExpiresAt.ToDateTime() > now;

                    if (isBusy)
                    {
                        if (session!.Status == "PendingReady" &&
                            queue != null &&
                            queue.Status == "Reserved" &&
                            queue.ReservationId == session.ReservationId)
                        {
                            return "Reserved";
                        }

                        throw new InvalidOperationException(
                            "User already has an active match session.");
                    }

                    if (queue != null && queue.Status == "Waiting")
                    {
                        transaction.Update(
                            queueReference,
                            new Dictionary<string, object>
                            {
                                ["LastSeenAt"] = timestamp,
                                ["UpdatedAt"] = timestamp
                            });

                        return "Waiting";
                    }

                    var entry = new FirebaseRandomMatchQueueEntry
                    {
                        UserId = userId,
                        Status = "Waiting",
                        ReservationId = null,
                        JoinedAt = timestamp,
                        UpdatedAt = timestamp,
                        LastSeenAt = timestamp
                    };

                    transaction.Set(queueReference, entry);

                    return "Waiting";
                });
        }



        public async IAsyncEnumerable<string> GetWaitingCandidatesAsync(
            string userId)
        {
            if (string.IsNullOrWhiteSpace(userId) ||
                userId.Contains('/'))
            {
                throw new ArgumentException("Invalid user ID.");
            }

            const int pageSize = 20;

            Query query = _firestoreDb
                .Collection("randomMatchQueue")
                .WhereEqualTo("Status", "Waiting")
                .OrderBy("JoinedAt")
                .Limit(pageSize);

            while (true)
            {
                var snapshot = await query.GetSnapshotAsync();

                if (snapshot.Documents.Count == 0)
                {
                    yield break;
                }

                foreach (var document in snapshot.Documents)
                {
                    if (document.Id != userId)
                    {
                        yield return document.Id;
                    }
                }

                if (snapshot.Documents.Count < pageSize)
                {
                    yield break;
                }

                var lastDocument =
                    snapshot.Documents[snapshot.Documents.Count - 1];

                query = query.StartAfter(lastDocument);
            }
        }



        public async Task<string?> TryReserveCandidateAsync(
            string userId,
            string candidateId)
        {
            if (string.IsNullOrWhiteSpace(userId) ||
                string.IsNullOrWhiteSpace(candidateId) ||
                userId.Contains('/') ||
                candidateId.Contains('/') ||
                userId == candidateId)
            {
                throw new ArgumentException("Invalid user pair.");
            }

            var users = _firestoreDb.Collection("users");
            var queue = _firestoreDb.Collection("randomMatchQueue");
            var sessions = _firestoreDb.Collection("matchSessions");

            var user1Reference = users.Document(userId);
            var user2Reference = users.Document(candidateId);

            var queue1Reference = queue.Document(userId);
            var queue2Reference = queue.Document(candidateId);

            var session1Reference = sessions.Document(userId);
            var session2Reference = sessions.Document(candidateId);

            var pairId = FirebaseMatchHistoryService
                .GeneratePairId(userId, candidateId);

            var historyReference = _firestoreDb
                .Collection("matchHistory")
                .Document(pairId);

            var reservationReference = _firestoreDb
                .Collection("matchReservations")
                .Document();

            return await _firestoreDb.RunTransactionAsync<string?>(
                async transaction =>
                {
                    // Đọc toàn bộ dữ liệu trước khi ghi.
                    var user1Snapshot = await transaction
                        .GetSnapshotAsync(user1Reference);

                    var user2Snapshot = await transaction
                        .GetSnapshotAsync(user2Reference);

                    var queue1Snapshot = await transaction
                        .GetSnapshotAsync(queue1Reference);

                    var queue2Snapshot = await transaction
                        .GetSnapshotAsync(queue2Reference);

                    var session1Snapshot = await transaction
                        .GetSnapshotAsync(session1Reference);

                    var session2Snapshot = await transaction
                        .GetSnapshotAsync(session2Reference);

                    var historySnapshot = await transaction
                        .GetSnapshotAsync(historyReference);

                    if (!user1Snapshot.Exists ||
                        !user2Snapshot.Exists ||
                        !queue1Snapshot.Exists ||
                        !queue2Snapshot.Exists ||
                        historySnapshot.Exists)
                    {
                        return null;
                    }

                    var user1 = user1Snapshot.ConvertTo<FirebaseUser>();
                    var user2 = user2Snapshot.ConvertTo<FirebaseUser>();

                    if (!user1.IsActive || user1.IsBanned || user1.IsDeleted ||
                        !user2.IsActive || user2.IsBanned || user2.IsDeleted)
                    {
                        return null;
                    }

                    var queue1 = queue1Snapshot
                        .ConvertTo<FirebaseRandomMatchQueueEntry>();

                    var queue2 = queue2Snapshot
                        .ConvertTo<FirebaseRandomMatchQueueEntry>();

                    if (queue1.Status != "Waiting" ||
                        queue2.Status != "Waiting")
                    {
                        return null;
                    }

                    var now = DateTime.UtcNow;
                    var timestamp = Timestamp.FromDateTime(now);


                    var presenceTimeout = TimeSpan.FromSeconds(60);

                    bool user1RecentlyActive =
                        now - queue1.LastSeenAt.ToDateTime() <= presenceTimeout;

                    bool user2RecentlyActive =
                        now - queue2.LastSeenAt.ToDateTime() <= presenceTimeout;

                    if (!user1RecentlyActive || !user2RecentlyActive)
                    {
                        return null;
                    }


                    var session1 = session1Snapshot.Exists
                        ? session1Snapshot.ConvertTo<FirebaseMatchSession>()
                        : null;

                    var session2 = session2Snapshot.Exists
                        ? session2Snapshot.ConvertTo<FirebaseMatchSession>()
                        : null;

                    bool user1Busy = session1 != null &&
                        (session1.Status == "PendingReady" &&
                         session1.ExpiresAt.ToDateTime() > now);

                    bool user2Busy = session2 != null &&
                        (session2.Status == "PendingReady" &&
                         session2.ExpiresAt.ToDateTime() > now);

                    if (user1Busy || user2Busy)
                    {
                        return null;
                    }

                    var expiresAt = Timestamp.FromDateTime(
                        now.AddSeconds(30));

                    var reservation = new FirebaseMatchReservation
                    {
                        ReservationId = reservationReference.Id,
                        User1Id = userId,
                        User2Id = candidateId,
                        MatchMode = "Random",
                        User1Ready = false,
                        User2Ready = false,
                        Status = "PendingReady",
                        ReadyExpiresAt = expiresAt,
                        CreatedAt = timestamp,
                        UpdatedAt = timestamp
                    };

                    transaction.Create(
                        reservationReference,
                        reservation);

                    transaction.Set(
                        session1Reference,
                        new FirebaseMatchSession
                        {
                            UserId = userId,
                            ReservationId = reservationReference.Id,
                            Status = "PendingReady",
                            ExpiresAt = expiresAt,
                            UpdatedAt = timestamp
                        });

                    transaction.Set(
                        session2Reference,
                        new FirebaseMatchSession
                        {
                            UserId = candidateId,
                            ReservationId = reservationReference.Id,
                            Status = "PendingReady",
                            ExpiresAt = expiresAt,
                            UpdatedAt = timestamp
                        });

                    var queueUpdate = new Dictionary<string, object>
                    {
                        ["Status"] = "Reserved",
                        ["ReservationId"] = reservationReference.Id,
                        ["UpdatedAt"] = timestamp
                    };

                    transaction.Update(queue1Reference, queueUpdate);
                    transaction.Update(queue2Reference, queueUpdate);

                    return reservationReference.Id;
                });
        }



        public async Task<(string Status, string? ReservationId)> FindMatchAsync(
            string userId,
            bool joinIfNeeded = true)
        {
            var queueStatus = joinIfNeeded
                ? await JoinQueueAsync(userId)
                : (await HeartbeatAsync(userId)).Status;

            if (queueStatus == "NotSearching")
            {
                return ("NotSearching", null);
            }


            if (queueStatus == "Waiting")
            {
                // Tìm các sinh viên đang chờ.
                var candidates = GetWaitingCandidatesAsync(userId);

                await foreach (var candidateId in candidates)
                {
                    // Thử ghép an toàn bằng Firestore Transaction.
                    var reservationId = await TryReserveCandidateAsync(
                        userId,
                        candidateId);

                    if (reservationId != null)
                    {
                        return ("Reserved", reservationId);
                    }
                }
            }

            // Kiểm tra lại phòng trường hợp request khác vừa ghép
            // người dùng trong lúc đang tìm ứng viên.
            var queueSnapshot = await _firestoreDb
                .Collection("randomMatchQueue")
                .Document(userId)
                .GetSnapshotAsync();

            if (queueSnapshot.Exists)
            {
                var entry = queueSnapshot
                    .ConvertTo<FirebaseRandomMatchQueueEntry>();

                if (entry.Status == "Reserved" &&
                    !string.IsNullOrWhiteSpace(entry.ReservationId))
                {
                    return ("Reserved", entry.ReservationId);
                }
            }

            return queueSnapshot.Exists ? ("Waiting", null) : ("NotSearching", null);
        }


        public async Task<(string Status, string? ReservationId)> HeartbeatAsync(
            string userId)
        {
            if (string.IsNullOrWhiteSpace(userId) ||
                userId.Contains('/'))
            {
                throw new ArgumentException("Invalid user ID.");
            }

            var queueReference = _firestoreDb
                .Collection("randomMatchQueue")
                .Document(userId);

            return await _firestoreDb.RunTransactionAsync(
                async transaction =>
                {
                    var snapshot = await transaction
                        .GetSnapshotAsync(queueReference);

                    if (!snapshot.Exists)
                    {
                        return ("NotSearching", (string?)null);
                    }

                    var queue = snapshot
                        .ConvertTo<FirebaseRandomMatchQueueEntry>();


                    if (queue.Status == "Reserved")
                    {
                        var timestamp = Timestamp.FromDateTime(DateTime.UtcNow);

                        transaction.Update(
                            queueReference,
                            new Dictionary<string, object>
                            {
                                ["LastSeenAt"] = timestamp,
                                ["UpdatedAt"] = timestamp
                            });

                        return ("Reserved", queue.ReservationId);
                    }


                    if (queue.Status != "Waiting")
                    {
                        return ("NotSearching", (string?)null);
                    }

                    var now = Timestamp.FromDateTime(DateTime.UtcNow);

                    transaction.Update(
                        queueReference,
                        new Dictionary<string, object>
                        {
                            ["LastSeenAt"] = now,
                            ["UpdatedAt"] = now
                        });

                    return ("Waiting", (string?)null);
                });
        }



        public async Task<string> CancelWaitingAsync(string userId)
        {
            if (string.IsNullOrWhiteSpace(userId) ||
                userId.Contains('/'))
            {
                throw new ArgumentException("Invalid user ID.");
            }

            var queueReference = _firestoreDb
                .Collection("randomMatchQueue")
                .Document(userId);

            return await _firestoreDb.RunTransactionAsync(
                async transaction =>
                {
                    var queueSnapshot = await transaction
                        .GetSnapshotAsync(queueReference);

                    if (!queueSnapshot.Exists)
                    {
                        return "NotSearching";
                    }

                    var queue = queueSnapshot
                        .ConvertTo<FirebaseRandomMatchQueueEntry>();

                    if (queue.Status == "Waiting")
                    {
                        transaction.Delete(queueReference);
                        return "Cancelled";
                    }

                    if (queue.Status != "Reserved" ||
                        string.IsNullOrWhiteSpace(queue.ReservationId))
                    {
                        return "NotSearching";
                    }

                    var reservationId = queue.ReservationId;

                    var reservationReference = _firestoreDb
                        .Collection("matchReservations")
                        .Document(reservationId);

                    var reservationSnapshot = await transaction
                        .GetSnapshotAsync(reservationReference);

                    if (!reservationSnapshot.Exists)
                    {
                        transaction.Delete(queueReference);
                        return "Cancelled";
                    }

                    var reservation = reservationSnapshot
                        .ConvertTo<FirebaseMatchReservation>();

                    if (reservation.User1Id != userId &&
                        reservation.User2Id != userId)
                    {
                        return "NotSearching";
                    }

                    if (reservation.Status == "Matched")
                    {
                        return "Matched";
                    }

                    if (reservation.Status != "PendingReady")
                    {
                        transaction.Delete(queueReference);
                        return "Cancelled";
                    }

                    bool isUser1 = reservation.User1Id == userId;

                    var otherUserId = isUser1
                        ? reservation.User2Id
                        : reservation.User1Id;

                    var otherQueueReference = _firestoreDb
                        .Collection("randomMatchQueue")
                        .Document(otherUserId);

                    var session1Reference = _firestoreDb
                        .Collection("matchSessions")
                        .Document(reservation.User1Id);

                    var session2Reference = _firestoreDb
                        .Collection("matchSessions")
                        .Document(reservation.User2Id);

                    // Đọc toàn bộ trước khi ghi.
                    var otherQueueSnapshot = await transaction
                        .GetSnapshotAsync(otherQueueReference);

                    var session1Snapshot = await transaction
                        .GetSnapshotAsync(session1Reference);

                    var session2Snapshot = await transaction
                        .GetSnapshotAsync(session2Reference);

                    var now = DateTime.UtcNow;
                    var timestamp = Timestamp.FromDateTime(now);

                    var newStatus = now >= reservation.ReadyExpiresAt.ToDateTime()
                        ? "Expired"
                        : "Cancelled";

                    transaction.Update(
                        reservationReference,
                        new Dictionary<string, object>
                        {
                            ["Status"] = newStatus,
                            ["UpdatedAt"] = timestamp
                        });

                    // Người nhấn Cancel luôn rời Queue.
                    transaction.Delete(queueReference);

                    // Người còn lại được xử lý theo trạng thái Ready.
                    if (otherQueueSnapshot.Exists)
                    {
                        var otherQueue = otherQueueSnapshot
                            .ConvertTo<FirebaseRandomMatchQueueEntry>();

                        if (otherQueue.Status == "Reserved" &&
                            otherQueue.ReservationId == reservationId)
                        {
                            bool otherReady = isUser1
                                ? reservation.User2Ready
                                : reservation.User1Ready;


                            if (otherReady)
                            {
                                transaction.Update(
                                    otherQueueReference,
                                    new Dictionary<string, object>
                                    {
                                        ["Status"] = "Waiting",
                                        ["ReservationId"] = null!,
                                        ["UpdatedAt"] = timestamp
                                    });
                            }

                            else
                            {
                                transaction.Delete(otherQueueReference);
                            }
                        }
                    }

                    // Giải phóng Session của User 1.
                    if (session1Snapshot.Exists)
                    {
                        var session1 = session1Snapshot
                            .ConvertTo<FirebaseMatchSession>();

                        if (session1.ReservationId == reservationId &&
                            session1.Status == "PendingReady")
                        {
                            transaction.Delete(session1Reference);
                        }
                    }

                    // Giải phóng Session của User 2.
                    if (session2Snapshot.Exists)
                    {
                        var session2 = session2Snapshot
                            .ConvertTo<FirebaseMatchSession>();

                        if (session2.ReservationId == reservationId &&
                            session2.Status == "PendingReady")
                        {
                            transaction.Delete(session2Reference);
                        }
                    }

                    return newStatus == "Expired"
                        ? "Expired"
                        : "Cancelled";
                });
        }


        public async Task<bool> TryRemoveStaleWaitingAsync(string userId)
        {
            if (string.IsNullOrWhiteSpace(userId) ||
                userId.Contains('/'))
            {
                throw new ArgumentException("Invalid user ID.");
            }

            var queueReference = _firestoreDb
                .Collection("randomMatchQueue")
                .Document(userId);

            return await _firestoreDb.RunTransactionAsync(
                async transaction =>
                {
                    var snapshot = await transaction
                        .GetSnapshotAsync(queueReference);

                    if (!snapshot.Exists)
                    {
                        return false;
                    }

                    var queue = snapshot
                        .ConvertTo<FirebaseRandomMatchQueueEntry>();

                    if (queue.Status != "Waiting")
                    {
                        return false;
                    }

                    var now = DateTime.UtcNow;
                    var lastSeen = queue.LastSeenAt.ToDateTime();

                    if (now - lastSeen <= TimeSpan.FromSeconds(60))
                    {
                        return false;
                    }

                    transaction.Delete(queueReference);

                    return true;
                });
        }





    }
}
