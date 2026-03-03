// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using TopToolbar.Models;
using TopToolbar.Models.Providers;
using TopToolbar.Serialization;
using TopToolbar.Services;
using TopToolbar.Services.Providers;
using TopToolbar.Services.Workspaces;

namespace TopToolbar.ViewModels
{
    public partial class SettingsViewModel
    {
        private readonly WorkspaceProviderConfigStore _workspaceConfigStore = new();
        private readonly WorkspaceDefinitionStore _workspaceDefinitionStore;
        private bool _suppressWorkspaceSave;

        public ObservableCollection<WorkspaceButtonViewModel> WorkspaceButtons { get; } = new();

        private WorkspaceButtonViewModel _selectedWorkspace;

        public WorkspaceButtonViewModel SelectedWorkspace
        {
            get => _selectedWorkspace;
            set
            {
                if (!ReferenceEquals(_selectedWorkspace, value))
                {
                    _selectedWorkspace = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasSelectedWorkspace));
                    OnPropertyChanged(nameof(IsWorkspaceSelected));

                    if (value != null)
                    {
                        if (IsGeneralSelected)
                        {
                            IsGeneralSelected = false;
                        }
                        if (IsTemplatesSelected)
                        {
                            IsTemplatesSelected = false;
                        }

                        if (SelectedGroup != null)
                        {
                            SelectedGroup = null;
                        }
                    }
                }
            }
        }

        public bool HasSelectedWorkspace => SelectedWorkspace != null;

        public bool IsWorkspaceSelected => !IsGeneralSelected && !IsTemplatesSelected && SelectedWorkspace != null && SelectedGroup == null;

        private void WorkspaceButtons_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems != null)
            {
                foreach (var item in e.NewItems.Cast<WorkspaceButtonViewModel>())
                {
                    HookWorkspaceButton(item);
                }
            }

            if (e.OldItems != null)
            {
                foreach (var item in e.OldItems.Cast<WorkspaceButtonViewModel>())
                {
                    UnhookWorkspaceButton(item);
                }
            }

            if (_suppressWorkspaceSave)
            {
                return;
            }

            ScheduleSave();
        }

        private void LoadWorkspaceButtons(
            WorkspaceProviderConfig config,
            System.Collections.Generic.IReadOnlyList<WorkspaceDefinition> definitions)
        {
            var selectedId = SelectedWorkspace?.WorkspaceId;

            foreach (var existing in WorkspaceButtons.ToList())
            {
                UnhookWorkspaceButton(existing);
            }

            WorkspaceButtons.Clear();

            if (config == null)
            {
                SelectedWorkspace = null;
                return;
            }

            config.Buttons ??= new System.Collections.Generic.List<WorkspaceButtonConfig>();

            var definitionLookup = (definitions ?? System.Array.Empty<WorkspaceDefinition>())
                .Where(ws => ws != null && !string.IsNullOrWhiteSpace(ws.Id))
                .ToDictionary(ws => ws.Id.Trim(), ws => ws, StringComparer.OrdinalIgnoreCase);

            var buttonLookup = (config.Buttons ?? new System.Collections.Generic.List<WorkspaceButtonConfig>())
                .Where(button => button != null)
                .Select(button => new
                {
                    Id = !string.IsNullOrWhiteSpace(button.WorkspaceId) ? button.WorkspaceId.Trim() : ExtractWorkspaceId(button.Id),
                    Button = button,
                })
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Id))
                .GroupBy(entry => entry.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().Button, StringComparer.OrdinalIgnoreCase);

            var allWorkspaceIds = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var id in definitionLookup.Keys)
            {
                allWorkspaceIds.Add(id);
            }

            foreach (var id in buttonLookup.Keys)
            {
                allWorkspaceIds.Add(id);
            }

            var orderedWorkspaceIds = allWorkspaceIds
                .OrderBy(id => GetTemplateDisplayName(definitionLookup.TryGetValue(id, out var definition) ? definition : null), StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(id => definitionLookup.TryGetValue(id, out var definition) ? definition?.LastLaunchedTime ?? long.MinValue : long.MinValue)
                .ThenBy(id => GetInstanceSortName(definitionLookup.TryGetValue(id, out var definition) ? definition : null, id), StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var workspaceId in orderedWorkspaceIds)
            {
                if (!definitionLookup.TryGetValue(workspaceId, out var definition))
                {
                    definition = new WorkspaceDefinition
                    {
                        Id = workspaceId,
                        Name = workspaceId,
                        CreationTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        Applications = new System.Collections.Generic.List<ApplicationDefinition>(),
                        Monitors = new System.Collections.Generic.List<MonitorDefinition>(),
                    };
                    definitionLookup[workspaceId] = definition;
                }

                if (!buttonLookup.TryGetValue(workspaceId, out var buttonConfig))
                {
                    buttonConfig = new WorkspaceButtonConfig
                    {
                        Id = BuildWorkspaceButtonId(workspaceId),
                        WorkspaceId = workspaceId,
                        Name = GetWorkspaceDisplayTitle(definition, workspaceId),
                        Description = string.Empty,
                        Enabled = true,
                        Icon = new ProviderIcon { Type = ProviderIconType.Glyph, Glyph = "\uE7F4" },
                    };
                }
                else
                {
                    buttonConfig.WorkspaceId = workspaceId;
                    if (string.IsNullOrWhiteSpace(buttonConfig.Name))
                    {
                        buttonConfig.Name = GetWorkspaceDisplayTitle(definition, workspaceId);
                    }
                }

                var viewModel = new WorkspaceButtonViewModel(buttonConfig, definition);
                viewModel.NotifyMetadataChanged();
                WorkspaceButtons.Add(viewModel);
            }

            SelectedWorkspace = !string.IsNullOrWhiteSpace(selectedId)
                ? WorkspaceButtons.FirstOrDefault(ws =>
                    string.Equals(ws.WorkspaceId, selectedId, StringComparison.OrdinalIgnoreCase))
                : null;
        }

        private async Task SaveWorkspaceConfigAsync()
        {
            var config = new WorkspaceProviderConfig
            {
                SchemaVersion = 1,
                ProviderId = "WorkspaceProvider",
                DisplayName = "Workspaces",
                Description = "Snapshot and restore desktop layouts",
                Author = "Microsoft",
                Version = "1.0.0",
                Enabled = true,
                Buttons = new System.Collections.Generic.List<WorkspaceButtonConfig>(),
            };

            var definitions = new System.Collections.Generic.List<WorkspaceDefinition>();

            for (var i = 0; i < WorkspaceButtons.Count; i++)
            {
                var workspace = WorkspaceButtons[i];
                var buttonConfig = new WorkspaceButtonConfig
                {
                    Id = string.IsNullOrWhiteSpace(workspace.Config.Id) ? BuildWorkspaceButtonId(workspace.WorkspaceId) : workspace.Config.Id,
                    WorkspaceId = workspace.WorkspaceId,
                    Name = workspace.DisplayTitle ?? string.Empty,
                    Description = workspace.Description ?? string.Empty,
                    Enabled = workspace.Enabled,
                    SortOrder = i + 1,
                    Icon = CloneIcon(workspace.Icon),
                };

                config.Buttons.Add(buttonConfig);

                var definitionClone = DeepClone(workspace.Definition) ?? new WorkspaceDefinition();
                definitionClone.Id = workspace.WorkspaceId;
                definitionClone.Name = workspace.Definition.Name ?? workspace.WorkspaceId;
                definitionClone.Applications = workspace.Apps
                    .Select(DeepClone)
                    .Where(app => app != null)
                    .ToList();
                definitionClone.Monitors = workspace.Definition.Monitors != null
                    ? workspace.Definition.Monitors.Select(DeepClone).Where(m => m != null).ToList()
                    : new System.Collections.Generic.List<MonitorDefinition>();

                definitions.Add(definitionClone);
            }

            await _workspaceDefinitionStore.SaveAllAsync(definitions, System.Threading.CancellationToken.None);
            await _workspaceConfigStore.SaveAsync(config);
        }

        public async Task RefreshWorkspacesAsync()
        {
            var workspaceConfig = await _workspaceConfigStore.LoadAsync();
            var workspaceDefinitions = await _workspaceDefinitionStore.LoadAllAsync(System.Threading.CancellationToken.None);

            void Apply()
            {
                _suppressWorkspaceSave = true;
                try
                {
                    LoadWorkspaceButtons(workspaceConfig, workspaceDefinitions);
                }
                finally
                {
                    _suppressWorkspaceSave = false;
                }
            }

            await RunOnUiThreadAsync(Apply).ConfigureAwait(false);
        }

        private void HookWorkspaceButton(WorkspaceButtonViewModel workspace)
        {
            if (workspace == null)
            {
                return;
            }

            workspace.PropertyChanged += Workspace_PropertyChanged;
            workspace.Apps.CollectionChanged += WorkspaceApps_CollectionChanged;
            foreach (var app in workspace.Apps)
            {
                HookWorkspaceApp(app);
            }
            workspace.Definition.Applications = workspace.Apps.ToList();
        }

        private void UnhookWorkspaceButton(WorkspaceButtonViewModel workspace)
        {
            if (workspace == null)
            {
                return;
            }

            workspace.PropertyChanged -= Workspace_PropertyChanged;
            workspace.Apps.CollectionChanged -= WorkspaceApps_CollectionChanged;
            foreach (var app in workspace.Apps)
            {
                UnhookWorkspaceApp(app);
            }
        }

        private void Workspace_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (_suppressWorkspaceSave)
            {
                return;
            }

            ScheduleSave();
        }

        private void WorkspaceApps_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems != null)
            {
                foreach (var app in e.NewItems.Cast<ApplicationDefinition>())
                {
                    HookWorkspaceApp(app);
                }
            }

            if (e.OldItems != null)
            {
                foreach (var app in e.OldItems.Cast<ApplicationDefinition>())
                {
                    UnhookWorkspaceApp(app);
                }
            }

            if (_suppressWorkspaceSave)
            {
                return;
            }

            ScheduleSave();
        }

        private void HookWorkspaceApp(ApplicationDefinition app)
        {
            if (app == null)
            {
                return;
            }

            app.PropertyChanged += WorkspaceApp_PropertyChanged;
        }

        private void UnhookWorkspaceApp(ApplicationDefinition app)
        {
            if (app == null)
            {
                return;
            }

            app.PropertyChanged -= WorkspaceApp_PropertyChanged;
        }

        private void WorkspaceApp_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (_suppressWorkspaceSave)
            {
                return;
            }

            if (string.Equals(e.PropertyName, nameof(ApplicationDefinition.IsExpanded), StringComparison.Ordinal)
                || string.Equals(e.PropertyName, nameof(ApplicationDefinition.DisplayName), StringComparison.Ordinal))
            {
                return;
            }

            ScheduleSave();
        }

        public ApplicationDefinition AddWorkspaceApp(WorkspaceButtonViewModel workspace)
        {
            if (workspace == null)
            {
                return null;
            }

            var app = new ApplicationDefinition
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = "New application",
                IsExpanded = true,
            };

            workspace.Apps.Add(app);
            ScheduleSave();
            return app;
        }

        public void RemoveWorkspace(WorkspaceButtonViewModel workspace)
        {
            if (workspace == null)
            {
                return;
            }

            WorkspaceButtons.Remove(workspace);
            if (ReferenceEquals(SelectedWorkspace, workspace))
            {
                SelectedWorkspace = WorkspaceButtons.FirstOrDefault();
            }

            ScheduleSave();
        }

        public void RemoveWorkspaceApp(WorkspaceButtonViewModel workspace, ApplicationDefinition app)
        {
            if (workspace == null || app == null)
            {
                return;
            }

            workspace.RemoveApp(app);
            ScheduleSave();
        }

        public bool TrySetWorkspaceCatalogIcon(WorkspaceButtonViewModel workspace, string catalogId)
        {
            if (workspace == null || string.IsNullOrWhiteSpace(catalogId))
            {
                return false;
            }

            if (IconCatalogService.TryGetById(catalogId, out var entry))
            {
                workspace.SetCatalogIcon(entry.Id);
                ScheduleSave();
                return true;
            }

            return false;
        }

        public bool TrySetWorkspaceGlyphIcon(WorkspaceButtonViewModel workspace, string glyph)
        {
            if (workspace == null)
            {
                return false;
            }

            workspace.SetGlyph(glyph);
            ScheduleSave();
            return true;
        }

        public Task<bool> TrySetWorkspaceImageIconFromFileAsync(WorkspaceButtonViewModel workspace, string sourcePath)
        {
            if (workspace == null || string.IsNullOrWhiteSpace(sourcePath))
            {
                return Task.FromResult(false);
            }

            var targetPath = CopyIconAsset(workspace.WorkspaceId, sourcePath);
            workspace.SetImage(targetPath);
            ScheduleSave();
            return Task.FromResult(true);
        }

        public void ResetWorkspaceIcon(WorkspaceButtonViewModel workspace)
        {
            if (workspace == null)
            {
                return;
            }

            workspace.ResetToDefaultIcon();
            ScheduleSave();
        }

        private static ProviderIcon CloneIcon(ProviderIcon icon)
        {
            if (icon == null)
            {
                return new ProviderIcon { Type = ProviderIconType.Glyph, Glyph = "\uE7F4" };
            }

            return new ProviderIcon
            {
                Type = icon.Type,
                Path = icon.Path ?? string.Empty,
                Glyph = icon.Glyph ?? string.Empty,
                CatalogId = icon.CatalogId ?? string.Empty,
            };
        }

        private static T DeepClone<T>(T value)
        {
            if (value == null)
            {
                return default;
            }

            // AOT-compatible deep clone for known types
            if (value is WorkspaceDefinition wd)
            {
                return (T)(object)JsonSerializer.Deserialize(
                    JsonSerializer.Serialize(wd, DeepCloneJsonContext.Default.WorkspaceDefinition),
                    DeepCloneJsonContext.Default.WorkspaceDefinition);
            }

            if (value is ApplicationDefinition ad)
            {
                return (T)(object)JsonSerializer.Deserialize(
                    JsonSerializer.Serialize(ad, DeepCloneJsonContext.Default.ApplicationDefinition),
                    DeepCloneJsonContext.Default.ApplicationDefinition);
            }

            if (value is MonitorDefinition md)
            {
                return (T)(object)JsonSerializer.Deserialize(
                    JsonSerializer.Serialize(md, DeepCloneJsonContext.Default.MonitorDefinition),
                    DeepCloneJsonContext.Default.MonitorDefinition);
            }

            // Fallback for other types (will not work with AOT but keeps existing behavior)
            throw new NotSupportedException($"DeepClone does not support type {typeof(T).Name} in AOT mode.");
        }

        private static string BuildWorkspaceButtonId(string workspaceId)
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

        private static string GetWorkspaceDisplayTitle(WorkspaceDefinition definition, string fallbackId)
        {
            if (!string.IsNullOrWhiteSpace(definition?.WorkspaceTitle))
            {
                return definition.WorkspaceTitle.Trim();
            }

            if (!string.IsNullOrWhiteSpace(definition?.Name))
            {
                return definition.Name.Trim();
            }

            return fallbackId ?? string.Empty;
        }

        private static string GetTemplateDisplayName(WorkspaceDefinition definition)
        {
            if (definition == null)
            {
                return "Workspace";
            }

            if (!string.IsNullOrWhiteSpace(definition.WorkspaceTitle) && !string.IsNullOrWhiteSpace(definition.InstanceName))
            {
                var suffix = " · " + definition.InstanceName.Trim();
                if (definition.WorkspaceTitle.EndsWith(suffix, StringComparison.Ordinal))
                {
                    var prefix = definition.WorkspaceTitle[..^suffix.Length].Trim();
                    if (!string.IsNullOrWhiteSpace(prefix))
                    {
                        return prefix;
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(definition.TemplateName))
            {
                return definition.TemplateName.Trim();
            }

            if (!string.IsNullOrWhiteSpace(definition.Name))
            {
                return definition.Name.Trim();
            }

            return "Workspace";
        }

        private static string GetInstanceSortName(WorkspaceDefinition definition, string workspaceId)
        {
            if (!string.IsNullOrWhiteSpace(definition?.InstanceName))
            {
                return definition.InstanceName.Trim();
            }

            if (!string.IsNullOrWhiteSpace(definition?.WorkspaceTitle))
            {
                return definition.WorkspaceTitle.Trim();
            }

            if (!string.IsNullOrWhiteSpace(definition?.Name))
            {
                return definition.Name.Trim();
            }

            return workspaceId ?? string.Empty;
        }
    }
}
