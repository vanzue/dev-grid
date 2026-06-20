// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TopToolbar.Actions;
using TopToolbar.Models;
using TopToolbar.Providers;
using TopToolbar.Services;

namespace TopToolbar.ViewModels
{
    public partial class SettingsViewModel
    {
        /// <summary>Rows for the Bar &amp; Ring matrix: each provider action with its bar/ring toggles.</summary>
        public ObservableCollection<ActionSurfaceRow> ActionSurfaceRows { get; } = new();

        private async Task<List<ActionSurfaceRow>> BuildActionSurfaceRowsAsync(CancellationToken cancellationToken)
        {
            var rows = new List<ActionSurfaceRow>();
            if (_actionProviderService == null)
            {
                return rows;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var providerId in _actionProviderService.RegisteredGroupProviderIds)
            {
                ButtonGroup group;
                try
                {
                    group = await _actionProviderService
                        .CreateGroupAsync(providerId, new ActionContext(), cancellationToken)
                        .ConfigureAwait(false);
                }
                catch
                {
                    continue;
                }

                if (group?.Buttons == null)
                {
                    continue;
                }

                foreach (var button in group.Buttons)
                {
                    if (button?.Action == null)
                    {
                        continue;
                    }

                    var key = ActionPinStore.GetActionKey(button);
                    if (string.IsNullOrWhiteSpace(key) || !seen.Add(key))
                    {
                        continue;
                    }

                    var surfaces = ActionPinStore.Instance.Get(key) ?? button.Surfaces;
                    var label = string.IsNullOrWhiteSpace(button.Name) ? button.Description : button.Name;
                    rows.Add(new ActionSurfaceRow(
                        key,
                        providerId,
                        label,
                        button.IconGlyph,
                        surfaces,
                        OnActionSurfaceRowToggled));
                }
            }

            return rows
                .OrderBy(r => r.Label, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private void ReplaceActionSurfaceRows(IReadOnlyList<ActionSurfaceRow> rows)
        {
            ActionSurfaceRows.Clear();
            foreach (var row in rows ?? Array.Empty<ActionSurfaceRow>())
            {
                ActionSurfaceRows.Add(row);
            }
        }

        private void OnActionSurfaceRowToggled(ActionSurfaceRow row, bool isBar)
        {
            if (row == null)
            {
                return;
            }

            // Never let an action be removed from every surface (it would become unreachable).
            if (!row.IsOnBar && !row.IsOnRing)
            {
                row.RevertSilently(isBar, true);
                return;
            }

            var existing = ActionPinStore.Instance.Get(row.Key) ?? row.BaseSurfaces;
            var preserved = existing & ~(ActionSurfaces.Bar | ActionSurfaces.Ring);
            var next = preserved
                | (row.IsOnBar ? ActionSurfaces.Bar : ActionSurfaces.None)
                | (row.IsOnRing ? ActionSurfaces.Ring : ActionSurfaces.None);

            row.BaseSurfaces = next;
            ActionPinStore.Instance.Set(row.Key, next);

            if (!string.IsNullOrWhiteSpace(row.ProviderId))
            {
                _actionProviderService?.RaiseProviderChanged(row.ProviderId, ProviderChangeKind.GroupUpdated);
            }
        }
    }
}
