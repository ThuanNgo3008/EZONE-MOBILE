using Google.Cloud.Firestore;

namespace WebChatEIU.Models.Firestore
{
    [FirestoreData]
    public class FirebaseChatMessage
    {
        [FirestoreDocumentId]
        public string MessageId { get; set; } = string.Empty;

        [FirestoreProperty]
        public string SenderId { get; set; } = string.Empty;

        [FirestoreProperty]
        public string Content { get; set; } = string.Empty;

        [FirestoreProperty]
        public Timestamp CreatedAt { get; set; } =
            Timestamp.FromDateTime(DateTime.UtcNow);
    }
}
