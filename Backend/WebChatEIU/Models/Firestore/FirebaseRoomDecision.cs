using Google.Cloud.Firestore;

namespace WebChatEIU.Models.Firestore
{
    [FirestoreData]
    public class FirebaseRoomDecision
    {
        [FirestoreDocumentId]
        public string RoomId { get; set; } = string.Empty;

        [FirestoreProperty]
        public bool? User1Continue { get; set; }

        [FirestoreProperty]
        public bool? User2Continue { get; set; }
    }
}