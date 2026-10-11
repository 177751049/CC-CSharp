using System.IO;
using System.Windows;
using CCRadar.Core.Infrastructure;

namespace CCRadar;

public partial class App : Application
{
    private static Mutex? _singleInstance;

    // 精简系统/VM 上 TabletInputService(WISPTIS) 异常时，WPF 创建窗口句柄会永久阻塞。
    // 必须在任何 WPF 窗口/控件创建之前禁用动态笔与触摸支持。
    static App()
    {
        AppContext.SetSwitch("Switch.System.Windows.Input.Stylus.DisableDynamicPenAndTouchSupport", true);
    }
    private static void TraceLog(string message)
    {
        try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "ccradar-startup.log"), $"[{DateTime.Now:HH:mm:ss.fff}] {message}\n"); }
        catch { }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        // VM 虚拟显卡驱动与 WPF 硬件渲染层（wpfgfx/DXGI）交互会挂死窗口句柄创建，
        // 强制进程级软件渲染绕过显卡驱动层。必须在任何窗口创建之前设置。
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        TraceLog($"startup begin, args=[{string.Join(' ', e.Args)}], renderMode=SoftwareOnly");
        try
        {
            base.OnStartup(e);
            TraceLog("base.OnStartup done (StartupUri window created)");
        }
        catch (Exception ex)
        {
            TraceLog($"base.OnStartup FAILED: {ex}");
            throw;
        }

        // 守护清理模式：由 LifetimeGuard 以隐藏进程拉起，父进程退出后清理资源。
        if (e.Args.Length == 5 && e.Args[0] == "--cleanup-guardian")
        {
            try { Core.Infrastructure.LifetimeGuard.RunAsync(e.Args).GetAwaiter().GetResult(); }
            catch (Exception ex) { Console.Error.WriteLine(ex.Message); }
            Shutdown(0);
            return;
        }

        _singleInstance = new Mutex(true, @"Global\CCRadar-SingleInstance", out var createdNew);
        TraceLog($"mutex createdNew={createdNew}");
        if (!createdNew)
        {
            MessageBox.Show("CC雷达 已在运行，请使用已打开的窗口。", "CC雷达", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(0);
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            TraceLog($"DispatcherUnhandledException: {args.Exception}");
            MessageBox.Show($"发生未处理异常：{args.Exception.Message}", "CC雷达", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            TraceLog($"AppDomain.UnhandledException: {args.ExceptionObject}");
            if (args.ExceptionObject is Exception ex) MessageBox.Show($"致命异常：{ex.Message}", "CC雷达", MessageBoxButton.OK, MessageBoxImage.Error);
        };

        // 手动创建并显示主窗口（不使用 StartupUri，句柄创建全程留痕）。
        TraceLog("creating MainWindow...");
        var window = new MainWindow();
        TraceLog("MainWindow created, calling Show()...");
        window.Show();
        var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        TraceLog($"Show() returned, handle={handle}, isVisible={window.IsVisible}, state={window.WindowState}");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.ReleaseMutex();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
