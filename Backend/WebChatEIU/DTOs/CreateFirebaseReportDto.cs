using System.ComponentModel.DataAnnotations;

namespace WebChatEIU.DTOs
{
    public class CreateFirebaseReportDto
    {
        [Required]
        [RegularExpression("^(Message|User)$")]
        public string ReportType { get; set; } = string.Empty;

        [Required]
        [StringLength(100)]
        public string Reason { get; set; } = string.Empty;

        [StringLength(1000)]
        public string? Description { get; set; }

        public string? ReportedMessageId { get; set; }
    }
}
