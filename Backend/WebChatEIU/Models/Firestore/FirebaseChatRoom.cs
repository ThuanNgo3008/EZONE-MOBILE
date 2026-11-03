using Google.Cloud.Firestore;

namespace WebChatEIU.Models.Firestore
{
    [FirestoreData]
    public class FirebaseChatRoom
    {
        [FirestoreDocumentId]
        public string RoomId { get; set; } = string.Empty;

        [FirestoreProperty]
        public string User1Id { get; set; } = string.Empty;

        [FirestoreProperty]
        public string User2Id { get; set; } = string.Empty;

        [FirestoreProperty]
        public string Status { get; set; } = "Active";

        [FirestoreProperty]
        public string User1Nickname { get; set; } = string.Empty;

        [FirestoreProperty]
        public string User2Nickname { get; set; } = string.Empty;

        [FirestoreProperty]
        public int AffinityScore { get; set; }

        [FirestoreProperty]
        public string MatchMode { get; set; } = "Random";

        [FirestoreProperty]
        public string? Purpose { get; set; }

        // Temporary Chat
        [FirestoreProperty]
        public Timestamp? TemporaryStartedAt { get; set; }

        [FirestoreProperty]
        public Timestamp? TemporaryEndsAt { get; set; }

        // Continuation Decision
        [FirestoreProperty]
        public Timestamp? DecisionEndsAt { get; set; }

        // Mutual Identity Reveal
        [FirestoreProperty]
        public bool IsRevealed { get; set; } = false;

        [FirestoreProperty]
        public Timestamp? BecamePersistentAt { get; set; }

        [FirestoreProperty]
        public Timestamp? RevealedAt { get; set; }

        // Room Closure
        [FirestoreProperty]
        public Timestamp? ClosedAt { get; set; }

        [FirestoreProperty]
        public bool CleanupPending { get; set; } = false;

        [FirestoreProperty]
        public Timestamp CreatedAt { get; set; } =
            Timestamp.FromDateTime(DateTime.UtcNow);

        [FirestoreProperty]
        public Timestamp UpdatedAt { get; set; } =
            Timestamp.FromDateTime(DateTime.UtcNow);
    }
}
