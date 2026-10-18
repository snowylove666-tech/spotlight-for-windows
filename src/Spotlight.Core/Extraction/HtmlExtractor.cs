using System.Net;
using System.Text.RegularExpressions;

namespace Spotlight.Core.Extraction;

public sealed partial class HtmlExtractor : IContentExtractor
{
    public bool CanHandle(string extension) =>
        extension.Equals(".html", StringComparison.OrdinalIgnoreCase) || extension.Equals(".htm", StringComparison.OrdinalIgnoreCase);

    public string Extract(string path, int maxChars, CancellationToken ct)
    {
        var html = TextDecoder.ReadFile(path, maxChars * 2);
        var text = StripTags(html);
        return text.Length > maxChars ? text[..maxChars] : text;
    }

    public static string StripTags(string html)
    {
        html = ScriptOrStyle().Replace(html, " ");
        html = Tag().Replace(html, " ");
        return WebUtility.HtmlDecode(html);
    }

    [GeneratedRegex(@"<(script|style)[^>]*>.*?</\1>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ScriptOrStyle();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tag();
}
