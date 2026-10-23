using System.Drawing.Drawing2D;

using System.Text.Json;

namespace GestorArchivos.Sync.Selector.Windows;

public partial class Form1 : Form
{
    private readonly List<SyncSelection> _selections = new();
    private readonly ListBox _list = new();
    private readonly Label _status = new();
    private readonly List<RemovalRequest> _removals = new();
    private bool _dirty;
    private DateTime _lastSharedWriteUtc;
    private readonly System.Windows.Forms.Timer _refreshTimer = new() { Interval = 3000 };

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

    private static readonly string RemovalPath = Path.Combine(Path.GetDirectoryName(SharedConfigPath)!, "removal-requests.json");

    public Form1()
    {
        Text = "Gestor de Archivos - Selección de sincronización";
        Width = 850;
        Height = 540;
        MinimumSize = new Size(650, 420);
        StartPosition = FormStartPosition.CenterScreen;

        Icon = CreateAppIcon();
        BuildInterface();
        LoadSelections();
        _lastSharedWriteUtc = File.Exists(SharedConfigPath)
            ? File.GetLastWriteTimeUtc(SharedConfigPath) : DateTime.MinValue;
        _refreshTimer.Tick += (_, _) => RefreshExternalSelections();
        _refreshTimer.Start();
        FormClosed += (_, _) => _refreshTimer.Dispose();
    }

    private static readonly Color Ink = Color.FromArgb(31, 41, 65);
    private static readonly Color Muted = Color.FromArgb(105, 115, 137);
    private static readonly Color Accent = Color.FromArgb(105, 87, 216);
    private static readonly Color Surface = Color.FromArgb(247, 248, 252);
    private static readonly Color Border = Color.FromArgb(225, 228, 237);

