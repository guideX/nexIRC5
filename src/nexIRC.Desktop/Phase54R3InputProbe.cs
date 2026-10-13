using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;

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

    private sealed class Probe : IDisposable
    {
        private const int MaximumRecords = 256;
        private const int MaximumRecordsPerClassification = 8;
        private const int WmKeyDown = 0x0100;
        private const int WmKeyUp = 0x0101;
        private const int WmSysKeyDown = 0x0104;
        private const int WmSysKeyUp = 0x0105;
        private const int WmContextMenu = 0x007B;
        private readonly Window _window;
        private readonly StreamWriter _writer;
        private readonly Dictionary<string, int> _classificationCounts = new(StringComparer.Ordinal);
        private HwndSource? _source;
        private int _records;
        private bool _disposed;

        public Probe(Window window, string outputPath)
        {
            _window = window;
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            _writer = new StreamWriter(new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.Read),
                new System.Text.UTF8Encoding(false)) { AutoFlush = true };
            _window.SourceInitialized += OnSourceInitialized;
            _window.Closed += OnClosed;
            _window.AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(OnPreviewMouseDown), true);
            _window.AddHandler(Mouse.MouseDownEvent, new MouseButtonEventHandler(OnMouseDown), true);
            _window.AddHandler(Keyboard.PreviewKeyDownEvent, new System.Windows.Input.KeyEventHandler(OnPreviewKeyDown), true);
            _window.AddHandler(Keyboard.KeyDownEvent, new System.Windows.Input.KeyEventHandler(OnKeyDown), true);
            _window.AddHandler(ContextMenuService.ContextMenuOpeningEvent, new ContextMenuEventHandler(OnContextMenuOpening), true);
            InputManager.Current.PreProcessInput += OnPreProcessInput;
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
                || message is WmKeyDown or WmKeyUp or WmSysKeyDown or WmSysKeyUp or WmContextMenu)
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

        private void OnMouseDown(object sender, MouseButtonEventArgs e) => Write("WPF_MOUSE_DOWN");

        private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e) => Write("WPF_PREVIEW_KEY_DOWN");

        private void OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e) => Write("WPF_KEY_DOWN");

        private void OnContextMenuOpening(object sender, ContextMenuEventArgs e) => Write("WPF_CONTEXT_MENU_OPENING");

        private void OnClosed(object? sender, EventArgs e) => Dispose();

        public void RecordAppEvent(string classification) => Write(classification);

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
            _window.SourceInitialized -= OnSourceInitialized;
            _window.Closed -= OnClosed;
            if (_source is not null) _source.RemoveHook(WindowProcedure);
            _writer.Dispose();
        }
    }
}
