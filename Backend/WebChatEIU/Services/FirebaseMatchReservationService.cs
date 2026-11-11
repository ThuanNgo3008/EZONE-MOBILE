using Google.Cloud.Firestore;
using WebChatEIU.Models.Firestore;

namespace WebChatEIU.Services
{
    public class FirebaseMatchReservationService
    {
        private readonly FirestoreDb _firestoreDb;
        private readonly FirebaseMatchEligibilityService _eligibility;

        public FirebaseMatchReservationService(FirestoreDb firestoreDb, FirebaseMatchEligibilityService eligibility)
        {
            _firestoreDb = firestoreDb;
            _eligibility = eligibility;
        }

        public async Task<string> CreateReservationAsync(
            string user1Id,
            string user2Id,
            string matchMode,
            string? purpose = null)
        {
            if (string.IsNullOrWhiteSpace(user1Id) ||
                string.IsNullOrWhiteSpace(user2Id) ||
                user1Id.Contains('/') ||
                user2Id.Contains('/') ||
                user1Id == user2Id)
            {
                throw new ArgumentException("Invalid user pair.");
            }

            if (matchMode != "Random" && matchMode != "Smart")
            {
                throw new ArgumentException("Invalid match mode.");
            }

            if (matchMode == "Smart" &&
                string.IsNullOrWhiteSpace(purpose))
            {
                throw new ArgumentException("Purpose is required.");
            }

            await _eligibility.EnsureAuthEligibleAsync(user1Id);
            await _eligibility.EnsureAuthEligibleAsync(user2Id);

            var pairId = FirebaseMatchHistoryService.GeneratePairId(
                user1Id,
                user2Id);

            var user1Reference = _firestoreDb
                .Collection("users")
                .Document(user1Id);

            var user2Reference = _firestoreDb
                .Collection("users")
                .Document(user2Id);

            var historyReference = _firestoreDb
                .Collection("matchHistory")
                .Document(pairId);

            var reservationReference = _firestoreDb
                .Collection("matchReservations")
                .Document();

            var session1Reference = _firestoreDb
                .Collection("matchSessions")
                .Document(user1Id);

            var session2Reference = _firestoreDb
                .Collection("matchSessions")
                .Document(user2Id);

            return await _firestoreDb.RunTransactionAsync(
                async transaction =>
                {
                    var user1Snapshot = await transaction
                        .GetSnapshotAsync(user1Reference);

                    var user2Snapshot = await transaction
                        .GetSnapshotAsync(user2Reference);

                    var historySnapshot = await transaction
                        .GetSnapshotAsync(historyReference);

                    var session1Snapshot = await transaction.GetSnapshotAsync(session1Reference);

                    var session2Snapshot = await transaction.GetSnapshotAsync(session2Reference);

                    if (!user1Snapshot.Exists || !user2Snapshot.Exists)
                    {
                        throw new InvalidOperationException(
                            "User not found.");
                    }

                    var user1 = user1Snapshot.ConvertTo<FirebaseUser>();
                    var user2 = user2Snapshot.ConvertTo<FirebaseUser>();

                    if (!FirebaseMatchEligibilityService.HasEligibleProfile(user1) ||
                        !FirebaseMatchEligibilityService.HasEligibleProfile(user2))
                    {
                        throw new InvalidOperationException(
                            "Users must be active.");
                    }

                    if (historySnapshot.Exists)
                    {
                        throw new InvalidOperationException(
                            "These users have already matched.");
                    }

                    var now = DateTime.UtcNow;
                    var timestamp = Timestamp.FromDateTime(now);

                    var session1 = session1Snapshot.Exists ? session1Snapshot.ConvertTo<FirebaseMatchSession>() : null;

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
                        throw new InvalidOperationException(
                            "One or both users already have an active match session.");
                    }

                    var reservation = new FirebaseMatchReservation
                    {
                        ReservationId = reservationReference.Id,
                        User1Id = user1Id,
                        User2Id = user2Id,
                        MatchMode = matchMode,
                        Purpose = purpose,

                        User1Ready = false,
                        User2Ready = false,

                        Status = "PendingReady",

                        ReadyExpiresAt = Timestamp.FromDateTime(
                            now.AddSeconds(30)),

                        CreatedAt = timestamp,
                        UpdatedAt = timestamp
                    };

                    transaction.Create(
                        reservationReference,
                        reservation);

                    var expiresAt = Timestamp.FromDateTime(now.AddSeconds(30));

                    transaction.Set(
                        session1Reference,
                        new FirebaseMatchSession
                        {
                            UserId = user1Id,
                            ReservationId = reservationReference.Id,
                            Status = "PendingReady",
                            ExpiresAt = expiresAt,
                            UpdatedAt = timestamp
                        });

                    transaction.Set(
                        session2Reference,
                        new FirebaseMatchSession
                        {
                            UserId = user2Id,
                            ReservationId = reservationReference.Id,
                            Status = "PendingReady",
                            ExpiresAt = expiresAt,
                            UpdatedAt = timestamp
                        });

                    return reservationReference.Id;
                });
        }


