// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TopToolbar.Logging;

namespace TopToolbar.Services.Everything;

public sealed class EverythingSearchService : IDisposable
{
    private const int DefaultMaxResults = 50;
    private readonly object _sync = new();
    private EverythingSdkClient _client;
    private bool _disposed;

    public Task<EverythingSearchResponse> SearchAsync(
        string query,
        int maxResults = DefaultMaxResults,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Task.FromResult(new EverythingSearchResponse
            {
                IsAvailable = true,
                StatusMessage = "Type to search files and folders.",
                Results = new List<EverythingSearchResult>(),
            });
        }

        var normalizedQuery = query.Trim();
        var normalizedMaxResults = Math.Clamp(maxResults, 1, 250);

        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var client = GetOrCreateClient();
                if (!client.IsLoaded)
                {
                    return EverythingSearchResponse.Unavailable(client.LoadError);
                }

                return client.Search(normalizedQuery, normalizedMaxResults);
            }
        }, cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_sync)
        {
            _client?.Dispose();
            _client = null;
        }
    }

    private EverythingSdkClient GetOrCreateClient()
    {
        if (_client != null)
        {
            return _client;
        }

        _client = EverythingSdkClient.Load();
        return _client;
    }

    private sealed class EverythingSdkClient : IDisposable
    {
        private const uint EverythingErrorIpc = 2;
        private const uint EverythingSortNameAscending = 1;
        private const uint EverythingRequestFullPathAndFileName = 0x00000004;
        private const uint EverythingRequestSize = 0x00000010;
        private const uint EverythingRequestDateModified = 0x00000040;

        private readonly IntPtr _libraryHandle;
        private readonly EverythingSetSearchW _setSearch;
        private readonly EverythingSetRequestFlags _setRequestFlags;
        private readonly EverythingSetMax _setMax;
        private readonly EverythingSetOffset _setOffset;
        private readonly EverythingSetSort _setSort;
        private readonly EverythingQueryW _query;
        private readonly EverythingGetNumResults _getNumResults;
        private readonly EverythingGetResultFullPathNameW _getResultFullPathName;
        private readonly EverythingIsFolderResult _isFolderResult;
        private readonly EverythingGetResultSize _getResultSize;
        private readonly EverythingGetResultDateModified _getResultDateModified;
        private readonly EverythingGetLastError _getLastError;
        private readonly EverythingReset _reset;
        private readonly EverythingCleanUp _cleanUp;
        private bool _disposed;

        private EverythingSdkClient(string loadError)
        {
            LoadError = loadError;
        }

        private EverythingSdkClient(
            IntPtr libraryHandle,
            EverythingSetSearchW setSearch,
            EverythingSetRequestFlags setRequestFlags,
            EverythingSetMax setMax,
            EverythingSetOffset setOffset,
            EverythingSetSort setSort,
            EverythingQueryW query,
            EverythingGetNumResults getNumResults,
            EverythingGetResultFullPathNameW getResultFullPathName,
            EverythingIsFolderResult isFolderResult,
            EverythingGetResultSize getResultSize,
            EverythingGetResultDateModified getResultDateModified,
            EverythingGetLastError getLastError,
            EverythingReset reset,
            EverythingCleanUp cleanUp)
        {
            _libraryHandle = libraryHandle;
            _setSearch = setSearch;
            _setRequestFlags = setRequestFlags;
            _setMax = setMax;
            _setOffset = setOffset;
            _setSort = setSort;
            _query = query;
            _getNumResults = getNumResults;
            _getResultFullPathName = getResultFullPathName;
            _isFolderResult = isFolderResult;
            _getResultSize = getResultSize;
            _getResultDateModified = getResultDateModified;
            _getLastError = getLastError;
            _reset = reset;
            _cleanUp = cleanUp;
            IsLoaded = true;
        }

        public bool IsLoaded { get; }

        public string LoadError { get; } = string.Empty;

        public static EverythingSdkClient Load()
        {
            var candidates = GetCandidateDllPaths();
            foreach (var candidate in candidates)
            {
                if (string.IsNullOrWhiteSpace(candidate) || !File.Exists(candidate))
                {
                    continue;
                }

                try
                {
                    var handle = NativeLibrary.Load(candidate);
                    return new EverythingSdkClient(
                        handle,
                        GetDelegate<EverythingSetSearchW>(handle, "Everything_SetSearchW"),
                        GetDelegate<EverythingSetRequestFlags>(handle, "Everything_SetRequestFlags"),
                        GetDelegate<EverythingSetMax>(handle, "Everything_SetMax"),
                        GetDelegate<EverythingSetOffset>(handle, "Everything_SetOffset"),
                        GetDelegate<EverythingSetSort>(handle, "Everything_SetSort"),
                        GetDelegate<EverythingQueryW>(handle, "Everything_QueryW"),
                        GetDelegate<EverythingGetNumResults>(handle, "Everything_GetNumResults"),
                        GetDelegate<EverythingGetResultFullPathNameW>(handle, "Everything_GetResultFullPathNameW"),
                        GetDelegate<EverythingIsFolderResult>(handle, "Everything_IsFolderResult"),
                        GetDelegate<EverythingGetResultSize>(handle, "Everything_GetResultSize"),
                        GetDelegate<EverythingGetResultDateModified>(handle, "Everything_GetResultDateModified"),
                        GetDelegate<EverythingGetLastError>(handle, "Everything_GetLastError"),
                        GetDelegate<EverythingReset>(handle, "Everything_Reset"),
                        GetDelegate<EverythingCleanUp>(handle, "Everything_CleanUp"));
                }
                catch (Exception ex)
                {
                    AppLogger.LogWarning($"EverythingSearch: failed to load SDK '{candidate}' - {ex.Message}");
                }
            }

            return new EverythingSdkClient(
                "Everything SDK DLL was not found. Place the matching Everything SDK DLL next to TopToolbar.exe, under Native\\Everything, or set EVERYTHING_SDK_DLL.");
        }

        public EverythingSearchResponse Search(string query, int maxResults)
        {
            try
            {
                _reset();
                _setSearch(query);
                _setRequestFlags(EverythingRequestFullPathAndFileName | EverythingRequestSize | EverythingRequestDateModified);
                _setOffset(0);
                _setMax((uint)maxResults);
                _setSort(EverythingSortNameAscending);

                if (!_query(true))
                {
                    var errorCode = _getLastError();
                    var message = errorCode == EverythingErrorIpc
                        ? "Everything is not running. Start Everything, then search again."
                        : $"Everything query failed. SDK error code: {errorCode}.";
                    return EverythingSearchResponse.Unavailable(message);
                }

                var resultCount = Math.Min(_getNumResults(), (uint)maxResults);
                var results = new List<EverythingSearchResult>((int)resultCount);
                for (uint i = 0; i < resultCount; i++)
                {
                    var fullPath = GetFullPath(i);
                    if (string.IsNullOrWhiteSpace(fullPath))
                    {
                        continue;
                    }

                    var isFolder = _isFolderResult(i);
                    results.Add(new EverythingSearchResult
                    {
                        FullPath = fullPath,
                        Name = Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                        DirectoryPath = Path.GetDirectoryName(fullPath) ?? string.Empty,
                        IsFolder = isFolder,
                        SizeBytes = isFolder ? null : GetSize(i),
                        DateModified = GetDateModified(i),
                    });
                }

                return new EverythingSearchResponse
                {
                    IsAvailable = true,
                    StatusMessage = results.Count == 0 ? "No results." : $"{results.Count} result(s).",
                    Results = results,
                };
            }
            catch (Exception ex)
            {
                AppLogger.LogError("EverythingSearch: query failed.", ex);
                return EverythingSearchResponse.Unavailable(ex.Message);
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                _cleanUp?.Invoke();
            }
            catch
            {
            }

            if (_libraryHandle != IntPtr.Zero)
            {
                try
                {
                    NativeLibrary.Free(_libraryHandle);
                }
                catch
                {
                }
            }
        }

        private string GetFullPath(uint index)
        {
            var length = _getResultFullPathName(index, null, 0);
            if (length == 0)
            {
                return string.Empty;
            }

            var buffer = new StringBuilder((int)length + 1);
            _getResultFullPathName(index, buffer, (uint)buffer.Capacity);
            return buffer.ToString();
        }

        private long? GetSize(uint index)
        {
            return _getResultSize(index, out var size)
                ? size
                : null;
        }

        private DateTime? GetDateModified(uint index)
        {
            if (!_getResultDateModified(index, out var fileTime) || fileTime <= 0)
            {
                return null;
            }

            try
            {
                return DateTime.FromFileTimeUtc(fileTime).ToLocalTime();
            }
            catch
            {
                return null;
            }
        }

        private static T GetDelegate<T>(IntPtr libraryHandle, string exportName)
            where T : Delegate
        {
            var export = NativeLibrary.GetExport(libraryHandle, exportName);
            return Marshal.GetDelegateForFunctionPointer<T>(export);
        }

        private static IReadOnlyList<string> GetCandidateDllPaths()
        {
            var fileName = GetArchitectureDllName();
            var candidates = new List<string>();
            var explicitPath = Environment.GetEnvironmentVariable("EVERYTHING_SDK_DLL");
            if (!string.IsNullOrWhiteSpace(explicitPath))
            {
                candidates.Add(Environment.ExpandEnvironmentVariables(explicitPath.Trim()));
            }

            var baseDirectory = AppContext.BaseDirectory;
            candidates.Add(Path.Combine(baseDirectory, fileName));
            candidates.Add(Path.Combine(baseDirectory, "Native", "Everything", fileName));
            candidates.Add(Path.Combine(AppPaths.Root, "Everything", fileName));
            return candidates;
        }

        private static string GetArchitectureDllName()
        {
            return RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.Arm64 => "EverythingARM64.dll",
                Architecture.Arm => "EverythingARM.dll",
                Architecture.X86 => "Everything32.dll",
                _ => "Everything64.dll",
            };
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
        private delegate void EverythingSetSearchW([MarshalAs(UnmanagedType.LPWStr)] string search);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void EverythingSetRequestFlags(uint requestFlags);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void EverythingSetMax(uint max);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void EverythingSetOffset(uint offset);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void EverythingSetSort(uint sort);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private delegate bool EverythingQueryW([MarshalAs(UnmanagedType.Bool)] bool wait);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate uint EverythingGetNumResults();

        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
        private delegate uint EverythingGetResultFullPathNameW(uint index, StringBuilder buffer, uint maxCount);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private delegate bool EverythingIsFolderResult(uint index);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private delegate bool EverythingGetResultSize(uint index, out long size);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private delegate bool EverythingGetResultDateModified(uint index, out long fileTime);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate uint EverythingGetLastError();

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void EverythingReset();

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void EverythingCleanUp();
    }
}
