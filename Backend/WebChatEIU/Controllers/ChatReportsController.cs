using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WebChatEIU.Data;
using WebChatEIU.DTOs;
using WebChatEIU.Models;

namespace WebChatEIU.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class ChatReportsController : ControllerBase
    {
        private readonly ApplicationDbContext _context;

        public ChatReportsController(ApplicationDbContext context)
        {
            _context = context;
        }

        [HttpPost]
        public async Task<IActionResult> CreateReport([FromBody] ChatReports report)
        {
            var room = await _context.ChatRooms.FindAsync(report.RoomId);

            if (room == null)
            {
                return NotFound("Room not found");
            }

            if (room.User1Id != report.ReporterId && room.User2Id != report.ReporterId)
            {
                return BadRequest("Reporter is not in this room");
            }

            int reportedUserId =
                room.User1Id == report.ReporterId
                ? room.User2Id
                : room.User1Id;

            report.ReportedUserId = reportedUserId;

            report.Status = "Pending";
            report.CreatedAt = DateTime.UtcNow;

            _context.ChatReports.Add(report);
            await _context.SaveChangesAsync();

            return Ok(new
            {
                message = "Report submitted successfully"
            });
        }

        [Authorize]
        [HttpGet("my")]
        public async Task<IActionResult> GetMyReports()
        {
            int userId = int.Parse(User.FindFirst("userId").Value);

            var reports = await _context.ChatReports
                .Where(r => r.ReporterId == userId)
                .OrderByDescending(r => r.CreatedAt)
                .ToListAsync();

            return Ok(reports);
        }

        [Authorize(Roles = "Admin")]
        [HttpPost("{reportId}/ban")]
        public async Task<IActionResult> BanReportedUser(int reportId)
        {
            var report = await _context.ChatReports.FindAsync(reportId);

            if (report == null)
            {
                return NotFound("Report not found");
            }

            var user = await _context.Users.FindAsync(report.ReportedUserId);

            if (user == null)
            {
                return NotFound("Reported user not found");
            }

            user.IsBanned = true;
            user.BanSource = "Admin";
            report.Status = "Resolved";

            await _context.SaveChangesAsync();

            return Ok(new
            {
                message = "Reported user has been banned.",
                reportedUserId = user.UserId,
                isBanned = user.IsBanned,
                banSource = user.BanSource
            });
        }

        // Update data API còn thiếu (khác Update profile) — admin đổi trạng thái
        // xử lý report mà không cần ban user, dùng cho AdminReportDetailScreen.
        [Authorize(Roles = "Admin")]
        [HttpPut("{reportId}")]
        public async Task<IActionResult> UpdateReportStatus(int reportId, [FromBody] UpdateReportStatusDto dto)
        {
            var report = await _context.ChatReports.FindAsync(reportId);

            if (report == null)
            {
                return NotFound("Report not found");
            }

            var allowedStatuses = new[] { "Pending", "Resolved" };

            if (string.IsNullOrWhiteSpace(dto.Status) || !allowedStatuses.Contains(dto.Status))
            {
                return BadRequest("Status must be either 'Pending' or 'Resolved'");
            }

            report.Status = dto.Status;
            await _context.SaveChangesAsync();

            return Ok(new
            {
                message = "Report status updated.",
                reportId = report.ReportId,
                status = report.Status
            });
        }

        [Authorize(Roles = "Admin")]
        [HttpDelete("{reportId}")]
        public async Task<IActionResult> DeleteReport(int reportId)
        {
            var report = await _context.ChatReports.FindAsync(reportId);

            if (report == null)
            {
                return NotFound("Report not found");
            }

            _context.ChatReports.Remove(report);

            await _context.SaveChangesAsync();

            return Ok(new
            {
                message = "Report deleted successfully."
            });
        }

        [Authorize(Roles = "Admin")]
        [HttpGet]
        public async Task<IActionResult> GetReports(
            int page = 1,
            int pageSize = 10,
            string? status = null,
            string sortBy = "createdAt",
            string sortOrder = "desc")
        {
            if (page <= 0)
            {
                page = 1;
            }

            if (pageSize <= 0 || pageSize > 100)
            {
                pageSize = 10;
            }

            var query = _context.ChatReports.AsQueryable();

            // Filtering
            if (!string.IsNullOrEmpty(status))
            {
                query = query.Where(r => r.Status == status);
            }

            // Sorting
            query = sortBy.ToLower() switch
            {
                "status" => sortOrder.ToLower() == "asc"
                    ? query.OrderBy(r => r.Status)
                    : query.OrderByDescending(r => r.Status),

                "reason" => sortOrder.ToLower() == "asc"
                    ? query.OrderBy(r => r.Reason)
                    : query.OrderByDescending(r => r.Reason),

                _ => sortOrder.ToLower() == "asc"
                    ? query.OrderBy(r => r.CreatedAt)
                    : query.OrderByDescending(r => r.CreatedAt)
            };

            var totalItems = await query.CountAsync();

            var reports = await query
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Join(
                    _context.Users,
                    r => r.ReportedUserId,
                    u => u.UserId,
                    (r, u) => new
                    {
                        r.ReportId,
                        r.RoomId,
                        r.ReporterId,
                        r.ReportedUserId,
                        r.Reason,
                        r.ViolatingMessage,
                        r.Status,
                        r.CreatedAt,
                        ReportedUserIsBanned = u.IsBanned,
                        ReportedUserBanSource = u.BanSource
                    })
                .ToListAsync();

            return Ok(new
            {
                page,
                pageSize,
                totalItems,
                totalPages = (int)Math.Ceiling(totalItems / (double)pageSize),
                sortBy,
                sortOrder,
                status,
                data = reports
            });
        }


    }
}