    private void BuildInterface()
    {
        BackColor = Surface;
        Font = new Font("Segoe UI", 9.5f);
        MinimumSize = new Size(720, 470);
        Width = 900;
        Height = 580;

        var shell = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(26, 22, 26, 22),
            ColumnCount = 1, RowCount = 4, BackColor = Surface
        };
        shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 86));
        shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 74));

        var heading = new Panel { Dock = DockStyle.Fill };
        heading.Controls.Add(new Label
        {
            Text = "Elige qué sincronizar", ForeColor = Ink,
            Font = new Font("Segoe UI Semibold", 19, FontStyle.Bold),
            Location = new Point(0, 0), AutoSize = true
        });
        heading.Controls.Add(new Label
        {
            Text = "Administra tus carpetas y archivos desde este dispositivo.",
            ForeColor = Muted, Font = new Font("Segoe UI", 10),
            Location = new Point(2, 42), AutoSize = true
        });

        var toolbarHost = new Panel { Dock = DockStyle.Fill };
        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false, AutoScroll = false, Padding = Padding.Empty
        };
        toolbar.Controls.Add(CreateButton("Carpeta", (_, _) => AddFolder(), true, "folder"));
        toolbar.Controls.Add(CreateButton("Archivos", (_, _) => AddFiles(), true, "file"));
        toolbar.Controls.Add(CreateButton("Quitar", (_, _) => RemoveSelection(), false, "remove"));

        var refresh = new Button
        {
            Dock = DockStyle.Right, Width = 46, Height = 39,
            Margin = Padding.Empty, Text = "", FlatStyle = FlatStyle.Flat,
            BackColor = Color.White, ForeColor = Accent,
            Cursor = Cursors.Hand, TabStop = true,
            AccessibleName = "Actualizar selecciones",
            AccessibleDescription = "Recargar las selecciones desde el sincronizador"
        };
        refresh.FlatAppearance.BorderColor = Border;
        refresh.FlatAppearance.BorderSize = 1;
        refresh.Click += (_, _) => RefreshExternalSelections(force: true);
        refresh.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(Accent, 2.4f)
            {
                StartCap = LineCap.Round, EndCap = LineCap.Round
            };
            var center = new PointF(refresh.ClientSize.Width / 2f,
                                    refresh.ClientSize.Height / 2f);
            var rect = new RectangleF(center.X - 9, center.Y - 9, 18, 18);
            e.Graphics.DrawArc(pen, rect, 42, 285);
            using var arrow = new SolidBrush(Accent);
            e.Graphics.FillPolygon(arrow, new[]
            {
                new PointF(center.X + 10, center.Y - 10),
                new PointF(center.X + 10, center.Y - 1),
                new PointF(center.X + 2, center.Y - 5)
            });
        };
        toolbarHost.Controls.Add(toolbar);
        toolbarHost.Controls.Add(refresh);
        refresh.BringToFront();

        var listPanel = new Panel
        {
            Dock = DockStyle.Fill, BackColor = Color.White,
            Padding = new Padding(12)
        };
        listPanel.Paint += (_, e) =>
        {
            using var pen = new Pen(Border);
            e.Graphics.DrawRectangle(pen, 0, 0, listPanel.Width - 1, listPanel.Height - 1);
        };
        var gridLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1,
            BackColor = Color.White, Margin = Padding.Empty
        };
        gridLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        gridLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var gridHeader = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(248, 249, 253) };
        gridHeader.Controls.Add(new Label
        {
            Text = "ELEMENTO", Font = new Font("Segoe UI Semibold", 8.5f, FontStyle.Bold),
            ForeColor = Muted, Location = new Point(58, 12), AutoSize = true
        });
        gridHeader.Controls.Add(new Label
        {
            Text = "ESTADO", Font = new Font("Segoe UI Semibold", 8.5f, FontStyle.Bold),
            ForeColor = Muted, Anchor = AnchorStyles.Right | AnchorStyles.Top,
            AutoSize = false, TextAlign = ContentAlignment.MiddleRight,
            Width = 110, Height = 26
        });
        gridHeader.Resize += (_, _) => gridHeader.Controls[1].Left = gridHeader.Width - 132;
        _list.Dock = DockStyle.Fill;
        _list.BorderStyle = BorderStyle.None;
        _list.BackColor = Color.White;
        _list.ForeColor = Ink;
        _list.Font = new Font("Segoe UI", 10);
        _list.DrawMode = DrawMode.OwnerDrawFixed;
        _list.ItemHeight = 54;
        _list.IntegralHeight = false;
        _list.HorizontalScrollbar = false;
        _list.DrawItem += DrawSelectionRow;
        gridLayout.Controls.Add(gridHeader, 0, 0);
        gridLayout.Controls.Add(_list, 0, 1);
        listPanel.Controls.Add(gridLayout);

        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 174));
        _status.Dock = DockStyle.Fill;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.ForeColor = Muted;
        var save = CreateButton("Guardar cambios", (_, _) => SaveSelections(), true, "save");
        save.Dock = DockStyle.Fill;
        save.Margin = new Padding(0, 13, 0, 13);
        footer.Controls.Add(_status, 0, 0);
        footer.Controls.Add(save, 1, 0);

        shell.Controls.Add(heading, 0, 0);
        shell.Controls.Add(toolbarHost, 0, 1);
        shell.Controls.Add(listPanel, 0, 2);
        shell.Controls.Add(footer, 0, 3);
        Controls.Add(shell);
    }

    private static Button CreateButton(string text, EventHandler handler, bool primary, string icon = "")
    {
        var button = new Button
        {
            Text = "    " + text, TextAlign = ContentAlignment.MiddleCenter,
            Width = text.Contains("Guardar") ? 174 : text.Contains("Quitar") ? 112 : 122,
            Height = 36, Margin = new Padding(0, 4, 9, 4),
            FlatStyle = FlatStyle.Flat, BackColor = primary ? Accent : Color.White,
            ForeColor = primary ? Color.White : Ink,
            Font = new Font("Segoe UI Semibold", 9, FontStyle.Bold),
            Cursor = Cursors.Hand, UseVisualStyleBackColor = false
        };
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = primary
            ? Color.FromArgb(91, 72, 196) : Color.FromArgb(239, 240, 248);
        button.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var border = new Pen(primary ? Accent : Border, 1);
            using var shape = Rounded(e.ClipRectangle.Width - 1, e.ClipRectangle.Height - 1, 8);
            e.Graphics.DrawPath(border, shape);
            if (!string.IsNullOrEmpty(icon))
            {
                using var pen = new Pen(primary ? Color.White : Accent, 1.8f)
                { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
                var x = Math.Max(12, (button.Width - TextRenderer.MeasureText(text, button.Font).Width) / 2 - 13);
                var y = button.Height / 2;
                DrawSymbol(e.Graphics, pen, icon, x, y);
            }
        };
        button.Click += handler;
        return button;
    }

    private static GraphicsPath Rounded(int width, int height, int radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(0, 0, d, d, 180, 90);
        path.AddArc(width - d, 0, d, d, 270, 90);
        path.AddArc(width - d, height - d, d, d, 0, 90);
        path.AddArc(0, height - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static void DrawSymbol(Graphics g, Pen pen, string symbol, int x, int y)
    {
        switch (symbol)
        {
            case "folder":
                g.DrawLines(pen, new[] { new Point(x - 8, y - 4), new Point(x - 3, y - 4),
                    new Point(x, y - 1), new Point(x + 8, y - 1),
                    new Point(x + 8, y + 6), new Point(x - 8, y + 6), new Point(x - 8, y - 4) });
                break;
            case "file":
                g.DrawLines(pen, new[] { new Point(x - 6, y - 8), new Point(x + 2, y - 8),
                    new Point(x + 6, y - 4), new Point(x + 6, y + 8),
                    new Point(x - 6, y + 8), new Point(x - 6, y - 8) });
                g.DrawLine(pen, x - 3, y, x + 3, y);
                break;
            case "remove":
                g.DrawLine(pen, x - 6, y, x + 6, y);
                break;
            case "save":
                g.DrawLines(pen, new[] { new Point(x - 6, y), new Point(x - 1, y + 5),
                    new Point(x + 7, y - 5) });
                break;
        }
    }

    private static Icon CreateAppIcon()
    {
        using var bitmap = new Bitmap(64, 64);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var bg = new SolidBrush(Accent);
            g.FillEllipse(bg, 2, 2, 60, 60);
            using var pen = new Pen(Color.White, 4.5f)
            { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawArc(pen, 17, 16, 30, 30, 205, 230);
            g.DrawLines(pen, new[] { new Point(41, 12), new Point(49, 18), new Point(41, 24) });
            g.DrawArc(pen, 17, 18, 30, 30, 25, 150);
            g.DrawLines(pen, new[] { new Point(21, 51), new Point(14, 44), new Point(22, 38) });
        }
        return Icon.FromHandle(bitmap.GetHicon());
    }

    private void DrawSelectionRow(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _selections.Count) return;
        var selection = _selections[e.Index];
        var selected = (e.State & DrawItemState.Selected) != 0;
        var available = selection.Kind == SelectionKind.Folder
            ? Directory.Exists(selection.Path) : File.Exists(selection.Path);
        using var background = new SolidBrush(selected
            ? Color.FromArgb(239, 236, 255) : Color.White);
        e.Graphics.FillRectangle(background, e.Bounds);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var x = e.Bounds.Left;
        var y = e.Bounds.Top;
        using var iconBackground = new SolidBrush(selection.Kind == SelectionKind.Folder
            ? Color.FromArgb(237, 231, 255) : Color.FromArgb(231, 242, 255));
        e.Graphics.FillEllipse(iconBackground, x + 13, y + 10, 34, 34);
        using var pen = new Pen(selection.Kind == SelectionKind.Folder
            ? Accent : Color.FromArgb(54, 115, 200), 1.7f);
        DrawSymbol(e.Graphics, pen,
            selection.Kind == SelectionKind.Folder ? "folder" : "file", x + 30, y + 27);
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(selection.Path));
        if (string.IsNullOrWhiteSpace(name)) name = selection.Path;
        var textWidth = Math.Max(60, e.Bounds.Width - 220);
        TextRenderer.DrawText(e.Graphics, name, new Font("Segoe UI Semibold", 10, FontStyle.Bold),
            new Rectangle(x + 60, y + 7, textWidth, 21), Ink,
            TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);
        TextRenderer.DrawText(e.Graphics, selection.Path, new Font("Segoe UI", 8.5f),
            new Rectangle(x + 60, y + 28, textWidth, 18), Muted,
            TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);
        TextRenderer.DrawText(e.Graphics, available ? "Disponible" : "No disponible",
            new Font("Segoe UI", 9), new Rectangle(e.Bounds.Right - 135, y + 15, 115, 24),
            available ? Color.FromArgb(30, 128, 90) : Color.FromArgb(190, 79, 75),
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        using var separator = new Pen(Border);
        e.Graphics.DrawLine(separator, x + 12, e.Bounds.Bottom - 1, e.Bounds.Right - 12, e.Bounds.Bottom - 1);
    }

    private DialogResult AskRemoval(SyncSelection selected)
    {
        using var dialog = new Form
        {
            Text = "Dejar de sincronizar",
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false, MinimizeBox = false,
            ShowInTaskbar = false, ClientSize = new Size(530, 330),
            BackColor = Color.White, Font = new Font("Segoe UI", 10)
        };
        var title = new Label
        {
            Text = "¿Qué deseas hacer con este contenido?",
            Font = new Font("Segoe UI Semibold", 15, FontStyle.Bold),
            ForeColor = Ink, Location = new Point(25, 23), AutoSize = true
        };
        var name = new Label
        {
            Text = selected.Path, ForeColor = Muted,
            Location = new Point(26, 66), Size = new Size(475, 38),
            AutoEllipsis = true
        };
        var keep = CreateButton("Conservar en ambos lugares", (_, _) =>
        { dialog.DialogResult = DialogResult.No; dialog.Close(); }, true);
        keep.SetBounds(25, 122, 480, 44);
        keep.Margin = Padding.Empty;
        var trash = CreateButton("Enviar contenido web a la papelera", (_, _) =>
        { dialog.DialogResult = DialogResult.Yes; dialog.Close(); }, false);
        trash.SetBounds(25, 177, 480, 44);
        trash.Margin = Padding.Empty;
        var detail = new Label
        {
            Text = "El contenido de este dispositivo se conservará en ambos casos.",
            ForeColor = Muted, Location = new Point(26, 234),
            Size = new Size(475, 25)
        };
        var cancel = CreateButton("Cancelar", (_, _) =>
        { dialog.DialogResult = DialogResult.Cancel; dialog.Close(); }, false);
        cancel.SetBounds(375, 277, 130, 36);
        dialog.Controls.AddRange(new Control[] { title, name, keep, trash, detail, cancel });
        dialog.CancelButton = cancel;
        dialog.AcceptButton = keep;
        return dialog.ShowDialog(this);
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
        if (refresh) _dirty = true;

        if (refresh)
            RefreshList();
    }

    private void RemoveSelection()
    {
        var index = _list.SelectedIndex;

        if (index < 0 || index >= _selections.Count)
            return;

        var selected = _selections[index];
        var choice = AskRemoval(selected);
        if (choice == DialogResult.Cancel) return;
        _removals.Add(new RemovalRequest { Path = selected.Path, Kind = (int)selected.Kind, SendToTrash = choice == DialogResult.Yes });
        _selections.RemoveAt(index);
        _dirty = true;
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
            _dirty = false;
            _lastSharedWriteUtc = File.GetLastWriteTimeUtc(SharedConfigPath);
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

    private void RefreshExternalSelections(bool force = false)
    {
        if (_dirty || _removals.Count != 0)
        {
            if (force)
                MessageBox.Show("Hay cambios sin guardar. Guarda o descarta los cambios antes de actualizar.",
                    "Actualizar selecciones", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (!File.Exists(SharedConfigPath))
        {
            if (force) RefreshList();
            return;
        }
        var modified = File.GetLastWriteTimeUtc(SharedConfigPath);
        if (!force && modified <= _lastSharedWriteUtc) return;
        try
        {
            var saved = JsonSerializer.Deserialize<List<SyncSelection>>(
                File.ReadAllText(SharedConfigPath));
            if (saved is null) return;
            _selections.Clear();
            foreach (var selection in saved)
                if (selection is not null &&
                    (selection.Kind == SelectionKind.File ||
                     selection.Kind == SelectionKind.Folder))
                    AddSelection(selection.Path, selection.Kind,
                        refresh: false, validateExistence: false);
            _lastSharedWriteUtc = modified;
            RefreshList();
        }
        catch (IOException) { /* Atomic writer may be replacing the file. */ }
        catch (UnauthorizedAccessException) { /* Retry on next tick. */ }
        catch (JsonException) { /* Retry on next tick. */ }
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
        public bool SendToTrash { get; set; }
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
