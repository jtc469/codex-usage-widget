using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace CodexUsageWidget;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args.Length == 2 && args[0].Equals("--render-preview", StringComparison.OrdinalIgnoreCase))
        {
            RenderPreview(args[1]);
            return;
        }

        using var mutex = new Mutex(true, "Local\\CodexUsageWidget", out var firstInstance);
        if (!firstInstance) return;

        Application.Run(new WidgetApplicationContext());
    }

    private static void RenderPreview(string path)
    {
        using var form = new UsageForm { Size = new Size(576, 40) };
        form.SetUsage(new UsageSnapshot(
            new UsageWindow(82, DateTimeOffset.Now.AddHours(2)),
            new UsageWindow(89, DateTimeOffset.Now.AddDays(6))));
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }
}

internal sealed class WidgetApplicationContext : ApplicationContext
{
    private const string StartupValueName = "Codex Usage Widget";
    private const string StartupKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private readonly UsageForm _form;
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _startupItem;
    private readonly AppServerClient _client = new();
    private readonly System.Windows.Forms.Timer _refreshTimer = new() { Interval = 60_000 };
    private bool _refreshing;
    private int _consecutiveFailures;
    private static readonly TimeSpan RefreshTimeout = TimeSpan.FromSeconds(45);

    public WidgetApplicationContext()
    {
        _form = new UsageForm();
        _form.Show();

        var menu = new ContextMenuStrip();
        menu.Items.Add("Refresh now", null, async (_, _) => await RefreshAsync());
        _startupItem = new ToolStripMenuItem("Start with Windows")
        {
            CheckOnClick = true,
            Checked = IsStartupEnabled()
        };
        _startupItem.CheckedChanged += (_, _) => SetStartup(_startupItem.Checked);
        menu.Items.Add(_startupItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Exit());

        _tray = new NotifyIcon
        {
            Icon = SystemIcons.Information,
            Text = "Codex usage: loading…",
            Visible = true,
            ContextMenuStrip = menu
        };
        _tray.DoubleClick += async (_, _) => await RefreshAsync();

        _refreshTimer.Tick += async (_, _) => await RefreshAsync();
        _refreshTimer.Start();
        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            // Task.Run moves every blocking pipe and process call in AppServerClient onto a
            // pool thread, so the UI thread keeps pumping messages. WaitAsync bounds the whole
            // attempt, so a wedged write can never leave _refreshing stuck true forever.
            var snapshot = await Task.Run(_client.GetUsageAsync).WaitAsync(RefreshTimeout);
            _consecutiveFailures = 0;
            _refreshTimer.Interval = 60_000;
            _form.SetUsage(snapshot);
            _tray.Text = Truncate($"Codex — 5h {snapshot.Primary.Remaining:0}% left, 7d {snapshot.Secondary.Remaining:0}% left", 63);
        }
        catch (Exception ex)
        {
            _consecutiveFailures = Math.Min(_consecutiveFailures + 1, 5);
            var retryMinutes = Math.Min(1 << (_consecutiveFailures - 1), 15);
            _refreshTimer.Interval = retryMinutes * 60_000;
            _form.SetError($"Usage unavailable · retry in {retryMinutes}m");
            _tray.Text = Truncate($"Codex unavailable; retry in {retryMinutes}m: {ex.Message}", 63);
        }
        finally
        {
            _refreshing = false;
        }
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..(max - 1)] + "…";

    private static bool IsStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(StartupKeyPath);
        return key?.GetValue(StartupValueName) is string;
    }

    private static void SetStartup(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(StartupKeyPath);
            if (enabled)
            {
                key.SetValue(StartupValueName, $"\"{Environment.ProcessPath}\" --startup");
            }
            else
            {
                key.DeleteValue(StartupValueName, false);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not change the startup setting.\n\n{ex.Message}", "Codex Usage Widget",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void Exit()
    {
        _refreshTimer.Stop();
        _tray.Visible = false;
        _tray.Dispose();
        _client.Dispose();
        _form.Close();
        ExitThread();
    }
}

internal sealed class UsageForm : Form
{
    private const int WsExTransparent = 0x20;
    private const int WsExToolWindow = 0x80;
    private const int WsExNoActivate = 0x08000000;
    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private readonly System.Windows.Forms.Timer _positionTimer = new() { Interval = 2_000 };
    private UsageSnapshot? _snapshot;
    private string? _error = "Loading Codex usage…";

    // Layout is column-based: the label, bar and percent columns are fixed width, and only
    // the reset text varies. These constants are shared by OnPaint and RequiredWidth so the
    // measured window width and the drawn layout can never disagree.
    private const int EdgePad = 13;
    private const int ModuleGap = 18;
    private const int ResetColumnX = 154;
    private const int LoadingWidth = 340;
    private static readonly Font LabelFont = new("Segoe UI Semibold", 8.5f);
    private static readonly Font ValueFont = new("Segoe UI Semibold", 8.5f);
    private static readonly Font DetailFont = new("Segoe UI", 8f);

    public UsageForm()
    {
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Color.FromArgb(28, 28, 30);
        DoubleBuffered = true;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        Opacity = 0.94;
        Width = 576;
        Height = 40;
        _positionTimer.Tick += (_, _) => PositionOverTaskbar();
        _positionTimer.Start();
        Shown += (_, _) => PositionOverTaskbar();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WsExTransparent | WsExToolWindow | WsExNoActivate;
            return cp;
        }
    }

    public void SetUsage(UsageSnapshot snapshot)
    {
        _snapshot = snapshot;
        _error = null;
        PositionOverTaskbar();
        Invalidate();
    }

    public void SetError(string error)
    {
        _error = error;
        Invalidate();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        using var path = RoundedRectangle(new Rectangle(0, 0, Width, Height), Math.Max(8, Height / 4));
        Region = new Region(path);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);

        using var border = new Pen(Color.FromArgb(65, 255, 255, 255), 1);
        using var borderPath = RoundedRectangle(new Rectangle(0, 0, Width - 1, Height - 1), Math.Max(8, Height / 4));
        g.DrawPath(border, borderPath);

        if (_snapshot is null)
        {
            TextRenderer.DrawText(g, _error ?? "Loading…", new Font("Segoe UI", 9f),
                new Rectangle(EdgePad, 0, Width - EdgePad * 2, Height), Color.Gainsboro,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            return;
        }

        var moduleWidth = (Width - EdgePad * 2 - ModuleGap) / 2;
        DrawModule(g, new Rectangle(EdgePad, 0, moduleWidth, Height), "5h", _snapshot.Primary);

        using var separator = new Pen(Color.FromArgb(45, 255, 255, 255));
        var separatorX = EdgePad + moduleWidth + ModuleGap / 2;
        g.DrawLine(separator, separatorX, 9, separatorX, Height - 9);

        DrawModule(g, new Rectangle(EdgePad + moduleWidth + ModuleGap, 0, moduleWidth, Height), "7d", _snapshot.Secondary);
    }

    private static void DrawModule(Graphics g, Rectangle bounds, string label, UsageWindow window)
    {
        var centerY = bounds.Top + bounds.Height / 2;
        TextRenderer.DrawText(g, label, LabelFont, new Point(bounds.Left, centerY - 8), Color.FromArgb(220, 220, 220),
            TextFormatFlags.NoPadding);

        var bar = new Rectangle(bounds.Left + 27, centerY - 4, 76, 8);
        using var trackPath = RoundedRectangle(bar, 4);
        using var trackBrush = new SolidBrush(Color.FromArgb(55, 255, 255, 255));
        g.FillPath(trackBrush, trackPath);

        var fillWidth = Math.Clamp((int)Math.Round(bar.Width * window.Remaining / 100d), 0, bar.Width);
        if (fillWidth > 0)
        {
            var fill = new Rectangle(bar.Left, bar.Top, Math.Max(4, fillWidth), bar.Height);
            using var fillPath = RoundedRectangle(fill, 4);
            using var fillBrush = new SolidBrush(UsageColor(window.Remaining));
            g.FillPath(fillBrush, fillPath);
        }

        var percentText = $"{window.Remaining:0}%";
        TextRenderer.DrawText(g, percentText, ValueFont, new Rectangle(bounds.Left + 112, 0, 39, bounds.Height),
            Color.WhiteSmoke, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

        var resetText = ResetText(window);
        TextRenderer.DrawText(g, resetText, DetailFont,
            new Rectangle(bounds.Left + ResetColumnX, 0, bounds.Width - ResetColumnX, bounds.Height),
            Color.FromArgb(185, 185, 190), TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
    }

    private static string ResetText(UsageWindow window) =>
        window.ResetsAt is null ? "" : $"↻ {FormatReset(window.ResetsAt.Value)}";

    // Width the content actually needs: the fixed columns plus the widest reset string.
    // Without this the window stays a flat 576px and leaves ~66px of dead box per module.
    private int RequiredWidth()
    {
        if (_snapshot is null) return LoadingWidth;
        var reset = Math.Max(ResetWidth(_snapshot.Primary), ResetWidth(_snapshot.Secondary));
        return EdgePad * 2 + ModuleGap + (ResetColumnX + reset) * 2;
    }

    private static int ResetWidth(UsageWindow window)
    {
        var text = ResetText(window);
        return text.Length == 0 ? 0 : TextRenderer.MeasureText(text, DetailFont,
            new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Width;
    }

    private static Color UsageColor(double remaining) => remaining switch
    {
        > 50 => Color.FromArgb(91, 201, 128),
        > 20 => Color.FromArgb(237, 184, 78),
        _ => Color.FromArgb(239, 102, 102)
    };

    private static string FormatReset(DateTimeOffset reset)
    {
        var local = reset.ToLocalTime();
        return local.Date == DateTimeOffset.Now.Date
            ? local.ToString("HH:mm")
            : local.ToString("d MMM");
    }

    private void PositionOverTaskbar()
    {
        var taskbar = FindWindow("Shell_TrayWnd", null);
        Rectangle taskbarRect;
        Rectangle screenRect;

        if (taskbar != IntPtr.Zero && GetWindowRect(taskbar, out var nativeRect))
        {
            taskbarRect = Rectangle.FromLTRB(nativeRect.Left, nativeRect.Top, nativeRect.Right, nativeRect.Bottom);
            screenRect = Screen.FromHandle(taskbar).Bounds;
        }
        else
        {
            screenRect = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
            var working = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1040);
            taskbarRect = new Rectangle(screenRect.Left, working.Bottom, screenRect.Width, screenRect.Bottom - working.Bottom);
        }

        var horizontal = taskbarRect.Width > taskbarRect.Height;
        var availableWidth = Math.Max(340, screenRect.Width / 2 - 90);
        var width = Math.Min(RequiredWidth(), availableWidth);
        var height = horizontal ? Math.Clamp(taskbarRect.Height - 8, 34, 44) : 40;
        var x = screenRect.Left + 10;
        var y = horizontal && taskbarRect.Top > screenRect.Top + screenRect.Height / 2
            ? taskbarRect.Top + Math.Max(4, (taskbarRect.Height - height) / 2)
            : screenRect.Bottom - height - 6;

        if (Width != width || Height != height) Size = new Size(width, height);
        SetWindowPos(Handle, HwndTopmost, x, y, width, height, SwpNoActivate | SwpShowWindow);
    }

    private static GraphicsPath RoundedRectangle(Rectangle rect, int radius)
    {
        var path = new GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(rect.Left, rect.Top, diameter, diameter, 180, 90);
        path.AddArc(rect.Right - diameter, rect.Top, diameter, diameter, 270, 90);
        path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rect.Left, rect.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string? windowName);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
}

internal sealed record UsageWindow(double Remaining, DateTimeOffset? ResetsAt);
internal sealed record UsageSnapshot(UsageWindow Primary, UsageWindow Secondary);

internal sealed class AppServerClient : IDisposable
{
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(20);
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private Process? _process;
    private StreamWriter? _stdin;
    private ChildProcessJob? _job;
    private int _nextId;

    public async Task<UsageSnapshot> GetUsageAsync()
    {
        try
        {
            await EnsureStartedAsync();
            var result = await RequestAsync("account/rateLimits/read", null, TimeSpan.FromSeconds(15));
            var limits = result.GetProperty("rateLimits");
            return new UsageSnapshot(ParseWindow(limits, "primary"), ParseWindow(limits, "secondary"));
        }
        catch (Exception ex)
        {
            // A JSON-RPC error proves that the server is alive. Authentication and
            // backend errors should not cause process churn; transport failures should.
            if (ex is not AppServerRpcException) Stop();
            throw;
        }
    }

    private async Task EnsureStartedAsync()
    {
        if (_process is { HasExited: false } && _stdin is not null) return;
        if (!await _startLock.WaitAsync(LockTimeout))
            throw new AppServerTransportException("Timed out waiting to start Codex.");
        try
        {
            if (_process is { HasExited: false } && _stdin is not null) return;
            StartProcess();
            await RequestAsync("initialize", new
            {
                clientInfo = new { name = "codex-usage-widget", title = "Codex Usage Widget", version = "1.0.0" },
                capabilities = (object?)null
            }, TimeSpan.FromSeconds(15));
            await WriteAsync(JsonSerializer.Serialize(new { method = "initialized" }));
        }
        finally
        {
            _startLock.Release();
        }
    }

    private void StartProcess()
    {
        Stop();
        var startInfo = new ProcessStartInfo
        {
            FileName = FindCodexExecutable(),
            Arguments = "app-server --stdio",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        _process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start Codex.");
        try
        {
            _job = new ChildProcessJob();
            _job.Assign(_process);
        }
        catch
        {
            try { if (!_process.HasExited) _process.Kill(true); } catch { }
            _process.Dispose();
            _process = null;
            _job?.Dispose();
            _job = null;
            throw;
        }
        _stdin = _process.StandardInput;
        _ = ReadLoopAsync(_process);
        _ = DrainErrorsAsync(_process);
    }

    private async Task<JsonElement> RequestAsync(string method, object? parameters, TimeSpan timeout)
    {
        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        var payload = parameters is null
            ? JsonSerializer.Serialize(new { method, id })
            : JsonSerializer.Serialize(new { method, id, @params = parameters });
        await WriteAsync(payload);
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var registration = timeoutSource.Token.Register(() => completion.TrySetException(
            new AppServerTransportException("Codex did not respond in time.")));
        try { return await completion.Task; }
        finally { _pending.TryRemove(id, out _); }
    }

    private async Task WriteAsync(string line)
    {
        if (!await _writeLock.WaitAsync(LockTimeout))
            throw new AppServerTransportException("Timed out waiting to send to Codex.");
        try
        {
            if (_stdin is null) throw new InvalidOperationException("Codex is not running.");
            await _stdin.WriteLineAsync(line);
            await _stdin.FlushAsync();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadLoopAsync(Process process)
    {
        try
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (!root.TryGetProperty("id", out var idElement) || !idElement.TryGetInt32(out var id)) continue;
                if (!_pending.TryRemove(id, out var completion)) continue;
                if (root.TryGetProperty("result", out var result))
                {
                    completion.TrySetResult(result.Clone());
                }
                else if (root.TryGetProperty("error", out var error))
                {
                    var message = error.TryGetProperty("message", out var text) ? text.GetString() : "Unknown Codex error";
                    var code = error.TryGetProperty("code", out var codeElement) && codeElement.TryGetInt32(out var parsedCode)
                        ? parsedCode
                        : 0;
                    completion.TrySetException(new AppServerRpcException(code, message ?? "Unknown Codex error"));
                }
            }
        }
        catch (Exception ex)
        {
            FailPending(ex);
        }
        finally
        {
            FailPending(new AppServerTransportException("Codex app server stopped."));
        }
    }

    private static async Task DrainErrorsAsync(Process process)
    {
        try { while (await process.StandardError.ReadLineAsync() is not null) { } }
        catch { }
    }

    private static UsageWindow ParseWindow(JsonElement limits, string property)
    {
        if (!limits.TryGetProperty(property, out var window) || window.ValueKind == JsonValueKind.Null)
            return new UsageWindow(0, null);
        var used = window.GetProperty("usedPercent").GetDouble();
        DateTimeOffset? reset = null;
        if (window.TryGetProperty("resetsAt", out var resetsAt) && resetsAt.ValueKind == JsonValueKind.Number)
            reset = DateTimeOffset.FromUnixTimeSeconds(resetsAt.GetInt64());
        return new UsageWindow(Math.Clamp(100 - used, 0, 100), reset);
    }

    private static string FindCodexExecutable()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var binRoot = Path.Combine(localAppData, "OpenAI", "Codex", "bin");
        if (Directory.Exists(binRoot))
        {
            var candidate = Directory.EnumerateFiles(binRoot, "codex.exe", SearchOption.AllDirectories)
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .FirstOrDefault();
            if (candidate is not null) return candidate.FullName;
        }
        return "codex";
    }

    private void FailPending(Exception error)
    {
        foreach (var pair in _pending)
            if (_pending.TryRemove(pair.Key, out var completion)) completion.TrySetException(error);
    }

    private void Stop()
    {
        // Terminate the child FIRST: closing a kill-on-close Job Object takes down the whole
        // tree. Disposing stdin flushes, so with the reader already gone that flush fails fast
        // on a broken pipe instead of blocking forever writing into a full one. Kill() needs no
        // argument because the job already covers descendants - Kill(true) would additionally
        // snapshot every process on the system for nothing.
        try { _job?.Dispose(); } catch { }
        _job = null;
        try { if (_process is { HasExited: false }) _process.Kill(); } catch { }
        try { _stdin?.Dispose(); } catch { }
        _stdin = null;
        try { _process?.Dispose(); } catch { }
        _process = null;
    }

    public void Dispose()
    {
        Stop();
        _startLock.Dispose();
        _writeLock.Dispose();
    }
}

internal sealed class AppServerRpcException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}

internal sealed class AppServerTransportException(string message) : Exception(message);

internal sealed class ChildProcessJob : IDisposable
{
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const int JobObjectExtendedLimitInformationClass = 9;
    private IntPtr _handle;

    public ChildProcessJob()
    {
        _handle = CreateJobObject(IntPtr.Zero, null);
        if (_handle == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Could not create a process job.");

        var information = new JobObjectExtendedLimitInformation();
        information.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
        var length = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
        var pointer = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.StructureToPtr(information, pointer, false);
            if (!SetInformationJobObject(_handle, JobObjectExtendedLimitInformationClass, pointer, (uint)length))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Could not configure the process job.");
        }
        catch
        {
            Dispose();
            throw;
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    public void Assign(Process process)
    {
        if (_handle == IntPtr.Zero || !AssignProcessToJobObject(_handle, process.Handle))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Could not contain the Codex process.");
    }

    public void Dispose()
    {
        var handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
        if (handle != IntPtr.Zero) CloseHandle(handle);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr jobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int informationClass, IntPtr information, uint informationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}
