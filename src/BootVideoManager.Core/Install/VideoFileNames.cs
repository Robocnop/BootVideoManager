using System.Globalization;
using System.Text;
using BootVideoManager.Core.Models;

namespace BootVideoManager.Core.Install;

/// <summary>Builds and validates the names of files placed in Steam's movies folder.</summary>
public static class VideoFileNames
{
    public const string Extension = ".webm";

    /// <summary><c>{slug}_{id}.webm</c>: readable, portable, and unique because slugs are not.</summary>
    public static string ForPost(Post post)
    {
        ArgumentNullException.ThrowIfNull(post);
        return $"{Sanitize(post.Slug, 60, lowerCase: true)}_{Sanitize(post.Id, 20, lowerCase: false)}{Extension}";
    }

    /// <summary><c>{original-name}_local-{hash prefix}.webm</c> for files imported from disk.</summary>
    public static string ForLocalImport(string originalFileName, string sha256Hex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(originalFileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(sha256Hex);
        return $"{Sanitize(Path.GetFileNameWithoutExtension(originalFileName), 60, lowerCase: true)}_local-{sha256Hex[..Math.Min(8, sha256Hex.Length)]}{Extension}";
    }

    /// <summary>
    /// True for a bare <c>.webm</c> file name: no directory part, no traversal, no hidden file.
    /// Guards every delete against manifest tampering.
    /// </summary>
    public static bool IsSafeFileName(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && name.Length > Extension.Length
        && !name.StartsWith('.')
        && name.IndexOfAny(['/', '\\', ':']) < 0
        && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && name.EndsWith(Extension, StringComparison.OrdinalIgnoreCase);

    /// <summary>Keeps ASCII letters, digits and dashes; accents are stripped, everything else becomes one underscore.</summary>
    internal static string Sanitize(string value, int maxLength, bool lowerCase)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsAsciiLetterOrDigit(c) || c == '-')
            {
                builder.Append(lowerCase ? char.ToLowerInvariant(c) : c);
            }
            else if (builder.Length > 0 && builder[^1] != '_')
            {
                builder.Append('_');
            }
        }

        var result = builder.ToString().Trim('_', '-');
        if (result.Length > maxLength)
        {
            result = result[..maxLength].TrimEnd('_', '-');
        }

        return result.Length == 0 ? "video" : result;
    }
}
