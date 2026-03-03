// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TopToolbar.Logging;
using TopToolbar.Services.Storage;
using TopToolbar.Serialization;

namespace TopToolbar.Services.Workspaces
{
    internal sealed class TemplateStore
    {
        private const int SaveRetryCount = 6;
        private const int SaveRetryDelayMilliseconds = 60;
        private readonly string _directoryPath;

        public TemplateStore(string directoryPath = null)
        {
            _directoryPath = string.IsNullOrWhiteSpace(directoryPath)
                ? WorkspaceStoragePaths.GetTemplatesDirectoryPath()
                : directoryPath;
        }

        public string DirectoryPath => _directoryPath;

        public async Task<IReadOnlyList<TemplateDefinition>> LoadAllAsync(CancellationToken cancellationToken)
        {
            if (!Directory.Exists(_directoryPath))
            {
                return Array.Empty<TemplateDefinition>();
            }

            var files = Directory.GetFiles(_directoryPath, "*.json", SearchOption.TopDirectoryOnly);
            if (files.Length == 0)
            {
                return Array.Empty<TemplateDefinition>();
            }

            var templates = new List<TemplateDefinition>(files.Length);
            foreach (var file in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var template = await TryLoadFileAsync(file, cancellationToken).ConfigureAwait(false);
                if (template != null)
                {
                    templates.Add(template);
                }
            }

            return templates;
        }

        public async Task<TemplateDefinition> LoadByNameAsync(string templateName, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(templateName))
            {
                return null;
            }

            var path = WorkspaceStoragePaths.GetTemplateFilePath(templateName, _directoryPath);
            if (!File.Exists(path))
            {
                return null;
            }

            return await TryLoadFileAsync(path, cancellationToken).ConfigureAwait(false);
        }

        public async Task SaveTemplateAsync(TemplateDefinition template, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(template);

            TemplateDefinitionValidator.CanonicalizeInPlace(template);
            var errors = TemplateDefinitionValidator.Validate(template);
            if (errors.Count > 0)
            {
                throw new ArgumentException($"Template '{template.Name}' is invalid: {string.Join("; ", errors)}", nameof(template));
            }

            var filePath = WorkspaceStoragePaths.GetTemplateFilePath(template.Name, _directoryPath);
            Directory.CreateDirectory(Path.GetDirectoryName(filePath) ?? _directoryPath);

            for (var attempt = 0; attempt < SaveRetryCount; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var expectedVersion = FileConcurrencyGuard.GetFileVersionUtcTicks(filePath);

                try
                {
                    if (await TrySaveAsync(filePath, template, expectedVersion, cancellationToken).ConfigureAwait(false))
                    {
                        return;
                    }
                }
                catch (IOException) when (attempt + 1 < SaveRetryCount)
                {
                }
                catch (UnauthorizedAccessException) when (attempt + 1 < SaveRetryCount)
                {
                }

                if (attempt + 1 < SaveRetryCount)
                {
                    await Task.Delay(SaveRetryDelayMilliseconds, cancellationToken).ConfigureAwait(false);
                }
            }

            throw new IOException("Failed to save template after multiple retries.");
        }

        public Task<bool> DeleteTemplateAsync(string templateName, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(templateName))
            {
                return Task.FromResult(false);
            }

            var filePath = WorkspaceStoragePaths.GetTemplateFilePath(templateName, _directoryPath);
            if (!File.Exists(filePath))
            {
                return Task.FromResult(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Delete(filePath);
            return Task.FromResult(true);
        }

        private async Task<TemplateDefinition> TryLoadFileAsync(string filePath, CancellationToken cancellationToken)
        {
            try
            {
                await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var template = await JsonSerializer.DeserializeAsync(
                    stream,
                    WorkspaceProviderJsonContext.Default.TemplateDefinition,
                    cancellationToken).ConfigureAwait(false);

                if (template == null)
                {
                    return null;
                }

                TemplateDefinitionValidator.CanonicalizeInPlace(template);
                var errors = TemplateDefinitionValidator.Validate(template);
                if (errors.Count == 0)
                {
                    return template;
                }

                AppLogger.LogWarning($"TemplateStore: skipping invalid template '{filePath}' - {string.Join("; ", errors)}");
                return null;
            }
            catch (JsonException ex)
            {
                AppLogger.LogWarning($"TemplateStore: failed to parse template '{filePath}' - {ex.Message}");
                return null;
            }
            catch (IOException ex)
            {
                AppLogger.LogWarning($"TemplateStore: failed to read template '{filePath}' - {ex.Message}");
                return null;
            }
        }

        private static async Task<bool> TrySaveAsync(
            string filePath,
            TemplateDefinition template,
            long expectedVersionTicks,
            CancellationToken cancellationToken)
        {
            await using var writeLock = await FileConcurrencyGuard
                .AcquireWriteLockAsync(filePath, cancellationToken)
                .ConfigureAwait(false);

            var currentVersion = FileConcurrencyGuard.GetFileVersionUtcTicks(filePath);
            if (currentVersion != expectedVersionTicks)
            {
                return false;
            }

            var tempPath = $"{filePath}.{Guid.NewGuid():N}.tmp";
            try
            {
                await using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await JsonSerializer.SerializeAsync(
                        stream,
                        template,
                        WorkspaceProviderJsonContext.Default.TemplateDefinition,
                        cancellationToken).ConfigureAwait(false);
                }

                File.Copy(tempPath, filePath, overwrite: true);
                return true;
            }
            finally
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch
                {
                }
            }
        }
    }
}
