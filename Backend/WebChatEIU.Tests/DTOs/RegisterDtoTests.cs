using WebChatEIU.DTOs;

namespace WebChatEIU.Tests.DTOs;

public class RegisterDtoTests
{
    private static RegisterDto ValidRegistration() => new()
    {
        Fullname = "Test Student",
        Email = "student@eiu.edu.vn",
        Password = "sample-password",
        MajorCode = "CSE",
        Cohort = "K24",
        BirthYear = 2000,
        Interests = new() { "Music", "Reading", "Sports" }
    };

    [Fact]
    public void CompleteRegistration_IsValid() =>
        Assert.Empty(DtoValidation.Validate(ValidRegistration()));

    [Theory]
    [InlineData(nameof(RegisterDto.Fullname), null)]
    [InlineData(nameof(RegisterDto.Fullname), "")]
    [InlineData(nameof(RegisterDto.Fullname), "   ")]
    [InlineData(nameof(RegisterDto.Email), null)]
    [InlineData(nameof(RegisterDto.Email), "")]
    [InlineData(nameof(RegisterDto.Email), "   ")]
    [InlineData(nameof(RegisterDto.Password), null)]
    [InlineData(nameof(RegisterDto.Password), "")]
    [InlineData(nameof(RegisterDto.Password), "   ")]
    [InlineData(nameof(RegisterDto.MajorCode), null)]
    [InlineData(nameof(RegisterDto.MajorCode), "")]
    [InlineData(nameof(RegisterDto.MajorCode), "   ")]
    [InlineData(nameof(RegisterDto.Cohort), null)]
    [InlineData(nameof(RegisterDto.Cohort), "")]
    [InlineData(nameof(RegisterDto.Cohort), "   ")]
    public void RequiredText_MissingValue_IsInvalid(string member, string? value)
    {
        var dto = ValidRegistration();
        switch (member)
        {
            case nameof(RegisterDto.Fullname): dto.Fullname = value!; break;
            case nameof(RegisterDto.Email): dto.Email = value!; break;
            case nameof(RegisterDto.Password): dto.Password = value!; break;
            case nameof(RegisterDto.MajorCode): dto.MajorCode = value!; break;
            case nameof(RegisterDto.Cohort): dto.Cohort = value!; break;
            default: throw new ArgumentOutOfRangeException(nameof(member));
        }
        DtoValidation.AssertInvalidMember(dto, member);
    }

    [Theory]
    [InlineData("student@gmail.com")]
    [InlineData("student@sub.eiu.edu.vn")]
    [InlineData("student@eiu.edu.vn.example.com")]
    [InlineData("not-an-email")]
    public void Email_OutsideSchoolDomainOrMalformed_IsInvalid(string email)
    {
        var dto = ValidRegistration();
        dto.Email = email;
        DtoValidation.AssertInvalidMember(dto, nameof(dto.Email));
    }

    [Theory]
    [InlineData("student@eiu.edu.vn")]
    [InlineData("student.name+tag@eiu.edu.vn")]
    public void Email_ValidSchoolAddress_IsValid(string email)
    {
        var dto = ValidRegistration();
        dto.Email = email;
        Assert.Empty(DtoValidation.Validate(dto));
    }

    [Theory]
    [InlineData(5, false)]
    [InlineData(6, true)]
    public void Password_MinimumLength_IsEnforced(int length, bool expectedValid)
    {
        var dto = ValidRegistration();
        dto.Password = new string('a', length);
        Assert.Equal(expectedValid, DtoValidation.Validate(dto).Count == 0);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1899)]
    [InlineData(2101)]
    public void BirthYear_MissingOrOutsideRange_IsInvalid(int? birthYear)
    {
        var dto = ValidRegistration();
        dto.BirthYear = birthYear;
        DtoValidation.AssertInvalidMember(dto, nameof(dto.BirthYear));
    }

    [Fact]
    public void BirthYear_FutureYearWithinDeclaredRange_IsInvalid()
    {
        var dto = ValidRegistration();
        // Remains in the future if validation crosses a UTC year boundary.
        dto.BirthYear = DateTime.UtcNow.Year + 2;
        DtoValidation.AssertInvalidMember(dto, nameof(dto.BirthYear));
    }

    [Theory]
    [InlineData(1900)]
    [InlineData(2000)]
    public void BirthYear_PastYearWithinRange_IsValid(int birthYear)
    {
        var dto = ValidRegistration();
        dto.BirthYear = birthYear;
        Assert.Empty(DtoValidation.Validate(dto));
    }

    [Fact]
    public void BirthYear_CurrentYear_IsValid()
    {
        var dto = ValidRegistration();
        dto.BirthYear = DateTime.UtcNow.Year;
        Assert.Empty(DtoValidation.Validate(dto));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(2, false)]
    [InlineData(3, true)]
    [InlineData(10, true)]
    [InlineData(11, false)]
    public void Interests_CountLimits_AreEnforced(int count, bool expectedValid)
    {
        var dto = ValidRegistration();
        dto.Interests = Enumerable.Range(0, count).Select(i => $"Interest {i}").ToList();
        Assert.Equal(expectedValid, DtoValidation.Validate(dto).Count == 0);
    }

    [Fact]
    public void Interests_Null_IsInvalid()
    {
        var dto = ValidRegistration();
        dto.Interests = null!;
        DtoValidation.AssertInvalidMember(dto, nameof(dto.Interests));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Interests_BlankEntry_IsInvalid(string? interest)
    {
        var dto = ValidRegistration();
        dto.Interests = new() { "Music", "Reading", interest! };
        DtoValidation.AssertInvalidMember(dto, nameof(dto.Interests));
    }

    [Theory]
    [InlineData("Music")]
    [InlineData("music")]
    [InlineData(" Music ")]
    public void Interests_DuplicateIgnoringCaseAndWhitespace_IsInvalid(string duplicate)
    {
        var dto = ValidRegistration();
        dto.Interests = new() { "Music", "Reading", duplicate };
        DtoValidation.AssertInvalidMember(dto, nameof(dto.Interests));
    }
}
