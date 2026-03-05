// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Microsoft.UI.Dispatching;
using TopToolbar.ViewModels;

namespace TopToolbar.Services
{
    public sealed class NotificationService : INotificationService
    {
        private readonly DispatcherQueue _dispatcher;
        private readonly Dictionary<Guid, DispatcherQueueTimer> _timers = new();

        public NotificationService(DispatcherQueue dispatcher)
        {
            _dispatcher = dispatcher;
        }

        public ObservableCollection<NotificationItem> Items { get; } = new();

        public int MaxVisible { get; set; } = 3;

        public TimeSpan DefaultDuration { get; set; } = TimeSpan.FromSeconds(7);

        public TimeSpan SuccessDuration { get; set; } = TimeSpan.FromSeconds(4);

        public void ShowError(string message) => _ = Show(NotificationKind.Error, message, DefaultDuration);

        public void ShowWarning(string message) => _ = Show(NotificationKind.Warning, message, DefaultDuration);

        public void ShowInfo(string message) => _ = Show(NotificationKind.Info, message, DefaultDuration);

        public void ShowSuccess(string message) => _ = Show(NotificationKind.Success, message, SuccessDuration);

        public Guid ShowProgress(string message) => Show(NotificationKind.Progress, message, null);

        public void UpdateProgress(Guid notificationId, string message)
        {
            if (notificationId == Guid.Empty)
            {
                return;
            }

            var normalized = NormalizeMessage(message);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return;
            }

            RunOnUi(() =>
            {
                var index = FindItemIndex(notificationId);
                if (index < 0)
                {
                    return;
                }

                var current = Items[index];
                Items[index] = current.WithMessage(current.Kind, normalized);
            });
        }

        public void CompleteProgress(Guid notificationId, string message, bool isError = false)
        {
            if (notificationId == Guid.Empty)
            {
                if (isError)
                {
                    ShowError(message);
                }
                else
                {
                    ShowSuccess(message);
                }

                return;
            }

            var normalized = NormalizeMessage(message);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                normalized = isError ? "Operation failed." : "Operation completed.";
            }

            RunOnUi(() =>
            {
                var index = FindItemIndex(notificationId);
                if (index < 0)
                {
                    if (isError)
                    {
                        _ = Show(NotificationKind.Error, normalized, DefaultDuration);
                    }
                    else
                    {
                        _ = Show(NotificationKind.Success, normalized, SuccessDuration);
                    }

                    return;
                }

                var targetKind = isError ? NotificationKind.Error : NotificationKind.Success;
                var current = Items[index];
                Items[index] = current.WithMessage(targetKind, normalized);
                StartOrResetTimer(notificationId, isError ? DefaultDuration : SuccessDuration);
            });
        }

        private Guid Show(NotificationKind kind, string message, TimeSpan? duration)
        {
            var normalized = NormalizeMessage(message);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return Guid.Empty;
            }

            var item = new NotificationItem(kind, normalized);

            RunOnUi(() =>
            {
                Items.Insert(0, item);
                TrimOverflow();

                if (!duration.HasValue)
                {
                    return;
                }

                StartOrResetTimer(item.Id, duration.Value);
            });

            return item.Id;
        }

        private void TrimOverflow()
        {
            while (Items.Count > MaxVisible)
            {
                var toRemove = Items[Items.Count - 1];
                Items.RemoveAt(Items.Count - 1);
                StopTimer(toRemove.Id);
            }
        }

        private void Remove(Guid id)
        {
            RunOnUi(() =>
            {
                for (int i = 0; i < Items.Count; i++)
                {
                    if (Items[i].Id == id)
                    {
                        Items.RemoveAt(i);
                        break;
                    }
                }

                StopTimer(id);
            });
        }

        private void StopTimer(Guid id)
        {
            if (_timers.TryGetValue(id, out var timer))
            {
                timer.Stop();
                _timers.Remove(id);
            }
        }

        private void StartOrResetTimer(Guid id, TimeSpan duration)
        {
            StopTimer(id);

            if (_dispatcher == null)
            {
                return;
            }

            var timer = _dispatcher.CreateTimer();
            timer.Interval = duration;
            timer.IsRepeating = false;
            timer.Tick += (_, __) =>
            {
                timer.Stop();
                Remove(id);
            };

            _timers[id] = timer;
            timer.Start();
        }

        private int FindItemIndex(Guid id)
        {
            for (int i = 0; i < Items.Count; i++)
            {
                if (Items[i].Id == id)
                {
                    return i;
                }
            }

            return -1;
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

            if (!_dispatcher.TryEnqueue(new DispatcherQueueHandler(() => action())))
            {
                action();
            }
        }

        private static string NormalizeMessage(string message)
        {
            var trimmed = message?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                return string.Empty;
            }

            var normalized = trimmed
                .Replace("\r\n", " ")
                .Replace('\n', ' ')
                .Replace('\r', ' ');

            return normalized;
        }
    }
}
