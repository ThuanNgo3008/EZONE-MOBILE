using Google.Cloud.Firestore;
using WebChatEIU.Models.Firestore;

namespace WebChatEIU.Services
{
    public class FirebaseReportService
    {
        private readonly FirestoreDb _firestoreDb;

        public FirebaseReportService(FirestoreDb firestoreDb)
        {
            _firestoreDb = firestoreDb;
        }

        public async Task<string> CreateReportAsync(
            string roomId,
            string reporterId,
            string reportType,
            string reason,
            string? description = null,
            string? reportedMessageId = null)
        {
            if (string.IsNullOrWhiteSpace(roomId) ||
                roomId.Contains('/') ||
                string.IsNullOrWhiteSpace(reporterId))
            {
                throw new ArgumentException("Invalid room or reporter.");
            }

            if (reportType != "Message" && reportType != "User")
            {
                throw new ArgumentException("Invalid report type.");
            }

            if (string.IsNullOrWhiteSpace(reason) ||
                reason.Length > 100 ||
                description?.Length > 1000)
            {
                throw new ArgumentException("Invalid report details.");
            }

            var roomReference = _firestoreDb
                .Collection("chatRooms")
                .Document(roomId);

            var roomSnapshot = await roomReference.GetSnapshotAsync();

            if (!roomSnapshot.Exists)
            {
                throw new InvalidOperationException("Room not found.");
            }

            var room = roomSnapshot.ConvertTo<FirebaseChatRoom>();

            if (room.User1Id != reporterId &&
                room.User2Id != reporterId)
            {
                throw new UnauthorizedAccessException("Access denied.");
            }

            if (room.Status != "Temporary" &&
                room.Status != "AwaitingDecision" &&
                room.Status != "Active")
            {
                throw new InvalidOperationException(
                    "Room is unavailable for reporting.");
            }

            var reportedUserId = room.User1Id == reporterId
                ? room.User2Id
                : room.User1Id;

            var messagesReference = roomReference.Collection("messages");

            var evidenceDocuments = new List<DocumentSnapshot>();

            if (reportType == "Message")
            {
                if (string.IsNullOrWhiteSpace(reportedMessageId) ||
                    reportedMessageId.Contains('/'))
                {
                    throw new ArgumentException("Invalid message ID.");
                }

                var messageSnapshot = await messagesReference
                    .Document(reportedMessageId)
                    .GetSnapshotAsync();

                if (!messageSnapshot.Exists)
                {
                    throw new InvalidOperationException(
                        "Reported message not found.");
                }

                var reportedMessage =
                    messageSnapshot.ConvertTo<FirebaseChatMessage>();

                if (reportedMessage.SenderId != reportedUserId)
                {
                    throw new ArgumentException(
                        "Message does not belong to the reported user.");
                }

                var before = await messagesReference
                    .WhereLessThan("CreatedAt", reportedMessage.CreatedAt)
                    .OrderByDescending("CreatedAt")
                    .Limit(4)
                    .GetSnapshotAsync();

                var after = await messagesReference
                    .WhereGreaterThan("CreatedAt", reportedMessage.CreatedAt)
                    .OrderBy("CreatedAt")
                    .Limit(4)
                    .GetSnapshotAsync();

                evidenceDocuments.AddRange(before.Documents);
                evidenceDocuments.Add(messageSnapshot);
                evidenceDocuments.AddRange(after.Documents);
            }
            else
            {
                var recentMessages = await messagesReference
                    .OrderByDescending("CreatedAt")
                    .Limit(10)
                    .GetSnapshotAsync();

                evidenceDocuments.AddRange(recentMessages.Documents);
            }

            var reportReference = _firestoreDb
                .Collection("reports")
                .Document();

            var report = new FirebaseReport
            {
                ReportId = reportReference.Id,
                RoomId = roomId,
                ReporterId = reporterId,
                ReportedUserId = reportedUserId,
                ReportType = reportType,
                Reason = reason.Trim(),
                Description = description?.Trim(),
                ReportedMessageId = reportedMessageId,
                Status = "Pending",
                EvidenceCaptured = evidenceDocuments.Count > 0,
                CreatedAt = Timestamp.FromDateTime(DateTime.UtcNow)
            };


            await _firestoreDb.RunTransactionAsync(async transaction =>
            {
                var latestRoomSnapshot = await transaction
                    .GetSnapshotAsync(roomReference);

                if (!latestRoomSnapshot.Exists)
                {
                    throw new InvalidOperationException("Room not found.");
                }

                var latestRoom = latestRoomSnapshot
                    .ConvertTo<FirebaseChatRoom>();

                if (latestRoom.User1Id != reporterId &&
                    latestRoom.User2Id != reporterId)
                {
                    throw new UnauthorizedAccessException("Access denied.");
                }

                if (latestRoom.Status != "Temporary" &&
                    latestRoom.Status != "AwaitingDecision" &&
                    latestRoom.Status != "Active")
                {
                    throw new InvalidOperationException(
                        "Room is unavailable for reporting.");
                }

                var capturedMessages = new List<FirebaseChatMessage>();

                foreach (var document in evidenceDocuments)
                {
                    var currentSnapshot = await transaction
                        .GetSnapshotAsync(document.Reference);

                    if (!currentSnapshot.Exists)
                    {
                        throw new InvalidOperationException(
                            "Evidence changed. Please retry.");
                    }

                    capturedMessages.Add(
                        currentSnapshot.ConvertTo<FirebaseChatMessage>());
                }


                var pairId = FirebaseMatchHistoryService.GeneratePairId(
                    latestRoom.User1Id,
                    latestRoom.User2Id
                );

                var historyReference = _firestoreDb
                    .Collection("matchHistory")
                    .Document(pairId);

                var historySnapshot = await transaction
                    .GetSnapshotAsync(historyReference);


                var session1Reference = _firestoreDb
                    .Collection("matchSessions")
                    .Document(latestRoom.User1Id);

                var session2Reference = _firestoreDb
                    .Collection("matchSessions")
                    .Document(latestRoom.User2Id);

                var session1Snapshot = await transaction
                    .GetSnapshotAsync(session1Reference);

                var session2Snapshot = await transaction
                    .GetSnapshotAsync(session2Reference);


                transaction.Create(reportReference, report);

                if (!historySnapshot.Exists)
                {
                    var users = new[]
                    {
                    latestRoom.User1Id,
                    latestRoom.User2Id
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
                            CreatedAt = Timestamp.FromDateTime(DateTime.UtcNow)
                        }
                    );
                }



                for (int i = 0; i < capturedMessages.Count; i++)
                {
                    var message = capturedMessages[i];
                    var messageId = evidenceDocuments[i].Id;

                    var evidenceReference = reportReference
                        .Collection("evidence")
                        .Document(messageId);

                    transaction.Create(
                        evidenceReference,
                        new Dictionary<string, object>
                        {
                            ["MessageId"] = messageId,
                            ["SenderId"] = message.SenderId,
                            ["Content"] = message.Content,
                            ["CreatedAt"] = message.CreatedAt
                        });
                }


                var now = Timestamp.FromDateTime(DateTime.UtcNow);


                bool cleanupPending =
                    latestRoom.TemporaryStartedAt.HasValue &&
                    !latestRoom.BecamePersistentAt.HasValue;

                transaction.Update(
                    roomReference,
                    new Dictionary<string, object>
                    {
                        ["Status"] = "Closed",
                        ["ClosedAt"] = now,
                        ["UpdatedAt"] = now,
                        ["CleanupPending"] = cleanupPending
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

            });


            return reportReference.Id;
        }
    }
}
