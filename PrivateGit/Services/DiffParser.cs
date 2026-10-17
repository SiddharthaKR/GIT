using System.Text.RegularExpressions;

namespace PrivateGit.Services;

public sealed record DiffLine(string Kind, int? OldLine, int? NewLine, string Text);
public sealed record ChangedFile(string Path, string Status, string OldMode, string NewMode,
    int? Added, int? Removed, IReadOnlyList<DiffLine> Lines, bool Truncated = false);
public sealed record ChangeSet(IReadOnlyList<ChangedFile> Files, int TotalFiles, string? Warning);

public static class DiffParser
{
    public const int MaxCharacters = 256 * 1024;
    public const int MaxLines = 5000;
    private static readonly Regex Hunk = new("^@@ -([0-9]+)(?:,[0-9]+)? \\+([0-9]+)(?:,[0-9]+)? @@", RegexOptions.CultureInvariant);
    public static IReadOnlyList<DiffLine> Parse(string section)
    {
        var output = new List<DiffLine>(); var oldLine = 0; var newLine = 0; var inHunk = false; var characters = 0;
        using var reader = new StringReader(section);
        // First line is the already-separated diff --git file header.
        _ = reader.ReadLine();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            characters += line.Length + 1;
            if (output.Count >= MaxLines || characters > MaxCharacters) break;
            var hunk = Hunk.Match(line);
            if (hunk.Success && int.TryParse(hunk.Groups[1].Value, out oldLine) && int.TryParse(hunk.Groups[2].Value, out newLine))
            { inHunk = true; output.Add(new("hunk", null, null, line)); }
            else if (inHunk && line.StartsWith('+')) output.Add(new("addition", null, newLine++, line));
            else if (inHunk && line.StartsWith('-')) output.Add(new("deletion", oldLine++, null, line));
            else if (inHunk && line.StartsWith(' ')) output.Add(new("context", oldLine++, newLine++, line));
            else output.Add(new("metadata", null, null, line));
        }
        return output;
    }
}
