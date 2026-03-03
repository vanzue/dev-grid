// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TopToolbar.Logging;

namespace TopToolbar.Services.Workspaces
{
    internal static class BuiltInTemplateSeeder
    {
        private static readonly string[] LegacyDefaultTemplateNames =
        {
            "coding-side-by-side",
            "review-triple",
            "release-grid",
        };

        private const string LegacyDefaultsRemovedMarkerFileName = ".legacy-defaults-removed";

        public static async Task RemoveLegacyDefaultsOnceAsync(CancellationToken cancellationToken)
        {
            var store = new TemplateStore();
            var markerPath = Path.Combine(store.DirectoryPath, LegacyDefaultsRemovedMarkerFileName);
            if (File.Exists(markerPath))
            {
                return;
            }

            Directory.CreateDirectory(store.DirectoryPath);

            foreach (var templateName in LegacyDefaultTemplateNames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    _ = await store.DeleteTemplateAsync(templateName, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    AppLogger.LogWarning($"BuiltInTemplateSeeder: failed removing legacy template '{templateName}' - {ex.Message}");
                }
            }

            try
            {
                File.WriteAllText(markerPath, DateTimeOffset.UtcNow.ToString("O"));
            }
            catch (Exception ex)
            {
                AppLogger.LogWarning($"BuiltInTemplateSeeder: failed writing migration marker - {ex.Message}");
            }
        }
    }
}
