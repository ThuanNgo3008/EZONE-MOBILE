using Google.Cloud.Firestore;

namespace WebChatEIU.Models.Firestore
{
    [FirestoreData]
    public class FirebaseMatchHistory
    {
        [FirestoreDocumentId]
        public string PairId { get; set; } = string.Empty;

        [FirestoreProperty]
        public string User1Id { get; set; } = string.Empty;

        [FirestoreProperty]
        public string User2Id { get; set; } = string.Empty;

        [FirestoreProperty]
        public string RoomId { get; set; } = string.Empty;

        [FirestoreProperty]
        public Timestamp CreatedAt { get; set; } =
            Timestamp.FromDateTime(DateTime.UtcNow);
    }
}
