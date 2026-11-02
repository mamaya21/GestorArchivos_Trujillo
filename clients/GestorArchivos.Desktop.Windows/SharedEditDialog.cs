using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Data;

namespace GestorArchivos.Desktop.Windows;

internal enum SharedEditChoice { Cancel, Continue, Copy }

internal static class SharedEditDialog
{
    public static SharedEditChoice Show(Window owner, string fileName)
    {
        var result = SharedEditChoice.Cancel;
        var dialog = new Window
        {
            Owner = owner, Title = "Abrir archivo compartido",
            Width = 565, Height = 292, MinWidth = 520,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brushes.White, ShowInTaskbar = false
        };
        var root = new StackPanel { Margin = new Thickness(28) };
        root.Children.Add(new TextBlock
        {
            Text = "Estás abriendo un archivo compartido",
            FontSize = 19, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(31, 41, 65)), Margin = new Thickness(0, 0, 0, 16)
        });
        root.Children.Add(new TextBlock
        {
            Text = "Archivo: " + fileName + "\nPuedes abrirlo y editarlo normalmente. Los cambios que guardes se sincronizarán con el servidor y los equipos vinculados.\nSi prefieres trabajar de manera independiente, puedes crear una copia.",
            TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(87, 101, 126)), LineHeight = 21, Margin = new Thickness(0, 0, 0, 25)
        });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        void Add(string title, SharedEditChoice choice, bool primary = false)
        {
            var button = new Button { Content = title, Padding = new Thickness(18, 11, 18, 11), Margin = new Thickness(0, 0, 9, 0), MinWidth = 112, IsDefault = primary, Cursor = System.Windows.Input.Cursors.Hand, FontWeight = FontWeights.SemiBold, Background = primary ? new SolidColorBrush(Color.FromRgb(29, 78, 235)) : Brushes.White, Foreground = primary ? Brushes.White : new SolidColorBrush(Color.FromRgb(31, 41, 65)), BorderBrush = new SolidColorBrush(Color.FromRgb(222, 228, 240)), BorderThickness = primary ? new Thickness(0) : new Thickness(1) };
            var template = new ControlTemplate(typeof(Button));
            var chrome = new FrameworkElementFactory(typeof(Border));
            chrome.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
            chrome.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Button.BorderBrushProperty));
            chrome.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Button.BorderThicknessProperty));
            chrome.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            content.SetValue(ContentPresenter.MarginProperty, new TemplateBindingExtension(Button.PaddingProperty));
            chrome.AppendChild(content);
            template.VisualTree = chrome;
            button.Template = template;
            button.Click += (_, _) => { result = choice; dialog.DialogResult = true; };
            buttons.Children.Add(button);
        }
        Add("Abrir archivo", SharedEditChoice.Continue, true);
        Add("Crear una copia", SharedEditChoice.Copy);
        Add("Cancelar", SharedEditChoice.Cancel);
        root.Children.Add(buttons);
        dialog.Content = root;
        dialog.ShowDialog();
        return result;
    }
}
