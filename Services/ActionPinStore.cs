// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using TopToolbar.Logging;
using TopToolbar.Models;

namespace TopToolbar.Services
{
    /// <summary>
    /// Single source of truth for which surfaces (bar/ring) an action is pinned to. Persists user
    /// overrides keyed by a stable action identity so that pin choices survive provider regeneration
    /// (e.g. workspace buttons are rebuilt on every refresh, yet keep their pin state here).
    /// </summary>
    internal sealed class ActionPinStore
    {
        private static readonly Lazy<ActionPinStore> LazyInstance = new(() => new ActionPinStore());

        private readonly object _gate = new();
        private readonly Dictionary<string, ActionSurfaces> _overrides = new(StringComparer.OrdinalIgnoreCase);
        private bool _loaded;

        public static ActionPinStore Instance => LazyInstance.Value;

        private static string StorePath => Path.Combine(AppPaths.ConfigDirectory, "action-pins.json");

        /// <summary>
        /// Stable identity for an action so pin state can be persisted independently of the (possibly
        /// regenerated) button instance. Provider actions key on provider + action id; command-line
        /// actions key on the button id.
        /// </summary>
        public static string GetActionKey(ToolbarButton button)
        {
            if (button == null)
            {
                return string.Empty;
            }

            var action = button.Action;
            if (action != null &&
                action.Type == ToolbarActionType.Provider &&
                !string.IsNullOrWhiteSpace(action.ProviderId) &&
                !string.IsNullOrWhiteSpace(action.ProviderActionId))
            {
                return $"provider:{action.ProviderId}/{action.ProviderActionId}";
            }

            return $"button:{button.Id}";
        }

        public static string GetProviderActionKey(string providerId, string actionId)
        {
            if (string.IsNullOrWhiteSpace(providerId) || string.IsNullOrWhiteSpace(actionId))
            {
                return string.Empty;
            }

            return $"provider:{providerId.Trim()}/{actionId.Trim()}";
        }

        public ActionSurfaces? Get(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return null;
            }

            EnsureLoaded();
            lock (_gate)
            {
                return _overrides.TryGetValue(key, out var surfaces) ? surfaces : null;
            }
        }

        public void Set(string key, ActionSurfaces surfaces)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return;
            }

            EnsureLoaded();
            lock (_gate)
            {
                _overrides[key] = surfaces;
                Save_NoLock();
            }
        }

        /// <summary>
        /// Applies persisted pin overrides onto the buttons of a freshly created group, leaving the
        /// provider-assigned default in place when no override exists.
        /// </summary>
        public void Apply(ButtonGroup group)
        {
            if (group?.Buttons == null)
            {
                return;
            }

            foreach (var button in group.Buttons)
            {
                if (button == null)
                {
                    continue;
                }

                var stored = Get(GetActionKey(button));
                if (stored.HasValue)
                {
                    button.Surfaces = stored.Value;
                }
            }
        }

        private void EnsureLoaded()
        {
            lock (_gate)
            {
                if (_loaded)
                {
                    return;
                }

                _loaded = true;
                try
                {
                    if (File.Exists(StorePath))
                    {
                        var json = File.ReadAllText(StorePath);
                        var data = JsonSerializer.Deserialize<Dictionary<string, int>>(json);
                        if (data != null)
                        {
                            foreach (var pair in data)
                            {
                                if (!string.IsNullOrWhiteSpace(pair.Key))
                                {
                                    _overrides[pair.Key] = (ActionSurfaces)pair.Value;
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.LogWarning($"ActionPinStore: load failed - {ex.Message}");
                }
            }
        }

        private void Save_NoLock()
        {
            try
            {
                var dir = Path.GetDirectoryName(StorePath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                var data = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var pair in _overrides)
                {
                    data[pair.Key] = (int)pair.Value;
                }

                var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(StorePath, json);
            }
            catch (Exception ex)
            {
                AppLogger.LogWarning($"ActionPinStore: save failed - {ex.Message}");
            }
        }
    }
}
