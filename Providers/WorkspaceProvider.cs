// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TopToolbar.Actions;
using TopToolbar.Logging;
using TopToolbar.Models;
using TopToolbar.Models.Providers;
using TopToolbar.Services;
using TopToolbar.Services.Providers;
using TopToolbar.Services.Workspaces;

namespace TopToolbar.Providers
{
    public sealed class WorkspaceProvider : IActionProvider, IToolbarGroupProvider, IDisposable, IChangeNotifyingActionProvider
    {
        private const string WorkspacePrefix = "workspace.launch:";
        private readonly WorkspaceProviderConfigStore _configStore;
        private readonly WorkspaceDefinitionStore _definitionStore;
        private readonly WorkspaceButtonStore _buttonStore;
        private readonly WorkspacesRuntimeService _workspacesService;

        // Caching + watcher fields
        private readonly object _cacheLock = new();
        private List<WorkspaceRecord> _cached = new();
        private bool _cacheLoaded;
        private int _version;
        private FileSystemWatcher _configWatcher;
        private FileSystemWatcher _definitionsWatcher;
        private System.Timers.Timer _debounceTimer;
        private bool _disposed;

        // Local event (UI or tests can hook) - optional
        public event EventHandler WorkspacesChanged;

        // Typed provider change event consumed by runtime
        public event EventHandler<ProviderChangedEventArgs> ProviderChanged;

        public WorkspaceProvider(string workspacesPath = null)
        {
            _configStore = new WorkspaceProviderConfigStore(workspacesPath);
            _definitionStore = new WorkspaceDefinitionStore(null, _configStore);
            _buttonStore = new WorkspaceButtonStore(_configStore, _definitionStore);
            _workspacesService = new WorkspacesRuntimeService(_configStore.FilePath);

            try
            {
                _definitionStore.PruneStaleRuntimeWorkspacesOnceAsync(CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                AppLogger.LogWarning($"WorkspaceProvider: startup runtime workspace prune failed - {ex.Message}");
            }

            StartWatcher();
        }

        private void StartWatcher()
        {
            try
            {
                _debounceTimer = new System.Timers.Timer(250) { AutoReset = false };
                _debounceTimer.Elapsed += async (_, __) =>
                {
                    try
                    {
                        await ReloadIfChangedAsync().ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        // Swallow, optional: add logging later
                    }
                };

                var handler = new FileSystemEventHandler((_, __) => RestartDebounce());
                var renamedHandler = new RenamedEventHandler((_, __) => RestartDebounce());

                _configWatcher = CreateWatcher(_configStore.FilePath, handler, renamedHandler);
                _definitionsWatcher = CreateWatcher(_definitionStore.FilePath, handler, renamedHandler);
            }
            catch (Exception)
            {
                // Ignore watcher setup failures
            }
        }

        private static FileSystemWatcher CreateWatcher(
            string filePath,
            FileSystemEventHandler handler,
            RenamedEventHandler renamedHandler)
        {
            var dir = Path.GetDirectoryName(filePath);
            var file = Path.GetFileName(filePath);
            if (string.IsNullOrWhiteSpace(dir) || string.IsNullOrWhiteSpace(file))
            {
                return null;
            }

            var watcher = new FileSystemWatcher(dir, file)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.CreationTime,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true,
            };

            watcher.Changed += handler;
            watcher.Created += handler;
            watcher.Deleted += handler;
            watcher.Renamed += renamedHandler;
            return watcher;
        }

        private static void DisposeWatcher(FileSystemWatcher watcher)
        {
            if (watcher == null)
            {
                return;
            }

            try
            {
                watcher.EnableRaisingEvents = false;
            }
            catch
            {
            }

            try
            {
                watcher.Dispose();
            }
            catch
            {
            }
        }

        private void RestartDebounce()
        {
            if (_debounceTimer == null)
            {
                return;
            }

            try
            {
                _debounceTimer.Stop();
                _debounceTimer.Start();
            }
            catch
            {
            }
        }

