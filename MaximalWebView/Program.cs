using System.Diagnostics;
using System.Drawing;
using System.Reactive.Linq;
using System.Reflection;
using CliWrap;
using Microsoft.Web.WebView2.Core;
using RxFileSystemWatcher;
using Serilog;
using Utils;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace MaximalWebView;

class Program
{
    internal const uint WM_SYNCHRONIZATIONCONTEXT_WORK_AVAILABLE = Constants.WM_USER + 1;
    private const string StaticFileDirectory = "static_files";
    static string StaticFileDirectoryPath => Path.Combine(ProjectDirectoryPath.Value, StaticFileDirectory);

#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    internal static CoreWebView2Controller _controller;
    internal static UiThreadSynchronizationContext _uiThreadSyncCtx;
    private static HWND _hwnd;
    private static ILogger _logger;
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.

    private static HostObject _hostObject = new HostObject();

    private const int StartingWidth = 920;
    private const int StartingHeight = 1050;

    //actually 002b36, Windows uses BBGGRR not RRGGBB
    const uint solarizedDarkBgColor = 0x362b00;

    private static Stopwatch _timeSinceLaunch = Stopwatch.StartNew();

    // hot reload stuff
    private static ObservableFileSystemWatcher? _staticFileWatcher;
    private static CancellationTokenSource _tailwindCTS = new();

    [STAThread]
    static int Main(string[] args)
    {
#if DEBUG 
        PInvoke.AllocConsole(); // Console.WriteLine() lazy debugging enabler
        _logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.Console()
                .CreateLogger();
#else
        _logger = Serilog.Core.Logger.None;
#endif

        _logger.Information($"Example log message");

        unsafe
        {
            HINSTANCE hInstance = PInvoke.GetModuleHandle((char*)null);
            ushort classId;

            HBRUSH backgroundBrush = PInvoke.CreateSolidBrush(solarizedDarkBgColor);
            if (backgroundBrush.IsNull)
            {
                // fallback to the system background color in case it fails
                backgroundBrush = (HBRUSH)(IntPtr)(SYS_COLOR_INDEX.COLOR_BACKGROUND + 1);
            }

            fixed (char* classNamePtr = "MaximalWebView")
            {
                WNDCLASSW wc = new WNDCLASSW();
                wc.lpfnWndProc = WndProc;
                wc.lpszClassName = classNamePtr;
                wc.hInstance = hInstance;
                wc.hbrBackground = backgroundBrush;
                wc.style = WNDCLASS_STYLES.CS_VREDRAW | WNDCLASS_STYLES.CS_HREDRAW;
                classId = PInvoke.RegisterClass(wc);

                if (classId == 0)
                    throw new Exception("class not registered");
            }

            fixed (char* windowNamePtr = $"MaximalWebView {Assembly.GetExecutingAssembly().GetName().Version}")
            {
                _hwnd = PInvoke.CreateWindowEx(
                    0,
                    (char*)classId,
                    windowNamePtr,
                    WINDOW_STYLE.WS_OVERLAPPEDWINDOW,
                    Constants.CW_USEDEFAULT, Constants.CW_USEDEFAULT, StartingWidth, StartingHeight,
                    new HWND(),
                    new HMENU(),
                    hInstance,
                    null);
            }
        }

        if (_hwnd.Value == 0)
            throw new Exception("hwnd not created");

        SetTitleBarColor(_hwnd);
        PInvoke.ShowWindow(_hwnd, SHOW_WINDOW_CMD.SW_NORMAL);

        _uiThreadSyncCtx = new UiThreadSynchronizationContext(_hwnd);
        SynchronizationContext.SetSynchronizationContext(_uiThreadSyncCtx);

        CreateCoreWebView2(_hwnd);

        MSG msg;
        while (PInvoke.GetMessage(out msg, new HWND(), 0, 0))
        {
            PInvoke.TranslateMessage(msg);
            PInvoke.DispatchMessage(msg);
        }

        return (int)msg.wParam.Value;
    }

