using Ganss.Xss;
using LinguaPath.Application.Common.Interfaces;

namespace LinguaPath.Infrastructure.Services;

/// <summary>
/// Allow-list sanitizer for lecture bodies written in the admin editor. Keeps formatting, lists, tables, links and images;
/// strips scripts, event handlers, iframes, forms and inline styles (NFR-05). Links open externally in the app.
/// </summary>
internal sealed class HtmlSanitizerService : IHtmlSanitizerService
{
    private static readonly HtmlSanitizer Sanitizer = Create();

    public string Sanitize(string? html) => string.IsNullOrWhiteSpace(html) ? string.Empty : Sanitizer.Sanitize(html).Trim();

    private static HtmlSanitizer Create()
    {
        var s = new HtmlSanitizer();
        s.AllowedTags.Clear();
        foreach (var tag in new[]
                 {
                     "p", "br", "hr", "h2", "h3", "h4", "strong", "b", "em", "i", "u", "s", "sub", "sup", "mark", "code", "pre",
                     "blockquote", "ul", "ol", "li", "a", "img", "span", "div",
                     "table", "thead", "tbody", "tr", "th", "td", "caption"
                 })
            s.AllowedTags.Add(tag);

        s.AllowedAttributes.Clear();
        foreach (var attr in new[] { "href", "src", "alt", "title", "class", "dir", "lang", "colspan", "rowspan" })
            s.AllowedAttributes.Add(attr);

        s.AllowedCssProperties.Clear();   // no inline styles: the app styles content itself
        s.AllowedSchemes.Clear();
        s.AllowedSchemes.Add("https");
        s.AllowedSchemes.Add("http");
        s.AllowedSchemes.Add("mailto");
        // Quill alignment / indent classes are harmless; everything else in class is stripped by the app stylesheet anyway.
        return s;
    }
}
