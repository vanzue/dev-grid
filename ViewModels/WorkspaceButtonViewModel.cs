// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using TopToolbar.Models;
using TopToolbar.Models.Providers;
using TopToolbar.Services;
using TopToolbar.Services.Workspaces;

namespace TopToolbar.ViewModels
{
    public sealed class WorkspaceButtonViewModel : ObservableObject
    {
        public WorkspaceButtonViewModel(WorkspaceButtonConfig config, WorkspaceDefinition definition)
        {
            Config = config ?? throw new ArgumentNullException(nameof(config));
            Definition = definition ?? new WorkspaceDefinition { Id = config.WorkspaceId, Name = config.Name };

            Apps = new ObservableCollection<ApplicationDefinition>(Definition.Applications ?? new System.Collections.Generic.List<ApplicationDefinition>());
            Apps.CollectionChanged += OnAppsCollectionChanged;
            Definition.Applications = Apps.ToList();
            NotifyIconChanged();
        }

        public WorkspaceButtonConfig Config { get; }

        public WorkspaceDefinition Definition { get; }

        public ObservableCollection<ApplicationDefinition> Apps { get; }

        public string WorkspaceId => Definition.Id;

        public string DisplayTitle =>
            !string.IsNullOrWhiteSpace(Definition.WorkspaceTitle)
                ? Definition.WorkspaceTitle
                : (!string.IsNullOrWhiteSpace(Name) ? Name : WorkspaceId);

        public string TemplateName =>
            string.IsNullOrWhiteSpace(Definition.TemplateName) ? "-" : Definition.TemplateName;

        public string InstanceName =>
            string.IsNullOrWhiteSpace(Definition.InstanceName) ? "-" : Definition.InstanceName;

        public string WorkspaceTitle =>
            string.IsNullOrWhiteSpace(Definition.WorkspaceTitle) ? DisplayTitle : Definition.WorkspaceTitle;

        public string RepoRoot =>
            string.IsNullOrWhiteSpace(Definition.RepoRoot) ? "-" : Definition.RepoRoot;

        public string FocusPriorityDisplay =>
            Definition.FocusPriority == null || Definition.FocusPriority.Count == 0
                ? "-"
                : string.Join(", ", Definition.FocusPriority.Where(role => !string.IsNullOrWhiteSpace(role)));

        public string LastLaunchedDisplay
        {
            get
            {
                if (!Definition.LastLaunchedTime.HasValue || Definition.LastLaunchedTime.Value <= 0)
                {
                    return "Never";
                }

                try
                {
                    return DateTimeOffset
                        .FromUnixTimeSeconds(Definition.LastLaunchedTime.Value)
                        .ToLocalTime()
                        .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                }
                catch
                {
                    return "Never";
                }
            }
        }

        public string Name
        {
            get => Config.Name;
            set
            {
                if (!string.Equals(Config.Name, value, StringComparison.Ordinal))
                {
                    Config.Name = value ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        var trimmed = value.Trim();
                        Definition.Name = trimmed;
                        if (!string.IsNullOrWhiteSpace(Definition.WorkspaceTitle) || !string.IsNullOrWhiteSpace(Definition.TemplateName))
                        {
                            Definition.WorkspaceTitle = trimmed;
                        }
                    }

                    OnPropertyChanged();
                    OnPropertyChanged(nameof(DisplayTitle));
                    OnPropertyChanged(nameof(WorkspaceTitle));
                }
            }
        }

        public string Description
        {
            get => Config.Description;
            set
            {
                if (!string.Equals(Config.Description, value, StringComparison.Ordinal))
                {
                    Config.Description = value ?? string.Empty;
                    OnPropertyChanged();
                }
            }
        }