    private static LRESULT WndProc(HWND hwnd, uint msg, WPARAM wParam, LPARAM lParam)
    {
        switch (msg)
        {
            case Constants.WM_SIZE:
                OnSize(hwnd, wParam, GetLowWord(lParam.Value), GetHighWord(lParam.Value));
                break;
            case WM_SYNCHRONIZATIONCONTEXT_WORK_AVAILABLE:
                _uiThreadSyncCtx.RunAvailableWorkOnCurrentThread();
                break;
            case Constants.WM_CLOSE:
                _uiThreadSyncCtx.RunAvailableWorkOnCurrentThread();
                _tailwindCTS.Cancel();
                PInvoke.PostQuitMessage(0);
                break;
        }

        return PInvoke.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private static void OnSize(HWND hwnd, WPARAM wParam, int width, int height)
    {
        if (_controller != null)
            _controller.Bounds = new Rectangle(0, 0, width, height);

        _logger.Verbose($"OnSize({width}, {height})");
    }

    private static void Log(string s) => _logger.Information($"{_timeSinceLaunch.ElapsedMilliseconds}ms: {s}");

    private static async void CreateCoreWebView2(HWND hwnd)
    {
        var environment = await CoreWebView2Environment.CreateAsync(null, null, null);

        _controller = await environment.CreateCoreWebView2ControllerAsync(hwnd);

        Log("Controller created");
        _controller.DefaultBackgroundColor = Color.Transparent; // avoid white flash on first render

        PInvoke.GetClientRect(hwnd, out var hwndRect);
        _controller.Bounds = new Rectangle(0, 0, hwndRect.right, hwndRect.bottom);

        _controller.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;
        _controller.CoreWebView2.DOMContentLoaded += CoreWebView2_DOMContentLoadedFirstTime;
        _controller.CoreWebView2.NavigationCompleted += CoreWebView2_NavigationCompleted;

        _controller.CoreWebView2.AddHostObjectToScript("dotnet", _hostObject);

        if (HotReloadManager.IsHotReloadEnabled()) // serve static files from filesystem
        {
            // TODO find a better hostname than maximalwebview.example
            _controller.CoreWebView2.SetVirtualHostNameToFolderMapping("maximalwebview.example",
                                                                       StaticFileDirectoryPath,
                                                                       CoreWebView2HostResourceAccessKind.Allow);
        }
        else // serve static files embedded in assembly
        {
            _controller.CoreWebView2.AddWebResourceRequestedFilter("*://maximalwebview.example/*", CoreWebView2WebResourceContext.All);
            _controller.CoreWebView2.WebResourceRequested += ServeStaticFileFromEmbeddedResources;
        }

        _controller.CoreWebView2.Navigate("https://maximalwebview.example/index.html");
        _controller.IsVisible = true;
    }

    private static void CoreWebView2_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        //PInvoke.PostMessage(_hwnd, Constants.WM_CLOSE, 0, 0);
    }

    private static void ServeStaticFileFromEmbeddedResources(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        //using var instrument = SuperluminalPerf.BeginEvent(nameof(ServeStaticFileFromEmbeddedResources));
        Uri uri = new(e.Request.Uri);
        Log("Resource requested: " + uri.ToString());

        string localPath = uri.LocalPath[1..];
        var assembly = Assembly.GetEntryAssembly();
        if (assembly != null)
        {
            // TODO clean this up, there's probably a less hacky way that doesn't involve string replacement
            var stream = assembly.GetManifestResourceStream($"{nameof(MaximalWebView)}.{StaticFileDirectory}.{localPath.Replace("/", ".")}");

            if (stream != null)
            {
                e.Response = _controller.CoreWebView2.Environment.CreateWebResourceResponse(stream, 200, "OK", "");
            }
            else
            {
                e.Response = _controller.CoreWebView2.Environment.CreateWebResourceResponse(null, 404, "NOTFOUND", "");
            }
        }
    }

