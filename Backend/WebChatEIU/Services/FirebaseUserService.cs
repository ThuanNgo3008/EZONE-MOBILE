using Google.Cloud.Firestore;
using WebChatEIU.Models.Firestore;

namespace WebChatEIU.Services
{
    public class FirebaseUserService
    {
        private readonly CollectionReference _users;

        public FirebaseUserService(FirestoreDb firestore)
        {
            _users = firestore.Collection("users");
        }


        // Lấy thông tin người dùng theo Firebase UID
        public async Task<FirebaseUser?> GetByIdAsync(string uid)
        {
            var snapshot = await _users.Document(uid).GetSnapshotAsync();

            if (!snapshot.Exists)
                return null;

            return snapshot.ConvertTo<FirebaseUser>();
        }

        // Tạo hồ sơ người dùng mới
        public async Task CreateAsync(FirebaseUser user)
        {
            if (string.IsNullOrWhiteSpace(user.UserId))
                throw new ArgumentException("UserId is required.");

            var document = _users.Document(user.UserId);

            await document.CreateAsync(user);
        }


        public async Task ActivateUserAsync(string uid)
        {
            await _users.Document(uid).UpdateAsync(
                "IsActive", true
            );
        }

    }
}