        private async Task<bool> ReloadIfChangedAsync()
        {
            var newList = await ReadWorkspacesFileAsync(CancellationToken.None).ConfigureAwait(false);
            bool changed;
            lock (_cacheLock)
            {
                if (!HasChanged(_cached, newList))
                {
                    return false;
                }

                _cached = new List<WorkspaceRecord>(newList);
                _cacheLoaded = true;
                _version++;
                changed = true;
            }

            if (changed)
            {
                try
                {
                    WorkspacesChanged?.Invoke(this, EventArgs.Empty);

                    // Use ActionsUpdated with the set of current workspace action ids
                    var actionIds = new List<string>();
                    foreach (var ws in newList)
                    {
                        if (!ws.Enabled)
                        {
                            continue;
                        }

                        actionIds.Add(BuildButtonIdInternal(ws.Id));
                    }

                    ProviderChanged?.Invoke(this, ProviderChangedEventArgs.ActionsUpdated(Id, actionIds));
                }
                catch
                {
                }
            }

            return true;
        }

        private static bool HasChanged(List<WorkspaceRecord> oldList, IReadOnlyList<WorkspaceRecord> newList)
        {
            if (oldList.Count != newList.Count)
            {
                return true;
            }

            for (int i = 0; i < oldList.Count; i++)
            {
                var o = oldList[i];
                var n = newList[i];

                if (!string.Equals(o.Id, n.Id, StringComparison.Ordinal))
                {
                    return true;
                }

                if (!string.Equals(o.DisplayName ?? string.Empty, n.DisplayName ?? string.Empty, StringComparison.Ordinal))
                {
                    return true;
                }

                if (!string.Equals(o.TemplateDisplayName ?? string.Empty, n.TemplateDisplayName ?? string.Empty, StringComparison.Ordinal))
                {
                    return true;
                }

                if (!string.Equals(o.InstanceName ?? string.Empty, n.InstanceName ?? string.Empty, StringComparison.Ordinal))
                {
                    return true;
                }

                if (!string.Equals(o.IconSignature ?? string.Empty, n.IconSignature ?? string.Empty, StringComparison.Ordinal))
                {
                    return true;
                }

                if (o.Enabled != n.Enabled)
                {
                    return true;
                }

                if (o.LastLaunchedTime != n.LastLaunchedTime)
                {
                    return true;
                }
            }

            return false;
        }

        private async Task<IReadOnlyList<WorkspaceRecord>> GetWorkspacesAsync(CancellationToken cancellationToken)
        {
            if (_cacheLoaded)
            {
                lock (_cacheLock)
                {
                    return _cached;
                }
            }

            var list = await ReadWorkspacesFileAsync(cancellationToken).ConfigureAwait(false);
            lock (_cacheLock)
            {
                if (!_cacheLoaded)
                {
                    _cached = new List<WorkspaceRecord>(list);
                    _cacheLoaded = true;
                    _version = 1;
                }

                return _cached;
            }
        }

        internal async Task<WorkspaceDefinition> SnapshotAsync(string workspaceName, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, nameof(WorkspaceProvider));

            var workspace = await _workspacesService.SnapshotAsync(workspaceName, cancellationToken).ConfigureAwait(false);
            if (workspace != null)
            {
                try
                {
                    await _buttonStore.EnsureButtonAsync(workspace, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                }

                try
                {
                    await ReloadIfChangedAsync().ConfigureAwait(false);
                }
                catch
                {
                }
            }

            return workspace;
        }

        internal async Task<bool> DeleteWorkspaceAsync(string workspaceId, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, nameof(WorkspaceProvider));

            var normalizedWorkspaceId = workspaceId?.Trim();
            if (string.IsNullOrWhiteSpace(normalizedWorkspaceId))
            {
                return false;
            }

            bool definitionRemoved = false;
            bool buttonRemoved = false;
            Exception definitionError = null;
            Exception buttonError = null;

