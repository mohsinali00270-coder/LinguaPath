namespace LinguaPath.Shared;

/// <summary>Kinds of posts in the feed (FR-L40, FR-A50).</summary>
public enum PostType
{
    Tip = 1,
    Article = 2,
    Video = 3,
    CulturalNote = 4,
    WordOfTheDay = 5
}

/// <summary>External lecture links (FR-A41).</summary>
public enum LinkType
{
    Website = 0,
    Video = 1,
    Article = 2,
    Pdf = 3,
    Podcast = 4
}

public enum ReviewEntityType
{
    Lecture = 1,
    Post = 2,
    Question = 3,
    /// <summary>A course roadmap (modules and lecture titles) drafted by AI.</summary>
    Course = 4,
    /// <summary>A reading passage, writing prompt, speaking or dictation sentence.</summary>
    PracticeItem = 5
}

/// <summary>Outcome of a review (FR-A70, FR-A71).</summary>
public enum ReviewStatus
{
    Pending = 0,
    Approved = 1,
    Rejected = 2
}

public enum ReportStatus
{
    Open = 0,
    Resolved = 1,
    Dismissed = 2
}

public enum ReportEntityType
{
    Lecture = 1,
    Post = 2,
    Question = 3
}
