
using System.Text.Json;

namespace GestorArchivos.Sync.Selector.Windows;

public partial class Form1 : Form
{
    private readonly List<SyncSelection> _selections = new();
    private readonly ListBox _list = new();
    private readonly Label _status = new();
    private readonly List<RemovalRequest> _removals = new();
    private static readonly string RemovalPath = Path.Combine(Path.GetDirectoryName(SharedConfigPath)!, "removal-requests.json");

    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData),
        "GestorArchivos",
        "Sync",
        "selections.json");

    private static readonly string SharedConfigPath = Path.Combine(
        Environment.GetFolderPath(
            Environment.SpecialFolder.CommonApplicationData),
        "GestorArchivos",
        "Sync",
        "selections.json");

    public Form1()
    {
        Text = "Gestor de Archivos - Selección de sincronización";
        Width = 850;
        Height = 540;
        MinimumSize = new Size(650, 420);
        StartPosition = FormStartPosition.CenterScreen;

        BuildInterface();
        LoadSelections();
    }

    private void BuildInterface()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(15),
            ColumnCount = 1,
            RowCount = 4
        };

        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 55));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 55));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 75));

        var title = new Label
        {
            Text = "Selecciona qué deseas sincronizar",
            Dock = DockStyle.Fill,
            Font = new Font(Font.FontFamily, 14, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft
        };

        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoScroll = true
        };

        toolbar.Controls.Add(CreateButton(
            "Agregar carpeta",
            (_, _) => AddFolder()));

        toolbar.Controls.Add(CreateButton(
            "Agregar archivos",
            (_, _) => AddFiles()));

        toolbar.Controls.Add(CreateButton(
            "Quitar selección",
            (_, _) => RemoveSelection()));

        _list.Dock = DockStyle.Fill;
        _list.HorizontalScrollbar = true;
        _list.Font = new Font(
            Font.FontFamily,
            10,
            FontStyle.Regular);

        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2
        };

        footer.ColumnStyles.Add(
            new ColumnStyle(SizeType.Percent, 100));

        footer.ColumnStyles.Add(
            new ColumnStyle(SizeType.Absolute, 150));

        _status.Dock = DockStyle.Fill;
        _status.TextAlign = ContentAlignment.MiddleLeft;

        var save = CreateButton(
            "Guardar selección",
            (_, _) => SaveSelections());

        save.Dock = DockStyle.Fill;

        footer.Controls.Add(_status, 0, 0);
        footer.Controls.Add(save, 1, 0);

        layout.Controls.Add(title, 0, 0);
        layout.Controls.Add(toolbar, 0, 1);
        layout.Controls.Add(_list, 0, 2);
        layout.Controls.Add(footer, 0, 3);

        Controls.Add(layout);
    }

    private static Button CreateButton(
        string text,
        EventHandler handler)
    {
        var button = new Button
        {
            Text = text,
            Width = 150,
            Height = 36,
            Margin = new Padding(0, 4, 10, 4)
        };

        button.Click += handler;
        return button;
    }

    private void AddFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description =
                "Selecciona una carpeta. Se incluirán todos sus archivos y subcarpetas.",
            ShowNewFolderButton = false,
            UseDescriptionForTitle = true
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        AddSelection(
            dialog.SelectedPath,
            SelectionKind.Folder);
    }

    private void AddFiles()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Selecciona uno o varios archivos",
            Multiselect = true,
            CheckFileExists = true,
            CheckPathExists = true
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        foreach (var file in dialog.FileNames)
        {
            AddSelection(
                file,
                SelectionKind.File,
                refresh: false);
        }

        RefreshList();
    }

    private void AddSelection(
        string path,
        SelectionKind kind,
        bool refresh = true,
        bool validateExistence = true)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        string fullPath;

        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception ex) when (
            ex is ArgumentException ||
            ex is NotSupportedException ||
            ex is PathTooLongException)
        {
            if (validateExistence)
            {
                MessageBox.Show(
                    "La ruta seleccionada no es válida.",
                    "Gestor de Archivos",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }

            return;
        }

        if (validateExistence)
        {
            var exists = kind == SelectionKind.Folder
                ? Directory.Exists(fullPath)
                : File.Exists(fullPath);

            if (!exists)
            {
                MessageBox.Show(
                    "La ubicación seleccionada no existe o no está disponible.",
                    "Gestor de Archivos",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);

                return;
            }
        }

        if (_selections.Any(existing =>
                IsSamePath(existing.Path, fullPath)))
        {
            if (refresh)
                RefreshList();

            return;
        }

        if (_selections.Any(existing =>
                existing.Kind == SelectionKind.Folder &&
                IsInsideFolder(fullPath, existing.Path)))
        {
            if (refresh)
                RefreshList();

            return;
        }

        if (kind == SelectionKind.Folder)
        {
            _selections.RemoveAll(existing =>
                IsInsideFolder(existing.Path, fullPath));
        }

        _selections.Add(new SyncSelection
        {
            Path = fullPath,
            Kind = kind
        });

        if (refresh)
            RefreshList();
    }

    private void RemoveSelection()
    {
        var index = _list.SelectedIndex;

        if (index < 0 || index >= _selections.Count)
            return;

        var selected = _selections[index];
        var choice = MessageBox.Show(this,
            "¿Qué deseas hacer con el contenido de esta selección?\n\n" +
            "Sí: conservar en este dispositivo y enviar el contenido de la web a la papelera.\n" +
            "No: conservar el contenido en este dispositivo y en la web.\n" +
            "Cancelar: mantener la sincronización.",
            "Quitar sincronización", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2);
        if (choice == DialogResult.Cancel) return;
        if (choice == DialogResult.Yes)
            _removals.Add(new RemovalRequest { Path = selected.Path, Kind = (int)selected.Kind });
        _selections.RemoveAt(index);
        RefreshList();
    }

    private void RefreshList()
    {
        _list.BeginUpdate();

        try
        {
            _list.Items.Clear();

            foreach (var selection in _selections)
            {
                var available = selection.Kind switch
                {
                    SelectionKind.Folder =>
                        Directory.Exists(selection.Path),

                    SelectionKind.File =>
                        File.Exists(selection.Path),

                    _ => false
                };

                var prefix = selection.Kind == SelectionKind.Folder
                    ? "[CARPETA]"
                    : "[ARCHIVO]";

                var availability = available
                    ? string.Empty
                    : " [NO DISPONIBLE]";

                _list.Items.Add(
                    $"{prefix} {selection.Path}{availability}");
            }
        }
        finally
        {
            _list.EndUpdate();
        }

        var unavailableCount = _selections.Count(selection =>
            selection.Kind == SelectionKind.Folder
                ? !Directory.Exists(selection.Path)
                : !File.Exists(selection.Path));

        _status.Text =
            $"{_selections.Count} elementos seleccionados";

        if (unavailableCount > 0)
        {
            _status.Text +=
                $" | {unavailableCount} no disponibles";
        }
    }

    private void SaveSelections()
    {
        try
        {
            var json = JsonSerializer.Serialize(
                _selections,
                new JsonSerializerOptions
                {
                    WriteIndented = true
                });

            // Guardar primero la configuración compartida.
            // Si falla, no informar que el servicio
            // ya recibió la nueva selección.
            // La solicitud se conserva hasta que el servicio confirme su ejecución.
            // No borrar solicitudes anteriores pendientes.
            if (_removals.Count > 0)
            {
                var pending = File.Exists(RemovalPath)
                    ? JsonSerializer.Deserialize<List<RemovalRequest>>(File.ReadAllText(RemovalPath)) ?? new()
                    : new List<RemovalRequest>();
                foreach (var removal in _removals)
                    if (!pending.Any(x => string.Equals(x.Path, removal.Path, StringComparison.OrdinalIgnoreCase)))
                        pending.Add(removal);
                WriteAtomically(RemovalPath, JsonSerializer.Serialize(pending));
            }
            WriteAtomically(SharedConfigPath, json);

            // Conservar también la configuración
            // del usuario interactivo.
            WriteAtomically(ConfigPath, json);

            // Solo la confirmación de una selección nueva habilita el motor.
            var pendingPath = Path.Combine(
                Path.GetDirectoryName(SharedConfigPath)!,
                "selection-pending.flag");
            if (File.Exists(pendingPath))
                File.Delete(pendingPath);

            _removals.Clear();
            MessageBox.Show(
                "Selecciones guardadas correctamente.",
                "Gestor de Archivos",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (UnauthorizedAccessException)
        {
            MessageBox.Show(
                "No se pudo guardar la configuración compartida. " +
                "Verifica los permisos de escritura en " +
                @"C:\ProgramData\GestorArchivos\Sync.",
                "Permisos insuficientes",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                ex.Message,
                "Error al guardar",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private static void WriteAtomically(
        string path,
        string json)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException(
                "No se pudo determinar el directorio de configuración.");

        Directory.CreateDirectory(directory);

        var temporaryPath = path + "." +
            Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            File.WriteAllText(temporaryPath, json);

            File.Move(
                temporaryPath,
                path,
                overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private void LoadSelections()
    {
        // La configuración compartida tiene prioridad
        // cuando ya existe.
        var path = File.Exists(SharedConfigPath)
            ? SharedConfigPath
            : ConfigPath;

        if (!File.Exists(path))
        {
            RefreshList();
            return;
        }

        try
        {
            var json = File.ReadAllText(path);

            var saved = JsonSerializer.Deserialize<
                List<SyncSelection>>(json);

            if (saved is not null)
            {
                foreach (var selection in saved)
                {
                    if (selection is null ||
                        string.IsNullOrWhiteSpace(selection.Path))
                    {
                        continue;
                    }

                    if (selection.Kind != SelectionKind.File &&
                        selection.Kind != SelectionKind.Folder)
                    {
                        continue;
                    }

                    AddSelection(
                        selection.Path,
                        selection.Kind,
                        refresh: false,
                        validateExistence: false);
                }
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"No se pudo cargar la configuración: {ex.Message}",
                "Gestor de Archivos",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }

        RefreshList();
    }

    private static bool IsSamePath(
        string first,
        string second)
    {
        return string.Equals(
            Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(first)),
            Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(second)),
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsInsideFolder(
        string path,
        string folder)
    {
        var normalizedPath = Path.GetFullPath(path);

        var normalizedFolder =
            Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(folder));

        return normalizedPath.StartsWith(
            normalizedFolder + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    }

    private sealed class RemovalRequest
    {
        public string Path { get; set; } = string.Empty;
        public int Kind { get; set; }
    }

    private enum SelectionKind
    {
        File,
        Folder
    }

    private sealed class SyncSelection
    {
        public string Path { get; set; } = string.Empty;
        public SelectionKind Kind { get; set; }
    }
}
