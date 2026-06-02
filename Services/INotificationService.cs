// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Threading.Tasks;
using TopToolbar.ViewModels;

namespace TopToolbar.Services
{
    public interface INotificationService
    {
        void ShowError(string message);

        void ShowWarning(string message);

        void ShowInfo(string message);

        void ShowSuccess(string message);

        Guid ShowProgress(string message);

        Guid ShowAction(NotificationKind kind, string message, string actionText, Func<Task> action, TimeSpan? duration = null);

        void UpdateProgress(Guid notificationId, string message);

        void CompleteProgress(Guid notificationId, string message, bool isError = false);
    }
}
