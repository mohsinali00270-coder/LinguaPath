namespace LinguaPath.Shared.Contracts.Certificates;

// ------------------------------------------------------------------ certificates (FR-L80 .. FR-L83, BR-06 .. BR-08, BR-12)

public record CertificateDto(
    int Id,
    string Number,
    int LearnerCourseId,
    string LearnerName,
    string LanguageName,
    string CourseTitle,
    CefrLevel Level,
    int FinalGrade,
    GradeBand Band,
    DateOnly StartedOn,
    DateOnly CompletedOn,
    string Duration,
    DateTimeOffset IssuedAt,
    bool IsRevoked,
    /// <summary>Public page with the verification code (FR-L82): what the QR code opens and what learners share.</summary>
    string VerifyUrl);

public record EligibilityItemDto(string Text, bool Done, string? Detail);

/// <summary>How close a learner course is to its certificate (BR-06).</summary>
public record CertificateProgressDto(
    int LearnerCourseId,
    string CourseTitle,
    bool Eligible,
    int? CertificateId,
    int CoreLecturesDone,
    int CoreLecturesTotal,
    int ExamsPassed,
    int ExamsTotal,
    int? CourseGrade,
    int MinGrade,
    IReadOnlyList<EligibilityItemDto> Checklist);

/// <summary>Public verification (FR-L82). Details only with the code printed on the certificate, so numbers can't be used to look people up.</summary>
public record VerificationDto(
    string Number,
    bool Found,
    bool Valid,
    bool Revoked,
    bool Detailed,
    string? LearnerName,
    string? LanguageName,
    string? CourseTitle,
    CefrLevel? Level,
    GradeBand? Band,
    int? FinalGrade,
    DateOnly? CompletedOn,
    DateTimeOffset? IssuedAt);
