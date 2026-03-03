// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using TopToolbar.Logging;
using TopToolbar.Services.Pinning;
using TopToolbar.Services.ShellIntegration;
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

            try
            {
                // Win11 menu integration is provided by the packaged COM verb.
                // Always remove legacy HKCU shell verbs to avoid duplicate entries.
                ContextMenuRegistrationService.RemoveRegistrationForCurrentUser();
            }
            catch (Exception ex)
            {
                AppLogger.LogWarning($"ContextMenuRegistration: cleanup failed - {ex.Message}");
            }

            Application.Start(args =>
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

            if (string.Equals(args[0], "ws", StringComparison.OrdinalIgnoreCase))
            {
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

            for (var i = 0; i < args.Length; i++)
            {
                if (!string.Equals(args[i], "--pin", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (i + 1 >= args.Length)
                {
                    AppLogger.LogWarning("PinCommand: '--pin' argument requires a path.");
                    return true;
                }

                var inputPath = args[i + 1];
                var ok = ToolbarPinService.TryPinPath(inputPath, out var message);
                if (ok)
                {
                    AppLogger.LogInfo($"PinCommand: '{inputPath}' => {message}");
                    Environment.ExitCode = 0;
                }
                else
                {
                    AppLogger.LogWarning($"PinCommand: '{inputPath}' failed - {message}");
                    Environment.ExitCode = 1;
                }

                return true;
            }

            return false;
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
            var suppressConsoleTrace = HasOption(commandArgs, "--quiet") || HasOption(commandArgs, "--json");
            WorkspaceRuntimeConsoleOptions.EnableConsoleTrace = !suppressConsoleTrace;

            try
            {
                using var orchestrator = new WorkspaceTemplateOrchestrator();
                switch (command.ToLowerInvariant())
                {
                    case "templates":
                        return await HandleTemplatesCommandAsync(orchestrator, commandArgs).ConfigureAwait(false);

                    case "new":
                        return await HandleNewCommandAsync(orchestrator, commandArgs).ConfigureAwait(false);

                    case "switch":
                        return await HandleSwitchCommandAsync(orchestrator, commandArgs).ConfigureAwait(false);

                    default:
                        Console.Error.WriteLine($"Unknown ws command '{command}'.");
                        PrintWorkspaceHelp();
                        return 2;
                }
            }
            finally
            {
                WorkspaceRuntimeConsoleOptions.EnableConsoleTrace = true;
            }
        }

        private static async Task<int> HandleTemplatesCommandAsync(WorkspaceTemplateOrchestrator orchestrator, string[] args)
        {
            if (args.Length > 0 && string.Equals(args[0], "show", StringComparison.OrdinalIgnoreCase))
            {
                if (args.Length < 2 || string.IsNullOrWhiteSpace(args[1]))
                {
                    Console.Error.WriteLine("Template name is required.");
                    return 2;
                }

                var templateName = args[1];
                if (string.IsNullOrWhiteSpace(templateName))
                {
                    Console.Error.WriteLine("Template name is required.");
                    return 2;
                }

                var template = await orchestrator.GetTemplateAsync(templateName, default).ConfigureAwait(false);
                if (template == null)
                {
                    Console.Error.WriteLine($"Template '{templateName}' was not found.");
                    return 3;
                }

                Console.WriteLine(JsonSerializer.Serialize(template, WorkspaceProviderJsonContext.Default.TemplateDefinition));
                return 0;
            }

            if (args.Length > 0)
            {
                Console.Error.WriteLine($"Unknown templates subcommand '{args[0]}'.");
                return 2;
            }

            var templates = await orchestrator.ListTemplatesAsync(default).ConfigureAwait(false);
            foreach (var template in templates.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
            {
                var description = string.IsNullOrWhiteSpace(template.Description) ? string.Empty : template.Description;
                Console.WriteLine($"{template.Name}\t{template.DisplayName}\t{description}");
            }

            return 0;
        }

        private static async Task<int> HandleNewCommandAsync(WorkspaceTemplateOrchestrator orchestrator, string[] args)
        {
            var template = GetOptionValue(args, "--template");
            var preset = GetOptionValue(args, "--preset");
            var layout = GetOptionValue(args, "--layout");
            var name = GetOptionValue(args, "--name");
            var repo = GetOptionValue(args, "--repo");
            var saveTemplate = GetOptionValue(args, "--save-template");
            var focusRaw = GetOptionValue(args, "--focus");
            var appSpecs = GetOptionValues(args, "--app");
            var noLaunch = HasOption(args, "--no-launch");

            if (string.IsNullOrWhiteSpace(name))
            {
                Console.Error.WriteLine("Workspace name is required.");
                return 2;
            }

            if (!string.IsNullOrWhiteSpace(template) && (!string.IsNullOrWhiteSpace(preset) || appSpecs.Count > 0))
            {
                Console.Error.WriteLine("Conflicting options: --template cannot be combined with --preset or --app.");
                return 2;
            }

            if (string.IsNullOrWhiteSpace(template) && string.IsNullOrWhiteSpace(preset) && appSpecs.Count == 0)
            {
                Console.Error.WriteLine("Either --template, --preset, or --app must be provided.");
                return 2;
            }

            var focusRoles = ParseFocusRoles(focusRaw);
            var parsedApps = new List<WorkspaceAppSpec>();
            foreach (var raw in appSpecs)
            {
                if (!AppSpecParser.TryParse(raw, out var parsed, out var parseError))
                {
                    Console.Error.WriteLine($"Invalid --app spec '{raw}': expected key=value pairs including role and exe. ({parseError})");
                    return 2;
                }

                parsedApps.Add(parsed);
            }

            var request = new WorkspaceTemplateOrchestrator.CreateWorkspaceRequest
            {
                TemplateName = template,
                PresetName = preset,
                LayoutStrategy = layout,
                InstanceName = name,
                RepoRoot = repo,
                FocusRoles = focusRoles,
                Apps = parsedApps,
                SaveTemplateName = saveTemplate,
                NoLaunch = noLaunch,
            };

            var result = await orchestrator
                .CreateWorkspaceAsync(request, default)
                .ConfigureAwait(false);

            if (result.Success)
            {
                Console.WriteLine(result.Message);
                if (result.Workspace != null)
                {
                    Console.WriteLine($"workspaceId={result.Workspace.Id}");
                }

                return 0;
            }

            Console.Error.WriteLine(result.Message);
            if (result.Workspace != null)
            {
                return 4;
            }

            return result.Message.Contains("was not found", StringComparison.OrdinalIgnoreCase) ? 3 : 2;
        }

        private static async Task<int> HandleSwitchCommandAsync(WorkspaceTemplateOrchestrator orchestrator, string[] args)
        {
            var outputJson = HasOption(args, "--json");
            var quiet = HasOption(args, "--quiet");
            var workspaceId = GetOptionValue(args, "--id");
            if (!string.IsNullOrWhiteSpace(workspaceId))
            {
                var byId = await orchestrator.SwitchByIdAsync(workspaceId, default).ConfigureAwait(false);
                if (byId.Success)
                {
                    if (outputJson)
                    {
                        PrintSwitchDiagnostics(byId.Diagnostics);
                    }
                    else if (!quiet)
                    {
                        Console.WriteLine(byId.Message);
                    }

                    return HasSwitchErrors(byId.Diagnostics) ? 5 : 0;
                }

                if (outputJson)
                {
                    PrintSwitchDiagnostics(byId.Diagnostics);
                }
                else if (!quiet)
                {
                    Console.Error.WriteLine(byId.Message);
                }

                return 4;
            }

            var template = GetOptionValue(args, "--template");
            var name = GetOptionValue(args, "--name");
            if (string.IsNullOrWhiteSpace(template) || string.IsNullOrWhiteSpace(name))
            {
                Console.Error.WriteLine("Switch requires --id or (--template and --name).");
                return 2;
            }

            var byName = await orchestrator
                .SwitchByTemplateAndNameAsync(template, name, default)
                .ConfigureAwait(false);
            if (byName.Success)
            {
                if (outputJson)
                {
                    PrintSwitchDiagnostics(byName.Diagnostics);
                }
                else if (!quiet)
                {
                    Console.WriteLine(byName.Message);
                }

                return HasSwitchErrors(byName.Diagnostics) ? 5 : 0;
            }

            if (outputJson)
            {
                PrintSwitchDiagnostics(byName.Diagnostics);
            }
            else if (!quiet)
            {
                Console.Error.WriteLine(byName.Message);
            }

            return 4;
        }

        private static bool HasSwitchErrors(WorkspaceSwitchDiagnostics diagnostics)
        {
            return diagnostics != null
                && diagnostics.Errors != null
                && diagnostics.Errors.Count > 0;
        }

        private static void PrintSwitchDiagnostics(WorkspaceSwitchDiagnostics diagnostics)
        {
            if (diagnostics == null)
            {
                return;
            }

            Console.WriteLine(JsonSerializer.Serialize(diagnostics, WorkspaceProviderJsonContext.Default.WorkspaceSwitchDiagnostics));
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

        private static List<string> GetOptionValues(string[] args, string option)
        {
            var values = new List<string>();
            if (args == null || args.Length == 0 || string.IsNullOrWhiteSpace(option))
            {
                return values;
            }

            for (var i = 0; i < args.Length; i++)
            {
                if (!string.Equals(args[i], option, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (i + 1 < args.Length && !string.IsNullOrWhiteSpace(args[i + 1]) && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    values.Add(args[i + 1]);
                }
            }

            return values;
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
            Console.WriteLine("ws templates");
            Console.WriteLine("ws templates show <template>");
            Console.WriteLine("ws new --template <template> --name \"<name>\" [--repo <path>] [--focus <roles>] [--no-launch]");
            Console.WriteLine("ws new --name \"<name>\" --repo <path> --preset <preset> [--focus <roles>] [--save-template <templateId>] [--no-launch]");
            Console.WriteLine("ws new --name \"<name>\" --layout <layout> --app \"role=...,exe=...\" [--app \"...\"] [--focus <roles>] [--save-template <templateId>] [--no-launch]");
            Console.WriteLine("ws switch --id <workspaceId> [--json] [--quiet]");
            Console.WriteLine("ws switch --template <template> --name \"<instanceName>\" [--json] [--quiet]");
        }

        private static IReadOnlyList<string> ParseFocusRoles(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return Array.Empty<string>();
            }

            return raw
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(role => role.Trim().ToLowerInvariant())
                .Where(role => !string.IsNullOrWhiteSpace(role))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
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
                Directory.CreateDirectory(WorkspaceStoragePaths.GetTemplatesDirectoryPath());
                BuiltInTemplateSeeder.RemoveLegacyDefaultsOnceAsync(default).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                AppLogger.LogError("Failed to ensure data directories", ex);
            }
        }

    }
}
