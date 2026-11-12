using Google.Cloud.Firestore;

namespace WebChatEIU.Models.Firestore
{
    [FirestoreData]
    public class FirebaseReport
    {
        [FirestoreDocumentId]
        public string ReportId { get; set; } = string.Empty;

        [FirestoreProperty]
        public string RoomId { get; set; } = string.Empty;

        [FirestoreProperty]
        public string ReporterId { get; set; } = string.Empty;

        [FirestoreProperty]
        public string ReportedUserId { get; set; } = string.Empty;

        [FirestoreProperty]
        public string ReportType { get; set; } = string.Empty;

        [FirestoreProperty]
        public string Reason { get; set; } = string.Empty;

        [FirestoreProperty]
        public string? Description { get; set; }

        [FirestoreProperty]
        public string? ReportedMessageId { get; set; }

        [FirestoreProperty]
        public string Status { get; set; } = "Pending";

        [FirestoreProperty]
        public bool EvidenceCaptured { get; set; } = false;

        [FirestoreProperty]
        public Timestamp CreatedAt { get; set; } =
            Timestamp.FromDateTime(DateTime.UtcNow);
    }
}
