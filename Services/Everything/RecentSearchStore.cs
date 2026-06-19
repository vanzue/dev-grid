// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using TopToolbar.Logging;

namespace TopToolbar.Services.Everything;

/// <summary>
/// Persists a most-recently-used list of files/folders that were acted upon from the
/// Everything search popup. Any action (open, reveal, copy, open-with, terminal,
/// properties) counts as a "use" and moves the item to the front.
/// </summary>
public sealed class RecentSearchStore
{
    public const int MaxEntries = 25;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly object _sync = new();
    private readonly string _filePath;
    private List<RecentSearchEntry> _entries;
    private bool _loaded;

    public RecentSearchStore(string filePath = null)
    {
        _filePath = string.IsNullOrWhiteSpace(filePath)
            ? Path.Combine(AppPaths.ConfigDirectory, "everything-recents.json")
            : filePath;
    }

    public IReadOnlyList<RecentSearchEntry> GetRecents(int max = MaxEntries)
    {
        lock (_sync)
        {
            EnsureLoaded();
            return _entries
                .OrderByDescending(e => e.LastUsedUtc)
                .Take(Math.Max(1, max))
                .ToList();
        }
    }

    /// <summary>
    /// Records a use of the given path: bumps the timestamp/count and moves it to the
    /// front. The in-memory list is updated synchronously; the file is saved off-thread.
    /// </summary>
    public void Record(string fullPath, string name, bool isFolder)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            return;
        }

        lock (_sync)
        {
            EnsureLoaded();

            var existing = _entries.FirstOrDefault(e =>
                string.Equals(e.FullPath, fullPath, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                existing.LastUsedUtc = DateTime.UtcNow;
                existing.UseCount++;
                if (!string.IsNullOrWhiteSpace(name))
                {
                    existing.Name = name;
                }

                existing.IsFolder = isFolder;
            }
            else
            {
                _entries.Add(new RecentSearchEntry
                {
                    FullPath = fullPath,
                    Name = string.IsNullOrWhiteSpace(name) ? Path.GetFileName(fullPath) : name,
                    IsFolder = isFolder,
                    LastUsedUtc = DateTime.UtcNow,
                    UseCount = 1,
                });
            }

            Trim();
        }

        _ = Task.Run(SaveSnapshot);
    }

    /// <summary>
    /// Removes the given paths from the store (e.g., files/folders that no longer exist).
    /// Saves off-thread if anything changed.
    /// </summary>
    public void RemoveMany(IEnumerable<string> fullPaths)
    {
        if (fullPaths == null)
        {
            return;
        }

        var toRemove = new HashSet<string>(
            fullPaths.Where(p => !string.IsNullOrWhiteSpace(p)),
            StringComparer.OrdinalIgnoreCase);
        if (toRemove.Count == 0)
        {
            return;
        }

        bool changed;
        lock (_sync)
        {
            EnsureLoaded();
            var before = _entries.Count;
            _entries = _entries
                .Where(e => e != null && !toRemove.Contains(e.FullPath))
                .ToList();
            changed = _entries.Count != before;
        }

        if (changed)
        {
            _ = Task.Run(SaveSnapshot);
        }
    }

    private void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        _entries = new List<RecentSearchEntry>();

        try
        {
            if (File.Exists(_filePath))
            {
                var json = File.ReadAllText(_filePath);
                var data = JsonSerializer.Deserialize<List<RecentSearchEntry>>(json, JsonOptions);
                if (data != null)
                {
                    _entries = data
                        .Where(e => e != null && !string.IsNullOrWhiteSpace(e.FullPath))
                        .ToList();
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogWarning($"RecentSearchStore: failed to load '{_filePath}' - {ex.Message}");
            _entries = new List<RecentSearchEntry>();
        }
    }

    private void Trim()
    {
        if (_entries.Count <= MaxEntries)
        {
            return;
        }

        _entries = _entries
            .OrderByDescending(e => e.LastUsedUtc)
            .Take(MaxEntries)
            .ToList();
    }

    private void SaveSnapshot()
    {
        try
        {
            string json;
            lock (_sync)
            {
                json = JsonSerializer.Serialize(_entries, JsonOptions);
            }

            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var temp = _filePath + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, _filePath, overwrite: true);
        }
        catch (Exception ex)
        {
            AppLogger.LogWarning($"RecentSearchStore: failed to save '{_filePath}' - {ex.Message}");
        }
    }
}
