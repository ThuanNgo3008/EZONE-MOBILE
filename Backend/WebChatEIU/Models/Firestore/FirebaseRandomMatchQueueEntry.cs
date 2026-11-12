using Google.Cloud.Firestore;

namespace WebChatEIU.Models.Firestore
{
    [FirestoreData]
    public class FirebaseRandomMatchQueueEntry
    {
        [FirestoreDocumentId]
        public string UserId { get; set; } = string.Empty;

        [FirestoreProperty]
        public string Status { get; set; } = "Waiting";

        [FirestoreProperty]
        public string? ReservationId { get; set; }

        [FirestoreProperty]
        public Timestamp JoinedAt { get; set; }

        [FirestoreProperty]
        public Timestamp UpdatedAt { get; set; }

        [FirestoreProperty]
        public Timestamp LastSeenAt { get; set; }
    }
}
