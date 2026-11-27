namespace LinguaPath.Domain.Constants;

/// <summary>Roles from SRS 1.3 (Moderator added with the social features, section 11).</summary>
public static class Roles
{
    public const string SuperAdmin = "SuperAdmin";
    public const string ContentAdmin = "ContentAdmin";
    public const string Reviewer = "Reviewer";
    public const string Moderator = "Moderator";
    public const string Learner = "Learner";

    public static readonly string[] AdminRoles = [SuperAdmin, ContentAdmin, Reviewer, Moderator];
    public static readonly string[] All = [SuperAdmin, ContentAdmin, Reviewer, Moderator, Learner];

    public static string Describe(string role) => role switch
    {
        SuperAdmin => "Full access: settings, admins, new-language AI approval, unblocking accounts",
        ContentAdmin => "Languages, lectures, posts, quizzes, question banks, AI generation, tournaments",
        Reviewer => "Approves or rejects AI-generated and manual content; grades flagged answers",
        Moderator => "Reviews reported and AI-flagged chat content; strikes, suspensions, blocks",
        Learner => "Mobile app user",
        _ => role
    };
}

/// <summary>Authorization policy names shared by API and admin panel.</summary>
public static class Policies
{
    public const string AdminPanel = "AdminPanel";      // any admin role
    public const string SuperAdminOnly = "SuperAdminOnly";
    public const string ContentManagers = "ContentManagers"; // SuperAdmin, ContentAdmin
    public const string Reviewers = "Reviewers";         // SuperAdmin, Reviewer
    public const string Moderators = "Moderators";       // SuperAdmin, Moderator
    public const string LearnerOnly = "LearnerOnly";
}
