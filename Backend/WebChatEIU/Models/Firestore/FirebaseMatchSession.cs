using Google.Cloud.Firestore;

namespace WebChatEIU.Models.Firestore
{
    [FirestoreData]
    public class FirebaseMatchSession
    {
        [FirestoreDocumentId]
        public string UserId { get; set; } = string.Empty;

        [FirestoreProperty]
        public string ReservationId { get; set; } = string.Empty;

        [FirestoreProperty]
        public string Status { get; set; } = "PendingReady";

        [FirestoreProperty]
        public string? RoomId { get; set; }

        [FirestoreProperty]
        public Timestamp ExpiresAt { get; set; }

        [FirestoreProperty]
        public Timestamp UpdatedAt { get; set; }
    }
}
