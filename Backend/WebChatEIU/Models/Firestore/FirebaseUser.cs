
using Google.Cloud.Firestore;
using System;
using System.Collections.Generic;

namespace WebChatEIU.Models.Firestore
{
    [FirestoreData]
    public class FirebaseUser
    {
        // Firebase Authentication UID
        [FirestoreDocumentId]
        public string UserId { get; set; } = string.Empty;

        // Thông tin tài khoản
        [FirestoreProperty]
        public string Email { get; set; } = string.Empty;

        [FirestoreProperty]
        public string Fullname { get; set; } = string.Empty;

        [FirestoreProperty]
        public string? Gender { get; set; }

        [FirestoreProperty]
        public string MajorCode { get; set; } = string.Empty;

        [FirestoreProperty]
        public string? AvatarUrl { get; set; }

        [FirestoreProperty]
        public string? SocialLink { get; set; }

        // AI Smart Match
        [FirestoreProperty]
        public string Cohort { get; set; } = string.Empty;

        [FirestoreProperty]
        public int? BirthYear { get; set; }

        [FirestoreProperty]
        public List<string> Interests { get; set; } = new();

        // Trạng thái ghép đôi
        [FirestoreProperty]
        public bool IsSearching { get; set; } = false;

        // Trạng thái tài khoản
        [FirestoreProperty]
        public bool IsActive { get; set; } = false;

        [FirestoreProperty]
        public bool IsBanned { get; set; } = false;

        [FirestoreProperty]
        public string BanSource { get; set; } = "None";

        // Soft delete
        [FirestoreProperty]
        public bool IsDeleted { get; set; } = false;

        [FirestoreProperty]
        public Timestamp? DeletedAt { get; set; }

        [FirestoreProperty]
        public Timestamp CreatedDate { get; set; } =
            Timestamp.FromDateTime(DateTime.UtcNow);
    }
}
