namespace LinguaPath.Shared;

/// <summary>
/// BR-32: learners must be at least 13 (store policy); under-18 accounts can chat with friends only.
/// Date of birth is optional — when it isn't given the account is treated as an adult, as the stores allow
/// for apps rated 13+. Shared by the API and the app so both refuse the same dates.
/// </summary>
public static class AgeRules
{
    public const int MinimumAge = 13;
    public const int AdultAge = 18;

    public static int Age(DateOnly dateOfBirth, DateOnly today)
    {
        var age = today.Year - dateOfBirth.Year;
        if (today < dateOfBirth.AddYears(age)) age--;
        return age;
    }

    /// <summary>A plausible date: not in the future and not more than 120 years ago.</summary>
    public static bool IsPlausible(DateOnly dateOfBirth, DateOnly today) =>
        dateOfBirth <= today && dateOfBirth >= today.AddYears(-120);

    public static bool IsTooYoung(DateOnly? dateOfBirth, DateOnly today) =>
        dateOfBirth is { } d && Age(d, today) < MinimumAge;

    public static bool IsMinor(DateOnly? dateOfBirth, DateOnly today) =>
        dateOfBirth is { } d && Age(d, today) < AdultAge;
}
