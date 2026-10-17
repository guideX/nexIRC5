using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using nexIRC.Application;

namespace nexIRC.Desktop;

/// <summary>
/// Bounded WPF input telemetry enabled only by an explicit Phase 54R3 test flag.
/// It records event classifications and native message IDs, never key values,
/// text, conversation identities, or pointer coordinates.
/// </summary>
internal static class Phase54R3InputProbe
{
    private const string ArgumentPrefix = "--phase54r3-input-probe=";
    private static string? _outputPath;
    private static Probe? _activeProbe;

    public static void ConfigureFromArguments(IEnumerable<string> arguments)
    {
        var argument = arguments.FirstOrDefault(value => value.StartsWith(ArgumentPrefix, StringComparison.OrdinalIgnoreCase));
        if (argument is null) return;

        var requestedPath = argument[ArgumentPrefix.Length..];
        if (string.IsNullOrWhiteSpace(requestedPath))
            throw new ArgumentException("The Phase 54R3 input probe requires a log path.");

        _outputPath = Path.GetFullPath(requestedPath);
    }

    public static void Attach(Window window)
    {
        if (_outputPath is not null)
            _activeProbe = new Probe(window, _outputPath);
    }

    public static void RecordAppEvent(string classification) => _activeProbe?.RecordAppEvent(classification);

    public static void RecordContextAction(string actionId, string? networkName, string? targetName) =>
        _activeProbe?.RecordContextAction(actionId, networkName, targetName);

    public static void RecordConversationState(string phase, WorkspaceView? view, string? composerText) =>
        _activeProbe?.RecordConversationState(phase, view, composerText);

    public static void RecordPinnedActivation(PinnedConversationNavigationItem item, bool isOpen) =>
        _activeProbe?.RecordPinnedActivation(item, isOpen);

    private sealed class Probe : IDisposable
    {
        private const int MaximumRecords = 256;
        private const int MaximumRecordsPerClassification = 8;
        private const int WmKeyDown = 0x0100;
        private const int WmKeyUp = 0x0101;
        private const int WmSysKeyDown = 0x0104;
        private const int WmSysKeyUp = 0x0105;
        private const int WmContextMenu = 0x007B;
        private const int WmLeftButtonDown = 0x0201;
        private const int WmLeftButtonUp = 0x0202;
        private const int WmRightButtonDown = 0x0204;
        private const int WmRightButtonUp = 0x0205;
        private const int WmNonClientLeftButtonDown = 0x00A1;
        private const int WmNonClientLeftButtonUp = 0x00A2;
        private readonly Window _window;
        private readonly MainWindowViewModel? _viewModel;
        private readonly StreamWriter _writer;
        private readonly Dictionary<string, int> _classificationCounts = new(StringComparer.Ordinal);
        private HwndSource? _source;
        private int _records;
        private bool _disposed;

