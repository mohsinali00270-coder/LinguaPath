namespace LinguaPath.Domain.Entities;

public enum SettingDataType
{
    String = 0,
    Int = 1,
    Decimal = 2,
    Bool = 3,
    Version = 4
}

/// <summary>Key/value system settings (SRS 7.6, FR-A111).</summary>
public class SystemSetting
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public string Group { get; set; } = "General";
    public string? Description { get; set; }
    public SettingDataType DataType { get; set; } = SettingDataType.String;
    public Guid? UpdatedBy { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}

/// <summary>Audit trail of admin actions (FR-A92, NFR-16).</summary>
public class AuditLog
{
    public long Id { get; set; }
    public Guid? UserId { get; set; }
    public string? UserEmail { get; set; }
    public string Action { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
    public string? EntityId { get; set; }
    public string? OldValues { get; set; }
    public string? NewValues { get; set; }
    public string? IpAddress { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>An entry in the learner's in-app inbox (FR-L112). Read entries are removed after 90 days.</summary>
public class Notification
{
    public long Id { get; set; }
    public Guid UserId { get; set; }
    public LinguaPath.Shared.NotificationType Type { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    /// <summary>JSON object of string values the app uses to open the right screen.</summary>
    public string? DataJson { get; set; }
    /// <summary>Stops a reminder going out twice, e.g. "streak:2026-11-17". Unique per learner.</summary>
    public string? DedupKey { get; set; }
    public bool PushSent { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
}
