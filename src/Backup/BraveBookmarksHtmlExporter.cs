using System.Globalization;
using System.Text;

namespace BraveBackup.Core;

/// <summary>Exports browser-compatible Netscape bookmark HTML following Chromium's bookmark HTML writer.</summary>
/// <remarks>See docs/brave-bookmarks-html.md for sources, root layout, timestamp conversion and optional-icon limits.</remarks>
public static class BraveBookmarksHtmlExporter
{
    private const string NewLine = "\r\n";
    private const long UnixEpochMicroseconds = 11644473600000000;

    /// <summary>Parses a Brave Bookmarks document and converts it to UTF-8-compatible bookmark HTML text.</summary>
    public static string ExportJson(string bookmarksJson) => Export(BraveBookmarksParser.Parse(bookmarksJson));

    /// <summary>Writes the browser header, toolbar wrapper, root descendants and nested folders in browser export order.</summary>
    /// <param name="bookmarks">Parsed bookmark data; URL/folder names and ordering are preserved.</param>
    /// <returns>Netscape bookmark HTML with CRLF line endings and optional timestamp fallbacks of zero.</returns>
    public static string Export(BraveBookmarksFile bookmarks)
    {
        ArgumentNullException.ThrowIfNull(bookmarks);
        if (bookmarks.Roots.GroupBy(root => root.Key, StringComparer.Ordinal).Any(group => group.Count() > 1))
            throw new InvalidDataException("Cannot export duplicate bookmark roots as browser HTML.");
        var output = new StringBuilder();
        output.Append("<!DOCTYPE NETSCAPE-Bookmark-file-1>\r\n")
            .Append("<!-- This is an automatically generated file.\r\n")
            .Append("     It will be read and overwritten.\r\n")
            .Append("     DO NOT EDIT! -->\r\n")
            .Append("<META HTTP-EQUIV=\"Content-Type\" CONTENT=\"text/html; charset=UTF-8\">\r\n")
            .Append("<TITLE>Bookmarks</TITLE>\r\n<H1>Bookmarks</H1>\r\n<DL><p>\r\n");
        var toolbar = bookmarks.Roots.FirstOrDefault(root => root.Key == "bookmark_bar");
        if (toolbar != null)
        {
            WriteFolderStart(output, toolbar.Name, toolbar.DateAdded, toolbar.DateModified, 1, toolbar: true);
            WriteChildren(output, toolbar.Children, 2);
            WriteFolderEnd(output, 1);
        }

        // Chromium exports the contents of Other/Mobile directly, without their permanent-folder wrappers.
        foreach (var key in new[] { "other", "synced" })
        {
            var root = bookmarks.Roots.FirstOrDefault(root => root.Key == key);
            if (root != null) WriteChildren(output, root.Children, 1);
        }

        // Retain unrecognized roots as ordinary folders instead of silently losing their contents.
        foreach (var root in bookmarks.Roots.Where(root => root.Key is not ("bookmark_bar" or "other" or "synced")))
        {
            WriteFolderStart(output, root.Name, root.DateAdded, root.DateModified, 1, toolbar: false);
            WriteChildren(output, root.Children, 2);
            WriteFolderEnd(output, 1);
        }

        output.Append("</DL><p>").Append(NewLine);
        return output.ToString();
    }

    /// <summary>Atomically writes HTML outside browser data, preserving existing output on failure or cancellation.</summary>
    /// <param name="bookmarks">The bookmark data to export.</param>
    /// <param name="filePath">The destination filename.</param>
    /// <param name="cancellationToken">Cancellation checked before writing and before replacement.</param>
    public static async Task ExportToFileAsync(BraveBookmarksFile bookmarks, string filePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bookmarks);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        cancellationToken.ThrowIfCancellationRequested();
        var html = Export(bookmarks);
        var destination = ExportDestinationGuard.Validate(filePath);
        var directory = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".bravebackup-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None, 4096, FileOptions.Asynchronous))
            await using (var writer =
                         new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
                await writer.WriteAsync(html.AsMemory(), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            ExportDestinationGuard.Validate(destination);
            File.Move(temporaryPath, destination, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    /// <summary>Recursively writes links/folders with Chromium's four-space indentation and attribute ordering.</summary>
    private static void WriteChildren(StringBuilder output, IReadOnlyList<BraveBookmark> children, int depth)
    {
        foreach (var bookmark in children)
        {
            if (bookmark.Type == BraveBookmarkType.Url)
            {
                output.Append(' ', depth * 4).Append("<DT><A HREF=\"")
                    .Append((bookmark.Url ?? string.Empty).Replace("\"", "&quot;", StringComparison.Ordinal))
                    .Append("\" ADD_DATE=\"").Append(ToUnixSeconds(bookmark.DateAdded)).Append("\">")
                    .Append(EscapeTitle(bookmark.Name)).Append("</A>").Append(NewLine);
            }
            else
            {
                WriteFolderStart(output, bookmark.Name, bookmark.DateAdded, bookmark.DateModified, depth,
                    toolbar: false);
                WriteChildren(output, bookmark.Children, depth + 1);
                WriteFolderEnd(output, depth);
            }
        }
    }

    /// <summary>Writes folder dates and the permanent-toolbar attribute used by browser importers.</summary>
    private static void WriteFolderStart(StringBuilder output, string name, string? dateAdded, string? dateModified,
        int depth, bool toolbar)
    {
        output.Append(' ', depth * 4).Append("<DT><H3 ADD_DATE=\"").Append(ToUnixSeconds(dateAdded))
            .Append("\" LAST_MODIFIED=\"").Append(ToUnixSeconds(dateModified)).Append('"');
        if (toolbar) output.Append(" PERSONAL_TOOLBAR_FOLDER=\"true\"");
        output.Append('>').Append(EscapeTitle(name)).Append("</H3>").Append(NewLine)
            .Append(' ', depth * 4).Append("<DL><p>").Append(NewLine);
    }

    /// <summary>Closes a folder using the browser's Netscape bookmark structure.</summary>
    private static void WriteFolderEnd(StringBuilder output, int depth) =>
        output.Append(' ', depth * 4).Append("</DL><p>").Append(NewLine);

    /// <summary>Escapes title markup while retaining UTF-8 Unicode text, matching Chromium EscapeForHTML.</summary>
    private static string EscapeTitle(string value) => value.Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal).Replace("'", "&#39;", StringComparison.Ordinal);

    /// <summary>Converts Chromium microseconds since 1601 to Unix seconds; missing/null/invalid times become zero.</summary>
    private static string ToUnixSeconds(string? value)
    {
        if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var microseconds) ||
            microseconds == 0)
            return "0";
        if (microseconds == long.MaxValue) return long.MaxValue.ToString(CultureInfo.InvariantCulture);
        if (microseconds == long.MinValue) return long.MinValue.ToString(CultureInfo.InvariantCulture);
        var difference = (decimal)microseconds - UnixEpochMicroseconds;
        if (difference < long.MinValue) return long.MinValue.ToString(CultureInfo.InvariantCulture);
        return decimal.Floor(difference / 1000000m).ToString(CultureInfo.InvariantCulture);
    }
}