            try
            {
                definitionRemoved = await _definitionStore.DeleteWorkspaceAsync(normalizedWorkspaceId, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                definitionError = ex;
                AppLogger.LogWarning($"WorkspaceProvider: failed to delete workspace definition '{normalizedWorkspaceId}' - {ex.Message}");
            }

            try
            {
                buttonRemoved = await _buttonStore.RemoveWorkspaceButtonAsync(normalizedWorkspaceId, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                buttonError = ex;
                AppLogger.LogWarning($"WorkspaceProvider: failed to delete workspace button '{normalizedWorkspaceId}' - {ex.Message}");
            }

            if (!definitionRemoved && !buttonRemoved)
            {
                if (definitionError != null)
                {
                    throw definitionError;
                }

                if (buttonError != null)
                {
                    throw buttonError;
                }

                return false;
            }

            try
            {
                await ReloadIfChangedAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLogger.LogWarning($"WorkspaceProvider: failed to refresh workspace cache after delete '{normalizedWorkspaceId}' - {ex.Message}");
            }

            return true;
        }

        public string Id => "WorkspaceProvider";

        public Task<ProviderInfo> GetInfoAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(new ProviderInfo("Workspaces", "1.0"));
        }

        public async IAsyncEnumerable<ActionDescriptor> DiscoverAsync(ActionContext context, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var workspaces = await GetWorkspacesAsync(cancellationToken).ConfigureAwait(false);
            var order = 0d;
            foreach (var workspace in workspaces)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!workspace.Enabled)
                {
                    continue;
                }

                var descriptor = new ActionDescriptor
                {
                    Id = WorkspacePrefix + workspace.Id,
                    ProviderId = Id,
                    Title = workspace.DisplayName,
                    Subtitle = workspace.Id,
                    Kind = ActionKind.Launch,
                    GroupHint = "workspaces",
                    Order = order++,
                    Icon = new ActionIcon { Type = ActionIconType.Glyph, Value = "\uE7F4" },
                    CanExecute = true,
                };

                if (!string.IsNullOrWhiteSpace(workspace.DisplayName))
                {
                    descriptor.Keywords.Add(workspace.DisplayName);
                }

                if (!string.IsNullOrWhiteSpace(workspace.TemplateDisplayName))
                {
                    descriptor.Keywords.Add(workspace.TemplateDisplayName);
                }

                if (!string.IsNullOrWhiteSpace(workspace.InstanceName))
                {
                    descriptor.Keywords.Add(workspace.InstanceName);
                }

