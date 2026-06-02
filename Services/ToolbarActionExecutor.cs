// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using TopToolbar.Actions;
using TopToolbar.Logging;
using TopToolbar.Models;

namespace TopToolbar.Services
{
    public sealed class ToolbarActionExecutor
    {
        private readonly ActionProviderService _providerService;
        private readonly ActionContextFactory _contextFactory;
        private readonly DispatcherQueue _dispatcher;
        private readonly INotificationService _notificationService;
        private readonly Func<ToolbarButton, ToolbarAction, string, CancellationToken, Task> _workspaceFailureHandler;

        public ToolbarActionExecutor(
            ActionProviderService providerService,
            ActionContextFactory contextFactory,
            DispatcherQueue dispatcher = null,
            INotificationService notificationService = null,
            Func<ToolbarButton, ToolbarAction, string, CancellationToken, Task> workspaceFailureHandler = null)
        {
            _providerService = providerService ?? throw new ArgumentNullException(nameof(providerService));
            _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
            _dispatcher = dispatcher;
            _notificationService = notificationService;
            _workspaceFailureHandler = workspaceFailureHandler;
        }

        public Task ExecuteAsync(ButtonGroup group, ToolbarButton button, CancellationToken cancellationToken = default)
        {
            if (button?.Action == null)
            {
                return Task.CompletedTask;
            }

            return button.Action.Type switch
            {
                ToolbarActionType.CommandLine => ExecuteCommandLineAsync(button, button.Action),
                ToolbarActionType.Provider => ExecuteProviderActionAsync(group, button, cancellationToken),
                _ => Task.CompletedTask,
            };
        }

        private Task ExecuteCommandLineAsync(ToolbarButton button, ToolbarAction action)
        {
            var error = TryLaunchProcess(action);
            if (!string.IsNullOrWhiteSpace(error))
            {
                _notificationService?.ShowError(BuildFailureMessage(button, error));
            }

            return Task.CompletedTask;
        }

