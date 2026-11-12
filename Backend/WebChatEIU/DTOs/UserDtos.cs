
using System.ComponentModel.DataAnnotations;

namespace WebChatEIU.DTOs
{
    public class RegisterDto : IValidatableObject
    {
        [Required]
        public string Fullname { get; set; } = string.Empty;

        [Required]
        [EmailAddress]
        [RegularExpression(
            @"^[a-zA-Z0-9._%+-]+@eiu\.edu\.vn$",
            ErrorMessage = "Email must belong to EIU")]
        public string Email { get; set; } = string.Empty;

        [Required]
        [MinLength(6)]
        public string Password { get; set; } = string.Empty;

        [Required]
        public string MajorCode { get; set; } = string.Empty;

       
        [Required]
        public string Cohort { get; set; } = string.Empty;

        
        [Required]
        [Range(1900, 2100)]
        public int? BirthYear { get; set; }


        [Required]
        [MinLength(3, ErrorMessage = "Select at least 3 interests")]
        [MaxLength(10, ErrorMessage = "Select at most 10 interests")]
        public List<string> Interests { get; set; } = new();

        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        {
            if (Interests == null)
                yield break;

            if (Interests.Any(string.IsNullOrWhiteSpace))
            {
                yield return new ValidationResult(
                    "Interests cannot be empty.",
                    new[] { nameof(Interests) });
            }

            var validInterests = Interests
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .ToList();

            if (validInterests.Distinct(StringComparer.OrdinalIgnoreCase).Count()
                != validInterests.Count)
            {
                yield return new ValidationResult(
                    "Duplicate interests are not allowed.",
                    new[] { nameof(Interests) });
            }

            if (BirthYear.HasValue && BirthYear.Value > DateTime.UtcNow.Year)
            {
                yield return new ValidationResult(
                    "Birth year cannot be in the future.",
                    new[] { nameof(BirthYear) });
            }
        }
    }

    public class ResendVerificationEmailDto
    {
        [Required]
        [EmailAddress]
        [RegularExpression(
            @"^[a-zA-Z0-9._%+-]+@eiu\.edu\.vn$",
            ErrorMessage = "Email must belong to EIU")]
        public string Email { get; set; } = string.Empty;
    }
}
