// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;

namespace TopToolbar.Services.Everything;

public sealed class EverythingSearchResult
{
    public string FullPath { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string DirectoryPath { get; init; } = string.Empty;

    public bool IsFolder { get; init; }

    public long? SizeBytes { get; init; }

    public DateTime? DateModified { get; init; }

    public string TypeLabel => IsFolder ? "Folder" : "File";

    public string IconGlyph => IsFolder ? "\uE8B7" : "\uE7C3";

    public string SizeLabel
    {
        get
        {
            if (IsFolder || !SizeBytes.HasValue)
            {
                return string.Empty;
            }

            var size = (double)SizeBytes.Value;
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            var unitIndex = 0;
            while (size >= 1024 && unitIndex < units.Length - 1)
            {
                size /= 1024;
                unitIndex++;
            }

            return unitIndex == 0
                ? $"{SizeBytes.Value} {units[unitIndex]}"
                : $"{size:0.#} {units[unitIndex]}";
        }
    }

    public string DateModifiedLabel => DateModified?.ToString("yyyy-MM-dd HH:mm") ?? string.Empty;
}
