// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;

namespace TopToolbar.ViewModels
{
    public enum NotificationKind
    {
        Error,
        Warning,
        Info,
        Success,
        Progress,
    }

    public sealed class NotificationItem
    {
        public NotificationItem(NotificationKind kind, string message)
            : this(Guid.NewGuid(), kind, message, DateTimeOffset.UtcNow, string.Empty)
        {
        }

        private NotificationItem(Guid id, NotificationKind kind, string message, DateTimeOffset createdAt, string actionText)
        {
            Id = id;
            Kind = kind;
            Message = message ?? string.Empty;
            CreatedAt = createdAt;
            ActionText = actionText ?? string.Empty;
        }

        public Guid Id { get; }

        public NotificationKind Kind { get; }

        public string Message { get; }

        public DateTimeOffset CreatedAt { get; }

        public string ActionText { get; }

        public bool HasAction => !string.IsNullOrWhiteSpace(ActionText);

        public NotificationItem WithMessage(NotificationKind kind, string message)
        {
            return new NotificationItem(Id, kind, message, CreatedAt, ActionText);
        }

        public NotificationItem WithActionText(string actionText)
        {
            return new NotificationItem(Id, Kind, Message, CreatedAt, actionText);
        }
    }
}
