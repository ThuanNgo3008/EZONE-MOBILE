using Google.Cloud.Firestore;

namespace WebChatEIU.Models.Firestore
{
    [FirestoreData]
    public class FirebaseMatchSession
    {
        // Reservation lock only. Successful mutual Ready deletes both owned sessions.
        // RoomId remains readable for historical sessions; Active chats do not hold locks.
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
