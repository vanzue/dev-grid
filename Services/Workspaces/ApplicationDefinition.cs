// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace TopToolbar.Services.Workspaces
{
    public sealed class ApplicationDefinition : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string name = null)
        {
            if (EqualityComparer<T>.Default.Equals(storage, value))
            {
                return false;
            }

            storage = value;
            OnPropertyChanged(name);
            return true;
        }

        private string _id = string.Empty;

        [JsonPropertyName("id")]
        public string Id
        {
            get => _id;
            set => SetProperty(ref _id, value ?? string.Empty);
        }

        private string _name = string.Empty;

        [JsonPropertyName("application")]
        public string Name
        {
            get => _name;
            set
            {
                if (SetProperty(ref _name, value ?? string.Empty))
                {
                    OnPropertyChanged(nameof(DisplayName));
                }
            }
        }

        private string _role = string.Empty;

        [JsonPropertyName("role")]
        public string Role
        {
            get => _role;
            set => SetProperty(ref _role, value ?? string.Empty);
        }

        private string _title = string.Empty;

        [JsonPropertyName("title")]
        public string Title
        {
            get => _title;
            set
            {
                if (SetProperty(ref _title, value ?? string.Empty))
                {
                    OnPropertyChanged(nameof(DisplayName));
                }
            }
        }

        private string _path = string.Empty;

        [JsonPropertyName("application-path")]
        public string Path
        {
            get => _path;
            set
            {
                if (SetProperty(ref _path, value ?? string.Empty))
                {
                    OnPropertyChanged(nameof(DisplayName));
                }
            }
        }

        private string _packageFullName = string.Empty;

        [JsonPropertyName("package-full-name")]
        public string PackageFullName
        {
            get => _packageFullName;
            set
            {
                if (SetProperty(ref _packageFullName, value ?? string.Empty))
                {
                    OnPropertyChanged(nameof(DisplayName));
                }
            }
        }

        private string _appUserModelId = string.Empty;

        [JsonPropertyName("app-user-model-id")]
        public string AppUserModelId
        {
            get => _appUserModelId;
            set
            {
                if (SetProperty(ref _appUserModelId, value ?? string.Empty))
                {
                    OnPropertyChanged(nameof(DisplayName));
                }
            }
        }

        private string _pwaAppId = string.Empty;

        [JsonPropertyName("pwa-app-id")]
        public string PwaAppId
        {
            get => _pwaAppId;
            set
            {
                if (SetProperty(ref _pwaAppId, value ?? string.Empty))
                {
                    OnPropertyChanged(nameof(DisplayName));
                }
            }
        }

        private string _commandLineArguments = string.Empty;

        [JsonPropertyName("command-line-arguments")]
        public string CommandLineArguments
        {
            get => _commandLineArguments;
            set => SetProperty(ref _commandLineArguments, value ?? string.Empty);
        }

        private string _workingDirectory = string.Empty;

        [JsonPropertyName("working-directory")]
        public string WorkingDirectory
        {
            get => _workingDirectory;
            set => SetProperty(ref _workingDirectory, value ?? string.Empty);
        }

        private bool _isElevated;

        [JsonPropertyName("is-elevated")]
        public bool IsElevated
        {
            get => _isElevated;
            set => SetProperty(ref _isElevated, value);
        }

        private bool _canLaunchElevated;

        [JsonPropertyName("can-launch-elevated")]
        public bool CanLaunchElevated
        {
            get => _canLaunchElevated;
            set => SetProperty(ref _canLaunchElevated, value);
        }

        private bool _launchNewIfUnbound;

        [JsonPropertyName("launch-new-if-unbound")]
        public bool LaunchNewIfUnbound
        {
            get => _launchNewIfUnbound;
            set => SetProperty(ref _launchNewIfUnbound, value);
        }

        private bool _minimized;

        [JsonPropertyName("minimized")]
        public bool Minimized
        {
            get => _minimized;
            set => SetProperty(ref _minimized, value);
        }

        private bool _maximized;

        [JsonPropertyName("maximized")]
        public bool Maximized
        {
            get => _maximized;
            set => SetProperty(ref _maximized, value);
        }

        private int _monitorIndex;

        [JsonPropertyName("monitor")]
        public int MonitorIndex
        {
            get => _monitorIndex;
            set => SetProperty(ref _monitorIndex, value);
        }

        private string _version = string.Empty;

        [JsonPropertyName("version")]
        public string Version
        {
            get => _version;
            set => SetProperty(ref _version, value ?? string.Empty);
        }

        private ApplicationPosition _position = new();

        [JsonPropertyName("position")]
        public ApplicationPosition Position
        {
            get => _position;
            set => SetProperty(ref _position, value ?? new ApplicationPosition());
        }

        [JsonIgnore]
        private bool _isExpanded;

        [JsonIgnore]
        public bool IsExpanded
        {
            get => _isExpanded;
            set => SetProperty(ref _isExpanded, value);
        }

        [JsonIgnore]
        public string DisplayName
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(Name))
                {
                    return Name;
                }

                if (!string.IsNullOrWhiteSpace(Title))
                {
                    return Title;
                }

                if (!string.IsNullOrWhiteSpace(Path))
                {
                    return Path;
                }

                if (!string.IsNullOrWhiteSpace(AppUserModelId))
                {
                    return AppUserModelId;
                }

                if (!string.IsNullOrWhiteSpace(PackageFullName))
                {
                    return PackageFullName;
                }

                if (!string.IsNullOrWhiteSpace(PwaAppId))
                {
                    return PwaAppId;
                }

                return "Application";
            }
        }

        public sealed class ApplicationPosition
        {
            [JsonPropertyName("X")]
            public int X { get; set; }

            [JsonPropertyName("Y")]
            public int Y { get; set; }

            [JsonPropertyName("width")]
            public int Width { get; set; }

            [JsonPropertyName("height")]
            public int Height { get; set; }

            public bool IsEmpty => Width == 0 || Height == 0;
        }
    }
}