        public async Task<(string Status, string? RoomId)> SubmitReadyAsync(
            string reservationId,
            string userId)
        {
            if (string.IsNullOrWhiteSpace(reservationId) ||
                reservationId.Contains('/') ||
                string.IsNullOrWhiteSpace(userId))
            {
                throw new ArgumentException("Invalid reservation or user.");
            }

            var reservationReference = _firestoreDb
                .Collection("matchReservations")
                .Document(reservationId);

            var newRoomReference = _firestoreDb
                .Collection("chatRooms")
                .Document();

            // Resolve participants before Auth calls, never from a transaction callback.
            var preflightSnapshot = await reservationReference.GetSnapshotAsync();
            if (!preflightSnapshot.Exists)
                throw new InvalidOperationException("Reservation not found.");
            var preflight = preflightSnapshot.ConvertTo<FirebaseMatchReservation>();
            if (preflight.User1Id != userId && preflight.User2Id != userId)
                throw new UnauthorizedAccessException("Access denied.");
            bool authChecked = preflight.Status == "PendingReady" &&
                DateTime.UtcNow < preflight.ReadyExpiresAt.ToDateTime();
            if (authChecked)
            {
                await _eligibility.EnsureAuthEligibleAsync(preflight.User1Id);
                await _eligibility.EnsureAuthEligibleAsync(preflight.User2Id);
            }

            return await _firestoreDb.RunTransactionAsync<(string Status, string? RoomId)>(async transaction =>
            {
                var snapshot = await transaction.GetSnapshotAsync(reservationReference);
                if (!snapshot.Exists)
                {
                    throw new InvalidOperationException("Reservation not found.");
                }
                var reservation = snapshot.ConvertTo<FirebaseMatchReservation>();
                bool isUser1 = reservation.User1Id == userId;

                if (!isUser1 && reservation.User2Id != userId)
                {
                    throw new UnauthorizedAccessException("Access denied.");
                }

                if (reservation.Status == "Matched" && !string.IsNullOrEmpty(reservation.RoomId))
                {
                    return ("Matched", reservation.RoomId);
                }

                if (reservation.Status != "PendingReady")
                {
                    throw new InvalidOperationException(
                "Reservation is no longer available.");
                }

                var now = DateTime.UtcNow;
                var timestamp = Timestamp.FromDateTime(now);


                var session1Reference = _firestoreDb
                    .Collection("matchSessions")
                    .Document(reservation.User1Id);

                var session2Reference = _firestoreDb
                    .Collection("matchSessions")
                    .Document(reservation.User2Id);

                var session1Snapshot = await transaction
                    .GetSnapshotAsync(session1Reference);

                var session2Snapshot = await transaction
                    .GetSnapshotAsync(session2Reference);

                var queue1Reference = _firestoreDb
                    .Collection("randomMatchQueue")
                    .Document(reservation.User1Id);

                var queue2Reference = _firestoreDb
                    .Collection("randomMatchQueue")
                    .Document(reservation.User2Id);

                var queue1Snapshot = await transaction
                    .GetSnapshotAsync(queue1Reference);

                var queue2Snapshot = await transaction
                    .GetSnapshotAsync(queue2Reference);



                var session1 = session1Snapshot.Exists
                    ? session1Snapshot.ConvertTo<FirebaseMatchSession>()
                    : null;

                var session2 = session2Snapshot.Exists
                    ? session2Snapshot.ConvertTo<FirebaseMatchSession>()
                    : null;


                if (now >= reservation.ReadyExpiresAt.ToDateTime())
                {
                    transaction.Update(
                        reservationReference,
                        new Dictionary<string, object>
                        {
                            ["Status"] = "Expired",
                            ["UpdatedAt"] = timestamp
                        });

                    if (session1 != null &&
                        session1.ReservationId == reservationId &&
                        session1.Status == "PendingReady")
                    {
                        transaction.Delete(session1Reference);
                    }

                    if (session2 != null &&
                        session2.ReservationId == reservationId &&
                        session2.Status == "PendingReady")
                    {
                        transaction.Delete(session2Reference);
                    }

                    // User 1: Ready thì quay lại Waiting, chưa Ready thì rời Queue.
                    if (queue1Snapshot.Exists)
                    {
                        var queue1 = queue1Snapshot
                            .ConvertTo<FirebaseRandomMatchQueueEntry>();

                        if (queue1.Status == "Reserved" &&
                            queue1.ReservationId == reservationId)
                        {
                            if (reservation.User1Ready)
                            {
                                transaction.Update(
                                    queue1Reference,
                                    new Dictionary<string, object>
                                    {
                                        ["Status"] = "Waiting",
                                        ["ReservationId"] = null!,
                                        ["UpdatedAt"] = timestamp
                                    });
                            }
                            else
                            {
                                transaction.Delete(queue1Reference);
                            }
                        }
                    }

                    // User 2: Xử lý tương tự.
                    if (queue2Snapshot.Exists)
                    {
                        var queue2 = queue2Snapshot
                            .ConvertTo<FirebaseRandomMatchQueueEntry>();

                        if (queue2.Status == "Reserved" &&
                            queue2.ReservationId == reservationId)
                        {
                            if (reservation.User2Ready)
                            {
                                transaction.Update(
                                    queue2Reference,
                                    new Dictionary<string, object>
                                    {
                                        ["Status"] = "Waiting",
                                        ["ReservationId"] = null!,
                                        ["UpdatedAt"] = timestamp
                                    });
                            }
                            else
                            {
                                transaction.Delete(queue2Reference);
                            }
                        }
                    }

                    return ("Expired", null);
                }


                if (!authChecked || reservation.User1Id != preflight.User1Id ||
                    reservation.User2Id != preflight.User2Id)
                    throw new InvalidOperationException("Reservation participants require a new eligibility check.");

                if (session1 == null || session2 == null)
                {
                    throw new InvalidOperationException(
                        "Match session not found.");
                }

                if (session1.ReservationId != reservationId ||
                    session2.ReservationId != reservationId ||
                    session1.Status != "PendingReady" ||
                    session2.Status != "PendingReady" ||
                    now >= session1.ExpiresAt.ToDateTime() ||
                    now >= session2.ExpiresAt.ToDateTime())
                {
                    throw new InvalidOperationException(
                        "Reservation no longer owns both match sessions.");
                }


                var user1Reference = _firestoreDb.Collection("users").Document(reservation.User1Id);

                var user2Reference = _firestoreDb.Collection("users").Document(reservation.User2Id);

                var pairId = FirebaseMatchHistoryService.GeneratePairId(reservation.User1Id, reservation.User2Id);

                var historyReference = _firestoreDb.Collection("matchHistory").Document(pairId);

                // Tất cả thao tác đọc phải thực hiện trước khi ghi.
                var user1Snapshot = await transaction.GetSnapshotAsync(user1Reference);

                var user2Snapshot = await transaction.GetSnapshotAsync(user2Reference);

                var historySnapshot = await transaction.GetSnapshotAsync(historyReference);

                if (!user1Snapshot.Exists || !user2Snapshot.Exists)
                {
                    throw new InvalidOperationException("User not found.");
                }

                var user1 = user1Snapshot.ConvertTo<FirebaseUser>();
                var user2 = user2Snapshot.ConvertTo<FirebaseUser>();

                if (!FirebaseMatchEligibilityService.HasEligibleProfile(user1) ||
                    !FirebaseMatchEligibilityService.HasEligibleProfile(user2))
                {
                    throw new InvalidOperationException("Users must be active.");
                }

                if (historySnapshot.Exists)
                {
                    throw new InvalidOperationException("Users have already matched.");
                }

                bool user1Ready = reservation.User1Ready || isUser1;
                bool user2Ready = reservation.User2Ready || !isUser1;

                if (!user1Ready || !user2Ready)
                {
                    transaction.Update(reservationReference, new Dictionary<string, object>
                    {
                        [isUser1 ? "User1Ready" : "User2Ready"] = true,
                        ["UpdatedAt"] = timestamp
                    });

                    return ("Waiting", null);
                }

                // Create persistent anonymous chat and release reservation locks atomically.
                var room = new FirebaseChatRoom
                {
                    RoomId = newRoomReference.Id,
                    User1Id = reservation.User1Id,
                    User2Id = reservation.User2Id,
                    User1Nickname = "Anonymous Fox",
                    User2Nickname = "Anonymous Panda",
                    Status = "Active",
                    MatchMode = reservation.MatchMode,
                    Purpose = reservation.Purpose,
                    AffinityScore = 0,
                    BecamePersistentAt = timestamp,
                    CreatedAt = timestamp,
                    UpdatedAt = timestamp
                };

                var users = new[]
                {
                        reservation.User1Id,
                        reservation.User2Id
                    };

                Array.Sort(users, StringComparer.Ordinal);

                // Tạo phòng và lịch sử ghép cặp cùng giao dịch.
                transaction.Create(newRoomReference, room);

                transaction.Create(
                historyReference,
                new FirebaseMatchHistory
                {
                    PairId = pairId,
                    User1Id = users[0],
                    User2Id = users[1],
                    RoomId = newRoomReference.Id,
                    CreatedAt = timestamp
                });

                transaction.Update(
                reservationReference,
                new Dictionary<string, object>
                {
                    ["User1Ready"] = true,
                    ["User2Ready"] = true,
                    ["Status"] = "Matched",
                    ["RoomId"] = newRoomReference.Id,
                    ["UpdatedAt"] = timestamp
                });


                // Ownership of both PendingReady sessions was verified above.
                transaction.Delete(session1Reference);
                transaction.Delete(session2Reference);


                if (reservation.MatchMode == "Random")
                {
                    if (queue1Snapshot.Exists)
                    {
                        var queue1 = queue1Snapshot
                            .ConvertTo<FirebaseRandomMatchQueueEntry>();

                        if (queue1.Status == "Reserved" &&
                            queue1.ReservationId == reservationId)
                        {
                            transaction.Delete(queue1Reference);
                        }
                    }

                    if (queue2Snapshot.Exists)
                    {
                        var queue2 = queue2Snapshot
                            .ConvertTo<FirebaseRandomMatchQueueEntry>();

                        if (queue2.Status == "Reserved" &&
                            queue2.ReservationId == reservationId)
                        {
                            transaction.Delete(queue2Reference);
                        }
                    }
                }


                return ("Matched", newRoomReference.Id);
            });
        }


