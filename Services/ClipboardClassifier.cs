using ClipboardManager.Models;

namespace ClipboardManager.Services;

internal static class ClipboardClassifier
{
    public static ClipboardItemType Classify(string text)
    {
        var trimmed = text.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) &&
            uri is not null &&
            (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
             uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
             uri.Scheme.Equals(Uri.UriSchemeMailto, StringComparison.OrdinalIgnoreCase)))
        {
            return ClipboardItemType.Link;
        }

        if (text.Contains('\n') || text.Contains('\r'))
        {
            string[] codeMarkers = ["{", "}", ";", "=>", "</", "#include", "def ", "function ", "using "];
            if (codeMarkers.Any(marker => text.Contains(marker, StringComparison.Ordinal)))
                return ClipboardItemType.Code;
        }

        return ClipboardItemType.Text;
    }
}