        private async Task ExecuteProviderActionAsync(ButtonGroup group, ToolbarButton button, CancellationToken cancellationToken)
        {
            var action = button.Action;
            if (string.IsNullOrWhiteSpace(action.ProviderId) || string.IsNullOrWhiteSpace(action.ProviderActionId))
            {
                AppLogger.LogWarning("ToolbarActionExecutor: provider metadata missing for dynamic action.");
                return;
            }

            RunOnUi(() =>
            {
                button.IsExecuting = true;
                button.ProgressMessage = string.Empty;
                button.ProgressValue = null;
                button.StatusMessage = string.Empty;
            });

            JsonElement? args = null;
            if (!string.IsNullOrWhiteSpace(action.ProviderArgumentsJson))
            {
                try
                {
                    using var doc = JsonDocument.Parse(action.ProviderArgumentsJson);
                    args = doc.RootElement.Clone();
                }
                catch (JsonException ex)
                {
                    AppLogger.LogWarning($"ToolbarActionExecutor: failed to parse provider arguments. - {ex.Message}");
                }
            }

            var context = _contextFactory.CreateForInvocation(group, button);
            var progress = new Progress<ActionProgress>(update =>
            {
                if (update == null)
                {
                    return;
                }

                RunOnUi(() =>
                {
                    if (update.Percent.HasValue)
                    {
                        button.ProgressValue = update.Percent.Value;
                    }

                    if (!string.IsNullOrWhiteSpace(update.Note))
                    {
                        button.ProgressMessage = update.Note;
                    }
                });
            });

            try
            {
                var result = await _providerService
                    .InvokeAsync(action.ProviderId, action.ProviderActionId, args, context, progress, cancellationToken)
                    .ConfigureAwait(false);

                if (result != null)
                {
                    var message = string.IsNullOrWhiteSpace(result.Message)
                        ? (result.Ok ? string.Empty : "Action failed.")
                        : result.Message;

                    RunOnUi(() =>
                    {
                        button.StatusMessage = message;
                    });

                    if (!result.Ok)
                    {
                        var failureMessage = BuildFailureMessage(button, message);
                        var workspaceFailureHandler = _workspaceFailureHandler;
                        if (ShouldOfferWorkspaceRemoval(action) && workspaceFailureHandler != null)
                        {
                            await workspaceFailureHandler(button, action, message, cancellationToken).ConfigureAwait(false);
                        }
                        else
                        {
                            _notificationService?.ShowError(failureMessage);
                        }
                    }
                    else if (ShouldShowWorkspaceSuccess(action))
                    {
                        var successMessage = string.IsNullOrWhiteSpace(message)
                            ? "Workspace ready."
                            : message;
                        _notificationService?.ShowSuccess(successMessage);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                RunOnUi(() =>
                {
                    button.StatusMessage = "Cancelled.";
                });
                throw;
            }
            catch (Exception ex)
            {
                var message = string.IsNullOrWhiteSpace(ex.Message) ? "Action failed." : ex.Message;
                RunOnUi(() =>
                {
                    button.StatusMessage = message;
                });
                var workspaceFailureHandler = _workspaceFailureHandler;
                if (ShouldOfferWorkspaceRemoval(action) && workspaceFailureHandler != null)
                {
                    await workspaceFailureHandler(button, action, message, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    _notificationService?.ShowError(BuildFailureMessage(button, message));
                }
                AppLogger.LogError($"ToolbarActionExecutor: provider invocation threw an exception. - {ex.Message}");
            }
            finally
            {
                RunOnUi(() =>
                {
                    button.ProgressValue = null;
                    button.ProgressMessage = string.Empty;
                    button.IsExecuting = false;
                });
            }
        }

        private static bool ShouldShowWorkspaceSuccess(ToolbarAction action)
        {
            if (action == null)
            {
                return false;
            }

            if (!string.Equals(action.ProviderId, "WorkspaceProvider", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return !string.IsNullOrWhiteSpace(action.ProviderActionId)
                && action.ProviderActionId.StartsWith("workspace.launch:", StringComparison.OrdinalIgnoreCase);
        }

        private static bool ShouldOfferWorkspaceRemoval(ToolbarAction action)
        {
            return ShouldShowWorkspaceSuccess(action);
        }

        private void RunOnUi(Action action)
        {
            if (action == null)
            {
                return;
            }

            if (_dispatcher == null || _dispatcher.HasThreadAccess)
            {
                action();
                return;
            }

            _dispatcher.TryEnqueue(() => action());
        }

        private static string TryLaunchProcess(ToolbarAction action)
        {
            if (string.IsNullOrWhiteSpace(action.Command))
            {
                return null;
            }

            try
            {
                var file = action.Command!.Trim();
                var args = action.Arguments ?? string.Empty;

                // Expand environment variables
                file = Environment.ExpandEnvironmentVariables(file);
                args = Environment.ExpandEnvironmentVariables(args);

                // Smart detection: if Command is a folder path, open with explorer
                if (Directory.Exists(file))
                {
                    AppLogger.LogInfo($"Launch: detected folder path, opening with explorer: '{file}'");
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"\"{file}\"",
                        UseShellExecute = true,
                    });
                    return null;
                }

                // Smart detection: if Command is a URL, open with default browser
                if (Uri.TryCreate(file, UriKind.Absolute, out var uri) &&
                    (uri.Scheme == "http" || uri.Scheme == "https" || uri.Scheme == "mailto"))
                {
                    AppLogger.LogInfo($"Launch: detected URL, opening with default handler: '{file}'");
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = file,
                        UseShellExecute = true,
                    });
                    return null;
                }

                // Smart detection: if Command is a file (not .exe), open with default app
                if (File.Exists(file))
                {
                    var fileExt = Path.GetExtension(file)?.ToLowerInvariant();
                    if (fileExt != ".exe" && fileExt != ".bat" && fileExt != ".cmd" && fileExt != ".ps1" && fileExt != ".vbs" && fileExt != ".js")
                    {
                        AppLogger.LogInfo($"Launch: detected file, opening with default app: '{file}'");
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = file,
                            UseShellExecute = true,
                        });
                        return null;
                    }
                }

                // If quoted path, extract path
                if (file.StartsWith('"'))
                {
                    int end = file.IndexOf('"', 1);
                    if (end > 1)
                    {
                        args = file.Substring(end + 1).TrimStart() + (string.IsNullOrEmpty(args) ? string.Empty : (" " + args));
                        file = file.Substring(1, end - 1);
                    }
                }
                else
                {
                    // Handle common pattern: `notepad "C:\\path\\file.txt"`
                    // Split on first space to separate executable and inline arguments
                    int space = file.IndexOf(' ');
                    if (space > 0)
                    {
                        var candidateExe = file.Substring(0, space).Trim();
                        var remainder = file.Substring(space + 1).TrimStart();

                        // Treat remainder as part of arguments while keeping existing explicit Arguments
                        args = string.IsNullOrEmpty(args) ? remainder : $"{remainder} {args}".Trim();
                        file = candidateExe;
                    }
                }

                var workingDir = string.IsNullOrWhiteSpace(action.WorkingDirectory) ? Environment.CurrentDirectory : action.WorkingDirectory;

                // Resolve via WorkingDirectory and PATH if needed (handles name-only commands like `code`)
                var resolved = ResolveCommandToFilePath(file, workingDir);
                if (!string.IsNullOrEmpty(resolved))
                {
                    file = resolved;
                }

                var ext = Path.GetExtension(file)?.ToLowerInvariant();

                ProcessStartInfo psi;

                if (ext == ".ps1")
                {
                    // PowerShell script: prefer PowerShell 7 if available, else Windows PowerShell
                    var host = "pwsh.exe";
                    psi = new ProcessStartInfo
                    {
                        FileName = host,
                        Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{file}\" {args}".Trim(),
                        WorkingDirectory = workingDir,
                        UseShellExecute = true,
                        Verb = action.RunAsAdmin ? "runas" : "open",
                    };
                }
                else if (ext == ".bat" || ext == ".cmd")
                {
                    psi = new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = $"/c \"\"{file}\" {args}\"".Trim(),
                        WorkingDirectory = workingDir,
                        UseShellExecute = true,
                        Verb = action.RunAsAdmin ? "runas" : "open",
                    };
                }
                else if (ext == ".vbs" || ext == ".js")
                {
                    psi = new ProcessStartInfo
                    {
                        FileName = "wscript.exe",
                        Arguments = $"\"{file}\" {args}".Trim(),
                        WorkingDirectory = workingDir,
                        UseShellExecute = true,
                        Verb = action.RunAsAdmin ? "runas" : "open",
                    };
                }
                else
                {
                    // For explorer.exe, ensure path arguments are properly quoted
                    var finalArgs = args;
                    var fileNameLower = Path.GetFileName(file)?.ToLowerInvariant();
                    if (fileNameLower == "explorer.exe" && !string.IsNullOrWhiteSpace(args))
                    {
                        var trimmedArgs = args.Trim();
                        // If args looks like a path and isn't already quoted, quote it
                        if (!trimmedArgs.StartsWith('"') && !trimmedArgs.StartsWith('/') && !trimmedArgs.StartsWith('-'))
                        {
                            // Expand environment variables in the path
                            var expandedPath = Environment.ExpandEnvironmentVariables(trimmedArgs);
                            finalArgs = $"\"{expandedPath}\"";
                        }
                    }

                    psi = new ProcessStartInfo
                    {
                        FileName = file,
                        Arguments = finalArgs,
                        WorkingDirectory = workingDir,
                        UseShellExecute = true,
                        Verb = action.RunAsAdmin ? "runas" : "open",
                    };
                }

