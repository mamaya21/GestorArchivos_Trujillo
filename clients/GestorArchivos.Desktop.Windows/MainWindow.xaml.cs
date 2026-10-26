using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace GestorArchivos.Desktop.Windows;

public partial class MainWindow : Window
{
    private Uri? _serverUri;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var configPath = Path.Combine(AppContext.BaseDirectory, "desktopsettings.json");
            if (!File.Exists(configPath))
                throw new InvalidOperationException("No existe desktopsettings.json.");

            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(configPath));
            var address = document.RootElement.GetProperty("ServerUrl").GetString();

            if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttps &&
                 !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)))
                throw new InvalidOperationException(
                    "ServerUrl debe utilizar HTTPS o HTTP exclusivamente en localhost.");

            _serverUri = uri;
            var userData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GestorArchivos", "Desktop", "WebView2");
            Directory.CreateDirectory(userData);

            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userData);
            await Browser.EnsureCoreWebView2Async(environment);

            Browser.CoreWebView2.Settings.AreDevToolsEnabled = false;
            Browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            Browser.CoreWebView2.NewWindowRequested += (_, args) =>
            {
                args.Handled = true;
                if (Uri.TryCreate(args.Uri, UriKind.Absolute, out var target) &&
                    target.Scheme == Uri.UriSchemeHttps)
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                        target.AbsoluteUri) { UseShellExecute = true });
            };

            Browser.CoreWebView2.Navigate(_serverUri.AbsoluteUri);
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private void Browser_NavigationCompleted(
        object sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.IsSuccess)
            ErrorPanel.Visibility = Visibility.Collapsed;
        else
            ShowError($"No se pudo abrir el servidor (WebErrorStatus: {e.WebErrorStatus}).");
    }

    private void Reload_Click(object sender, RoutedEventArgs e)
    {
        if (Browser.CoreWebView2 is null || _serverUri is null)
        {
            _ = InitializeAgainAsync();
            return;
        }

        ErrorPanel.Visibility = Visibility.Collapsed;
        Browser.CoreWebView2.Navigate(_serverUri.AbsoluteUri);
    }

    private async Task InitializeAgainAsync()
    {
        await Task.Yield();
        MainWindow_Loaded(this, new RoutedEventArgs());
    }

    private void ShowError(string message)
    {
        ErrorMessage.Text = message;
        ErrorPanel.Visibility = Visibility.Visible;
    }
}
