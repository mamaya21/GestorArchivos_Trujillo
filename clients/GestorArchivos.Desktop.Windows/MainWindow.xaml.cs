using System.Collections.ObjectModel;
using System.Security.Cryptography;
using GestorArchivos.Desktop.Windows.Services;
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
    private readonly Dictionary<Guid, DesktopEditSession> _editSessions = new();
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
        Closed += (_, _) =>
        {
            foreach (var session in _editSessions.Values) session.Dispose();
            _http.Dispose();
        };
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
            if (_editSessions.TryGetValue(item.Id, out var existing))
            {
                existing.CheckForChanges();
                StatusText.Text = "La edición de " + item.Name + " sigue supervisada.";
                return;
            }

            StatusText.Text = "Preparando " + item.Name + "...";
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GestorArchivos", "Desktop", "Cache", item.Id.ToString("N"));
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, Path.GetFileName(item.Name));

            using var metadataResponse = await SendAsync(HttpMethod.Get, $"files/{item.Id}");
            using var metadata = await JsonDocument.ParseAsync(
                await metadataResponse.Content.ReadAsStreamAsync());
            var serverHash = metadata.RootElement.GetProperty("hashSha256").GetString() ?? "";
            var serverVersion = metadata.RootElement.GetProperty("version").GetInt32();

            string? localHash = null;
            if (File.Exists(path))
            {
                await using var local = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                localHash = Convert.ToHexString(await SHA256.HashDataAsync(local));
            }

            if (localHash is not null &&
                !string.Equals(localHash, serverHash, StringComparison.OrdinalIgnoreCase))
            {
                var choice = MessageBox.Show(
                    "La copia guardada en este equipo es diferente de la versión del servidor.\n\n" +
                    "Sí: continuar con mi copia local.\n" +
                    "No: abrir la versión del servidor sin borrar mi copia local.\n" +
                    "Cancelar: no abrir ningún archivo.\n\n" +
                    "Tus cambios locales se conservarán en todos los casos.",
                    "Selecciona qué versión abrir",
                    MessageBoxButton.YesNoCancel, MessageBoxImage.Question,
                    MessageBoxResult.Cancel);

                if (choice == MessageBoxResult.Cancel)
                {
                    StatusText.Text = _folderStatus;
                    return;
                }

                if (choice == MessageBoxResult.No)
                {
                    var serverCopy = Path.Combine(directory, "VersionServidor",
                        Path.GetFileName(item.Name));
                    Directory.CreateDirectory(Path.GetDirectoryName(serverCopy)!);
                    await DownloadServerCopyAsync(item.Id, serverCopy);
                    Process.Start(new ProcessStartInfo(serverCopy) { UseShellExecute = true });
                    StatusText.Text = "Versión del servidor abierta. Tu copia local permanece intacta.";
                    return;
                }

                // No asumir que la copia local pertenece a la versión actual:
                // se abre sin sincronización automática para impedir sobrescrituras.
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                StatusText.Text = "Copia local abierta. Se conserva sin sobrescribir la versión del servidor.";
                return;
            }

            if (localHash is null)
            {
                await DownloadServerCopyAsync(item.Id, path);
                await using var local = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                localHash = Convert.ToHexString(await SHA256.HashDataAsync(local));
                if (!string.IsNullOrEmpty(serverHash) &&
                    !string.Equals(localHash, serverHash, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("La descarga no coincide con el archivo del servidor.");
            }

            var session = new DesktopEditSession(_http, item.Id, path, serverVersion,
                localHash, message => Dispatcher.InvokeAsync(() => StatusText.Text = message));
            _editSessions[item.Id] = session;
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch
            {
                _editSessions.Remove(item.Id);
                session.Dispose();
                throw;
            }
            StatusText.Text = "Archivo abierto. Los cambios guardados se enviarán automáticamente.";
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async Task DownloadServerCopyAsync(Guid fileId, string destination)
    {
        using var response = await SendAsync(HttpMethod.Get, $"files/{fileId}/download");
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew,
                FileAccess.Write, FileShare.None))
                await response.Content.CopyToAsync(file);
            File.Move(temporary, destination, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
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
        foreach (var session in _editSessions.Values) session.Dispose();
        _editSessions.Clear();
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
