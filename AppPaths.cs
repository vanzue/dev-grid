// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;

namespace TopToolbar;

internal static class AppPaths
{
    private const string StandaloneRootFolderName = "TopToolbar-Standalone";
    private const string RootOverrideEnvironmentVariable = "TOPTOOLBAR_APPDATA_ROOT";

    public static string Root => ResolveRootPath();

    public static string Logs => Path.Combine(Root, "Logs");

    public static string ConfigFile => Path.Combine(Root, "toolbar.config.json");

    public static string ProfilesDirectory => Path.Combine(Root, "Profiles");

    public static string ProvidersDirectory => Path.Combine(Root, "Providers");

    public static string ConfigDirectory => Path.Combine(Root, "config");

    public static string ProviderDefinitionsDirectory => Path.Combine(ConfigDirectory, "providers");

    public static string IconsDirectory => Path.Combine(Root, "icons");

    private static string ResolveRootPath()
    {
        var overridePath = Environment.GetEnvironmentVariable(RootOverrideEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return overridePath.Trim();
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            StandaloneRootFolderName);
    }
}
