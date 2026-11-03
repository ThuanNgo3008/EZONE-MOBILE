using System.ComponentModel.DataAnnotations;

namespace WebChatEIU.DTOs
{
    public class SubmitRoomDecisionDto
    {
        [Required]
        public bool? ContinueChat { get; set; }
    }
}