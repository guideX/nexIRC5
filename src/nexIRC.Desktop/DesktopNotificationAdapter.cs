using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using nexIRC.Application;

namespace nexIRC.Desktop;

/// <summary>
/// Optional Windows desktop adapter. It uses the small, built-in taskbar
/// balloon mechanism so the unpackaged WPF executable does not pretend to
/// provide identity-dependent Windows 10/11 toast activation. The adapter is
/// replaceable and receives already-classified application notifications.
/// </summary>
public sealed class DesktopNotificationAdapter : IDisposable
{
    private readonly Func<ApplicationPreferences> _preferences;
    private readonly NotifyIcon _icon;
    private readonly IDisposable _subscription;
    private readonly Action<IrcNotification>? _activate;
    private IrcNotification? _lastNotification;
    private int _disposed;

    public DesktopNotificationAdapter(IIrcNotificationService notifications, Func<ApplicationPreferences> preferences, Action<IrcNotification>? activate = null)
    {
        ArgumentNullException.ThrowIfNull(notifications);
        _preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));
        _activate = activate;
        _icon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "nexIRC 5",
            Visible = true
        };
        _icon.BalloonTipClicked += OnBalloonTipClicked;
        _subscription = notifications.Subscribe(OnNotification);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _subscription.Dispose();
        _icon.BalloonTipClicked -= OnBalloonTipClicked;
        _icon.Visible = false;
        _icon.Dispose();
    }

    private void OnNotification(IrcNotification notification)
    {
        if (Volatile.Read(ref _disposed) != 0 || notification.IsViewActive || notification.IsOwnMessage)
        {
            return;
        }

        var preferences = _preferences();
        if (!preferences.NotificationsEnabled)
        {
            return;
        }

        if (notification.Type == IrcNotificationType.Highlight && !preferences.HighlightNotifications)
        {
            return;
        }

        if (notification.Type == IrcNotificationType.PrivateMessage && !preferences.PrivateMessageNotifications)
        {
            return;
        }

        if (notification.Type == IrcNotificationType.Error && !preferences.ConnectionNotifications)
        {
            return;
        }

        if (notification.Type is not (IrcNotificationType.Highlight or IrcNotificationType.PrivateMessage or IrcNotificationType.Error))
        {
            return;
        }

        var title = notification.Type switch
        {
            IrcNotificationType.PrivateMessage => $"Private message · {notification.Sender ?? "nexIRC"}",
            IrcNotificationType.Highlight => $"Highlight · {notification.Sender ?? "nexIRC"}",
            _ => "nexIRC connection"
        };
        var summary = notification.Summary.Length > 240 ? notification.Summary[..240] : notification.Summary;
        _lastNotification = notification;
        try
        {
            _icon.ShowBalloonTip(3500, title, summary, notification.Type == IrcNotificationType.Error ? ToolTipIcon.Error : ToolTipIcon.Info);
        }
        catch
        {
            // OS notification failures are an optional presentation concern.
        }
    }

    private void OnBalloonTipClicked(object? sender, EventArgs e)
    {
        if (_lastNotification is { } notification)
        {
            try { _activate?.Invoke(notification); } catch { }
        }
    }
}
