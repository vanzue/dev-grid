// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;

namespace TopToolbar.Services.Workspaces
{
    /// <summary>
    /// Provides shared workspace storage locations used by TopToolbar modules.
    /// </summary>
    internal static class WorkspaceStoragePaths
    {
        private const string WorkspaceProviderFileName = "WorkspaceProvider.json";
        private const string WorkspaceDefinitionsFileName = "workspaces.json";
        private const string TemplatesDirectoryName = "templates";
        private const string WorkspaceIconsDirectoryName = "workspaces";

        internal static string GetProviderConfigPath()
        {
            return Path.Combine(AppPaths.ProvidersDirectory, WorkspaceProviderFileName);
        }

        internal static string GetDefaultWorkspacesPath()
        {
            return GetProviderConfigPath();
        }

        internal static string GetWorkspaceDefinitionsPath(string providerConfigPath = null)
        {
            if (!string.IsNullOrWhiteSpace(providerConfigPath))
            {
                var defaultConfigPath = GetProviderConfigPath();
                if (string.Equals(providerConfigPath, defaultConfigPath, StringComparison.OrdinalIgnoreCase))
                {
                    return Path.Combine(AppPaths.ConfigDirectory, WorkspaceDefinitionsFileName);
                }

                var directory = Path.GetDirectoryName(providerConfigPath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    return Path.Combine(directory, WorkspaceDefinitionsFileName);
                }
            }

            return Path.Combine(AppPaths.ConfigDirectory, WorkspaceDefinitionsFileName);
        }

        internal static string GetTemplatesDirectoryPath()
        {
            return Path.Combine(AppPaths.ConfigDirectory, TemplatesDirectoryName);
        }

        internal static string GetWorkspaceIconsDirectoryPath()
        {
            return Path.Combine(AppPaths.IconsDirectory, WorkspaceIconsDirectoryName);
        }

        internal static string GetWorkspaceIconPath(string workspaceId, long versionTicks)
        {
            var stem = NormalizeWorkspaceIconStem(workspaceId);
            return Path.Combine(GetWorkspaceIconsDirectoryPath(), $"{stem}.{versionTicks}.png");
        }

        internal static void DeleteWorkspaceIcons(string workspaceId, string exceptPath = null)
        {
            var directory = GetWorkspaceIconsDirectoryPath();
            if (!Directory.Exists(directory))
            {
                return;
            }

            var stem = NormalizeWorkspaceIconStem(workspaceId);
            foreach (var path in Directory.GetFiles(directory, $"{stem}.*.png"))
            {
                if (!string.IsNullOrWhiteSpace(exceptPath)
                    && string.Equals(Path.GetFullPath(path), Path.GetFullPath(exceptPath), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                File.Delete(path);
            }
        }

        internal static string GetTemplateFilePath(string templateName, string templatesDirectoryPath = null)
        {
            var normalizedName = NormalizeTemplateName(templateName);
            var directory = string.IsNullOrWhiteSpace(templatesDirectoryPath)
                ? GetTemplatesDirectoryPath()
                : templatesDirectoryPath;
            return Path.Combine(directory, $"{normalizedName}.json");
        }

        internal static string NormalizeTemplateName(string templateName)
        {
            return (templateName ?? string.Empty).Trim().ToLowerInvariant();
        }

        private static string NormalizeWorkspaceIconStem(string workspaceId)
        {
            var value = string.IsNullOrWhiteSpace(workspaceId)
                ? "workspace"
                : workspaceId.Trim();
            var invalid = Path.GetInvalidFileNameChars();
            foreach (var c in invalid)
            {
                value = value.Replace(c, '_');
            }

            return value;
        }

        internal static string GetLegacyPowerToysPath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft",
                "PowerToys",
                "Workspaces",
                "workspaces.json");
        }
    }
}
