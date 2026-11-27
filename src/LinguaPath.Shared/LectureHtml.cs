using System.Net;

namespace LinguaPath.Shared;

/// <summary>
/// Wraps a (sanitized) lecture body in a small, self-contained HTML page with the app's reading styles.
/// Used by the app's WebView and by the admin "Preview as learner" (FR-L32, FR-A47), so both look the same.
/// </summary>
public static class LectureHtml
{
    /// <summary>URL scheme the reader page uses to tell the app which word was tapped: linguapath://word?w=hola.</summary>
    public const string WordScheme = "linguapath";

    public static string Build(string? bodyHtml, TextDirection direction = TextDirection.Ltr, bool dark = false, string? languageCode = null,
        bool interactive = false)
    {
        var script = interactive ? TapScript : string.Empty;
        var dir = direction == TextDirection.Rtl ? "rtl" : "ltr";
        var lang = WebUtility.HtmlEncode(languageCode ?? "en");
        var fg = dark ? "#F1F5F9" : "#0F172A";
        var muted = dark ? "#A3AEC4" : "#5B6478";
        // Matches the app page background so the reader blends into the screen.
        var bg = dark ? "#080F22" : "#F6F7FB";
        var border = dark ? "#243250" : "#E4E7F0";
        var soft = dark ? "#172340" : "#F1EBFE";
        var accent = dark ? "#C4B5FD" : "#6D28D9";
        return $$"""
            <!DOCTYPE html>
            <html lang="{{lang}}" dir="{{dir}}">
            <head>
            <meta charset="utf-8" />
            <meta name="viewport" content="width=device-width, initial-scale=1, maximum-scale=3" />
            <style>
              html { -webkit-text-size-adjust: 100%; }
              body { margin: 0; padding: 4px 2px 24px; font: 17px/1.6 -apple-system, "Segoe UI", Roboto, "Noto Sans", "Noto Naskh Arabic", "Noto Sans CJK SC", sans-serif; color: {{fg}}; background: {{bg}}; word-wrap: break-word; }
              h2 { font-size: 1.3em; margin: 1.2em 0 .4em; } h3 { font-size: 1.1em; margin: 1em 0 .3em; } h4 { font-size: 1em; }
              p { margin: .6em 0; } ul, ol { padding-inline-start: 1.4em; } li { margin: .25em 0; }
              strong, b { font-weight: 700; } mark { background: #FDE68A; color: #111827; padding: 0 .15em; border-radius: 3px; }
              a { color: {{accent}}; }
              img { max-width: 100%; height: auto; border-radius: 10px; }
              blockquote { margin: 1em 0; padding: .6em 1em; background: {{soft}}; border-inline-start: 4px solid {{accent}}; border-radius: 10px; }
              table { border-collapse: collapse; width: 100%; margin: .8em 0; font-size: .95em; }
              th, td { border: 1px solid {{border}}; padding: .45em .6em; text-align: start; vertical-align: top; }
              th { background: {{soft}}; }
              code, pre { font-family: ui-monospace, Menlo, Consolas, monospace; font-size: .9em; }
              pre { white-space: pre-wrap; background: {{soft}}; padding: .6em; border-radius: 8px; }
              .ql-align-center { text-align: center; } .ql-align-right { text-align: right; }
              .muted { color: {{muted}}; }
            </style>
            </head>
            <body>{{bodyHtml ?? string.Empty}}{{script}}</body>
            </html>
            """;
    }

    /// <summary>
    /// Tap a word → the app gets linguapath://word?w=... (FR-L33). Links keep working; only plain text taps are captured.
    /// Uses the caret position under the finger, so it works for any script that separates words with spaces or punctuation.
    /// </summary>
    private const string TapScript = """
        <script>
        (function () {
          function wordAt(x, y) {
            var range = document.caretRangeFromPoint ? document.caretRangeFromPoint(x, y) : null;
            if (!range && document.caretPositionFromPoint) {
              var pos = document.caretPositionFromPoint(x, y);
              if (pos) { range = document.createRange(); range.setStart(pos.offsetNode, pos.offset); }
            }
            if (!range || !range.startContainer || range.startContainer.nodeType !== 3) return null;
            var text = range.startContainer.textContent, i = range.startOffset, s = i, e = i;
            var isWord = function (c) { return /[\p{L}\p{M}'\u2019-]/u.test(c); };
            while (s > 0 && isWord(text[s - 1])) s--;
            while (e < text.length && isWord(text[e])) e++;
            var w = text.substring(s, e).replace(/^['\u2019-]+|['\u2019-]+$/g, '');
            return w.length ? w : null;
          }
          document.addEventListener('click', function (ev) {
            if (ev.target.closest && ev.target.closest('a')) return;
            var w = wordAt(ev.clientX, ev.clientY);
            if (w) window.location.href = 'linguapath://word?w=' + encodeURIComponent(w);
          });
        })();
        </script>
        """;
}
