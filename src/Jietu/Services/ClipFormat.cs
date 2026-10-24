using System.Text;

namespace Jietu;

internal static class ClipFormat
{
    public static bool ShouldKeep(string? text, byte[]? html, byte[]? rtf)
    {
        if (html is { Length: > 0 } && HtmlLooksFormatted(html))
        {
            return true;
        }

        return (html == null || html.Length == 0) && rtf is { Length: > 0 } && RtfLooksFormatted(rtf);
    }

    private static readonly string[] FormattedTags =
    [
        "<b", "<strong", "<i", "<em", "<u", "<s", "<strike", "<ul", "<ol", "<li",
        "<h1", "<h2", "<h3", "<h4", "<h5", "<h6", "<pre", "<code", "<blockquote",
        "<table", "<img", "<a", "<sup", "<sub", "<del",
    ];

    private static bool HtmlLooksFormatted(byte[] html)
    {
        var fragment = Fragment(Encoding.UTF8.GetString(html));
        foreach (var tag in FormattedTags)
        {
            if (HasTag(fragment, tag))
            {
                return true;
            }
        }

        return HasBoldStyle(fragment) || HasDecoration(fragment);
    }

    private static string Fragment(string html)
    {
        const string start = "<!--StartFragment-->";
        const string end = "<!--EndFragment-->";
        var from = html.IndexOf(start, StringComparison.OrdinalIgnoreCase);
        if (from < 0)
        {
            return html;
        }

        from += start.Length;
        var to = html.IndexOf(end, from, StringComparison.OrdinalIgnoreCase);
        return to < 0 ? html[from..] : html[from..to];
    }

    private static bool HasTag(string html, string token)
    {
        var index = 0;
        while ((index = html.IndexOf(token, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var after = index + token.Length;
            if (after >= html.Length || html[after] is '>' or ' ' or '/' or '\t' or '\r' or '\n')
            {
                return true;
            }

            index = after;
        }

        return false;
    }

    private static bool HasBoldStyle(string html)
    {
        var index = 0;
        while ((index = html.IndexOf("font-weight", index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var tail = html[index..Math.Min(html.Length, index + 28)];
            if (tail.Contains("bold", StringComparison.OrdinalIgnoreCase) || tail.Contains("700") || tail.Contains("600"))
            {
                return true;
            }

            index += 11;
        }

        return false;
    }

    private static bool HasDecoration(string html)
    {
        if (html.IndexOf("text-decoration", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return false;
        }

        return html.Contains("underline", StringComparison.OrdinalIgnoreCase)
            || html.Contains("line-through", StringComparison.OrdinalIgnoreCase);
    }

    private static bool RtfLooksFormatted(byte[] rtf)
    {
        var text = Encoding.ASCII.GetString(rtf);
        return ControlOn(text, "\\b") || ControlOn(text, "\\i") || ControlOn(text, "\\ul");
    }

    private static bool ControlOn(string rtf, string control)
    {
        var index = 0;
        while ((index = rtf.IndexOf(control, index, StringComparison.Ordinal)) >= 0)
        {
            var after = index + control.Length;
            if (after >= rtf.Length || rtf[after] is '\\' or ' ' or '{' or '1')
            {
                return true;
            }

            index = after;
        }

        return false;
    }
}
