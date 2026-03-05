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
using TopToolbar.Models.Providers;
using TopToolbar.Services.Providers;
using TopToolbar.Services.Storage;
using TopToolbar.Serialization;

namespace TopToolbar.Services.Workspaces
{
    internal sealed class WorkspaceDefinitionStore
    {
        private const int SaveRetryCount = 6;
        private const int SaveRetryDelayMilliseconds = 60;
        private static readonly object SessionPruneLock = new();
        private static bool _sessionRuntimePruneAttempted;
        private readonly string _filePath;
        private readonly WorkspaceProviderConfigStore _configStore;

        private readonly struct DocumentSnapshot
        {
            public DocumentSnapshot(WorkspaceDocument document, long versionTicks, bool migratedFromLegacy)
            {
                Document = document ?? new WorkspaceDocument();
                VersionTicks = versionTicks;
                MigratedFromLegacy = migratedFromLegacy;
            }

            public WorkspaceDocument Document { get; }
            public long VersionTicks { get; }
            public bool MigratedFromLegacy { get; }
        }

        public WorkspaceDefinitionStore(
            string filePath = null,
            WorkspaceProviderConfigStore configStore = null)
        {
            _configStore = configStore;
            _filePath = string.IsNullOrWhiteSpace(filePath)
                ? WorkspaceStoragePaths.GetWorkspaceDefinitionsPath(_configStore?.FilePath)
                : filePath;
        }

        public string FilePath => _filePath;

        public async Task<IReadOnlyList<WorkspaceDefinition>> LoadAllAsync(
            CancellationToken cancellationToken
        )
        {
            var document = await LoadDocumentAsync(cancellationToken).ConfigureAwait(false);
            if (document.Workspaces == null || document.Workspaces.Count == 0)
            {
                return Array.Empty<WorkspaceDefinition>();
            }

            return CloneWorkspaces(document.Workspaces);
        }

        public async Task<WorkspaceDefinition> LoadByIdAsync(
            string workspaceId,
            CancellationToken cancellationToken
        )
        {
            if (string.IsNullOrWhiteSpace(workspaceId))
            {
                return null;
            }

            var document = await LoadDocumentAsync(cancellationToken).ConfigureAwait(false);
            var match = document.Workspaces?.FirstOrDefault(ws =>
                string.Equals(ws.Id, workspaceId, StringComparison.OrdinalIgnoreCase)
            );
            return match != null ? CloneWorkspace(match) : null;
        }

