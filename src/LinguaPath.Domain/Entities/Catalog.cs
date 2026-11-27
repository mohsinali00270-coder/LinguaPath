using LinguaPath.Domain.Common;
using LinguaPath.Shared;

namespace LinguaPath.Domain.Entities;

/// <summary>Languages (SRS 7.2, FR-A10, FR-A11).</summary>
public class Language : AuditableEntity<int>
{
    public string Name { get; set; } = string.Empty;
    public string NativeName { get; set; } = string.Empty;
    /// <summary>ISO 639-1 code, e.g. "es".</summary>
    public string IsoCode { get; set; } = string.Empty;
    public string? FlagUrl { get; set; }
    public TextDirection Direction { get; set; } = TextDirection.Ltr;
    /// <summary>Neural TTS voice name, e.g. "es-ES-ElviraNeural".</summary>
    public string? TtsVoice { get; set; }
    /// <summary>Speech-to-text locale, e.g. "es-ES".</summary>
    public string? SttLocale { get; set; }
    public CefrLevel MinLevel { get; set; } = CefrLevel.A1;
    public CefrLevel MaxLevel { get; set; } = CefrLevel.C1;
    public LanguageStatus Status { get; set; } = LanguageStatus.Draft;
    public int SortOrder { get; set; }
}
