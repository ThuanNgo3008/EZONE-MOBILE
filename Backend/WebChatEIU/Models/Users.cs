using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace WebChatEIU.Models
{
    [Table("Users")]
    public class Users
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int UserId { get; set; }

        [Required(ErrorMessage = "Email is required")]
        [EmailAddress(ErrorMessage = "Invalid email format")]
        [StringLength(100, ErrorMessage = "Email cannot exceed 100 characters")]
        [RegularExpression(@"^[a-zA-Z0-9._%+-]+@eiu\.edu\.vn$", ErrorMessage = "Email must be a valid EIU student email (@eiu.edu.vn)")]
        public string Email { get; set; }

        [Required(ErrorMessage = "Fullname is required")]
        [StringLength(200, ErrorMessage = "Fullname cannot exceed 200 characters")]
        public string Fullname { get; set; }


        [Required(ErrorMessage = "Password is required")]
        public string Password { get; set; }

        public string? Gender { get; set; }

        public string MajorCode { get; set; }

        [StringLength(255)]
        public string? AvatarUrl { get; set; }

        [StringLength(255)]
        public string? SocialLink { get; set; }

        public bool IsActive { get; set; } = false;

        public string? ActiveCode { get; set; }
        public bool IsSearching { get; set; } = false;

        public bool IsBanned { get; set; } = false;

        // "None" | "Admin" | "SystemAuto"
        public string BanSource { get; set; } = "None";

        // Soft delete: tài khoản "xóa" chỉ bị ẩn/khoá đăng nhập, KHÔNG xoá dữ liệu
        // liên quan (ChatRooms/Messages/ChatReports) để không ảnh hưởng tới lịch sử
        // của người dùng khác đã từng chat/report chung với tài khoản này.
        public bool IsDeleted { get; set; } = false;
        public DateTime? DeletedAt { get; set; }

        public DateTime CreatedDate { get; set; } = DateTime.UtcNow;

        public ICollection<UserRole> UserRoles { get; set; } = new List<UserRole>();
    }
}