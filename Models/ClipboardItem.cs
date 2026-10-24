namespace ClipboardManager.Models;

public sealed record ClipboardItem(
    long Id,
    string TextContent,
    DateTimeOffset Timestamp,
    ClipboardItemType ItemType)
{
    public DateTimeOffset LocalTimestamp => Timestamp.ToLocalTime();
    public string TypeLabel => ItemType.ToString().ToUpperInvariant();
}
