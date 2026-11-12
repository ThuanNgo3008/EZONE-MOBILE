using System.ComponentModel.DataAnnotations;

namespace WebChatEIU.Tests.DTOs;

internal static class DtoValidation
{
    public static List<ValidationResult> Validate(object dto)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(dto, new ValidationContext(dto), results,
            validateAllProperties: true);
        return results;
    }

    public static void AssertInvalidMember(object dto, string memberName)
    {
        Assert.Contains(Validate(dto), result => result.MemberNames.Contains(memberName));
    }
}