        public async Task<bool> TryExpireReservationAsync(string reservationId)
        {
            if (string.IsNullOrWhiteSpace(reservationId) ||
                reservationId.Contains('/'))
            {
                throw new ArgumentException("Invalid reservation ID.");
            }

            var reservationReference = _firestoreDb
                .Collection("matchReservations")
                .Document(reservationId);

            return await _firestoreDb.RunTransactionAsync(
                async transaction =>
                {
                    var snapshot = await transaction
                        .GetSnapshotAsync(reservationReference);

                    if (!snapshot.Exists)
                    {
                        return false;
                    }

                    var reservation = snapshot
                        .ConvertTo<FirebaseMatchReservation>();

                    var now = DateTime.UtcNow;

                    if (reservation.Status != "PendingReady" ||
                        now < reservation.ReadyExpiresAt.ToDateTime())
                    {
                        return false;
                    }

                    var session1Reference = _firestoreDb
                        .Collection("matchSessions")
                        .Document(reservation.User1Id);

                    var session2Reference = _firestoreDb
                        .Collection("matchSessions")
                        .Document(reservation.User2Id);

                    var session1Snapshot = await transaction
                        .GetSnapshotAsync(session1Reference);

                    var session2Snapshot = await transaction
                        .GetSnapshotAsync(session2Reference);


                    var queue1Reference = _firestoreDb
                        .Collection("randomMatchQueue")
                        .Document(reservation.User1Id);

                    var queue2Reference = _firestoreDb
                        .Collection("randomMatchQueue")
                        .Document(reservation.User2Id);

                    var queue1Snapshot = await transaction
                        .GetSnapshotAsync(queue1Reference);

                    var queue2Snapshot = await transaction
                        .GetSnapshotAsync(queue2Reference);


                    var timestamp = Timestamp.FromDateTime(now);

                    transaction.Update(
                        reservationReference,
                        new Dictionary<string, object>
                        {
                            ["Status"] = "Expired",
                            ["UpdatedAt"] = timestamp
                        });

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


                    if (queue1Snapshot.Exists)
                    {
                        var queue1 = queue1Snapshot
                            .ConvertTo<FirebaseRandomMatchQueueEntry>();

                        if (queue1.Status == "Reserved" &&
                            queue1.ReservationId == reservationId)
                        {
                            if (reservation.User1Ready)
                            {
                                transaction.Update(queue1Reference,
                                    new Dictionary<string, object>
                                    {
                                        ["Status"] = "Waiting",
                                        ["ReservationId"] = null!,
                                        ["UpdatedAt"] = timestamp
                                    });
                            }
                            else
                            {
                                transaction.Delete(queue1Reference);
                            }
                        }
                    }

                    if (queue2Snapshot.Exists)
                    {
                        var queue2 = queue2Snapshot
                            .ConvertTo<FirebaseRandomMatchQueueEntry>();

                        if (queue2.Status == "Reserved" &&
                            queue2.ReservationId == reservationId)
                        {
                            if (reservation.User2Ready)
                            {
                                transaction.Update(queue2Reference,
                                    new Dictionary<string, object>
                                    {
                                        ["Status"] = "Waiting",
                                        ["ReservationId"] = null!,
                                        ["UpdatedAt"] = timestamp
                                    });
                            }
                            else
                            {
                                transaction.Delete(queue2Reference);
                            }
                        }
                    }


                    return true;
                });
        }


    }
}
