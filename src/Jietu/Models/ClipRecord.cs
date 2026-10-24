namespace Jietu;

public static class ClipKind
{
    public const string Text = "text";
    public const string Image = "image";
    public const string File = "file";
    public const string Rich = "rich";
}

public static class ClipFiles
{
    public static IReadOnlyList<string> Paths(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public static string Preview(string? text)
    {
        var names = Paths(text).Select(path => Path.GetFileName(path.TrimEnd('\\', '/'))).Where(name => name.Length > 0).ToList();
        if (names.Count == 0)
        {
            return "文件";
        }

        var shown = string.Join("、", names.Take(3));
        return names.Count > 3 ? shown + " 等 " + names.Count + " 个文件" : shown;
    }
}

public static class ClipSource
{
    public const string Clipboard = "clipboard";
    public const string Screenshot = "screenshot";
}

public readonly record struct HistoryCursor(int Pinned, long CreatedUtc, string Id);

public sealed class ClipRecord
{
    public required string Id { get; init; }
    public required string Kind { get; init; }
    public string? Text { get; init; }
    public string? ImagePath { get; init; }
    public required string ContentHash { get; init; }
    public long CreatedUtc { get; init; }
    public required string Source { get; init; }
    public bool Pinned { get; init; }
}