        public bool Enabled
        {
            get => Config.Enabled;
            set
            {
                if (Config.Enabled != value)
                {
                    Config.Enabled = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(DisplayOpacity));
                }
            }
        }

        public ProviderIcon Icon => Config.Icon ??= new ProviderIcon();

        public double DisplayOpacity => Config.Enabled ? 1.0 : 0.5;

        public bool IsGlyphIcon => Icon.Type != ProviderIconType.Image;

        public bool IsImageIcon => Icon.Type == ProviderIconType.Image && !string.IsNullOrWhiteSpace(Icon.Path);

        public bool HasApps => Apps.Count > 0;

        public bool IsEmpty => Apps.Count == 0;

        public string IconGlyph
        {
            get
            {
                var icon = Icon;
                return icon.Type switch
                {
                    ProviderIconType.Glyph => !string.IsNullOrWhiteSpace(icon.Glyph) ? icon.Glyph : "\uE7F4",
                    ProviderIconType.Catalog => ResolveCatalogGlyph(icon.CatalogId),
                    _ => "\uE7F4",
                };
            }
        }

        public string IconImagePath
        {
            get
            {
                var icon = Icon;
                return icon.Type switch
                {
                    ProviderIconType.Image => icon.Path ?? string.Empty,
                    ProviderIconType.Catalog when !string.IsNullOrWhiteSpace(icon.CatalogId) => IconCatalogService.BuildCatalogPath(icon.CatalogId),
                    _ => string.Empty,
                };
            }
        }

        public void SetCatalogIcon(string catalogId)
        {
            if (Icon == null)
            {
                Config.Icon = new ProviderIcon();
            }

            Icon.Type = ProviderIconType.Catalog;
            Icon.CatalogId = catalogId ?? string.Empty;
            Icon.Path = string.Empty;
            Icon.Glyph = string.Empty;
            NotifyIconChanged();
        }

        public void SetGlyph(string glyph)
        {
            if (Icon == null)
            {
                Config.Icon = new ProviderIcon();
            }

            Icon.Type = ProviderIconType.Glyph;
            Icon.Glyph = glyph ?? string.Empty;
            Icon.Path = string.Empty;
            Icon.CatalogId = string.Empty;
            NotifyIconChanged();
        }

        public void SetImage(string path)
        {
            if (Icon == null)
            {
                Config.Icon = new ProviderIcon();
            }

            Icon.Type = ProviderIconType.Image;
            Icon.Path = path ?? string.Empty;
            Icon.Glyph = string.Empty;
            Icon.CatalogId = string.Empty;
            NotifyIconChanged();
        }

        public void ResetToDefaultIcon()
        {
            if (Icon == null)
            {
                Config.Icon = new ProviderIcon();
            }

            // Use the workspace catalog icon
            Icon.Type = ProviderIconType.Catalog;
            Icon.Glyph = string.Empty;
            Icon.Path = string.Empty;
            Icon.CatalogId = "workspace";
            NotifyIconChanged();
        }

        public void RemoveApp(ApplicationDefinition app)
        {
            if (app == null)
            {
                return;
            }

            Apps.Remove(app);
        }

        private void OnAppsCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            Definition.Applications = Apps.ToList();
            OnPropertyChanged(nameof(Apps));
            OnPropertyChanged(nameof(HasApps));
            OnPropertyChanged(nameof(IsEmpty));
        }

        private static string ResolveCatalogGlyph(string catalogId)
        {
            if (!string.IsNullOrWhiteSpace(catalogId) && IconCatalogService.TryGetById(catalogId, out var entry))
            {
                if (!string.IsNullOrWhiteSpace(entry.Glyph))
                {
                    return entry.Glyph;
                }
            }

            return "\uE7F4";
        }

        private void NotifyIconChanged()
        {
            OnPropertyChanged(nameof(Icon));
            OnPropertyChanged(nameof(IconGlyph));
            OnPropertyChanged(nameof(IconImagePath));
            OnPropertyChanged(nameof(IsGlyphIcon));
            OnPropertyChanged(nameof(IsImageIcon));
        }

        public void NotifyMetadataChanged()
        {
            OnPropertyChanged(nameof(DisplayTitle));
            OnPropertyChanged(nameof(TemplateName));
            OnPropertyChanged(nameof(InstanceName));
            OnPropertyChanged(nameof(WorkspaceTitle));
            OnPropertyChanged(nameof(RepoRoot));
            OnPropertyChanged(nameof(FocusPriorityDisplay));
            OnPropertyChanged(nameof(LastLaunchedDisplay));
        }
    }
}