        public Probe(Window window, string outputPath)
        {
            _window = window;
            _viewModel = (window as MainWindow)?.ViewModel;
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            _writer = new StreamWriter(new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.Read),
                new System.Text.UTF8Encoding(false)) { AutoFlush = true };
            _window.SourceInitialized += OnSourceInitialized;
            _window.Closed += OnClosed;
            _window.AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(OnPreviewMouseDown), true);
            _window.AddHandler(Mouse.PreviewMouseUpEvent, new MouseButtonEventHandler(OnPreviewMouseUp), true);
            _window.AddHandler(Mouse.MouseDownEvent, new MouseButtonEventHandler(OnMouseDown), true);
            _window.AddHandler(Mouse.MouseUpEvent, new MouseButtonEventHandler(OnMouseUp), true);
            _window.AddHandler(Keyboard.PreviewKeyDownEvent, new System.Windows.Input.KeyEventHandler(OnPreviewKeyDown), true);
            _window.AddHandler(Keyboard.PreviewKeyUpEvent, new System.Windows.Input.KeyEventHandler(OnPreviewKeyUp), true);
            _window.AddHandler(Keyboard.KeyDownEvent, new System.Windows.Input.KeyEventHandler(OnKeyDown), true);
            _window.AddHandler(Keyboard.KeyUpEvent, new System.Windows.Input.KeyEventHandler(OnKeyUp), true);
            _window.AddHandler(ContextMenuService.ContextMenuOpeningEvent, new ContextMenuEventHandler(OnContextMenuOpening), true);
            InputManager.Current.PreProcessInput += OnPreProcessInput;
            if (_viewModel is not null)
            {
                _viewModel.PinnedConversations.CollectionChanged += OnPinnedConversationsChanged;
                RecordPinnedProjection();
            }
            Write("PROBE_READY");
        }

        private void OnSourceInitialized(object? sender, EventArgs e)
        {
            _source = HwndSource.FromHwnd(new WindowInteropHelper(_window).Handle);
            _source?.AddHook(WindowProcedure);
            Write(_source is null ? "HWND_HOOK_UNAVAILABLE" : "HWND_HOOK_READY");
        }

        private IntPtr WindowProcedure(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if ((message >= 0x0200 && message <= 0x020E)
                || message is WmKeyDown or WmKeyUp or WmSysKeyDown or WmSysKeyUp or WmContextMenu
                    or WmNonClientLeftButtonDown or WmNonClientLeftButtonUp)
            {
                Write($"WIN32_MESSAGE id=0x{message:X4}");
            }
            return IntPtr.Zero;
        }

        private void OnPreProcessInput(object? sender, PreProcessInputEventArgs e)
        {
            if (e.StagingItem.Input is System.Windows.Input.MouseEventArgs mouse)
                Write($"WPF_PREPROCESS kind={mouse.GetType().Name}");
            else if (e.StagingItem.Input is System.Windows.Input.KeyEventArgs key)
                Write($"WPF_PREPROCESS kind={key.GetType().Name}");
        }

        private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e) => Write("WPF_PREVIEW_MOUSE_DOWN");

        private void OnPreviewMouseUp(object sender, MouseButtonEventArgs e) => Write("WPF_PREVIEW_MOUSE_UP");

        private void OnMouseDown(object sender, MouseButtonEventArgs e) => Write("WPF_MOUSE_DOWN");

        private void OnMouseUp(object sender, MouseButtonEventArgs e) => Write("WPF_MOUSE_UP");

        private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e) => Write("WPF_PREVIEW_KEY_DOWN");

        private void OnPreviewKeyUp(object sender, System.Windows.Input.KeyEventArgs e) => Write("WPF_PREVIEW_KEY_UP");

        private void OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e) => Write("WPF_KEY_DOWN");

        private void OnKeyUp(object sender, System.Windows.Input.KeyEventArgs e) => Write("WPF_KEY_UP");

        private void OnContextMenuOpening(object sender, ContextMenuEventArgs e) => Write("WPF_CONTEXT_MENU_OPENING");

        private void OnClosed(object? sender, EventArgs e) => Dispose();

        public void RecordAppEvent(string classification) => Write(classification);

        public void RecordContextAction(string actionId, string? networkName, string? targetName) =>
            Write($"APP_CONTEXT_MENU_ACTION id={actionId} target={Fingerprint(ConversationFingerprint(networkName, targetName))}");

        public void RecordConversationState(string phase, WorkspaceView? view, string? composerText)
        {
            if (view is null) return;
            composerText = _viewModel?.GetAcceptanceDraftSnapshot(view) ?? composerText;
            var bytes = Encoding.UTF8.GetByteCount(composerText ?? string.Empty);
            var networkName = _viewModel?.Sessions.Networks.FirstOrDefault(network => network.Id == view.NetworkId)?.DisplayName;
            var activeFingerprint = _viewModel?.ActiveView is { } active
                ? Fingerprint(ConversationFingerprint(_viewModel.Sessions.Networks.FirstOrDefault(network => network.Id == active.NetworkId)?.DisplayName, active.Title))
                : "none";
            Write($"APP_VIEW_STATE phase={phase} target={Fingerprint(ConversationFingerprint(networkName, view.Title))} view={Fingerprint(view.Id.ToString("N"))} active={activeFingerprint} open={view.IsViewOpen.ToString().ToLowerInvariant()} entries={view.EntryCount.ToString(CultureInfo.InvariantCulture)} unread={view.UnreadCount.ToString(CultureInfo.InvariantCulture)} important={view.IsImportant.ToString().ToLowerInvariant()} composerBytes={bytes.ToString(CultureInfo.InvariantCulture)} composer={Fingerprint(composerText)}");
        }

        public void RecordPinnedActivation(PinnedConversationNavigationItem item, bool wasOpen)
        {
            var composerText = _viewModel?.GetAcceptanceDraftSnapshot(item.View) ?? string.Empty;
            var composerBytes = Encoding.UTF8.GetByteCount(composerText);
            var activeFingerprint = _viewModel?.ActiveView is { } active
                ? Fingerprint(ConversationFingerprint(_viewModel.Sessions.Networks.FirstOrDefault(network => network.Id == active.NetworkId)?.DisplayName, active.Title))
                : "none";
            Write($"APP_PINNED_ACTIVATION target={Fingerprint(ConversationFingerprint(item.Network.DisplayName, item.View.Title))} view={Fingerprint(item.View.Id.ToString("N"))} active={activeFingerprint} wasOpen={wasOpen.ToString().ToLowerInvariant()} open={item.View.IsViewOpen.ToString().ToLowerInvariant()} entries={item.View.EntryCount.ToString(CultureInfo.InvariantCulture)} unread={item.View.UnreadCount.ToString(CultureInfo.InvariantCulture)} important={item.View.IsImportant.ToString().ToLowerInvariant()} composerBytes={composerBytes.ToString(CultureInfo.InvariantCulture)} composer={Fingerprint(composerText)}");
            RecordPinnedProjection();
        }

        private void OnPinnedConversationsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) =>
            RecordPinnedProjection();

        private void RecordPinnedProjection()
        {
            if (_viewModel is null) return;
            var items = _viewModel.PinnedConversations.Select(static item => Fingerprint(ConversationFingerprint(item.Navigation.NetworkDisplayName, item.Navigation.Name)));
            Write($"APP_PIN_PROJECTION count={_viewModel.PinnedConversations.Count.ToString(CultureInfo.InvariantCulture)} order={string.Join(',', items)}");
        }

        private static string ConversationFingerprint(string? network, string? conversation) =>
            string.IsNullOrWhiteSpace(network) ? conversation ?? string.Empty : $"{network} · {conversation}";

        private static string Fingerprint(string? value)
        {
            if (string.IsNullOrEmpty(value)) return "none";
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
            return Convert.ToHexString(hash.AsSpan(0, 6)).ToLowerInvariant();
        }

        private void Write(string record)
        {
            if (_disposed) return;
            var count = _classificationCounts.GetValueOrDefault(record);
            if (count >= MaximumRecordsPerClassification || _records >= MaximumRecords) return;
            _classificationCounts[record] = count + 1;
            _records++;
            _writer.WriteLine(record);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            InputManager.Current.PreProcessInput -= OnPreProcessInput;
            if (_viewModel is not null)
                _viewModel.PinnedConversations.CollectionChanged -= OnPinnedConversationsChanged;
            _window.SourceInitialized -= OnSourceInitialized;
            _window.Closed -= OnClosed;
            if (_source is not null) _source.RemoveHook(WindowProcedure);
            _writer.Dispose();
        }
    }
}
