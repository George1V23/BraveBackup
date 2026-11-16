using System;
using System.Collections.Generic;

namespace UI.ViewModels;

/// <summary>Identifies the user-selected bookmark export format independently of the backup archive.</summary>
public enum BookmarkExportFormat
{
    /// <summary>Netscape bookmark HTML, as used by Brave's manual bookmark export.</summary>
    BrowserHtml,

    /// <summary>The existing BraveBackup schema-version-1 bookmark JSON.</summary>
    Json
}

/// <summary>Describes an export choice and the matching filename/filter metadata.</summary>
public sealed record BookmarkExportFormatOption(
    BookmarkExportFormat Format,
    string DisplayName,
    string Extension,
    string MimeType)
{
    /// <summary>Gets the primary/default browser-compatible HTML option.</summary>
    public static BookmarkExportFormatOption BrowserHtml { get; } =
        new(BookmarkExportFormat.BrowserHtml, "HTML — Brave browser format (.html)", "html", "text/html");

    /// <summary>Gets the existing application JSON option.</summary>
    public static BookmarkExportFormatOption Json { get; } =
        new(BookmarkExportFormat.Json, "JSON — BraveBackup format (.json)", "json", "application/json");

    /// <summary>Gets choices in priority order with browser HTML first.</summary>
    public static IReadOnlyList<BookmarkExportFormatOption> All { get; } =
        Array.AsReadOnly(new[] { BrowserHtml, Json });

    /// <inheritdoc />
    public override string ToString() => DisplayName;
}