    private static async void CoreWebView2_DOMContentLoadedFirstTime(object? sender, CoreWebView2DOMContentLoadedEventArgs e)
    {
        _controller.CoreWebView2.DOMContentLoaded -= CoreWebView2_DOMContentLoadedFirstTime;
        Log("DomContentLoaded");

        // Test code to debug Tailwind watch not generating console output
        // try
        // {
        //     Log($"Running Tailwind from {ProjectDirectoryPath.Value}");
        //     await Cli.Wrap("tailwindcss")
        //         .WithArguments("-i ./tailwind/tailwind-input.css -c ./tailwind/tailwind.config.js -o ./static_files/tailwind.css --content ./static_files/*.html --watch")
        //         .WithWorkingDirectory(ProjectDirectoryPath.Value)
        //         .WithStandardOutputPipe(PipeTarget.ToDelegate(l => Console.WriteLine(l)))
        //         .WithStandardErrorPipe(PipeTarget.ToDelegate(l => Console.WriteLine(l)))
        //         .ExecuteAsync();
        // }
        // catch (Exception ex)
        // {
        //     Console.WriteLine(ex);
        // }

        // Set up Hot Reload once at startup
        // TODO move this into hot reload manager
        if (HotReloadManager.IsHotReloadEnabled())
        {
            _logger.Debug("Hot reload is enabled.");
            try
            {

                // SetupAndStartFileSystemWatcher();
                // await SetupAndRunTailwindJIT(_tailwindCTS.Token);
            }
            catch (Exception ex)
            {
                _logger.Error(ex.Demystify(), "Error setting up Hot Reload");
            }
        }
    }

    private static async Task SetupAndRunTailwindJIT(CancellationToken cancellationToken)
    {
        // Clean up any orphaned Tailwind processes from previous runs
        // We handle cleanup when the application is closed gracefully, but closing the VS debugger
        // terminates the application with no opportunity for cleanup
        foreach (var process in Process.GetProcessesByName("tailwindcss"))
        {
            process.Kill();
        }

        Console.WriteLine($"Running Tailwind watcher in {ProjectDirectoryPath.Value}");

        // TODO: why isn't this giving any console output?
        // Is Tailwind detecting when the command is run programatically?
        await Cli.Wrap("tailwindcss")
            .WithArguments("-i ./tailwind/tailwind-input.css  -c ./tailwind/tailwind.config.js -o ./static_files/tailwind.css --content=./static_files/*.html --watch")
            .WithWorkingDirectory(ProjectDirectoryPath.Value)
            .WithPipeToConsole()
            .ExecuteAsync(cancellationToken);
    }

    // TODO: replace this with the nice IAsyncEnumerable watcher
    private static void SetupAndStartFileSystemWatcher()
    {
        _logger.Information("Setting up filesystem watcher...");
        _staticFileWatcher = new ObservableFileSystemWatcher(new FileSystemWatcher(StaticFileDirectoryPath));
        _staticFileWatcher.Start();

        _staticFileWatcher.Changed
            .Concat(_staticFileWatcher.Created)
            .Concat(_staticFileWatcher.Deleted)
            .Concat(_staticFileWatcher.Renamed)
            .Buffer(TimeSpan.FromMilliseconds(150))
            .Where(x => x.Any())
            .ObserveOn(_uiThreadSyncCtx)
            .Subscribe(args =>
            {
                _logger.Information($"FileSystemEvent: {string.Join(',', args.Select(a => $"{a.ChangeType} {a.Name}"))}");
                _controller.CoreWebView2.Reload();
            });
    }

    private static void SetTitleBarColor(HWND hwnd)
    {
        unsafe
        {
            const uint DWMWA_CAPTION_COLOR = 35;

            // 0x002b36  RGB (solarized-base03)
            WInterop.Gdi.Native.COLORREF colorRef = Color.FromArgb(0x00, 0x2b, 0x36);
            HRESULT setBgResult = PInvoke.DwmSetWindowAttribute(hwnd, (DWMWINDOWATTRIBUTE)DWMWA_CAPTION_COLOR, &colorRef, 4);
            // TODO: check result, log warning on failure. Likely to fail before Windows 11 
        }
    }

    private static async void CoreWebView2_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        var webMessage = e.TryGetWebMessageAsString();
        if (string.IsNullOrEmpty(webMessage))
            return;

        // simulate moving some slow operation to a background thread
        await Task.Run(() => Thread.Sleep(200));

        // this will blow up if not run on the UI thread, so the SynchronizationContext needs to have been wired up correctly
        await _controller.CoreWebView2.ExecuteScriptAsync($"alert('Hi from the UI thread! I got a message from the browser: {webMessage}')");
    }

    private static int GetLowWord(nint value)
    {
        uint xy = (uint)value;
        int x = unchecked((short)xy);
        return x;
    }

    private static int GetHighWord(nint value)
    {
        uint xy = (uint)value;
        int y = unchecked((short)(xy >> 16));
        return y;
    }
}
