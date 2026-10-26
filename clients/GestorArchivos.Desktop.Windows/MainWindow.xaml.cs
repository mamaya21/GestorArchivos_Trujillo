using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;

namespace GestorArchivos.Desktop.Windows;

public partial class MainWindow : Window
{
    private readonly HttpClient _http = new(new HttpClientHandler
    {
        CookieContainer = new CookieContainer(),
        UseCookies = true
    });
    private readonly ObservableCollection<ExplorerItem> _items = [];
    private readonly Stack<Guid?> _history = new();
    private Guid? _folderId;
    private readonly Stack<string> _folderNames = new();
    private ICollectionView? _itemsView;
    private string _folderStatus = "Listo para trabajar";

    public MainWindow()
    {
        InitializeComponent();
        _itemsView = CollectionViewSource.GetDefaultView(_items);
        _itemsView.Filter = item => item is ExplorerItem file &&
            (string.IsNullOrWhiteSpace(SearchBox.Text) ||
             file.Name.Contains(SearchBox.Text.Trim(), StringComparison.CurrentCultureIgnoreCase));
        ItemsList.ItemsSource = _itemsView;
        Loaded += OnLoaded;
        Closed += (_, _) => _http.Dispose();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "desktopsettings.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var value = doc.RootElement.GetProperty("ServerUrl").GetString();
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttps &&
                 !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)))
                throw new InvalidOperationException("Configure ServerUrl con HTTPS o HTTP en localhost.");
            _http.BaseAddress = new Uri(uri.AbsoluteUri.TrimEnd('/') + "/");
            StatusText.Text = "Servidor: " + _http.BaseAddress.Host;
        }
        catch (Exception ex)
        {
            LoginButton.IsEnabled = false;
            LoginError.Text = ex.Message;
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(method, "api/" + path.TrimStart('/'));
        if (body is not null)
            request.Content = JsonContent.Create(body);
        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        if (!response.IsSuccessStatusCode)
        {
            var details = await response.Content.ReadAsStringAsync();
            response.Dispose();
            throw new HttpRequestException($"HTTP {(int)response.StatusCode}: {details[..Math.Min(details.Length, 240)]}", null, response.StatusCode);
        }
        return response;
    }

    private void Login_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && LoginButton.IsEnabled)
        {
            e.Handled = true;
            Login_Click(LoginButton, new RoutedEventArgs());
        }
    }

    private async void Login_Click(object sender, RoutedEventArgs e)
    {
        LoginButton.IsEnabled = false;
        try
        {
            using var response = await SendAsync(HttpMethod.Post, "auth/login",
                new { userName = UserNameBox.Text.Trim(), password = PasswordInput.Password });
            using var me = await SendAsync(HttpMethod.Get, "auth/me");
            using var doc = await JsonDocument.ParseAsync(await me.Content.ReadAsStreamAsync());
            UserLabel.Text = doc.RootElement.GetProperty("userName").GetString() ?? "";
            PasswordInput.Clear();
            LoginPanel.Visibility = Visibility.Collapsed;
            ExplorerPanel.Visibility = Visibility.Visible;
            await LoadFolderAsync(null);
        }
        catch (Exception ex)
        {
            LoginError.Text = ex is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized }
                ? "Usuario o contraseña incorrectos. Inténtalo nuevamente."
                : ex is HttpRequestException { StatusCode: HttpStatusCode.Forbidden }
                    ? "Tu cuenta no tiene permiso para acceder. Contacta al administrador."
                    : ex is HttpRequestException or TaskCanceledException
                        ? "No pudimos conectar con el servidor. Verifica tu conexión e inténtalo nuevamente."
                        : "No fue posible iniciar sesión. Inténtalo nuevamente.";
        }
        finally { LoginButton.IsEnabled = true; }
    }

    private async Task LoadFolderAsync(Guid? folderId)
    {
        try
        {
            var path = "folders" + (folderId.HasValue ? "?parentId=" + folderId.Value : "");
            using var response = await SendAsync(HttpMethod.Get, path);
            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
            _items.Clear();
            foreach (var folder in doc.RootElement.GetProperty("folders").EnumerateArray())
                _items.Add(new ExplorerItem(
                    folder.GetProperty("id").GetGuid(),
                    folder.GetProperty("nombre").GetString() ?? "",
                    true, 0, 0));
            foreach (var file in doc.RootElement.GetProperty("files").EnumerateArray())
                _items.Add(new ExplorerItem(
                    file.GetProperty("id").GetGuid(),
                    file.GetProperty("nombre").GetString() ?? "",
                    false,
                    file.GetProperty("tamanoBytes").GetInt64(),
                    file.GetProperty("version").GetInt32()));
            _folderId = folderId;
            BreadcrumbText.Text = _folderNames.Count == 0 ? "Inicio" :
                "Inicio  /  " + string.Join("  /  ", _folderNames.Reverse());
            _folderStatus = $"{_items.Count} elementos";
            StatusText.Text = _folderStatus;
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private ExplorerItem? Selected => ItemsList.SelectedItem as ExplorerItem;
    private async void Home_Click(object sender, RoutedEventArgs e)
    {
        _history.Clear();
        _folderNames.Clear();
        await LoadFolderAsync(null);
    }
    private async void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_history.Count > 0)
        {
            if (_folderNames.Count > 0) _folderNames.Pop();
            await LoadFolderAsync(_history.Pop());
        }
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadFolderAsync(_folderId);

    private async void ItemsList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        var item = Selected;
        if (item is null) return;
        if (item.IsFolder)
        {
            _history.Push(_folderId);
            _folderNames.Push(item.Name);
            await LoadFolderAsync(item.Id);
            return;
        }
        await OpenFileAsync(item);
    }

    private async Task OpenFileAsync(ExplorerItem item)
    {
        try
        {
            StatusText.Text = "Abriendo " + item.Name + "...";
            using var response = await SendAsync(HttpMethod.Get, $"files/{item.Id}/download");
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GestorArchivos", "Desktop", "Cache", item.Id.ToString("N"));
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, Path.GetFileName(item.Name));
            await using (var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
                await response.Content.CopyToAsync(file);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            StatusText.Text = "Archivo enviado a Windows: " + item.Name +
                ". Los cambios locales aún no se guardan automáticamente en el servidor.";
            // No se puede inferir el cierre real del documento a partir de Process.Start:
            // algunas aplicaciones reutilizan procesos ya abiertos.
            await Task.Delay(TimeSpan.FromSeconds(5));
            StatusText.Text = _folderStatus;
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        _itemsView?.Refresh();
    }

    private async void NewFile_Click(object sender, RoutedEventArgs e)
    {
        if (!_folderId.HasValue)
        {
            MessageBox.Show("Selecciona primero una carpeta.", "Nuevo archivo");
            return;
        }
        var name = Microsoft.VisualBasic.Interaction.InputBox(
            "Nombre del archivo de texto:", "Nuevo archivo", "Nuevo archivo.txt").Trim();
        if (string.IsNullOrWhiteSpace(name)) return;
        if (!name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            name += ".txt";
        try
        {
            using var response = await SendAsync(HttpMethod.Post, "files/empty",
                new { name, folderId = _folderId.Value });
            await LoadFolderAsync(_folderId);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void NewFolder_Click(object sender, RoutedEventArgs e)
    {
        var name = Microsoft.VisualBasic.Interaction.InputBox("Nombre de la carpeta:", "Nueva carpeta");
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            using var response = await SendAsync(HttpMethod.Post, "folders",
                new { name, parentId = _folderId });
            await LoadFolderAsync(_folderId);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        var item = Selected;
        if (item is null) return;
        var name = Microsoft.VisualBasic.Interaction.InputBox("Nuevo nombre:", "Renombrar", item.Name);
        if (string.IsNullOrWhiteSpace(name) || name == item.Name) return;
        try
        {
            using var response = await SendAsync(HttpMethod.Put,
                (item.IsFolder ? "folders/" : "files/") + item.Id + "/rename",
                new { name });
            await LoadFolderAsync(_folderId);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        var selected = ItemsList.SelectedItems.Cast<ExplorerItem>().ToArray();
        if (selected.Length == 0) return;
        if (MessageBox.Show($"¿Enviar {selected.Length} elemento(s) a la papelera?",
                "Confirmar", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            foreach (var item in selected)
            {
                using var response = await SendAsync(HttpMethod.Delete,
                    (item.IsFolder ? "folders/" : "files/") + item.Id);
            }
            await LoadFolderAsync(_folderId);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void Upload_Click(object sender, RoutedEventArgs e)
    {
        if (!_folderId.HasValue)
        {
            MessageBox.Show("Seleccione primero una carpeta.");
            return;
        }
        var dialog = new OpenFileDialog { Multiselect = true };
        if (dialog.ShowDialog() != true) return;
        try
        {
            foreach (var path in dialog.FileNames)
            {
                await using var stream = File.OpenRead(path);
                using var form = new MultipartFormDataContent();
                form.Add(new StringContent(_folderId.Value.ToString()), "folderId");
                form.Add(new StreamContent(stream), "file", Path.GetFileName(path));
                using var request = new HttpRequestMessage(HttpMethod.Post, "api/files/upload")
                { Content = form };
                using var response = await _http.SendAsync(request);
                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException($"No se pudo subir {Path.GetFileName(path)}: HTTP {(int)response.StatusCode}");
            }
            await LoadFolderAsync(_folderId);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void Logout_Click(object sender, RoutedEventArgs e)
    {
        try { using var response = await SendAsync(HttpMethod.Post, "auth/logout"); }
        catch (Exception ex) { ShowError(ex); }
        _items.Clear();
        _folderId = null;
        _history.Clear();
        _folderNames.Clear();
        SearchBox.Clear();
        UserLabel.Text = "";
        ExplorerPanel.Visibility = Visibility.Collapsed;
        LoginPanel.Visibility = Visibility.Visible;
    }

    private void ShowError(Exception ex) => StatusText.Text = ex.Message;

    private sealed record ExplorerItem(Guid Id, string Name, bool IsFolder, long Size, int Version)
    {
        public string Kind => IsFolder ? "Carpeta" : "Archivo";
        public string SizeText => IsFolder ? "" : Size.ToString("N0") + " bytes";
        public string VersionText => IsFolder ? "" : Version.ToString();
        public string Icon
        {
            get
            {
                if (IsFolder) return "📁";
                var extension = Path.GetExtension(Name).ToLowerInvariant();
                return extension switch
                {
                    ".pdf" => "📕",
                    ".doc" or ".docx" or ".rtf" => "📘",
                    ".xls" or ".xlsx" or ".csv" => "📗",
                    ".ppt" or ".pptx" => "📙",
                    ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" => "🖼️",
                    ".zip" or ".rar" or ".7z" => "🗜️",
                    ".mp4" or ".mov" or ".avi" => "🎬",
                    ".mp3" or ".wav" => "🎵",
                    _ => "📄"
                };
            }
        }
    }
}
