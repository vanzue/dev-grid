// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using TopToolbar.Models;
using TopToolbar.Serialization;

namespace TopToolbar.Services
{
    public class ToolbarConfigService
    {
        private readonly string _configPath;

        public string ConfigPath => _configPath;

        private static readonly JsonSerializerOptions JsonOptions = ToolbarConfigJsonContext.Default.Options;

        public ToolbarConfigService(string configPath = "")
        {
            _configPath = string.IsNullOrEmpty(configPath) ? GetDefaultPath() : configPath;
            Directory.CreateDirectory(Path.GetDirectoryName(_configPath)!);
        }

        public async Task<ToolbarConfig> LoadAsync()
        {
            ToolbarConfig config;

            if (!File.Exists(_configPath))
            {
                config = CreateDefault();
                EnsureDefaults(config);
                await SaveAsync(config);
                return config;
            }

            await using var stream = File.OpenRead(_configPath);
            config = await JsonSerializer.DeserializeAsync(stream, ToolbarConfigJsonContext.Default.ToolbarConfig) ?? new ToolbarConfig();
            EnsureDefaults(config);
            return config;
        }

        public async Task SaveAsync(ToolbarConfig config)
        {
            EnsureDefaults(config);
            await using var stream = File.Create(_configPath);
            await JsonSerializer.SerializeAsync(stream, config, ToolbarConfigJsonContext.Default.ToolbarConfig);
        }

        private static void EnsureDefaults(ToolbarConfig config)
        {
            config ??= new ToolbarConfig();
            config.Groups ??= new List<ButtonGroup>();
            config.Bindings ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            config.DefaultActions ??= new DefaultActionsConfig();
            config.DefaultActions.MediaPlayPause ??= new DefaultActionItemConfig();
            if (!Enum.IsDefined(typeof(ToolbarDisplayMode), config.DisplayMode))
            {
                config.DisplayMode = ToolbarDisplayMode.TopBar;
            }

            if (!Enum.IsDefined(typeof(ToolbarTheme), config.Theme))
            {
                config.Theme = ToolbarTheme.WarmFrosted;
            }

            foreach (var group in config.Groups)
            {
                group.Layout ??= new ToolbarGroupLayout();
                group.Providers ??= new ObservableCollection<string>();
                group.StaticActions ??= new ObservableCollection<string>();
                group.Buttons ??= new ObservableCollection<ToolbarButton>();
            }
        }

        private static string GetDefaultPath()
        {
            return AppPaths.ConfigFile;
        }

        private static ToolbarConfig CreateDefault()
        {
            var system = new ButtonGroup
            {
                Name = "System",
                Layout = new ToolbarGroupLayout
                {
                    Style = ToolbarGroupLayoutStyle.Icon,
                    Overflow = ToolbarGroupOverflowMode.Wrap,
                },
            };

            return new ToolbarConfig
            {
                Groups =
                {
                    system,
                },
                Bindings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Alt+1"] = "palette.open",
                    ["Ctrl+Space"] = "palette.open",
                    ["Alt+W"] = "workspace.switcher",
                },
                DisplayMode = ToolbarDisplayMode.TopBar,
                TopBarEnabled = true,
                RadialMenuEnabled = true,
            };
        }
    }
}
