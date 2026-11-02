using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GestorArchivos.Desktop.Windows;

internal enum SharedEditChoice { Cancel, Continue, Copy }

internal static class SharedEditDialog
{
    public static SharedEditChoice Show(Window owner, string fileName)
    {
        var result = SharedEditChoice.Cancel;
        var dialog = new Window
        {
            Owner = owner, Title = "Editar archivo compartido",
            Width = 555, Height = 260, MinWidth = 520,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brushes.White, ShowInTaskbar = false
        };
        var root = new StackPanel { Margin = new Thickness(24) };
        root.Children.Add(new TextBlock
        {
            Text = "Vas a editar un archivo del servidor compartido.",
            FontSize = 17, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12)
        });
        root.Children.Add(new TextBlock
        {
            Text = "Archivo: " + fileName + "\nLos cambios guardados se enviarán al servidor y se sincronizarán con los equipos vinculados.\nTambién puedes crear una copia independiente en la misma carpeta.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 23)
        });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        void Add(string title, SharedEditChoice choice, bool primary = false)
        {
            var button = new Button { Content = title, Padding = new Thickness(17, 9, 17, 9), Margin = new Thickness(0, 0, 8, 0), MinWidth = 110, IsDefault = primary };
            button.Click += (_, _) => { result = choice; dialog.DialogResult = true; };
            buttons.Children.Add(button);
        }
        Add("Continuar", SharedEditChoice.Continue, true);
        Add("Crear una copia", SharedEditChoice.Copy);
        Add("Cancelar", SharedEditChoice.Cancel);
        root.Children.Add(buttons);
        dialog.Content = root;
        dialog.ShowDialog();
        return result;
    }
}
