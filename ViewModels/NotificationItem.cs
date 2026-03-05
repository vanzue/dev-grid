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
            : this(Guid.NewGuid(), kind, message, DateTimeOffset.UtcNow)
        {
        }

        private NotificationItem(Guid id, NotificationKind kind, string message, DateTimeOffset createdAt)
        {
            Id = id;
            Kind = kind;
            Message = message ?? string.Empty;
            CreatedAt = createdAt;
        }

        public Guid Id { get; }

        public NotificationKind Kind { get; }

        public string Message { get; }

        public DateTimeOffset CreatedAt { get; }

        public NotificationItem WithMessage(NotificationKind kind, string message)
        {
            return new NotificationItem(Id, kind, message, CreatedAt);
        }
    }
}
