using Google.Cloud.Firestore;

namespace WebChatEIU.Models.Firestore
{
    [FirestoreData]
    public class FirebaseMatchReservation
    {
        [FirestoreDocumentId]
        public string ReservationId { get; set; } = string.Empty;

        [FirestoreProperty]
        public string User1Id { get; set; } = string.Empty;

        [FirestoreProperty]
        public string User2Id { get; set; } = string.Empty;

        [FirestoreProperty]
        public string MatchMode { get; set; } = "Random";

        [FirestoreProperty]
        public string? Purpose { get; set; }

        [FirestoreProperty]
        public bool User1Ready { get; set; } = false;

        [FirestoreProperty]
        public bool User2Ready { get; set; } = false;

        [FirestoreProperty]
        public string Status { get; set; } = "PendingReady";

        [FirestoreProperty]
        public Timestamp ReadyExpiresAt { get; set; }

        [FirestoreProperty]
        public string? RoomId { get; set; }

        [FirestoreProperty]
        public Timestamp CreatedAt { get; set; }

        [FirestoreProperty]
        public Timestamp UpdatedAt { get; set; }
    }
}
