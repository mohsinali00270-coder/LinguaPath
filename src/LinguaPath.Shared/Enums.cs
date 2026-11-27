namespace LinguaPath.Shared;

/// <summary>CEFR levels used as the expertise scale (SRS 1.4).</summary>
public enum CefrLevel
{
    A1 = 1,
    A2 = 2,
    B1 = 3,
    B2 = 4,
    C1 = 5,
    C2 = 6
}

/// <summary>Language lifecycle (FR-A11).</summary>
public enum LanguageStatus
{
    Draft = 0,
    ComingSoon = 1,
    Published = 2,
    Disabled = 3
}

public enum TextDirection
{
    Ltr = 0,
    Rtl = 1
}

public enum UserType
{
    Learner = 0,
    Admin = 1
}

public enum DevicePlatform
{
    Unknown = 0,
    Android = 1,
    iOS = 2,
    Windows = 3,
    MacCatalyst = 4
}

/// <summary>What an email one-time code is for.</summary>
public enum OtpPurpose
{
    VerifyEmail = 1,
    ResetPassword = 2
}