                descriptor.Keywords.Add(workspace.Id);
                yield return descriptor;
            }
        }

        public async Task<ButtonGroup> CreateGroupAsync(ActionContext context, CancellationToken cancellationToken)
        {
            var group = new ButtonGroup
            {
                Id = "workspaces",
                Name = "Workspaces",
                Description = "Saved workspace layouts",
                Layout = new ToolbarGroupLayout
                {
                    Style = ToolbarGroupLayoutStyle.Capsule,
                    Overflow = ToolbarGroupOverflowMode.Menu,
                    MaxInline = 8,
                },
            };

            var workspaces = await GetWorkspacesAsync(cancellationToken).ConfigureAwait(false);
            foreach (var workspace in workspaces)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!workspace.Enabled)
                {
                    continue;
                }

                var button = new ToolbarButton
                {
                    Id = BuildButtonIdInternal(workspace.Id),
                    Name = workspace.DisplayName,
                    Description = workspace.Id,
                    IconGlyph = "\uE7F4",
                    IconType = ToolbarIconType.Catalog,
                    Action = new ToolbarAction
                    {
                        Type = ToolbarActionType.Provider,
                        ProviderId = Id,
                        ProviderActionId = WorkspacePrefix + workspace.Id,
                    },
                };

                ApplyIcon(button, workspace.Icon);
                group.Buttons.Add(button);
            }

            return group;
        }

        private static string BuildButtonIdInternal(string workspaceId)
        {
            return string.IsNullOrWhiteSpace(workspaceId) ? string.Empty : $"workspace::{workspaceId}";
        }

        private static string ExtractWorkspaceId(string buttonId)
        {
            if (string.IsNullOrWhiteSpace(buttonId))
            {
                return string.Empty;
            }

            const string prefix = "workspace::";
            return buttonId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? buttonId.Substring(prefix.Length)
                : buttonId;
        }

        private static void ApplyIcon(ToolbarButton button, ProviderIcon icon)
        {
            if (button == null)
            {
                return;
            }

            if (icon == null)
            {
                if (string.IsNullOrWhiteSpace(button.IconGlyph))
                {
                    button.IconGlyph = "\uE7F4";
                }

                button.IconType = ToolbarIconType.Catalog;
                return;
            }

            switch (icon.Type)
            {
                case ProviderIconType.Image:
                    if (!string.IsNullOrWhiteSpace(icon.Path))
                    {
                        button.IconType = ToolbarIconType.Image;
                        button.IconPath = icon.Path;
                        button.IconGlyph = string.Empty;
                    }

                    break;

                case ProviderIconType.Catalog:
                    if (!string.IsNullOrWhiteSpace(icon.CatalogId) && IconCatalogService.TryGetById(icon.CatalogId, out var entry))
                    {
                        button.IconType = ToolbarIconType.Catalog;
                        button.IconPath = IconCatalogService.BuildCatalogPath(entry.Id);
                        button.IconGlyph = entry.Glyph ?? button.IconGlyph;
                    }
                    else if (!string.IsNullOrWhiteSpace(icon.Path))
                    {
                        button.IconType = ToolbarIconType.Catalog;
                        button.IconPath = icon.Path;
                    }

                    break;

                case ProviderIconType.Glyph:
                    if (!string.IsNullOrWhiteSpace(icon.Glyph))
                    {
                        button.IconType = ToolbarIconType.Catalog;
                        button.IconGlyph = icon.Glyph;
                        button.IconPath = string.Empty;
                    }

                    break;
            }

            if (button.IconType != ToolbarIconType.Image && string.IsNullOrWhiteSpace(button.IconGlyph))
            {
                button.IconGlyph = "\uE7F4";
            }
        }

        private static string BuildIconSignature(ProviderIcon icon)
        {
            if (icon == null)
            {
                return "none";
            }

            return string.Join("|", icon.Type.ToString(), icon.Path ?? string.Empty, icon.Glyph ?? string.Empty, icon.CatalogId ?? string.Empty);
        }

        public async Task<ActionResult> InvokeAsync(
            string actionId,
            JsonElement? args,
            ActionContext context,
            IProgress<ActionProgress> progress,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(actionId) || !actionId.StartsWith(WorkspacePrefix, StringComparison.OrdinalIgnoreCase))
            {
                return new ActionResult
                {
                    Ok = false,
                    Message = "Invalid workspace action id.",
                };
            }

            var workspaceId = actionId.Substring(WorkspacePrefix.Length).Trim();
            if (string.IsNullOrWhiteSpace(workspaceId))
            {
                return new ActionResult
                {
                    Ok = false,
                    Message = "Workspace identifier is empty.",
                };
            }

            try
            {
                var exitCode = await RunLauncherAsync(workspaceId, cancellationToken).ConfigureAwait(false);
                var ok = exitCode == 0;
                return new ActionResult
                {
                    Ok = ok,
                    Message = ok ? string.Empty : $"Launcher exit code {exitCode}.",
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new ActionResult
                {
                    Ok = false,
                    Message = ex.Message,
                };
            }
        }

        private async Task<int> RunLauncherAsync(string workspaceId, CancellationToken cancellationToken)
        {
            try
            {
                var success = await _workspacesService
                    .LaunchWorkspaceAsync(workspaceId, cancellationToken, progress: null, allowLaunchMissingWindows: false)
                    .ConfigureAwait(false);
                return success ? 0 : 1;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppLogger.LogWarning($"WorkspaceProvider: failed to launch workspace '{workspaceId}' - {ex.Message}");
                return 1;
            }
        }

        private async Task<IReadOnlyList<WorkspaceRecord>> ReadWorkspacesFileAsync(CancellationToken cancellationToken)
        {
            var definitions = await _definitionStore.LoadAllAsync(cancellationToken).ConfigureAwait(false);
            if (definitions.Count == 0)
            {
                return Array.Empty<WorkspaceRecord>();
            }

            var config = await _configStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            var records = new List<WorkspaceRecord>(definitions.Count);
            foreach (var workspace in definitions)
            {
                if (workspace == null)
                {
                    continue;
                }

                var id = workspace.Id?.Trim();
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                var hasApps = workspace.Applications != null && workspace.Applications.Count > 0;
                var hasTemplate = !string.IsNullOrWhiteSpace(workspace.TemplateName);
                if (!hasApps && !hasTemplate)
                {
                    // Hide legacy placeholder entries that cannot launch anything.
                    continue;
                }

                if (!WorkspaceRuntimeSession.IsCurrentSession(workspace.RuntimeSessionId))
                {
                    // Workspace instances are session-scoped and should not survive app restart.
                    continue;
                }

                var button = config.Buttons?.FirstOrDefault(b =>
                    string.Equals(b.WorkspaceId, id, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(b.Id, BuildButtonIdInternal(id), StringComparison.OrdinalIgnoreCase));

                var displayName = ResolveDisplayTitle(workspace, id);
                var templateDisplayName = ResolveTemplateDisplayName(workspace, displayName);
                var instanceName = ResolveInstanceName(workspace, displayName, id);
                var iconSignature = BuildIconSignature(button?.Icon);
                var enabled = button?.Enabled ?? true;
                var lastLaunchedTime = workspace.LastLaunchedTime ?? long.MinValue;
                var icon = button?.Icon ?? new ProviderIcon { Type = ProviderIconType.Glyph, Glyph = "\uE7F4" };

                records.Add(new WorkspaceRecord(
                    id,
                    displayName,
                    templateDisplayName,
                    instanceName,
                    lastLaunchedTime,
                    icon,
                    iconSignature,
                    enabled));
            }

            return records
                .OrderBy(record => record.TemplateDisplayName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(record => record.LastLaunchedTime)
                .ThenBy(record => record.InstanceName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string ResolveDisplayTitle(WorkspaceDefinition workspace, string fallbackId)
        {
            if (!string.IsNullOrWhiteSpace(workspace?.WorkspaceTitle))
            {
                return workspace.WorkspaceTitle.Trim();
            }

            if (!string.IsNullOrWhiteSpace(workspace?.Name))
            {
                return workspace.Name.Trim();
            }

            return fallbackId ?? string.Empty;
        }

        private static string ResolveTemplateDisplayName(WorkspaceDefinition workspace, string fallbackDisplayName)
        {
            if (workspace == null)
            {
                return fallbackDisplayName ?? string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(workspace.WorkspaceTitle) && !string.IsNullOrWhiteSpace(workspace.InstanceName))
            {
                var suffix = " · " + workspace.InstanceName.Trim();
                if (workspace.WorkspaceTitle.EndsWith(suffix, StringComparison.Ordinal))
                {
                    var prefix = workspace.WorkspaceTitle[..^suffix.Length].Trim();
                    if (!string.IsNullOrWhiteSpace(prefix))
                    {
                        return prefix;
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(workspace.TemplateName))
            {
                return workspace.TemplateName.Trim();
            }

            return fallbackDisplayName ?? string.Empty;
        }

        private static string ResolveInstanceName(WorkspaceDefinition workspace, string fallbackDisplayName, string fallbackId)
        {
            if (!string.IsNullOrWhiteSpace(workspace?.InstanceName))
            {
                return workspace.InstanceName.Trim();
            }

            if (!string.IsNullOrWhiteSpace(fallbackDisplayName))
            {
                return fallbackDisplayName;
            }

            return fallbackId ?? string.Empty;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            try
            {
                try
                {
                    _debounceTimer?.Stop();
                }
                catch
                {
                }

                try
                {
                    _debounceTimer?.Dispose();
                }
                catch
                {
                }

                _debounceTimer = null;

                DisposeWatcher(_configWatcher);
                _configWatcher = null;

                DisposeWatcher(_definitionsWatcher);
                _definitionsWatcher = null;

                try
                {
                    _workspacesService?.Dispose();
                }
                catch
                {
                }

                lock (_cacheLock)
                {
                    _cached.Clear();
                    _cacheLoaded = false;
                    _version = 0;
                }

                // Release any external subscribers
                WorkspacesChanged = null;
                ProviderChanged = null;
            }
            finally
            {
                GC.SuppressFinalize(this);
            }
        }

        private sealed record WorkspaceRecord(
            string Id,
            string DisplayName,
            string TemplateDisplayName,
            string InstanceName,
            long LastLaunchedTime,
            ProviderIcon Icon,
            string IconSignature,
            bool Enabled);
    }
}
