// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;

namespace TopToolbar.Services
{
    /// <summary>
    /// Terminal-aware per-app state for a workspace launch, surfaced in the launch-status flyout.
    /// </summary>
    public enum WorkspaceLaunchItemState
    {
        Pending,
        Launching,
        Reused,
        Launched,
        Failed,
    }

    /// <summary>
    /// One line item in the launch-status flyout (one per workspace application).
    /// </summary>
    public sealed class WorkspaceLaunchStatusItem
    {
        public WorkspaceLaunchStatusItem(string appId, string displayName)
        {
            AppId = appId ?? string.Empty;
            DisplayName = displayName ?? string.Empty;
            State = WorkspaceLaunchItemState.Pending;
            Detail = string.Empty;
        }

        public string AppId { get; }

        public string DisplayName { get; }

        public WorkspaceLaunchItemState State { get; set; }

        public string Detail { get; set; }

        public bool IsTerminal =>
            State == WorkspaceLaunchItemState.Reused
            || State == WorkspaceLaunchItemState.Launched
            || State == WorkspaceLaunchItemState.Failed;
    }

    /// <summary>
    /// Shared, thread-safe hub that the workspace launcher (background threads) publishes per-app
    /// launch progress to, and the toast/notification UI subscribes to in order to render a
    /// launch-status flyout. UI-agnostic: the renderer owns placement and auto-dismiss timing.
    /// </summary>
    public sealed class WorkspaceLaunchStatusService
    {
        public static WorkspaceLaunchStatusService Instance { get; } = new();

        private readonly object _gate = new();
        private readonly List<WorkspaceLaunchStatusItem> _items = new();
        private string _title = string.Empty;
        private bool _active;
        private bool _completed;

        /// <summary>Raised (possibly off the UI thread) whenever the session state changes.</summary>
        public event EventHandler Changed;

        public bool IsActive
        {
            get
            {
                lock (_gate)
                {
                    return _active;
                }
            }
        }

        public bool IsCompleted
        {
            get
            {
                lock (_gate)
                {
                    return _completed;
                }
            }
        }

        public string Title
        {
            get
            {
                lock (_gate)
                {
                    return _title;
                }
            }
        }

        /// <summary>
        /// Returns a defensive copy of the current items so the UI can render without holding the lock.
        /// </summary>
        public IReadOnlyList<WorkspaceLaunchStatusItem> Snapshot()
        {
            lock (_gate)
            {
                var copy = new List<WorkspaceLaunchStatusItem>(_items.Count);
                foreach (var item in _items)
                {
                    copy.Add(new WorkspaceLaunchStatusItem(item.AppId, item.DisplayName)
                    {
                        State = item.State,
                        Detail = item.Detail,
                    });
                }

                return copy;
            }
        }

        /// <summary>True when a session is active and every item has reached a terminal state.</summary>
        public bool AllItemsTerminal()
        {
            lock (_gate)
            {
                if (!_active || _items.Count == 0)
                {
                    return false;
                }

                foreach (var item in _items)
                {
                    if (!item.IsTerminal)
                    {
                        return false;
                    }
                }

                return true;
            }
        }

        /// <summary>Begins a new launch session, replacing any previous one. All items start Pending.</summary>
        public void Begin(string title, IEnumerable<(string Id, string Name)> items)
        {
            lock (_gate)
            {
                _title = title ?? string.Empty;
                _items.Clear();
                if (items != null)
                {
                    foreach (var (id, name) in items)
                    {
                        _items.Add(new WorkspaceLaunchStatusItem(id, name));
                    }
                }

                _active = _items.Count > 0;
                _completed = false;
            }

            RaiseChanged();
        }

        /// <summary>Updates the state (and optional detail) of a single item by app id.</summary>
        public void Update(string appId, WorkspaceLaunchItemState state, string detail = null)
        {
            if (string.IsNullOrWhiteSpace(appId))
            {
                return;
            }

            var changed = false;
            lock (_gate)
            {
                if (!_active)
                {
                    return;
                }

                foreach (var item in _items)
                {
                    if (string.Equals(item.AppId, appId, StringComparison.OrdinalIgnoreCase))
                    {
                        item.State = state;
                        if (detail != null)
                        {
                            item.Detail = detail;
                        }

                        changed = true;
                        break;
                    }
                }
            }

            if (changed)
            {
                RaiseChanged();
            }
        }

        /// <summary>Marks the session as completed (launch pipeline finished). UI handles dismissal.</summary>
        public void Complete()
        {
            lock (_gate)
            {
                if (!_active)
                {
                    return;
                }

                _completed = true;
            }

            RaiseChanged();
        }

        /// <summary>Clears the session and hides the flyout.</summary>
        public void Clear()
        {
            var hadState = false;
            lock (_gate)
            {
                hadState = _active || _items.Count > 0;
                _active = false;
                _completed = false;
                _items.Clear();
                _title = string.Empty;
            }

            if (hadState)
            {
                RaiseChanged();
            }
        }

        private void RaiseChanged()
        {
            try
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }
            catch
            {
                // Status publishing must never break the launch pipeline.
            }
        }
    }
}
