// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Microsoft.UI.Xaml;

namespace TopToolbar.Models;

public partial class ToolbarButton : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private string _id = Guid.NewGuid().ToString();

    public string Id
    {
        get => _id;
        set
        {
            if (_id != value)
            {
                _id = value;
                OnPropertyChanged();
            }
        }
    }

    private string _name = string.Empty;

        public string Name
        {
            get => _name;
            set
            {
                if (_name != value)
                {
                    _name = value ?? string.Empty;
                    OnPropertyChanged();

                    // Also notify DisplayName since it depends on Name
                    OnPropertyChanged(nameof(DisplayName));
                    OnPropertyChanged(nameof(ToolTipText));
                }
            }
        }

    private string _description = string.Empty;

        public string Description
        {
            get => _description;
            set
            {
                if (_description != value)
                {
                    _description = value ?? string.Empty;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasDescription));
                    OnPropertyChanged(nameof(ToolTipText));
                }
            }
        }

    private ToolbarIconType _iconType = ToolbarIconType.Catalog;

    public ToolbarIconType IconType
    {
        get => _iconType;
        set
        {
            var normalized = value == ToolbarIconType.Glyph ? ToolbarIconType.Catalog : value;
            if (_iconType != normalized)
            {
                _iconType = normalized;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IconTypeIndex));
                OnPropertyChanged(nameof(IsImageIcon));
                OnPropertyChanged(nameof(IsCatalogIcon));
            }
        }
    }

    public int IconTypeIndex
    {
        get => IconType == ToolbarIconType.Image ? 1 : 0;
        set
        {
            var newType = value == 1 ? ToolbarIconType.Image : ToolbarIconType.Catalog;
            IconType = newType;
        }
    }

    private string _iconGlyph = "\uE10F";

    public string IconGlyph
    {
        get => _iconGlyph;
        set
        {
            var normalized = NormalizeGlyph(value);
            if (_iconGlyph != normalized)
            {
                _iconGlyph = normalized;
                OnPropertyChanged();
            }
        }
    }

    private static string NormalizeGlyph(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        var s = input.Trim();

        try
        {
            if (s.StartsWith("\\u", StringComparison.OrdinalIgnoreCase) && s.Length >= 6)
            {
                s = s.Substring(2);
            }
            else if (s.StartsWith("U+", StringComparison.OrdinalIgnoreCase) && s.Length >= 5)
            {
                s = s.Substring(2);
            }
            else if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && s.Length >= 4)
            {
                s = s.Substring(2);
            }
            else if (s.StartsWith("&#x", StringComparison.OrdinalIgnoreCase) && s.EndsWith(';'))
            {
                s = s.Substring(3, s.Length - 4);
            }

            bool isHex = true;
            foreach (var c in s)
            {
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                {
                    isHex = false;
                    break;
                }
            }

            if (isHex && s.Length >= 4 && s.Length <= 6)
            {
                var codePoint = Convert.ToInt32(s, 16);
                return char.ConvertFromUtf32(codePoint);
            }
        }
        catch
        {
        }

        return input;
    }

    private string _iconPath = string.Empty;

    public string IconPath
    {
        get => _iconPath;
        set
        {
            if (_iconPath != value)
            {
                _iconPath = value ?? string.Empty;
                OnPropertyChanged();
            }
        }
    }

    private bool _isEnabled = true;

        public bool IsEnabled
        {
            get => _isEnabled;
            set
            {
                if (_isEnabled != value)
                {
                    _isEnabled = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsActionEnabled));
                }
            }
        }

    private bool _isIconCustomized;

    public bool IsIconCustomized
    {
        get => _isIconCustomized;
        set
        {
            if (_isIconCustomized != value)
            {
                _isIconCustomized = value;
                OnPropertyChanged();
            }
        }
    }

    [JsonIgnore]
    private bool _isExecuting;

    [JsonIgnore]
    private bool _isDimmed;

    [JsonIgnore]
    public bool IsDimmed
    {
        get => _isDimmed;
        set
        {
            if (_isDimmed != value)
            {
                _isDimmed = value;
                OnPropertyChanged();
            }
        }
    }

    [JsonIgnore]
    private bool _canExecuteWhenDimmed;

    [JsonIgnore]
    public bool CanExecuteWhenDimmed
    {
        get => _canExecuteWhenDimmed;
        set
        {
            if (_canExecuteWhenDimmed != value)
            {
                _canExecuteWhenDimmed = value;
                OnPropertyChanged();
            }
        }
    }

    [JsonIgnore]
        public bool IsExecuting
        {
            get => _isExecuting;
            set
            {
                if (_isExecuting != value)
                {
                    _isExecuting = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsActionEnabled));
                    OnPropertyChanged(nameof(IconOpacity));
                    OnPropertyChanged(nameof(ProgressVisibility));
                }
            }
        }

    [JsonIgnore]
    private double? _progressValue;

    [JsonIgnore]
    public double? ProgressValue
    {
        get => _progressValue;
        set
        {
            if (_progressValue != value)
            {
                _progressValue = value;
                OnPropertyChanged();
            }
        }
    }

    [JsonIgnore]
    private string _progressMessage = string.Empty;

    [JsonIgnore]
        public string ProgressMessage
        {
            get => _progressMessage;
            set
            {
                if (_progressMessage != value)
                {
                    _progressMessage = value ?? string.Empty;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(ToolTipText));
                }
            }
        }

    [JsonIgnore]
    private string _statusMessage = string.Empty;

    [JsonIgnore]
        public string StatusMessage
        {
            get => _statusMessage;
            set
            {
                if (_statusMessage != value)
                {
                    _statusMessage = value ?? string.Empty;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(ToolTipText));
                }
            }
        }

    [JsonIgnore]
    private double? _sortOrder;

    [JsonIgnore]
    public double? SortOrder
    {
        get => _sortOrder;
        set
        {
            if (_sortOrder != value)
            {
                _sortOrder = value;
                OnPropertyChanged();
            }
        }
    }

    [JsonIgnore]
    private bool _isExpanded;

    [JsonIgnore]
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded != value)
            {
                _isExpanded = value;
                OnPropertyChanged();
            }
        }
    }

    [JsonIgnore]
    public bool IsImageIcon => IconType == ToolbarIconType.Image;

    [JsonIgnore]
    public bool IsCatalogIcon => IconType == ToolbarIconType.Catalog;

    [JsonIgnore]
    public bool IsActionEnabled => IsEnabled && !IsExecuting;

    [JsonIgnore]
    public double IconOpacity => IsExecuting ? 0.4 : 1.0;

    [JsonIgnore]
    public Visibility ProgressVisibility => IsExecuting ? Visibility.Visible : Visibility.Collapsed;

    private ActionSurfaces _surfaces = ActionSurfaces.Bar;

    /// <summary>
    /// Surfaces this action is pinned to (top bar and/or radial ring). Defaults to <see cref="ActionSurfaces.Bar"/>.
    /// Persistence of user overrides is handled by <c>ActionPinStore</c>; this property is the live,
    /// observable value the UI binds to.
    /// </summary>
    public ActionSurfaces Surfaces
    {
        get => _surfaces;
        set
        {
            if (_surfaces != value)
            {
                _surfaces = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsPinnedToBar));
                OnPropertyChanged(nameof(IsPinnedToRing));
            }
        }
    }

    [JsonIgnore]
    public bool IsPinnedToBar => (Surfaces & ActionSurfaces.Bar) != 0;

    [JsonIgnore]
    public bool IsPinnedToRing => (Surfaces & ActionSurfaces.Ring) != 0;

    private ToolbarAction _action = new();

    public ToolbarAction Action
    {
        get => _action;
        set
        {
            if (!Equals(_action, value))
            {
                _action = value ?? new ToolbarAction();
                OnPropertyChanged();
            }
        }
    }

    public ToolbarButton Clone()
    {
        var clone = new ToolbarButton
        {
            Id = Id,
            Name = Name,
            Description = Description,
            IconType = IconType,
            IconGlyph = IconGlyph,
            IconPath = IconPath,
            IsEnabled = IsEnabled,
            IsIconCustomized = IsIconCustomized,
            CanExecuteWhenDimmed = CanExecuteWhenDimmed,
            Action = Action?.Clone() ?? new ToolbarAction(),
            Surfaces = Surfaces,
        };

        clone.SortOrder = SortOrder;
        return clone;
    }

    // Computed / convenience properties (not serialized)
    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? "New Button" : Name;

    [JsonIgnore]
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

    [JsonIgnore]
    public string ToolTipText => BuildToolTipText();

    private string BuildToolTipText()
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(Name))
        {
            parts.Add(Name);
        }

        if (!string.IsNullOrWhiteSpace(Description))
        {
            parts.Add(Description);
        }

        if (!string.IsNullOrWhiteSpace(ProgressMessage))
        {
            parts.Add(ProgressMessage);
        }
        else if (!string.IsNullOrWhiteSpace(StatusMessage))
        {
            parts.Add(StatusMessage);
        }

        if (parts.Count == 0)
        {
            return Name ?? string.Empty;
        }

        return string.Join(Environment.NewLine, parts);
    }
}
