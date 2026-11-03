using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GestorArchivos.Desktop.Windows;

internal sealed class ConflictChoiceDialog : Window
{
    public string? Resolution { get; private set; }
    public bool ApplyToAll { get; private set; }

    public ConflictChoiceDialog(Window owner, string fileName, bool isFolder)
    {
        Owner = owner;
        Title = "Resolver conflicto";
        Width = 490;
        SizeToContent = SizeToContent.Height;
        MinHeight = 235;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(247, 249, 253));
        FontFamily = new FontFamily("Segoe UI");

        var root = new StackPanel { Margin = new Thickness(24) };
        Content = root;
        root.Children.Add(new TextBlock
        {
            Text = "Ya existe un elemento con este nombre",
            FontSize = 18, FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(30, 45, 75)),
            Margin = new Thickness(0, 0, 0, 12)
        });
        root.Children.Add(new TextBlock
        {
            Text = fileName, FontSize = 14, TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(75, 90, 116)),
            Margin = new Thickness(0, 0, 0, 18)
        });
        var apply = new CheckBox
        {
            Content = "Aplicar a todos los conflictos",
            Margin = new Thickness(0, 0, 0, 18)
        };
        root.Children.Add(apply);

        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        root.Children.Add(buttons);
        AddAction(buttons, "Conservar ambos", "keepBoth", apply, true);
        if (!isFolder) AddAction(buttons, "Reemplazar", "replace", apply, false);
        AddAction(buttons, "Omitir", "skip", apply, false);
        AddAction(buttons, "Cancelar", null, apply, false);
    }

    private void AddAction(Panel parent, string label, string? resolution, CheckBox apply, bool primary)
    {
        var button = new Button
        {
            Content = label, Padding = new Thickness(12, 9, 12, 9),
            Margin = new Thickness(0, 0, 7, 7), MinWidth = 82,
            Cursor = System.Windows.Input.Cursors.Hand,
            Background = primary ? new SolidColorBrush(Color.FromRgb(36, 86, 232)) : Brushes.White,
            Foreground = primary ? Brushes.White : new SolidColorBrush(Color.FromRgb(32, 48, 77)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(216, 224, 237)),
            BorderThickness = new Thickness(1)
        };
        button.Click += (_, _) =>
        {
            Resolution = resolution;
            ApplyToAll = apply.IsChecked == true;
            DialogResult = true;
        };
        parent.Children.Add(button);
    }
}
