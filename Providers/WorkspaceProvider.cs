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
        private readonly WorkspaceThumbnailRenderer _thumbnailRenderer;

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
            _thumbnailRenderer = new WorkspaceThumbnailRenderer();

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

                if (!string.Equals(o.WorkspaceKind ?? string.Empty, n.WorkspaceKind ?? string.Empty, StringComparison.Ordinal))
                {
                    return true;
                }

                if (!string.Equals(o.ParentWorkspaceId ?? string.Empty, n.ParentWorkspaceId ?? string.Empty, StringComparison.Ordinal))
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
                string thumbnailPath = null;
                try
                {
                    thumbnailPath = _thumbnailRenderer.Render(workspace);
                }
                catch (Exception ex)
                {
                    AppLogger.LogWarning($"WorkspaceProvider: failed to render thumbnail for workspace '{workspace.Id}' - {ex.Message}");
                }

                try
                {
                    if (!string.IsNullOrWhiteSpace(thumbnailPath))
                    {
                        var previousPath = await _buttonStore.SetWorkspaceIconAsync(workspace, thumbnailPath, cancellationToken)
                            .ConfigureAwait(false);
                        DeletePreviousWorkspaceIcon(workspace.Id, previousPath, thumbnailPath);
                    }
                    else
                    {
                        await _buttonStore.EnsureButtonAsync(workspace, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.LogWarning($"WorkspaceProvider: failed to update workspace button for '{workspace.Id}' - {ex.Message}");
                    TryDeleteFile(thumbnailPath);
                    try
                    {
                        await _buttonStore.EnsureButtonAsync(workspace, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception fallbackEx)
                    {
                        AppLogger.LogWarning($"WorkspaceProvider: failed to ensure fallback workspace button for '{workspace.Id}' - {fallbackEx.Message}");
                    }
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
                WorkspaceStoragePaths.DeleteWorkspaceIcons(normalizedWorkspaceId);
            }
            catch (Exception ex)
            {
                AppLogger.LogWarning($"WorkspaceProvider: failed to delete workspace thumbnail(s) '{normalizedWorkspaceId}' - {ex.Message}");
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

        internal async Task<WorkspaceDefinition> RenameWorkspaceAsync(
            string workspaceId,
            string name,
            CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, nameof(WorkspaceProvider));

            var normalizedWorkspaceId = workspaceId?.Trim();
            var normalizedName = name?.Trim();
            if (string.IsNullOrWhiteSpace(normalizedWorkspaceId) ||
                string.IsNullOrWhiteSpace(normalizedName))
            {
                return null;
            }

            var existing = await _definitionStore.LoadByIdAsync(normalizedWorkspaceId, cancellationToken)
                .ConfigureAwait(false);
            if (existing == null)
            {
                return null;
            }

            var renamed = await _definitionStore.UpdateWorkspaceNameAsync(
                    normalizedWorkspaceId,
                    normalizedName,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!renamed)
            {
                return null;
            }

            await _buttonStore.RenameWorkspaceButtonAsync(
                    normalizedWorkspaceId,
                    normalizedName,
                    cancellationToken)
                .ConfigureAwait(false);

            try
            {
                await ReloadIfChangedAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLogger.LogWarning($"WorkspaceProvider: failed to refresh workspace cache after rename '{normalizedWorkspaceId}' - {ex.Message}");
            }

            existing.Name = normalizedName;
            return existing;
        }

        private static void DeletePreviousWorkspaceIcon(string workspaceId, string previousPath, string currentPath)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(previousPath)
                    && !string.Equals(previousPath, currentPath, StringComparison.OrdinalIgnoreCase))
                {
                    TryDeleteFile(previousPath);
                }

                WorkspaceStoragePaths.DeleteWorkspaceIcons(workspaceId, currentPath);
            }
            catch (Exception ex)
            {
                AppLogger.LogWarning($"WorkspaceProvider: failed to prune old workspace thumbnail(s) '{workspaceId}' - {ex.Message}");
            }
        }

        private static void TryDeleteFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return;
            }

            File.Delete(path);
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

                var isColdWorkspace = string.Equals(workspace.WorkspaceKind, WorkspaceKinds.Cold, StringComparison.OrdinalIgnoreCase);
                var button = new ToolbarButton
                {
                    Id = BuildButtonIdInternal(workspace.Id),
                    Name = workspace.DisplayName,
                    Description = isColdWorkspace
                        ? $"Cold · {workspace.Id}"
                        : $"Hot · {workspace.Id}",
                    IconGlyph = "\uE7F4",
                    IconType = ToolbarIconType.Catalog,
                    IsDimmed = isColdWorkspace,
                    CanExecuteWhenDimmed = isColdWorkspace,
                    Surfaces = ActionSurfaces.Bar | ActionSurfaces.Ring,
                    Action = new ToolbarAction
                    {
                        Type = ToolbarActionType.Provider,
                        ProviderId = Id,
                        ProviderActionId = WorkspacePrefix + workspace.Id,
                        ProviderArgumentsJson = BuildWorkspaceActionArgumentsJson(workspace),
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

        private static string BuildWorkspaceActionArgumentsJson(WorkspaceRecord workspace)
        {
            if (workspace == null)
            {
                return string.Empty;
            }

            try
            {
                var payload = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["workspaceId"] = workspace.Id ?? string.Empty,
                    ["workspaceKind"] = workspace.WorkspaceKind ?? string.Empty,
                    ["parentWorkspaceId"] = workspace.ParentWorkspaceId ?? string.Empty,
                };
                return JsonSerializer.Serialize(payload);
            }
            catch
            {
                return string.Empty;
            }
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
                return await RunLauncherAsync(workspaceId, cancellationToken).ConfigureAwait(false);
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

        private async Task<ActionResult> RunLauncherAsync(string workspaceId, CancellationToken cancellationToken)
        {
            try
            {
                var workspace = await _definitionStore.LoadByIdAsync(workspaceId, cancellationToken).ConfigureAwait(false);
                if (workspace == null)
                {
                    return new ActionResult
                    {
                        Ok = false,
                        Message = "Workspace was not found.",
                    };
                }

                WorkspaceSwitchDiagnostics diagnostics;
                if (WorkspaceKinds.IsCold(workspace))
                {
                    var hotInstance = await CreateHotInstanceFromColdAsync(workspace, cancellationToken).ConfigureAwait(false);
                    diagnostics = await _workspacesService
                        .LaunchWorkspaceDetailedAsync(hotInstance.Id, cancellationToken, allowLaunchMissingWindows: true)
                        .ConfigureAwait(false);
                }
                else
                {
                    diagnostics = await _workspacesService
                        .LaunchWorkspaceDetailedAsync(workspaceId, cancellationToken, allowLaunchMissingWindows: false)
                        .ConfigureAwait(false);
                }

                var message = BuildWorkspaceLaunchMessage(workspace, diagnostics);

                return new ActionResult
                {
                    Ok = diagnostics?.Ok == true,
                    Message = message,
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppLogger.LogWarning($"WorkspaceProvider: failed to launch workspace '{workspaceId}' - {ex.Message}");
                return new ActionResult
                {
                    Ok = false,
                    Message = ex.Message,
                };
            }
        }

        private static string BuildWorkspaceLaunchMessage(
            WorkspaceDefinition workspace,
            WorkspaceSwitchDiagnostics diagnostics)
        {
            if (diagnostics == null)
            {
                return string.Empty;
            }

            var errors = diagnostics.Errors?
                .Where(message => !string.IsNullOrWhiteSpace(message))
                .ToList() ?? new List<string>();
            var warnings = diagnostics.Warnings?
                .Where(message => !string.IsNullOrWhiteSpace(message))
                .ToList() ?? new List<string>();

            if (diagnostics.Ok)
            {
                // Hot workspace switching is a live-window operation. A missing bound window
                // can be expected over time, so keep those warnings in logs/diagnostics but
                // don't surface them as a yellow success state in the main UI. Real errors
                // (for example focus failures) should still bubble up.
                if (WorkspaceKinds.IsHot(workspace))
                {
                    return errors.Count > 0 ? string.Join("; ", errors) : string.Empty;
                }

                if (warnings.Count > 0)
                {
                    return string.Join("; ", warnings);
                }

                return errors.Count > 0 ? string.Join("; ", errors) : string.Empty;
            }

            if (errors.Count > 0)
            {
                return string.Join("; ", errors);
            }

            return warnings.Count > 0 ? string.Join("; ", warnings) : string.Empty;
        }

        private async Task<WorkspaceDefinition> CreateHotInstanceFromColdAsync(
            WorkspaceDefinition coldWorkspace,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(coldWorkspace);

            var now = DateTimeOffset.UtcNow;
            var hotWorkspace = CloneWorkspaceDefinition(coldWorkspace);
            hotWorkspace.Id = Guid.NewGuid().ToString("N");
            hotWorkspace.RuntimeSessionOnly = true;
            hotWorkspace.RuntimeSessionId = WorkspaceRuntimeSession.SessionId;
            hotWorkspace.WorkspaceKind = WorkspaceKinds.Hot;
            hotWorkspace.ParentWorkspaceId = coldWorkspace.Id?.Trim() ?? string.Empty;
            hotWorkspace.CreationTime = now.ToUnixTimeSeconds();
            hotWorkspace.LastLaunchedTime = null;
            hotWorkspace.MoveExistingWindows = true;
            hotWorkspace.Name = string.IsNullOrWhiteSpace(coldWorkspace.Name)
                ? "hot"
                : $"{coldWorkspace.Name.Trim()} · {now:HH:mm:ss}";

            if (hotWorkspace.Applications != null)
            {
                foreach (var app in hotWorkspace.Applications)
                {
                    if (app == null)
                    {
                        continue;
                    }

                    app.LaunchNewIfUnbound = true;
                }
            }

            await _definitionStore.SaveWorkspaceAsync(hotWorkspace, cancellationToken).ConfigureAwait(false);

            string thumbnailPath = null;
            try
            {
                thumbnailPath = _thumbnailRenderer.Render(hotWorkspace);
            }
            catch (Exception ex)
            {
                AppLogger.LogWarning($"WorkspaceProvider: failed to render thumbnail for launched hot workspace '{hotWorkspace.Id}' - {ex.Message}");
            }

            if (!string.IsNullOrWhiteSpace(thumbnailPath))
            {
                var previousPath = await _buttonStore.SetWorkspaceIconAsync(hotWorkspace, thumbnailPath, cancellationToken)
                    .ConfigureAwait(false);
                DeletePreviousWorkspaceIcon(hotWorkspace.Id, previousPath, thumbnailPath);
            }
            else
            {
                await _buttonStore.EnsureButtonAsync(hotWorkspace, cancellationToken).ConfigureAwait(false);
            }

            try
            {
                await ReloadIfChangedAsync().ConfigureAwait(false);
            }
            catch
            {
            }

            return hotWorkspace;
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
                if (!hasApps)
                {
                    // Hide entries that cannot launch anything.
                    continue;
                }

                var kind = WorkspaceKinds.Normalize(workspace.WorkspaceKind, workspace.RuntimeSessionOnly);
                var isHot = string.Equals(kind, WorkspaceKinds.Hot, StringComparison.OrdinalIgnoreCase);
                if (isHot && !WorkspaceRuntimeSession.IsCurrentSession(workspace.RuntimeSessionId))
                {
                    // Hot workspace instances are session-scoped and should not survive app restart.
                    continue;
                }

                var button = config.Buttons?.FirstOrDefault(b =>
                    string.Equals(b.WorkspaceId, id, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(b.Id, BuildButtonIdInternal(id), StringComparison.OrdinalIgnoreCase));

                var displayName = ResolveDisplayTitle(workspace, id);
                var iconSignature = BuildIconSignature(button?.Icon);
                var enabled = button?.Enabled ?? true;
                var lastLaunchedTime = workspace.LastLaunchedTime ?? long.MinValue;
                var icon = button?.Icon ?? new ProviderIcon { Type = ProviderIconType.Glyph, Glyph = "\uE7F4" };

                records.Add(new WorkspaceRecord(
                    id,
                    displayName,
                    lastLaunchedTime,
                    icon,
                    iconSignature,
                    enabled,
                    kind,
                    workspace.ParentWorkspaceId?.Trim() ?? string.Empty));
            }

            var coldRecords = records
                .Where(record => string.Equals(record.WorkspaceKind, WorkspaceKinds.Cold, StringComparison.OrdinalIgnoreCase))
                .OrderBy(record => record.DisplayName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ThenBy(record => record.Id ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var hotRecords = records
                .Where(record => string.Equals(record.WorkspaceKind, WorkspaceKinds.Hot, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var ordered = new List<WorkspaceRecord>(records.Count);
            var attachedHotIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var cold in coldRecords)
            {
                ordered.Add(cold);

                var attached = hotRecords
                    .Where(hot => string.Equals(hot.ParentWorkspaceId, cold.Id, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(hot => hot.LastLaunchedTime)
                    .ThenBy(hot => hot.DisplayName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(hot => hot.Id ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                foreach (var hot in attached)
                {
                    attachedHotIds.Add(hot.Id);
                    ordered.Add(hot);
                }
            }

            var standaloneHots = hotRecords
                .Where(hot => !attachedHotIds.Contains(hot.Id))
                .OrderByDescending(hot => hot.LastLaunchedTime)
                .ThenBy(hot => hot.DisplayName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ThenBy(hot => hot.Id ?? string.Empty, StringComparer.OrdinalIgnoreCase);

            ordered.AddRange(standaloneHots);
            return ordered;
        }

        private static string ResolveDisplayTitle(WorkspaceDefinition workspace, string fallbackId)
        {
            if (!string.IsNullOrWhiteSpace(workspace?.Name))
            {
                return workspace.Name.Trim();
            }

            return fallbackId ?? string.Empty;
        }

        internal async Task<bool> IsColdWorkspaceAsync(string workspaceId, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, nameof(WorkspaceProvider));
            if (string.IsNullOrWhiteSpace(workspaceId))
            {
                return false;
            }

            var workspace = await _definitionStore.LoadByIdAsync(workspaceId.Trim(), cancellationToken).ConfigureAwait(false);
            return WorkspaceKinds.IsCold(workspace);
        }

        internal async Task<bool> IsHotWorkspaceAsync(string workspaceId, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, nameof(WorkspaceProvider));
            if (string.IsNullOrWhiteSpace(workspaceId))
            {
                return false;
            }

            var workspace = await _definitionStore.LoadByIdAsync(workspaceId.Trim(), cancellationToken).ConfigureAwait(false);
            return WorkspaceKinds.IsHot(workspace);
        }

        internal int HideWorkspaceWindows(string workspaceId)
        {
            ObjectDisposedException.ThrowIf(_disposed, nameof(WorkspaceProvider));
            return _workspacesService.HideWorkspaceWindows(workspaceId);
        }

        internal int KillWorkspaceWindows(string workspaceId)
        {
            ObjectDisposedException.ThrowIf(_disposed, nameof(WorkspaceProvider));
            return _workspacesService.KillWorkspaceWindows(workspaceId);
        }

        internal async Task<WorkspaceDefinition> PersistHotWorkspaceAsync(
            string hotWorkspaceId,
            string coldWorkspaceName,
            CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, nameof(WorkspaceProvider));
            var normalizedId = hotWorkspaceId?.Trim();
            if (string.IsNullOrWhiteSpace(normalizedId))
            {
                return null;
            }

            var hotWorkspace = await _definitionStore.LoadByIdAsync(normalizedId, cancellationToken).ConfigureAwait(false);
            if (!WorkspaceKinds.IsHot(hotWorkspace))
            {
                return null;
            }

            var coldWorkspace = CloneWorkspaceDefinition(hotWorkspace);
            coldWorkspace.Id = Guid.NewGuid().ToString("N");
            coldWorkspace.RuntimeSessionOnly = false;
            coldWorkspace.RuntimeSessionId = string.Empty;
            coldWorkspace.WorkspaceKind = WorkspaceKinds.Cold;
            coldWorkspace.ParentWorkspaceId = string.Empty;
            coldWorkspace.LastLaunchedTime = null;
            coldWorkspace.CreationTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            coldWorkspace.Name = string.IsNullOrWhiteSpace(coldWorkspaceName)
                ? hotWorkspace.Name?.Trim() ?? $"cold-{coldWorkspace.Id[..6]}"
                : coldWorkspaceName.Trim();

            await _definitionStore.SaveWorkspaceAsync(coldWorkspace, cancellationToken).ConfigureAwait(false);

            hotWorkspace.ParentWorkspaceId = coldWorkspace.Id;
            await _definitionStore.SaveWorkspaceAsync(hotWorkspace, cancellationToken).ConfigureAwait(false);

            try
            {
                var thumbnailPath = _thumbnailRenderer.Render(coldWorkspace);
                if (!string.IsNullOrWhiteSpace(thumbnailPath))
                {
                    var previousPath = await _buttonStore.SetWorkspaceIconAsync(coldWorkspace, thumbnailPath, cancellationToken)
                        .ConfigureAwait(false);
                    DeletePreviousWorkspaceIcon(coldWorkspace.Id, previousPath, thumbnailPath);
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogWarning($"WorkspaceProvider: failed to render persisted cold workspace thumbnail '{coldWorkspace.Id}' - {ex.Message}");
            }

            await _buttonStore.EnsureButtonAsync(coldWorkspace, cancellationToken).ConfigureAwait(false);
            await _buttonStore.EnsureButtonAsync(hotWorkspace, cancellationToken).ConfigureAwait(false);
            await ReloadIfChangedAsync().ConfigureAwait(false);
            return coldWorkspace;
        }

        private static WorkspaceDefinition CloneWorkspaceDefinition(WorkspaceDefinition source)
        {
            if (source == null)
            {
                return new WorkspaceDefinition();
            }

            return new WorkspaceDefinition
            {
                Id = source.Id ?? string.Empty,
                Name = source.Name ?? string.Empty,
                FocusedApplicationId = source.FocusedApplicationId ?? string.Empty,
                CreationTime = source.CreationTime,
                LastLaunchedTime = source.LastLaunchedTime,
                IsShortcutNeeded = source.IsShortcutNeeded,
                MoveExistingWindows = source.MoveExistingWindows,
                RuntimeSessionOnly = source.RuntimeSessionOnly,
                RuntimeSessionId = source.RuntimeSessionId ?? string.Empty,
                WorkspaceKind = WorkspaceKinds.Normalize(source.WorkspaceKind, source.RuntimeSessionOnly),
                ParentWorkspaceId = source.ParentWorkspaceId ?? string.Empty,
                Monitors = source.Monitors?
                    .Select(monitor => new MonitorDefinition
                    {
                        Id = monitor?.Id ?? string.Empty,
                        InstanceId = monitor?.InstanceId ?? string.Empty,
                        Number = monitor?.Number ?? 0,
                        Dpi = monitor?.Dpi ?? 0,
                        DpiAwareRect = monitor?.DpiAwareRect == null
                            ? new MonitorDefinition.MonitorRect()
                            : new MonitorDefinition.MonitorRect
                            {
                                Left = monitor.DpiAwareRect.Left,
                                Top = monitor.DpiAwareRect.Top,
                                Width = monitor.DpiAwareRect.Width,
                                Height = monitor.DpiAwareRect.Height,
                            },
                        DpiUnawareRect = monitor?.DpiUnawareRect == null
                            ? new MonitorDefinition.MonitorRect()
                            : new MonitorDefinition.MonitorRect
                            {
                                Left = monitor.DpiUnawareRect.Left,
                                Top = monitor.DpiUnawareRect.Top,
                                Width = monitor.DpiUnawareRect.Width,
                                Height = monitor.DpiUnawareRect.Height,
                            },
                        DpiAwareWorkRect = monitor?.DpiAwareWorkRect == null
                            ? new MonitorDefinition.MonitorRect()
                            : new MonitorDefinition.MonitorRect
                            {
                                Left = monitor.DpiAwareWorkRect.Left,
                                Top = monitor.DpiAwareWorkRect.Top,
                                Width = monitor.DpiAwareWorkRect.Width,
                                Height = monitor.DpiAwareWorkRect.Height,
                            },
                        DpiUnawareWorkRect = monitor?.DpiUnawareWorkRect == null
                            ? new MonitorDefinition.MonitorRect()
                            : new MonitorDefinition.MonitorRect
                            {
                                Left = monitor.DpiUnawareWorkRect.Left,
                                Top = monitor.DpiUnawareWorkRect.Top,
                                Width = monitor.DpiUnawareWorkRect.Width,
                                Height = monitor.DpiUnawareWorkRect.Height,
                            },
                    })
                    .ToList() ?? new List<MonitorDefinition>(),
                Applications = source.Applications?
                    .Select(app => new ApplicationDefinition
                    {
                        Id = app?.Id ?? string.Empty,
                        Name = app?.Name ?? string.Empty,
                        Role = app?.Role ?? string.Empty,
                        Title = app?.Title ?? string.Empty,
                        Path = app?.Path ?? string.Empty,
                        PackageFullName = app?.PackageFullName ?? string.Empty,
                        AppUserModelId = app?.AppUserModelId ?? string.Empty,
                        PwaAppId = app?.PwaAppId ?? string.Empty,
                        CommandLineArguments = app?.CommandLineArguments ?? string.Empty,
                        WorkingDirectory = app?.WorkingDirectory ?? string.Empty,
                        IsElevated = app?.IsElevated ?? false,
                        CanLaunchElevated = app?.CanLaunchElevated ?? false,
                        LaunchNewIfUnbound = app?.LaunchNewIfUnbound ?? false,
                        Minimized = app?.Minimized ?? false,
                        Maximized = app?.Maximized ?? false,
                        MonitorIndex = app?.MonitorIndex ?? 0,
                        ZOrder = app?.ZOrder ?? 0,
                        Version = app?.Version ?? string.Empty,
                        Position = app?.Position == null
                            ? new ApplicationDefinition.ApplicationPosition()
                            : new ApplicationDefinition.ApplicationPosition
                            {
                                X = app.Position.X,
                                Y = app.Position.Y,
                                Width = app.Position.Width,
                                Height = app.Position.Height,
                            },
                    })
                    .ToList() ?? new List<ApplicationDefinition>(),
            };
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
            long LastLaunchedTime,
            ProviderIcon Icon,
            string IconSignature,
            bool Enabled,
            string WorkspaceKind,
            string ParentWorkspaceId);
    }
}
