using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using WebChatEIU.Data;
using WebChatEIU.DTOs;
using WebChatEIU.Models;
using WebChatEIU.Services;

namespace WebChatEIU.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class UsersController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IConfiguration _configuration;
        private readonly IEmailService _emailService;
        private readonly ILogger<UsersController> _logger;

        public UsersController(
            ApplicationDbContext context,
            IConfiguration configuration,
            IEmailService emailService,
            ILogger<UsersController> logger)
        {
            _context = context;
            _configuration = configuration;
            _emailService = emailService;
            _logger = logger;
        }


        // ====================== Register/Log in ============================================

        [HttpPost("register")]
        public async Task<IActionResult> Register([FromBody] RegisterDto dto)
        {
            // Kiểm tra email này đã tồn tại chưa, nếu đã tồn tại thì báo email đã tồn tại
            var emailExists = await _context.Users.AnyAsync(u => u.Email == dto.Email);
            if (emailExists)
            {
                return BadRequest("This email address has already been used");
            }
            // Sau khi kiểm tra email chưa tồn tại, tiến hành quá trình băm mật khẩu bằng BCrypt
            string passwordHash = BCrypt.Net.BCrypt.HashPassword(dto.Password);

            // Mã xác thực sẽ random
            Random rand = new Random();
            string activationCode = rand.Next(100000, 999999).ToString();
            // tạo người dùng mới 
            var newUser = new Users
            {
                Fullname = dto.Fullname,
                Email = dto.Email,
                Password = passwordHash,
                MajorCode = dto.MajorCode,
                ActiveCode = activationCode,
                IsActive = false,
                CreatedDate = DateTime.UtcNow
            };

            _context.Users.Add(newUser);
            await _context.SaveChangesAsync();

            // Không trả activationCode trong response nữa — trước đây ai gọi API
            // cũng tự lấy được code và tự kích hoạt, làm mất hết ý nghĩa xác thực
            // qua email. Gửi thất bại cũng không rollback đăng ký, chỉ log lỗi để
            // debug — tài khoản vẫn tạo xong, user có thể xin cấp lại code sau.
            try
            {
                Console.WriteLine($"[REGISTER] About to send activation email to {newUser.Email}...");
                await _emailService.SendActivationEmailAsync(newUser.Email, newUser.Fullname, activationCode);
                Console.WriteLine($"[REGISTER] ✅ Email sent successfully to {newUser.Email}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[REGISTER] ❌ EMAIL FAILED: {ex.GetType().Name}: {ex.Message}");
                _logger.LogError(ex, "Failed to send activation email to {Email}", newUser.Email);
            }

            return Ok(new
            {
                message = "Registration successful! Please check your email for the activation code."
            });
        }

        [HttpGet("activate")]
        public async Task<IActionResult> ActivateByGet([FromQuery] string code)
        {
            if (string.IsNullOrEmpty(code))
            {
                return BadRequest("Activation code cannot be empty");
            }
            var user = await _context.Users.FirstOrDefaultAsync(u => u.ActiveCode == code);

            if (user == null)
            {
                return BadRequest("The activation code is incorrect or does not exist");
            }

            if (user.IsActive)
            {
                return BadRequest("This account was already activated");
            }

            // Cập nhật trạng thái kích hoạt tài khoản
            user.IsActive = true;
            user.ActiveCode = null; // Xóa mã kích hoạt sau khi dùng xong để bảo mật

            _context.Users.Update(user);
            await _context.SaveChangesAsync();

            return Ok(new { message = "You can now log in." });
        }

        [HttpPost("activate")]
        public async Task<IActionResult> ActivateByPost([FromBody] ActivateDto dto)
        {
            // Tìm người dùng có mã kích hoạt trùng với mã trong body JSON gửi lên
            var user = await _context.Users.FirstOrDefaultAsync(u => u.ActiveCode == dto.Code);

            if (user == null)
            {
                return BadRequest("The activation code is incorrect or does not exist");
            }

            if (user.IsActive)
            {
                return BadRequest("This account was already activated");
            }

            // Cập nhật trạng thái kích hoạt tài khoản
            user.IsActive = true;
            user.ActiveCode = null;

            _context.Users.Update(user);
            await _context.SaveChangesAsync();

            return Ok(new { message = "You can now log in." });
        }

        //[HttpPost("login")]
        //public async Task<IActionResult> LoginByPost([FromBody] LoginDto dto)
        //{
        //    var user = await _context.Users
        //        .Include(u => u.UserRoles)
        //        .ThenInclude(ur => ur.Role)
        //        .FirstOrDefaultAsync(u => u.Email == dto.Email);

        //    if (user == null)
        //    {
        //        return BadRequest("Incorrect email or password!");
        //    }

        //    if (user.IsActive == false)
        //    {
        //        return BadRequest("Your account is not yet activated!");
        //    }

        //    if (user.IsBanned == true)
        //    {
        //        return BadRequest("Your account has been banned for violating community standards!");
        //    }


        //    bool isPasswordValid = BCrypt.Net.BCrypt.Verify(dto.Password, user.Password);
        //    if (!isPasswordValid)
        //    {
        //        return BadRequest("Incorrect email or password!");
        //    }

        //    var claims = new List<Claim>
        //    {
        //        new Claim(JwtRegisteredClaimNames.Sub, user.Email),
        //        new Claim("userId", user.UserId.ToString())
        //    };

        //    claims.AddRange(
        //        user.UserRoles.Select(ur =>
        //            new Claim(ClaimTypes.Role, ur.Role.Name))
        //    );

        //    var key = new SymmetricSecurityKey(
        //        Encoding.UTF8.GetBytes(_configuration["Jwt:Key"])
        //    );

        //    var creds = new SigningCredentials(
        //        key,
        //        SecurityAlgorithms.HmacSha256
        //    );

        //    var token = new JwtSecurityToken(
        //        issuer: _configuration["Jwt:Issuer"],
        //        audience: _configuration["Jwt:Audience"],
        //        claims: claims,
        //        expires: DateTime.Now.AddHours(3),
        //        signingCredentials: creds
        //    );

        //    var jwt = new JwtSecurityTokenHandler().WriteToken(token);

        //    return Ok(new
        //    {
        //        message = "Login successful!",
        //        token = jwt,
        //        userId = user.UserId,
        //        fullname = user.Fullname,
        //        avatarUrl = user.AvatarUrl
        //    });
        //}


        // ============================ Profile (self) ===============================================
        [Authorize]
        [HttpGet("me")]
        public async Task<IActionResult> GetMe()
        {
            int userId = int.Parse(User.FindFirst("userId").Value);

            var user = await _context.Users.FirstOrDefaultAsync(u => u.UserId == userId);

            if (user == null)
            {
                return NotFound("Not Found");
            }

            return Ok(new
            {
                userId = user.UserId,
                email = user.Email,
                fullname = user.Fullname,
                gender = user.Gender,
                majorCode = user.MajorCode,
                avatarUrl = user.AvatarUrl,
                socialLink = user.SocialLink,
                isActive = user.IsActive,
                isBanned = user.IsBanned,
                createdDate = user.CreatedDate
            });
        }


        // ============================ Update =======================================================
        [Authorize]
        [HttpPut("{id}")]
        [Consumes("multipart/form-data")]
        public async Task<ActionResult> UpdateUsers(int id, [FromForm] UpdateUserDto dto)
        {
            int currentUserId = int.Parse(User.FindFirst("userId").Value);

            if (id != currentUserId)
            {
                return Forbid();
            }

            var existing = await _context.Users
                .FirstOrDefaultAsync(u => u.UserId == id && u.IsActive);

            if (existing == null)
            {
                return NotFound("No user found to update");
            }

            existing.Fullname = dto.Fullname;
            existing.Gender = dto.Gender;
            existing.MajorCode = dto.MajorCode;
            existing.SocialLink = dto.SocialLink;

            if (dto.AvatarFile != null && dto.AvatarFile.Length > 0)
            {
                var uploadFolder = Path.Combine(
                    Directory.GetCurrentDirectory(),
                    "wwwroot",
                    "avatar_images"
                );

                if (!Directory.Exists(uploadFolder))
                {
                    Directory.CreateDirectory(uploadFolder);
                }

                var avatarFileName =
                    Guid.NewGuid().ToString()
                    + Path.GetExtension(dto.AvatarFile.FileName);

                var fullPath = Path.Combine(uploadFolder, avatarFileName);

                using (var stream = new FileStream(fullPath, FileMode.Create))
                {
                    await dto.AvatarFile.CopyToAsync(stream);
                }

                existing.AvatarUrl = "/avatar_images/" + avatarFileName;
            }

            await _context.SaveChangesAsync();

            return NoContent();
        }


        [Authorize(Roles = "Admin")]
        [HttpGet("{id}")]
        public async Task<ActionResult<Users>> GetUser(int id)
        {
            var user = await _context.Users.FirstOrDefaultAsync(u => u.UserId == id && u.IsActive);
            if (user == null) return NotFound("Not Found");

            return Ok(user);
        }


        // ============================ Delete account (soft delete) =================================
        [Authorize]
        [HttpDelete("{id}")]
        public async Task<IActionResult> DeleteAccount(int id, [FromBody] DeleteAccountDto dto)
        {
            int currentUserId = int.Parse(User.FindFirst("userId").Value);

            if (id != currentUserId)
            {
                return Forbid();
            }

            var user = await _context.Users.FirstOrDefaultAsync(u => u.UserId == id);

            if (user == null)
            {
                return NotFound("User not found");
            }

            if (string.IsNullOrEmpty(dto?.Password) || !BCrypt.Net.BCrypt.Verify(dto.Password, user.Password))
            {
                return BadRequest("Incorrect password");
            }

            // Soft delete: chỉ đánh dấu đã xóa + khóa đăng nhập (nhờ HasQueryFilter
            // trong ApplicationDbContext), KHÔNG đụng tới ChatRooms/Messages/ChatReports
            // để giữ nguyên lịch sử chat/report cho phía đối phương.
            user.IsDeleted = true;
            user.DeletedAt = DateTime.UtcNow;

            // Đóng các room đang Waiting/Active của user này để đối phương không bị
            // treo chờ vô thời hạn. Không xóa Messages (khác với hành động Leave).
            var openRooms = await _context.ChatRooms
                .Where(r => (r.User1Id == id || r.User2Id == id)
                    && (r.Status == ChatRooms.RoomStatus.Waiting || r.Status == ChatRooms.RoomStatus.Active))
                .ToListAsync();

            foreach (var room in openRooms)
            {
                room.Status = ChatRooms.RoomStatus.Closed;
                room.UpdatedAt = DateTime.UtcNow;
            }

            await _context.SaveChangesAsync();

            return Ok(new { message = "Account deleted." });
        }

    }
}
