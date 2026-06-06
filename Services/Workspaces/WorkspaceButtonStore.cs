// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TopToolbar.Models.Providers;
using TopToolbar.Services.Providers;

namespace TopToolbar.Services.Workspaces
{
    internal sealed class WorkspaceButtonStore
    {
        private readonly WorkspaceProviderConfigStore _configStore;
        private readonly WorkspaceDefinitionStore _definitionStore;

        public WorkspaceButtonStore(
            WorkspaceProviderConfigStore configStore = null,
            WorkspaceDefinitionStore definitionStore = null)
        {
            _configStore = configStore ?? new WorkspaceProviderConfigStore();
            _definitionStore = definitionStore ?? new WorkspaceDefinitionStore(null, _configStore);
        }

        public string FilePath => _configStore.FilePath;

        public Task<WorkspaceProviderConfig> LoadConfigAsync(CancellationToken cancellationToken = default)
        {
            return _configStore.LoadAsync(cancellationToken);
        }

        public async Task SaveButtonsAsync(
            IEnumerable<WorkspaceButtonConfig> buttons,
            CancellationToken cancellationToken = default)
        {
            var config = await _configStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            config.Buttons = buttons?.Where(b => b != null).ToList() ?? new List<WorkspaceButtonConfig>();
            if (await ShouldClearLegacyDataAsync(config, cancellationToken).ConfigureAwait(false))
            {
                config.Data = null;
            }

            await _configStore.SaveAsync(config, cancellationToken).ConfigureAwait(false);
        }

        public async Task EnsureButtonAsync(
            WorkspaceDefinition workspace,
            CancellationToken cancellationToken = default)
        {
            if (workspace == null)
            {
                return;
            }

            var config = await _configStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            EnsureWorkspaceButton(config, workspace);
            if (await ShouldClearLegacyDataAsync(config, cancellationToken).ConfigureAwait(false))
            {
                config.Data = null;
            }

            await _configStore.SaveAsync(config, cancellationToken).ConfigureAwait(false);
        }

        public async Task<string> SetWorkspaceIconAsync(
            WorkspaceDefinition workspace,
            string iconPath,
            CancellationToken cancellationToken = default)
        {
            if (workspace == null || string.IsNullOrWhiteSpace(iconPath))
            {
                return string.Empty;
            }

            var config = await _configStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            EnsureWorkspaceButton(config, workspace);

            var buttonId = BuildButtonId(workspace.Id);
            var button = config.Buttons.FirstOrDefault(b =>
                string.Equals(b.WorkspaceId, workspace.Id, StringComparison.OrdinalIgnoreCase)
                || string.Equals(b.Id, buttonId, StringComparison.OrdinalIgnoreCase));
            if (button == null)
            {
                return string.Empty;
            }

            var previousPath = button.Icon?.Type == ProviderIconType.Image
                ? button.Icon.Path ?? string.Empty
                : string.Empty;
            button.Icon = new ProviderIcon
            {
                Type = ProviderIconType.Image,
                Path = iconPath,
            };

            if (await ShouldClearLegacyDataAsync(config, cancellationToken).ConfigureAwait(false))
            {
                config.Data = null;
            }

            await _configStore.SaveAsync(config, cancellationToken).ConfigureAwait(false);
            return previousPath;
        }

        public async Task<bool> RemoveWorkspaceButtonAsync(
            string workspaceId,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(workspaceId))
            {
                return false;
            }

            var config = await _configStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (config.Buttons == null || config.Buttons.Count == 0)
            {
                return false;
            }

            var removed = config.Buttons.RemoveAll(button =>
                string.Equals(button.WorkspaceId, workspaceId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(button.Id, BuildButtonId(workspaceId), StringComparison.OrdinalIgnoreCase));

            if (removed == 0)
            {
                return false;
            }

            if (await ShouldClearLegacyDataAsync(config, cancellationToken).ConfigureAwait(false))
            {
                config.Data = null;
            }

            await _configStore.SaveAsync(config, cancellationToken).ConfigureAwait(false);
            return true;
        }

        private async Task<bool> ShouldClearLegacyDataAsync(
            WorkspaceProviderConfig config,
            CancellationToken cancellationToken)
        {
            if (config?.Data?.Workspaces == null || config.Data.Workspaces.Count == 0)
            {
                return true;
            }

            if (_definitionStore == null)
            {
                return false;
            }

            var definitions = await _definitionStore.LoadAllAsync(cancellationToken).ConfigureAwait(false);
            return definitions.Count > 0;
        }

        private static void EnsureWorkspaceButton(
            WorkspaceProviderConfig config,
            WorkspaceDefinition workspace
        )
        {
            config.Buttons ??= new List<WorkspaceButtonConfig>();

            var buttonId = BuildButtonId(workspace.Id);
            var button = config.Buttons.FirstOrDefault(b =>
                string.Equals(b.WorkspaceId, workspace.Id, StringComparison.OrdinalIgnoreCase)
                || string.Equals(b.Id, buttonId, StringComparison.OrdinalIgnoreCase)
            );

            if (button == null)
            {
                button = new WorkspaceButtonConfig
                {
                    Id = buttonId,
                    WorkspaceId = workspace.Id,
                    Name = string.IsNullOrWhiteSpace(workspace.Name)
                        ? workspace.Id
                        : workspace.Name,
                    Description = workspace.Id,
                    Enabled = true,
                    Icon = new ProviderIcon { Type = ProviderIconType.Glyph, Glyph = "\uE7F4" },
                };

                config.Buttons.Add(button);
            }
            else
            {
                button.WorkspaceId = workspace.Id;
                if (string.IsNullOrWhiteSpace(button.Name))
                {
                    button.Name = string.IsNullOrWhiteSpace(workspace.Name)
                        ? workspace.Id
                        : workspace.Name;
                }

                if (string.IsNullOrWhiteSpace(button.Description))
                {
                    button.Description = workspace.Id;
                }

                button.Enabled = true;
            }
        }

        private static string BuildButtonId(string workspaceId) =>
            string.IsNullOrWhiteSpace(workspaceId) ? string.Empty : $"workspace::{workspaceId}";
    }
}