                AppLogger.LogInfo($"Launch: file='{file}', ext='{ext}', args='{args}', wd='{workingDir}', runAsAdmin={action.RunAsAdmin}");
                var p = Process.Start(psi);
                if (p != null)
                {
                    AppLogger.LogInfo($"Launch: started pid={p.Id}");
                }
                else
                {
                    // Shell-activated launches (URI/document handlers) may succeed without a process handle.
                    AppLogger.LogInfo("Launch: started via shell without process handle.");
                }
                return null;
            }
            catch (Win32Exception ex)
            {
                AppLogger.LogError($"Launch: Win32Exception {ex.NativeErrorCode} {ex.Message}");
                return ex.Message;
            }
            catch (Exception ex)
            {
                AppLogger.LogError($"Launch: Exception {ex.GetType().Name} {ex.Message}");
                return ex.Message;
            }
        }

        private static string BuildFailureMessage(ToolbarButton button, string detail)
        {
            var name = button?.DisplayName ?? string.Empty;
            if (string.IsNullOrWhiteSpace(detail))
            {
                return string.IsNullOrWhiteSpace(name) ? "Action failed." : $"{name} failed.";
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                return detail;
            }

            return $"{name}: {detail}";
        }

        private static string ResolveCommandToFilePath(string file, string workingDir)
        {
            if (string.IsNullOrWhiteSpace(file))
            {
                return null;
            }

            try
            {
                var candidate = file.Trim();
                candidate = Environment.ExpandEnvironmentVariables(candidate);

                bool hasRoot = Path.IsPathRooted(candidate);
                bool hasExt = Path.HasExtension(candidate);

                if (hasRoot || candidate.Contains('\\') || candidate.Contains('/'))
                {
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }

                    // Try alternate PATHEXT extensions as a fallback
                    var dirName = Path.GetDirectoryName(candidate) ?? string.Empty;
                    var nameNoExtOnly = Path.GetFileNameWithoutExtension(candidate);
                    var nameNoExt = string.IsNullOrEmpty(dirName) ? nameNoExtOnly : Path.Combine(dirName, nameNoExtOnly);
                    foreach (var ext in GetPathExtensions())
                    {
                        var p = nameNoExt + ext;
                        if (File.Exists(p))
                        {
                            return p;
                        }
                    }

                    return null;
                }

                var dirs = new System.Collections.Generic.List<string>();
                if (!string.IsNullOrWhiteSpace(workingDir) && Directory.Exists(workingDir))
                {
                    dirs.Add(workingDir);
                }

                dirs.Add(Environment.CurrentDirectory);
                var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
                foreach (var d in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    dirs.Add(d);
                }

                foreach (var dir in dirs)
                {
                    var basePath = Path.Combine(dir, candidate);
                    if (hasExt)
                    {
                        if (File.Exists(basePath))
                        {
                            return basePath;
                        }

                        var nameNoExtOnly = Path.GetFileNameWithoutExtension(candidate);
                        var nameNoExt = Path.Combine(dir, nameNoExtOnly);
                        foreach (var ext in GetPathExtensions())
                        {
                            var p = nameNoExt + ext;
                            if (File.Exists(p))
                            {
                                return p;
                            }
                        }
                    }
                    else
                    {
                        foreach (var ext in GetPathExtensions())
                        {
                            var p = basePath + ext;
                            if (File.Exists(p))
                            {
                                return p;
                            }
                        }
                    }
                }
            }
            catch
            {
            }

            return null;
        }

        private static System.Collections.Generic.IEnumerable<string> GetPathExtensions()
        {
            var pathext = Environment.GetEnvironmentVariable("PATHEXT");
            if (string.IsNullOrWhiteSpace(pathext))
            {
                return new[] { ".COM", ".EXE", ".BAT", ".CMD", ".VBS", ".JS", ".WS", ".MSC", ".PS1" };
            }

            return pathext.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim());
        }
    }
}
