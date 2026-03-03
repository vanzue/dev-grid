// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using TopToolbar.Logging;

namespace TopToolbar.Providers
{
    /// <summary>
    /// Built-in provider that manages workspace providers automatically.
    /// </summary>
    public sealed class BuiltinProvider : IDisposable
    {
        private readonly List<IActionProvider> _providers = new();
        private readonly List<IDisposable> _disposables = new();
        private bool _disposed;

        /// <summary>
        /// Gets all registered built-in providers
        /// </summary>
        public IReadOnlyList<IActionProvider> Providers => _providers.AsReadOnly();

        /// <summary>
        /// Initializes and loads all built-in providers (workspace providers)
        /// </summary>
        public void Initialize()
        {
            ObjectDisposedException.ThrowIf(_disposed, nameof(BuiltinProvider));

            // Clear any existing providers
            DisposeProviders();

            LoadWorkspaceProvider();
        }

        /// <summary>
        /// Registers all loaded providers to the ActionProviderRuntime
        /// </summary>
        public void RegisterProvidersTo(ActionProviderRuntime runtime)
        {
            ArgumentNullException.ThrowIfNull(runtime);

            foreach (var provider in _providers)
            {
                try
                {
                    runtime.RegisterProvider(provider);
                }
                catch (Exception ex)
                {
                    // Log error but continue with other providers
                    try
                    {
                        AppLogger.LogWarning($"BuiltinProvider: Failed to register provider '{provider.Id}': {ex.Message}");
                    }
                    catch
                    {
                        // Ignore logging errors
                    }
                }
            }
        }

        /// <summary>
        /// Loads the workspace provider
        /// </summary>
        private void LoadWorkspaceProvider()
        {
            try
            {
                var workspaceProvider = new WorkspaceProvider();
                _providers.Add(workspaceProvider);
                _disposables.Add(workspaceProvider);
            }
            catch (Exception ex)
            {
                try
                {
                    AppLogger.LogWarning($"BuiltinProvider: Failed to load workspace provider: {ex.Message}");
                }
                catch
                {
                    // Ignore logging errors
                }
            }
        }

        /// <summary>
        /// Disposes all loaded providers
        /// </summary>
        private void DisposeProviders()
        {
            foreach (var disposable in _disposables)
            {
                try
                {
                    disposable?.Dispose();
                }
                catch
                {
                    // Ignore disposal errors
                }
            }

            _disposables.Clear();
            _providers.Clear();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            DisposeProviders();
            GC.SuppressFinalize(this);
        }
    }
}
