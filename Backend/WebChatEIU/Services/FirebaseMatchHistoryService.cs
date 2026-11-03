using System.Security.Cryptography;
using System.Text;
using Google.Cloud.Firestore;
using WebChatEIU.Models.Firestore;

namespace WebChatEIU.Services
{
    public class FirebaseMatchHistoryService
    {
        private readonly CollectionReference _matchHistory;
        private readonly FirestoreDb _firestoreDb;

        public FirebaseMatchHistoryService(FirestoreDb firestoreDb)
        {
            _firestoreDb = firestoreDb;
            _matchHistory = firestoreDb.Collection("matchHistory");
        }

        public async Task<bool> HasMatchedAsync(
            string user1Id,
            string user2Id)
        {
            var pairId = GeneratePairId(user1Id, user2Id);

            var snapshot = await _matchHistory
                .Document(pairId)
                .GetSnapshotAsync();

            return snapshot.Exists;
        }

        public async Task<bool> RecordMatchAsync(
            string user1Id,
            string user2Id,
            string roomId)
        {
            if (string.IsNullOrWhiteSpace(roomId) ||
                roomId.Contains('/'))
            {
                throw new ArgumentException("Invalid room ID.");
            }

            var pairId = GeneratePairId(user1Id, user2Id);
            var document = _matchHistory.Document(pairId);

            var users = new[] { user1Id, user2Id };
            Array.Sort(users, StringComparer.Ordinal);

            return await _firestoreDb.RunTransactionAsync(
                async transaction =>
                {
                    var snapshot = await transaction
                        .GetSnapshotAsync(document);

                    if (snapshot.Exists)
                    {
                        return false;
                    }

                    var history = new FirebaseMatchHistory
                    {
                        PairId = pairId,
                        User1Id = users[0],
                        User2Id = users[1],
                        RoomId = roomId,
                        CreatedAt = Timestamp.FromDateTime(
                            DateTime.UtcNow)
                    };

                    transaction.Create(document, history);

                    return true;
                });
        }

        internal static string GeneratePairId(
            string user1Id,
            string user2Id)
        {
            if (string.IsNullOrWhiteSpace(user1Id) ||
                string.IsNullOrWhiteSpace(user2Id) ||
                user1Id == user2Id)
            {
                throw new ArgumentException("Invalid user pair.");
            }

            var users = new[] { user1Id, user2Id };
            Array.Sort(users, StringComparer.Ordinal);

            var pair = $"{users[0].Length}:{users[0]}" +
                       $"{users[1].Length}:{users[1]}";

            var hash = SHA256.HashData(
                Encoding.UTF8.GetBytes(pair));

            return Convert.ToHexString(hash).ToLowerInvariant();
        }
    }
}