        public async Task SaveWorkspaceAsync(
            WorkspaceDefinition workspace,
            CancellationToken cancellationToken
        )
        {
            ArgumentNullException.ThrowIfNull(workspace);

            for (var attempt = 0; attempt < SaveRetryCount; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var snapshot = await LoadDocumentSnapshotAsync(cancellationToken).ConfigureAwait(false);
                var document = snapshot.Document;
                document.Workspaces ??= new List<WorkspaceDefinition>();

                document.Workspaces.RemoveAll(ws => IsSameLogicalWorkspace(ws, workspace));

                document.Workspaces.Insert(0, workspace);

                try
                {
                    if (await TrySaveDocumentAsync(document, snapshot.VersionTicks, cancellationToken)
                        .ConfigureAwait(false))
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

            throw new IOException("Failed to save workspace definition after multiple retries.");
        }

        public async Task<bool> DeleteWorkspaceAsync(
            string workspaceId,
            CancellationToken cancellationToken
        )
        {
            if (string.IsNullOrWhiteSpace(workspaceId))
            {
                return false;
            }

            for (var attempt = 0; attempt < SaveRetryCount; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var snapshot = await LoadDocumentSnapshotAsync(cancellationToken).ConfigureAwait(false);
                var document = snapshot.Document;
                if (document.Workspaces == null || document.Workspaces.Count == 0)
                {
                    return false;
                }

                var removed = document.Workspaces.RemoveAll(ws =>
                    string.Equals(ws.Id, workspaceId, StringComparison.OrdinalIgnoreCase)
                );
                if (removed == 0)
                {
                    return false;
                }

                try
                {
                    if (await TrySaveDocumentAsync(document, snapshot.VersionTicks, cancellationToken)
                        .ConfigureAwait(false))
                    {
                        return true;
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

            throw new IOException("Failed to delete workspace definition after multiple retries.");
        }

        public Task SaveAllAsync(
            IReadOnlyList<WorkspaceDefinition> workspaces,
            CancellationToken cancellationToken)
        {
            var list = workspaces != null
                ? workspaces.Where(ws => ws != null).ToList()
                : new List<WorkspaceDefinition>();

            var document = new WorkspaceDocument { Workspaces = list };
            return SaveDocumentWithRetryAsync(document, cancellationToken);
        }

        public async Task<bool> UpdateLastLaunchedTimeAsync(
            string workspaceId,
            long timestamp,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(workspaceId))
            {
                return false;
            }

            for (var attempt = 0; attempt < SaveRetryCount; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var snapshot = await LoadDocumentSnapshotAsync(cancellationToken).ConfigureAwait(false);
                var document = snapshot.Document;
                if (document.Workspaces == null || document.Workspaces.Count == 0)
                {
                    return false;
                }

                var updated = false;
                foreach (var workspace in document.Workspaces)
                {
                    if (workspace == null)
                    {
                        continue;
                    }

                    if (string.Equals(workspace.Id, workspaceId, StringComparison.OrdinalIgnoreCase))
                    {
                        workspace.LastLaunchedTime = timestamp;
                        updated = true;
                        break;
                    }
                }

                if (!updated)
                {
                    return false;
                }

                try
                {
                    if (await TrySaveDocumentAsync(document, snapshot.VersionTicks, cancellationToken)
                        .ConfigureAwait(false))
                    {
                        return true;
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

            throw new IOException("Failed to update workspace launch time after multiple retries.");
        }
        
        private async Task<WorkspaceDocument> LoadDocumentAsync(CancellationToken cancellationToken)
        {
            var snapshot = await LoadDocumentSnapshotAsync(cancellationToken).ConfigureAwait(false);
            if (snapshot.MigratedFromLegacy)
            {
                try
                {
                    await SaveDocumentWithRetryAsync(snapshot.Document, cancellationToken).ConfigureAwait(false);
                    await TryClearLegacyConfigAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    AppLogger.LogWarning($"WorkspaceDefinitionStore: migration persistence failed - {ex.Message}");
                }
            }

            await ApplySessionRuntimePruneIfNeededAsync(snapshot.Document, cancellationToken).ConfigureAwait(false);

            return snapshot.Document;
        }

        internal async Task PruneStaleRuntimeWorkspacesOnceAsync(CancellationToken cancellationToken)
        {
            var snapshot = await LoadDocumentSnapshotAsync(cancellationToken).ConfigureAwait(false);
            await ApplySessionRuntimePruneIfNeededAsync(snapshot.Document, cancellationToken).ConfigureAwait(false);
        }

        private async Task ApplySessionRuntimePruneIfNeededAsync(
            WorkspaceDocument document,
            CancellationToken cancellationToken)
        {
            if (!TryBeginSessionRuntimePrune())
            {
                return;
            }

            var removed = PruneCrossSessionRuntimeWorkspaces(document);
            if (removed > 0)
            {
                AppLogger.LogInfo($"WorkspaceDefinitionStore: pruned {removed} stale runtime workspace(s) from previous session.");
                try
                {
                    await SaveDocumentWithRetryAsync(document, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    AppLogger.LogWarning($"WorkspaceDefinitionStore: failed to persist runtime workspace prune - {ex.Message}");
                }
            }

            await CleanupOrphanedWorkspaceButtonsAsync(document, cancellationToken).ConfigureAwait(false);
        }

        private static bool TryBeginSessionRuntimePrune()
        {
            lock (SessionPruneLock)
            {
                if (_sessionRuntimePruneAttempted)
                {
                    return false;
                }

                _sessionRuntimePruneAttempted = true;
                return true;
            }
        }

        private static int PruneCrossSessionRuntimeWorkspaces(WorkspaceDocument document)
        {
            if (document?.Workspaces == null || document.Workspaces.Count == 0)
            {
                return 0;
            }

            return document.Workspaces.RemoveAll(IsCrossSessionRuntimeWorkspace);
        }

        private static bool IsCrossSessionRuntimeWorkspace(WorkspaceDefinition workspace)
        {
            if (workspace == null)
            {
                return false;
            }

            // Workspace instances are runtime/session concepts. Anything not stamped
            // with the current session id should be removed on startup.
            return !WorkspaceRuntimeSession.IsCurrentSession(workspace.RuntimeSessionId);
        }

        private async Task CleanupOrphanedWorkspaceButtonsAsync(
            WorkspaceDocument document,
            CancellationToken cancellationToken)
        {
            if (_configStore == null)
            {
                return;
            }

            try
            {
                var config = await _configStore.LoadAsync(cancellationToken).ConfigureAwait(false);
                if (config?.Buttons == null || config.Buttons.Count == 0)
                {
                    return;
                }

                var activeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var workspaces = document?.Workspaces ?? new List<WorkspaceDefinition>();
                foreach (var workspace in workspaces)
                {
                    if (workspace == null || string.IsNullOrWhiteSpace(workspace.Id))
                    {
                        continue;
                    }

                    activeIds.Add(workspace.Id);
                }

                var removed = config.Buttons.RemoveAll(button =>
                {
                    if (button == null || string.IsNullOrWhiteSpace(button.WorkspaceId))
                    {
                        return false;
                    }

                    return !activeIds.Contains(button.WorkspaceId.Trim());
                });

                if (removed <= 0)
                {
                    return;
                }

                await _configStore.SaveAsync(config, cancellationToken).ConfigureAwait(false);
                AppLogger.LogInfo($"WorkspaceDefinitionStore: removed {removed} orphaned workspace button(s) for stale sessions.");
            }
            catch (Exception ex)
            {
                AppLogger.LogWarning($"WorkspaceDefinitionStore: cleanup orphaned workspace buttons failed - {ex.Message}");
            }
        }

        private async Task<DocumentSnapshot> LoadDocumentSnapshotAsync(CancellationToken cancellationToken)
        {
            if (!File.Exists(_filePath))
            {
                var migrated = await TryMigrateFromProviderConfigAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (migrated != null && migrated.Workspaces != null && migrated.Workspaces.Count > 0)
                {
                    return new DocumentSnapshot(migrated, 0, migratedFromLegacy: true);
                }

                return new DocumentSnapshot(new WorkspaceDocument(), 0, migratedFromLegacy: false);
            }

            var versionTicks = FileConcurrencyGuard.GetFileVersionUtcTicks(_filePath);

            try
            {
                await using var stream = new FileStream(
                    _filePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite);
                var document = await JsonSerializer.DeserializeAsync(
                    stream,
                    WorkspaceProviderJsonContext.Default.WorkspaceDocument,
                    cancellationToken).ConfigureAwait(false);
                return new DocumentSnapshot(document ?? new WorkspaceDocument(), versionTicks, migratedFromLegacy: false);
            }
            catch (JsonException)
            {
                return new DocumentSnapshot(new WorkspaceDocument(), versionTicks, migratedFromLegacy: false);
            }
            catch (IOException)
            {
                return new DocumentSnapshot(new WorkspaceDocument(), versionTicks, migratedFromLegacy: false);
            }
        }

        private async Task SaveDocumentWithRetryAsync(
            WorkspaceDocument document,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(document);

            for (var attempt = 0; attempt < SaveRetryCount; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var expectedVersion = FileConcurrencyGuard.GetFileVersionUtcTicks(_filePath);
                try
                {
                    if (await TrySaveDocumentAsync(document, expectedVersion, cancellationToken).ConfigureAwait(false))
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

            throw new IOException("Failed to save workspace document after multiple retries.");
        }

        private async Task<bool> TrySaveDocumentAsync(
            WorkspaceDocument document,
            long expectedVersionTicks,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(document);

            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await using var writeLock = await FileConcurrencyGuard
                .AcquireWriteLockAsync(_filePath, cancellationToken)
                .ConfigureAwait(false);

            var currentVersion = FileConcurrencyGuard.GetFileVersionUtcTicks(_filePath);
            if (currentVersion != expectedVersionTicks)
            {
                return false;
            }

            var tempPath = $"{_filePath}.{Guid.NewGuid():N}.tmp";
            try
            {
                await using (var stream = new FileStream(
                    tempPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None))
                {
                    await JsonSerializer.SerializeAsync(
                        stream,
                        document,
                        WorkspaceProviderJsonContext.Default.WorkspaceDocument,
                        cancellationToken).ConfigureAwait(false);
                }

                File.Copy(tempPath, _filePath, overwrite: true);
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

        private async Task<WorkspaceDocument> TryMigrateFromProviderConfigAsync(
            CancellationToken cancellationToken)
        {
            if (_configStore == null)
            {
                return null;
            }

            WorkspaceProviderConfig config;
            try
            {
                config = await _configStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                return null;
            }

            if (config.Data?.Workspaces == null || config.Data.Workspaces.Count == 0)
            {
                return null;
            }

            var workspaces = CloneWorkspaces(config.Data.Workspaces).ToList();
            return new WorkspaceDocument { Workspaces = workspaces };
        }

        private async Task TryClearLegacyConfigAsync(CancellationToken cancellationToken)
        {
            if (_configStore == null)
            {
                return;
            }

            try
            {
                var config = await _configStore.LoadAsync(cancellationToken).ConfigureAwait(false);
                if (config.Data?.Workspaces == null || config.Data.Workspaces.Count == 0)
                {
                    return;
                }

                config.Data = null;
                await _configStore.SaveAsync(config, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
            }
        }

        private static IReadOnlyList<WorkspaceDefinition> CloneWorkspaces(
            IEnumerable<WorkspaceDefinition> source
        )
        {
            var list = new List<WorkspaceDefinition>();
            foreach (var workspace in source)
            {
                var clone = CloneWorkspace(workspace);
                if (clone != null)
                {
                    list.Add(clone);
                }
            }

            return list;
        }

        private static WorkspaceDefinition CloneWorkspace(WorkspaceDefinition workspace)
        {
            if (workspace == null)
            {
                return null;
            }

            var json = JsonSerializer.Serialize(workspace, DefaultJsonContext.Default.WorkspaceDefinition);
            return JsonSerializer.Deserialize(json, DefaultJsonContext.Default.WorkspaceDefinition);
        }

        private static bool IsSameLogicalWorkspace(WorkspaceDefinition existing, WorkspaceDefinition incoming)
        {
            if (existing == null || incoming == null)
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(existing.Id)
                && !string.IsNullOrWhiteSpace(incoming.Id)
                && string.Equals(existing.Id, incoming.Id, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(existing.TemplateName)
                && !string.IsNullOrWhiteSpace(incoming.TemplateName)
                && !string.IsNullOrWhiteSpace(existing.InstanceName)
                && !string.IsNullOrWhiteSpace(incoming.InstanceName))
            {
                return string.Equals(existing.TemplateName, incoming.TemplateName, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(existing.InstanceName, incoming.InstanceName, StringComparison.OrdinalIgnoreCase);
            }

            return false;
        }
    }
}
