// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TopToolbar.Actions;
using TopToolbar.Models;
using Timer = System.Timers.Timer;
using Path = System.IO.Path;

namespace TopToolbar
{
    public sealed partial class ToolbarWindow
    {
        private static readonly string[] PreferredGroupProviderOrder =
        {
            "WorkspaceProvider",
        };

        private static readonly string[] PreferredDynamicGroupOrder =
        {
            "workspaces",
        };

        private void RegisterProviders()
        {
            try
            {
                // Initialize and register all built-in providers (workspace and MCP providers)
                _builtinProvider.Initialize();
                _builtinProvider.RegisterProvidersTo(_providerRuntime);
            }
            catch (Exception ex)
            {
                // Log error but continue
                try
                {
                    Debug.WriteLine($"ToolbarWindow: Failed to register built-in providers: {ex.Message}");
                }
                catch
                {
                    // Ignore logging errors
                }
            }
        }

        private void StartWatchingConfig()
        {
            try
            {
                var path = _configService.ConfigPath;
                var dir = Path.GetDirectoryName(path);
                var file = Path.GetFileName(path);
                if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(file))
                {
                    return;
                }

                _configWatcherDebounce = new Timer(250) { AutoReset = false };
                _configWatcherDebounce.Elapsed += async (s, e) =>
                {
                    await _vm.LoadAsync(this.DispatcherQueue);
                    await RunOnUiThreadAsync(SyncStaticGroupsIntoStore);
                    await RefreshDynamicProviderGroupsAsync(CancellationToken.None);

                    await RunOnUiThreadAsync(() =>
                    {
                        ApplyTheme(_vm.Theme);
                        _requireCtrlForTopBarTrigger = _vm.RequireCtrlForTopBarTrigger;
                        ApplyDisplayMode(_vm.DisplayMode);
                        if (_currentDisplayMode == ToolbarDisplayMode.TopBar)
                        {
                            ResizeToContent();
                        }

                        // Keep the leftmost groups visible after config/theme refreshes.
                        ToolbarScrollViewer?.ChangeView(0, null, null, disableAnimation: true);
                    });
                };

                _configWatcher = new FileSystemWatcher(dir, file)
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                    IncludeSubdirectories = false,
                    EnableRaisingEvents = true,
                };

                FileSystemEventHandler onChanged = (s, e) =>
                {
                    _configWatcherDebounce.Stop();
                    _configWatcherDebounce.Start();
                };
                RenamedEventHandler onRenamed = (s, e) =>
                {
                    _configWatcherDebounce.Stop();
                    _configWatcherDebounce.Start();
                };

                _configWatcher.Changed += onChanged;
                _configWatcher.Created += onChanged;
                _configWatcher.Deleted += onChanged;
                _configWatcher.Renamed += onRenamed;
            }
            catch
            {
                // ignore watcher failures
            }
        }

        // Sync static (config) groups into the central store so subsequent dynamic rebuilds retain them.
        private void SyncStaticGroupsIntoStore()
        {
            // Workspace-center mode intentionally excludes legacy static groups.
        }

        private static bool IsDynamicGroupId(string groupId)
        {
            if (string.IsNullOrWhiteSpace(groupId))
            {
                return false;
            }

            return string.Equals(groupId, "workspaces", StringComparison.OrdinalIgnoreCase);
        }

        private static int GetProviderOrder(string providerId)
        {
            for (var i = 0; i < PreferredGroupProviderOrder.Length; i++)
            {
                if (string.Equals(PreferredGroupProviderOrder[i], providerId, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            return int.MaxValue;
        }

        private IReadOnlyList<string> GetOrderedGroupProviderIds()
        {
            return _providerService.RegisteredGroupProviderIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .OrderBy(GetProviderOrder)
                .ThenBy(id => id, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private bool IsRegisteredGroupProvider(string providerId)
        {
            if (string.IsNullOrWhiteSpace(providerId))
            {
                return false;
            }

            foreach (var registeredId in _providerService.RegisteredGroupProviderIds)
            {
                if (string.Equals(registeredId, providerId, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private async System.Threading.Tasks.Task RefreshDynamicProviderGroupsAsync(CancellationToken cancellationToken)
        {
            foreach (var providerId in GetOrderedGroupProviderIds())
            {
                cancellationToken.ThrowIfCancellationRequested();
                await RefreshProviderGroupAsync(providerId, cancellationToken).ConfigureAwait(true);
            }

            await RunOnUiThreadAsync(NormalizeDynamicGroupOrder);
        }

        private async System.Threading.Tasks.Task RefreshProviderGroupAsync(string providerId, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(providerId))
            {
                return;
            }

            try
            {
                var context = new ActionContext();
                var group = await _providerService.CreateGroupAsync(providerId, context, cancellationToken).ConfigureAwait(true);
                if (group == null)
                {
                    return;
                }

                await RunOnUiThreadAsync(() =>
                {
                    _store.UpsertProviderGroup(group);
                });
            }
            catch
            {
            }
        }

        private void NormalizeDynamicGroupOrder()
        {
            try
            {
                var existing = new Dictionary<string, ButtonGroup>(StringComparer.OrdinalIgnoreCase);
                foreach (var id in PreferredDynamicGroupOrder)
                {
                    var group = _store.Groups.FirstOrDefault(g =>
                        g != null && string.Equals(g.Id, id, StringComparison.OrdinalIgnoreCase));
                    if (group != null)
                    {
                        existing[id] = group;
                    }
                }

                foreach (var id in PreferredDynamicGroupOrder)
                {
                    _store.RemoveGroup(id);
                }

                foreach (var id in PreferredDynamicGroupOrder)
                {
                    if (existing.TryGetValue(id, out var group))
                    {
                        _store.UpsertProviderGroup(group);
                    }
                }
            }
            catch
            {
            }
        }

        private Task RunOnUiThreadAsync(Action action)
        {
            if (action == null)
            {
                return Task.CompletedTask;
            }

            if (DispatcherQueue == null || DispatcherQueue.HasThreadAccess)
            {
                action();
                return Task.CompletedTask;
            }

            var tcs = new TaskCompletionSource<bool>();
            if (!DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    action();
                    tcs.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            }))
            {
                tcs.TrySetCanceled();
            }

            return tcs.Task;
        }

        // Compatibility shim for existing workspace-only call sites.
        private System.Threading.Tasks.Task RefreshWorkspaceGroupAsync()
        {
            return RefreshProviderGroupAsync("WorkspaceProvider", CancellationToken.None);
        }
    }
}
