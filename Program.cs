// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using TopToolbar.Logging;
using TopToolbar.Services.Workspaces;
using TopToolbar.Serialization;

namespace TopToolbar
{
    public class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            try
            {
                AppLogger.Initialize(AppPaths.Logs);
                EnsureAppDirectories();
                AppLogger.LogInfo($"Logger initialized. Logs directory: {AppPaths.Logs}");
                AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                {
                    try
                    {
                        var message =
                            $"AppDomain unhandled exception (IsTerminating={e.IsTerminating})";
                        if (e.ExceptionObject is Exception exception)
                        {
                            AppLogger.LogError(message, exception);
                        }
                        else
                        {
                            AppLogger.LogError($"{message} - {e.ExceptionObject}");
                        }
                    }
                    catch { }
                };
                TaskScheduler.UnobservedTaskException += (_, e) =>
                {
                    try
                    {
                        AppLogger.LogError("Unobserved task exception", e.Exception);
                        e.SetObserved();
                    }
                    catch { }
                };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"AppLogger init failed: {ex.Message}");
            }

            if (TryHandleCommandLine(args))
            {
                return;
            }

            Application.Start(_ =>
            {
                _ = new App();
            });
        }

        private static bool TryHandleCommandLine(string[] args)
        {
            if (args == null || args.Length == 0)
            {
                return false;
            }

            if (!string.Equals(args[0], "ws", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            try
            {
                var exitCode = HandleWorkspaceCommandAsync(args.Skip(1).ToArray()).GetAwaiter().GetResult();
                Environment.ExitCode = exitCode;
            }
            catch (Exception ex)
            {
                AppLogger.LogError("WorkspaceCommand: unhandled exception", ex);
                Console.Error.WriteLine(ex.Message);
                Environment.ExitCode = 1;
            }

            return true;
        }

        private static async Task<int> HandleWorkspaceCommandAsync(string[] args)
        {
            if (args == null || args.Length == 0)
            {
                PrintWorkspaceHelp();
                return 2;
            }

            var command = args[0];
            var commandArgs = args.Skip(1).ToArray();
            var suppressConsoleTrace = HasOption(commandArgs, "--quiet")
                || HasOption(commandArgs, "--json");
            WorkspaceRuntimeConsoleOptions.EnableConsoleTrace = !suppressConsoleTrace;

            try
            {
                if (!string.Equals(command, "switch", StringComparison.OrdinalIgnoreCase))
                {
                    Console.Error.WriteLine($"Unknown ws command '{command}'.");
                    PrintWorkspaceHelp();
                    return 2;
                }

                return await HandleSwitchCommandAsync(commandArgs).ConfigureAwait(false);
            }
            finally
            {
                WorkspaceRuntimeConsoleOptions.EnableConsoleTrace = true;
            }
        }

        private static async Task<int> HandleSwitchCommandAsync(string[] args)
        {
            var outputJson = HasOption(args, "--json");
            var quiet = HasOption(args, "--quiet");
            var workspaceId = GetOptionValue(args, "--id");
            if (string.IsNullOrWhiteSpace(workspaceId))
            {
                Console.Error.WriteLine("Switch requires --id <workspaceId>.");
                return 2;
            }

            using var runtime = new WorkspacesRuntimeService();
            var diagnostics = await runtime
                .LaunchWorkspaceDetailedAsync(workspaceId, CancellationToken.None)
                .ConfigureAwait(false);

            if (outputJson)
            {
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(
                    diagnostics,
                    WorkspaceProviderJsonContext.Default.WorkspaceSwitchDiagnostics));
            }
            else if (!quiet)
            {
                if (diagnostics?.Ok == true)
                {
                    Console.WriteLine($"Workspace '{workspaceId}' launched.");
                }
                else
                {
                    Console.Error.WriteLine($"Workspace '{workspaceId}' failed to launch.");
                }
            }

            return diagnostics?.Ok == true && (diagnostics.Errors == null || diagnostics.Errors.Count == 0)
                ? 0
                : 4;
        }

        private static string GetOptionValue(string[] args, string option)
        {
            if (args == null || args.Length == 0 || string.IsNullOrWhiteSpace(option))
            {
                return string.Empty;
            }

            for (var i = 0; i < args.Length; i++)
            {
                if (!string.Equals(args[i], option, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (i + 1 >= args.Length)
                {
                    return string.Empty;
                }

                var next = args[i + 1];
                if (!string.IsNullOrWhiteSpace(next) && next.StartsWith("--", StringComparison.Ordinal))
                {
                    return string.Empty;
                }

                return next;
            }

            return string.Empty;
        }

        private static bool HasOption(string[] args, string option)
        {
            if (args == null || args.Length == 0 || string.IsNullOrWhiteSpace(option))
            {
                return false;
            }

            return args.Any(arg => string.Equals(arg, option, StringComparison.OrdinalIgnoreCase));
        }

        private static void PrintWorkspaceHelp()
        {
            Console.WriteLine("ws switch --id <workspaceId> [--json] [--quiet]");
        }

        private static void EnsureAppDirectories()
        {
            try
            {
                Directory.CreateDirectory(AppPaths.Root);
                Directory.CreateDirectory(AppPaths.IconsDirectory);
                Directory.CreateDirectory(AppPaths.ProfilesDirectory);
                Directory.CreateDirectory(AppPaths.ProvidersDirectory);
                Directory.CreateDirectory(AppPaths.ConfigDirectory);
            }
            catch (Exception ex)
            {
                AppLogger.LogError("Failed to ensure data directories", ex);
            }
        }
    }
}
