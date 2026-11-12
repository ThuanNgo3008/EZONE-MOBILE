using Google.Cloud.Firestore;
using WebChatEIU.Models.Firestore;

namespace WebChatEIU.Services
{
    public class FirebaseTemporaryMessageCleanupService
    {
        private const int BatchSize = 200;

        private readonly FirestoreDb _firestoreDb;

        public FirebaseTemporaryMessageCleanupService(
            FirestoreDb firestoreDb)
        {
            _firestoreDb = firestoreDb;
        }

        public async Task<int> DeleteTemporaryMessagesAsync(
            string roomId)
        {
            if (string.IsNullOrWhiteSpace(roomId) ||
                roomId.Contains('/'))
            {
                throw new ArgumentException("Invalid room ID.");
            }

            var roomReference = _firestoreDb
                .Collection("chatRooms")
                .Document(roomId);

            var roomSnapshot = await roomReference
                .GetSnapshotAsync();

            if (!roomSnapshot.Exists)
            {
                throw new InvalidOperationException(
                    "Room not found.");
            }

            var room = roomSnapshot
                .ConvertTo<FirebaseChatRoom>();

            bool canDelete =
                room.Status == "Closed" &&
                room.CleanupPending &&
                room.TemporaryStartedAt.HasValue &&
                !room.BecamePersistentAt.HasValue;

            if (!canDelete)
            {
                throw new InvalidOperationException(
                    "Room is not eligible for message cleanup.");
            }

            var messages = roomReference.Collection("messages");


            var reportsSnapshot = await _firestoreDb
                .Collection("reports")
                .WhereEqualTo("RoomId", roomId)
                .GetSnapshotAsync();

            bool hasUnconfirmedEvidence = reportsSnapshot.Documents
                .Any(document =>
                    !document.ConvertTo<FirebaseReport>().EvidenceCaptured);

            if (hasUnconfirmedEvidence)
            {
                var remainingMessages = await messages
                    .Limit(1)
                    .GetSnapshotAsync();

                if (remainingMessages.Count > 0)
                {
                    throw new InvalidOperationException(
                        "Cannot delete messages before report evidence is confirmed.");
                }
            }


            int deletedCount = 0;

            while (true)
            {
                var snapshot = await messages
                    .Limit(BatchSize)
                    .GetSnapshotAsync();

                if (snapshot.Count == 0)
                {
                    break;
                }

                var batch = _firestoreDb.StartBatch();

                foreach (var document in snapshot.Documents)
                {
                    batch.Delete(document.Reference);
                }

                await batch.CommitAsync();

                deletedCount += snapshot.Count;
            }

            return deletedCount;
        }
    }
}
