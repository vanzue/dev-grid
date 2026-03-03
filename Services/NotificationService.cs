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

        public void ShowError(string message) => Show(NotificationKind.Error, message, DefaultDuration);

        public void ShowWarning(string message) => Show(NotificationKind.Warning, message, DefaultDuration);

        public void ShowInfo(string message) => Show(NotificationKind.Info, message, DefaultDuration);

        public void ShowSuccess(string message) => Show(NotificationKind.Info, message, SuccessDuration);

        private void Show(NotificationKind kind, string message, TimeSpan duration)
        {
            var normalized = NormalizeMessage(message);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return;
            }

            var item = new NotificationItem(kind, normalized);

            RunOnUi(() =>
            {
                Items.Insert(0, item);
                TrimOverflow();

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
                    Remove(item.Id);
                };

                _timers[item.Id] = timer;
                timer.Start();
            });
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
