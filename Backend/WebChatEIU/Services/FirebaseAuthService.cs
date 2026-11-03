
using FirebaseAdmin.Auth;
using Google.Cloud.Firestore;
using WebChatEIU.DTOs;
using WebChatEIU.Models.Firestore;

namespace WebChatEIU.Services
{
    public class FirebaseAuthService
    {
        private readonly FirebaseUserService _userService;
        private readonly IEmailService _emailService;

        public FirebaseAuthService(FirebaseUserService userService, IEmailService emailService)
        {
            _userService = userService;
            _emailService = emailService;
        }


        public async Task<string> RegisterAsync(RegisterDto dto)
        {
            var args = new UserRecordArgs
            {
                Email = dto.Email,
                Password = dto.Password,
                DisplayName = dto.Fullname,
                EmailVerified = false,
                Disabled = false
            };

            // 1. Tạo tài khoản Firebase Authentication
            var authUser = await FirebaseAuth.DefaultInstance
                .CreateUserAsync(args);

            // 2. Tạo hồ sơ Firestore
            try
            {
                var user = new FirebaseUser
                {
                    UserId = authUser.Uid,
                    Email = dto.Email,
                    Fullname = dto.Fullname,
                    MajorCode = dto.MajorCode,
                    Cohort = dto.Cohort,
                    BirthYear = dto.BirthYear,
                    Interests = dto.Interests,
                    IsActive = false,
                    CreatedDate = Timestamp.FromDateTime(
                        DateTime.UtcNow)
                };

                await _userService.CreateAsync(user);
            }
            catch
            {
                // Chỉ rollback Auth khi tạo Firestore thất bại
                await FirebaseAuth.DefaultInstance
                    .DeleteUserAsync(authUser.Uid);

                throw;
            }

            // 3. Tạo link và gửi email xác thực
            // Nếu gửi email lỗi, giữ nguyên Auth và Firestore
            // để người dùng có thể yêu cầu gửi lại.
            try
            {
                var verificationLink =
                    await GenerateVerificationLinkAsync(dto.Email);

                await _emailService.SendVerificationLinkAsync(
                    dto.Email,
                    dto.Fullname,
                    verificationLink
                );
            }
            catch (Exception ex)
            {
                throw new VerificationEmailDeliveryException(
                    authUser.Uid,
                    ex
                );
            }

            return authUser.Uid;
        }



        public async Task<string> GenerateVerificationLinkAsync(string email)
        {
            return await FirebaseAuth.DefaultInstance
                .GenerateEmailVerificationLinkAsync(email);
        }

        public async Task ResendVerificationEmailAsync(string email)
        {
            var auth = FirebaseAuth.DefaultInstance;

            var user = await auth.GetUserByEmailAsync(email);

            if (user.EmailVerified)
                return;

            var verificationLink =
                await auth.GenerateEmailVerificationLinkAsync(email);

            await _emailService.SendVerificationLinkAsync(
                email,
                user.DisplayName ?? "EZone User",
                verificationLink
            );
        }



        public async Task<bool> SyncEmailVerificationAsync(string uid)
        {
            // Kiểm tra tài khoản trên Firebase Authentication
            var authUser = await FirebaseAuth.DefaultInstance
                .GetUserAsync(uid);

            if (!authUser.EmailVerified || authUser.Disabled)
                return false;

            // Kiểm tra hồ sơ Firestore
            var user = await _userService.GetByIdAsync(uid);

            if (user == null || user.IsBanned || user.IsDeleted)
                return false;

            // Chỉ cập nhật trạng thái kích hoạt
            if (!user.IsActive)
            {
                await _userService.ActivateUserAsync(uid);
            }

            return true;
        }



    }
}
