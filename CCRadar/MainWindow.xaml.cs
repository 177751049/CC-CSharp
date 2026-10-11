using System.Windows;
using CCRadar.ViewModels;

namespace CCRadar;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();

    public MainWindow()
    {
        var trace = Path.Combine(Path.GetTempPath(), "ccradar-startup.log");
        void TraceLog(string message) => File.AppendAllText(trace, $"[{DateTime.Now:HH:mm:ss.fff}] {message}\n");
        try
        {
            TraceLog("MainWindow ctor begin");
            InitializeComponent();
            TraceLog("InitializeComponent done");
            DataContext = _viewModel;
            SourceInitialized += (_, _) => TraceLog($"SourceInitialized handle={new System.Windows.Interop.WindowInteropHelper(this).Handle}");
            IsVisibleChanged += (_, e) => TraceLog($"IsVisible -> {(bool)e.NewValue}");
            Loaded += (_, _) => TraceLog($"Loaded: Left={Left} Top={Top} W={ActualWidth} H={ActualHeight} State={WindowState}");
            TraceLog("DataContext set, MainWindow ready");
        }
        catch (Exception ex)
        {
            TraceLog($"MainWindow ctor FAILED: {ex}");
            throw;
        }
    }

    private async void OnStart(object sender, RoutedEventArgs e) => await _viewModel.StartAsync();

    private async void OnStop(object sender, RoutedEventArgs e) => await _viewModel.StopAsync();

    private void OnOpenWeb(object sender, RoutedEventArgs e) => _viewModel.OpenWeb();

    private async void OnRefreshNeighbors(object sender, RoutedEventArgs e) => await _viewModel.RefreshLanNeighborsAsync();

    private async void OnRefreshHotspot(object sender, RoutedEventArgs e) => await _viewModel.RefreshHotspotAsync();
}
