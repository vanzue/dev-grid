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
using TopToolbar.Serialization;

namespace TopToolbar.Services.Workspaces
{
    internal sealed class TemplateNormalizationService
    {
        internal sealed class NormalizeResult
        {
            public string DirectoryPath { get; set; } = string.Empty;

            public int FilesScanned { get; set; }

            public int FilesNormalized { get; set; }

            public int FilesUnchanged { get; set; }

            public int FilesFailed { get; set; }

            public List<NormalizeItem> Items { get; set; } = new();
        }

        internal sealed class NormalizeItem
        {
            public string SourceFilePath { get; set; } = string.Empty;

            public string TargetFilePath { get; set; } = string.Empty;

            public string TemplateName { get; set; } = string.Empty;

            public bool Success { get; set; }

            public bool Changed { get; set; }

            public string Message { get; set; } = string.Empty;
        }

        private readonly string _directoryPath;

        public TemplateNormalizationService(string directoryPath = null)
        {
            _directoryPath = string.IsNullOrWhiteSpace(directoryPath)
                ? WorkspaceStoragePaths.GetTemplatesDirectoryPath()
                : directoryPath;
        }

        public async Task<NormalizeResult> NormalizeAllAsync(bool dryRun, CancellationToken cancellationToken)
        {
            var result = new NormalizeResult
            {
                DirectoryPath = _directoryPath,
            };

            if (!Directory.Exists(_directoryPath))
            {
                return result;
            }

            var files = Directory.GetFiles(_directoryPath, "*.json", SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var reservedTargets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var filePath in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                result.FilesScanned++;

                var item = new NormalizeItem
                {
                    SourceFilePath = filePath,
                    TargetFilePath = filePath,
                };
                result.Items.Add(item);

                try
                {
                    await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    var template = await JsonSerializer.DeserializeAsync(
                        stream,
                        WorkspaceProviderJsonContext.Default.TemplateDefinition,
                        cancellationToken).ConfigureAwait(false);

                    if (template == null)
                    {
                        item.Success = false;
                        item.Message = "Template content is empty.";
                        result.FilesFailed++;
                        continue;
                    }

                    TemplateDefinitionStandardizer.StandardizeInPlace(template);
                    var errors = TemplateDefinitionValidator.Validate(template);
                    if (errors.Count > 0)
                    {
                        item.TemplateName = template.Name ?? string.Empty;
                        item.Success = false;
                        item.Message = string.Join("; ", errors);
                        result.FilesFailed++;
                        continue;
                    }

                    var targetPath = WorkspaceStoragePaths.GetTemplateFilePath(template.Name, _directoryPath);
                    item.TemplateName = template.Name;
                    item.TargetFilePath = targetPath;

                    var sourceFullPath = Path.GetFullPath(filePath);
                    var targetFullPath = Path.GetFullPath(targetPath);
                    if (reservedTargets.TryGetValue(targetFullPath, out var ownerPath)
                        && !string.Equals(ownerPath, sourceFullPath, StringComparison.OrdinalIgnoreCase))
                    {
                        item.Success = false;
                        item.Message = $"Target file conflict: '{targetPath}' is also produced by '{ownerPath}'.";
                        result.FilesFailed++;
                        continue;
                    }

                    reservedTargets[targetFullPath] = sourceFullPath;

                    if (!string.Equals(sourceFullPath, targetFullPath, StringComparison.OrdinalIgnoreCase)
                        && File.Exists(targetPath))
                    {
                        item.Success = false;
                        item.Message = $"Target file '{targetPath}' already exists.";
                        result.FilesFailed++;
                        continue;
                    }

                    var normalizedJson = JsonSerializer.Serialize(
                        template,
                        WorkspaceProviderJsonContext.Default.TemplateDefinition);

                    var sourceJson = await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
                    var changed = !string.Equals(sourceJson, normalizedJson, StringComparison.Ordinal)
                        || !string.Equals(sourceFullPath, targetFullPath, StringComparison.OrdinalIgnoreCase);

                    item.Changed = changed;
                    item.Success = true;

                    if (changed)
                    {
                        if (!dryRun)
                        {
                            await File.WriteAllTextAsync(targetPath, normalizedJson, cancellationToken).ConfigureAwait(false);
                            if (!string.Equals(sourceFullPath, targetFullPath, StringComparison.OrdinalIgnoreCase))
                            {
                                File.Delete(filePath);
                            }
                        }

                        item.Message = dryRun ? "Will normalize." : "Normalized.";
                        result.FilesNormalized++;
                    }
                    else
                    {
                        item.Message = "Already normalized.";
                        result.FilesUnchanged++;
                    }
                }
                catch (JsonException ex)
                {
                    item.Success = false;
                    item.Message = $"JSON parse failed: {ex.Message}";
                    result.FilesFailed++;
                }
                catch (Exception ex)
                {
                    item.Success = false;
                    item.Message = ex.Message;
                    result.FilesFailed++;
                }
            }

            return result;
        }
    }
}
