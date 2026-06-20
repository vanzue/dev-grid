// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;
using System.Text.Json;
using TopToolbar.Logging;

namespace TopToolbar.Services.Workspaces
{
    /// <summary>
    /// Resolves the <c>ms-avd:</c> reconnect URI for a Windows App (Windows 365 / Cloud PC)
    /// remote session from its app-user-model-id. The information lives in the Windows App
    /// LaunchFiles cache and is shared by snapshot capture and launch-time reconnection.
    /// </summary>
    internal static class Windows365LaunchResolver
    {
        private const string AppIdMarker = "!Windows365:";

        private const string PackageFamilyName = "MicrosoftCorporationII.Windows365_8wekyb3d8bbwe";

        internal readonly record struct RemoteIdentity(
            bool Found,
            string Provider,
            string ConnectionId,
            string ResourceId,
            string UserName,
            string LaunchUri,
            string DisplayName);

        /// <summary>
        /// Returns true when the supplied app-user-model-id identifies a Windows App remote
        /// session (it carries the <c>!Windows365:&lt;cloudPcId&gt;</c> marker).
        /// </summary>
        public static bool IsRemoteAumid(string appUserModelId)
        {
            return !string.IsNullOrWhiteSpace(appUserModelId)
                && appUserModelId.IndexOf(AppIdMarker, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Resolves the remote session identity (including the <c>ms-avd:</c> reconnect URI)
        /// for the supplied app-user-model-id. Returns <see cref="RemoteIdentity.Found"/> = false
        /// when the AUMID is not a remote session or the LaunchFiles cache is unavailable.
        /// </summary>
        public static RemoteIdentity Resolve(string appUserModelId, string fallbackDisplayName)
        {
            if (!IsRemoteAumid(appUserModelId))
            {
                return default;
            }

            var markerIndex = appUserModelId.IndexOf(AppIdMarker, StringComparison.OrdinalIgnoreCase);
            var cloudPcId = appUserModelId.Substring(markerIndex + AppIdMarker.Length).Trim();
            if (string.IsNullOrWhiteSpace(cloudPcId)
                || cloudPcId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                return default;
            }

            var launchFilesDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Packages",
                PackageFamilyName,
                "LocalCache",
                "LaunchFiles");
            var settingsPath = Path.Combine(launchFilesDirectory, cloudPcId + ".json");
            var rdpPath = Path.Combine(launchFilesDirectory, cloudPcId + ".rdp");

            var settings = ReadLaunchSettings(settingsPath);
            var resourceId = ReadResourceId(rdpPath);
            if (string.IsNullOrWhiteSpace(settings.UserName) || string.IsNullOrWhiteSpace(resourceId))
            {
                return default;
            }

            var launchUri = "ms-avd:connect?resourceid="
                + Uri.EscapeDataString(resourceId)
                + "&username="
                + Uri.EscapeDataString(settings.UserName);
            var displayName = string.IsNullOrWhiteSpace(settings.DisplayName)
                ? fallbackDisplayName ?? string.Empty
                : settings.DisplayName;

            return new RemoteIdentity(
                true,
                "windows-app",
                cloudPcId,
                resourceId,
                settings.UserName,
                launchUri,
                displayName);
        }

        private readonly record struct LaunchSettings(string UserName, string DisplayName);

        private static LaunchSettings ReadLaunchSettings(string settingsPath)
        {
            if (string.IsNullOrWhiteSpace(settingsPath) || !File.Exists(settingsPath))
            {
                return default;
            }

            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(settingsPath));
                var username = string.Empty;
                var displayName = string.Empty;
                if (document.RootElement.TryGetProperty("UserName", out var userElement)
                    && userElement.ValueKind == JsonValueKind.String)
                {
                    username = userElement.GetString()?.Trim() ?? string.Empty;
                }

                if (document.RootElement.TryGetProperty("WorkspaceDisplayName", out var displayNameElement)
                    && displayNameElement.ValueKind == JsonValueKind.String)
                {
                    displayName = displayNameElement.GetString()?.Trim() ?? string.Empty;
                }

                return new LaunchSettings(username, displayName);
            }
            catch (Exception ex)
            {
                AppLogger.LogWarning($"Windows365LaunchResolver: failed to read launch settings '{settingsPath}' - {ex.Message}");
            }

            return default;
        }

        private static string ReadResourceId(string rdpPath)
        {
            if (string.IsNullOrWhiteSpace(rdpPath) || !File.Exists(rdpPath))
            {
                return string.Empty;
            }

            try
            {
                foreach (var line in File.ReadLines(rdpPath))
                {
                    const string prefix = "remoteapplicationprogram:s:||";
                    if (!line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var resourceId = line.Substring(prefix.Length).Trim();
                    if (!string.IsNullOrWhiteSpace(resourceId))
                    {
                        return resourceId;
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogWarning($"Windows365LaunchResolver: failed to read RDP launch file '{rdpPath}' - {ex.Message}");
            }

            return string.Empty;
        }
    }
}
