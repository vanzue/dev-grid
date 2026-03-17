// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TopToolbar.Services.Agents
{
    internal static class ProcessTreeHelper
    {
        private const uint SnapshotProcess = 0x00000002;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ProcessEntry32
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Process32First(IntPtr hSnapshot, ref ProcessEntry32 lppe);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Process32Next(IntPtr hSnapshot, ref ProcessEntry32 lppe);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);

        public static int FindAncestorProcessIdByName(int processId, params string[] processNames)
        {
            if (processId <= 0 || processNames == null || processNames.Length == 0)
            {
                return 0;
            }

            var map = BuildProcessParentMap();
            if (map.Count == 0)
            {
                return 0;
            }

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in processNames)
            {
                var value = (raw ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                names.Add(value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? value[..^4] : value);
            }

            var cursor = processId;
            for (var i = 0; i < 24; i++)
            {
                if (!map.TryGetValue(cursor, out var node) || node.ParentProcessId <= 0)
                {
                    return 0;
                }

                var parentId = node.ParentProcessId;
                if (!map.TryGetValue(parentId, out var parentNode))
                {
                    return 0;
                }

                var parentName = parentNode.ExecutableName;
                if (!string.IsNullOrWhiteSpace(parentName) && names.Contains(parentName))
                {
                    return parentId;
                }

                cursor = parentId;
            }

            return 0;
        }

        public static bool ProcessExists(int processId)
        {
            if (processId <= 0)
            {
                return false;
            }

            try
            {
                using var process = Process.GetProcessById(processId);
                return !process.HasExited;
            }
            catch
            {
                return false;
            }
        }

        public static int FindDescendantProcessIdByName(int rootProcessId, params string[] processNames)
        {
            if (rootProcessId <= 0 || processNames == null || processNames.Length == 0)
            {
                return 0;
            }

            var map = BuildProcessParentMap();
            if (map.Count == 0 || !map.ContainsKey(rootProcessId))
            {
                return 0;
            }

            var targetNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in processNames)
            {
                var value = (raw ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                targetNames.Add(value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? value[..^4] : value);
            }

            if (targetNames.Count == 0)
            {
                return 0;
            }

            var childrenByParent = new Dictionary<int, List<int>>();
            foreach (var entry in map)
            {
                var pid = entry.Key;
                var parentPid = entry.Value.ParentProcessId;
                if (!childrenByParent.TryGetValue(parentPid, out var children))
                {
                    children = new List<int>();
                    childrenByParent[parentPid] = children;
                }

                children.Add(pid);
            }

            var queue = new Queue<int>();
            var visited = new HashSet<int>();
            queue.Enqueue(rootProcessId);
            visited.Add(rootProcessId);

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (!childrenByParent.TryGetValue(current, out var children) || children == null)
                {
                    continue;
                }

                foreach (var childPid in children)
                {
                    if (!visited.Add(childPid))
                    {
                        continue;
                    }

                    if (map.TryGetValue(childPid, out var node)
                        && !string.IsNullOrWhiteSpace(node.ExecutableName)
                        && targetNames.Contains(node.ExecutableName))
                    {
                        return childPid;
                    }

                    queue.Enqueue(childPid);
                }
            }

            return 0;
        }

        private static Dictionary<int, ProcessNode> BuildProcessParentMap()
        {
            var result = new Dictionary<int, ProcessNode>();
            var snapshot = CreateToolhelp32Snapshot(SnapshotProcess, 0);
            if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
            {
                return result;
            }

            try
            {
                var entry = new ProcessEntry32
                {
                    dwSize = (uint)Marshal.SizeOf<ProcessEntry32>(),
                };

                if (!Process32First(snapshot, ref entry))
                {
                    return result;
                }

                do
                {
                    var pid = unchecked((int)entry.th32ProcessID);
                    var parentPid = unchecked((int)entry.th32ParentProcessID);
                    var name = (entry.szExeFile ?? string.Empty).Trim();
                    if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        name = name[..^4];
                    }

                    result[pid] = new ProcessNode(parentPid, name);
                }
                while (Process32Next(snapshot, ref entry));
            }
            finally
            {
                _ = CloseHandle(snapshot);
            }

            return result;
        }

        private readonly record struct ProcessNode(int ParentProcessId, string ExecutableName);
    }
}
