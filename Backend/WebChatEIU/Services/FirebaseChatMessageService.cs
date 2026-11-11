
using Google.Cloud.Firestore;
using WebChatEIU.Models.Firestore;

namespace WebChatEIU.Services
{
    public class FirebaseChatMessageService
    {
        private readonly FirestoreDb _firestoreDb;

        public FirebaseChatMessageService(FirestoreDb firestoreDb)
        {
            _firestoreDb = firestoreDb;
        }

        public async Task<FirebaseChatMessage> SendMessageAsync(
            string roomId,
            string senderId,
            string content,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(roomId) ||
                roomId.Contains('/') ||
                string.IsNullOrWhiteSpace(senderId))
            {
                throw new ArgumentException("Invalid room or sender.");
            }

            if (string.IsNullOrWhiteSpace(content) ||
                content.Length > 2000)
            {
                throw new ArgumentException("Invalid message content.");
            }

            var roomReference = _firestoreDb
                .Collection("chatRooms")
                .Document(roomId);

            var messageReference = roomReference
                .Collection("messages")
                .Document();

            var message = new FirebaseChatMessage
            {
                MessageId = messageReference.Id,
                SenderId = senderId,
                Content = content.Trim(),
                CreatedAt = Timestamp.FromDateTime(DateTime.UtcNow)
            };

            await _firestoreDb.RunTransactionAsync(async transaction =>
            {
                var snapshot = await transaction
                    .GetSnapshotAsync(roomReference);

                if (!snapshot.Exists)
                {
                    throw new InvalidOperationException(
                        "Room not found.");
                }

                var room = snapshot.ConvertTo<FirebaseChatRoom>();

                if (room.User1Id != senderId &&
                    room.User2Id != senderId)
                {
                    throw new UnauthorizedAccessException(
                        "User is not a member of this room.");
                }


                var now = DateTime.UtcNow;

                bool isActiveRoom = room.Status == "Active";

                bool isValidTemporaryRoom =
                    room.Status == "Temporary" &&
                    room.TemporaryStartedAt.HasValue &&
                    room.TemporaryEndsAt.HasValue &&
                    now >= room.TemporaryStartedAt.Value.ToDateTime() &&
                    now < room.TemporaryEndsAt.Value.ToDateTime();

                if (!isActiveRoom && !isValidTemporaryRoom)
                {
                    throw new InvalidOperationException(
                        "Room is not accepting messages.");
                }


                transaction.Create(messageReference, message);
                if (!snapshot.ContainsField("UpdatedAt") || message.CreatedAt.CompareTo(room.UpdatedAt) > 0)
                    transaction.Update(roomReference, "UpdatedAt", message.CreatedAt);
            }, cancellationToken: cancellationToken);

            return message;
        }


        public async Task<(
            List<FirebaseChatMessage> Messages,
            string? NextCursor)> GetMessagesAsync(
            string roomId,
            string userId,
            int pageSize = 20,
            string? cursor = null)
        {
            if (string.IsNullOrWhiteSpace(roomId) ||
                roomId.Contains('/') ||
                string.IsNullOrWhiteSpace(userId))
            {
                throw new ArgumentException("Invalid room or user.");
            }

            if (pageSize < 1 || pageSize > 50)
            {
                throw new ArgumentException(
                    "Page size must be between 1 and 50.");
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

            if (room.User1Id != userId && room.User2Id != userId)
            {
                throw new UnauthorizedAccessException(
                    "User is not a member of this room.");
            }

            var messagesReference = roomReference.Collection("messages");

            Query query = messagesReference
                .OrderByDescending("CreatedAt")
                .Limit(pageSize + 1);

            if (!string.IsNullOrWhiteSpace(cursor))
            {
                if (cursor.Contains('/'))
                {
                    throw new ArgumentException("Invalid cursor.");
                }

                var cursorSnapshot = await messagesReference
                    .Document(cursor)
                    .GetSnapshotAsync();

                if (!cursorSnapshot.Exists)
                {
                    throw new ArgumentException("Invalid cursor.");
                }

                query = query.StartAfter(cursorSnapshot);
            }

            var snapshot = await query.GetSnapshotAsync();

            var documents = snapshot.Documents
                .Take(pageSize)
                .ToList();

            bool hasMore = snapshot.Documents.Count > pageSize;

            string? nextCursor = hasMore
                ? documents[^1].Id
                : null;

            var messages = documents
                .Select(document =>
                    document.ConvertTo<FirebaseChatMessage>())
                .ToList();

            return (messages, nextCursor);
        }

    }
}
