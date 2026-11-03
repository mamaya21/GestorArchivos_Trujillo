using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace GestorArchivos.Desktop.Windows;

public partial class MainWindow
{
    private Point? _dragOrigin;
    private bool _dragSelecting;
    private SelectionMarquee? _selectionMarquee;
    private AdornerLayer? _marqueeLayer;

    private void ItemsList_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        _dragOrigin = e.GetPosition(ItemsList);
        _dragSelecting = false;
    }

    private void ItemsList_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragOrigin is not Point start) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndDragSelection();
            return;
        }

        var current = e.GetPosition(ItemsList);
        if (!_dragSelecting)
        {
            if (Math.Abs(current.X - start.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(current.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;

            _dragSelecting = true;
            _marqueeLayer = AdornerLayer.GetAdornerLayer(ItemsList);
            if (_marqueeLayer is not null)
            {
                _selectionMarquee = new SelectionMarquee(ItemsList);
                _marqueeLayer.Add(_selectionMarquee);
            }
            ItemsList.CaptureMouse();
        }

        var selection = new Rect(start, current);
        _selectionMarquee?.SetBounds(selection);
        var additive = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var matched = new List<object>();
        foreach (var item in ItemsList.Items)
        {
            if (ItemsList.ItemContainerGenerator.ContainerFromItem(item) is not ListViewItem row)
                continue;
            if (!row.IsVisible) continue;
            var topLeft = row.TranslatePoint(new Point(), ItemsList);
            var bounds = new Rect(topLeft, row.RenderSize);
            if (selection.IntersectsWith(bounds)) matched.Add(item);
        }

        if (!additive) ItemsList.SelectedItems.Clear();
        foreach (var item in matched)
            if (!ItemsList.SelectedItems.Contains(item))
                ItemsList.SelectedItems.Add(item);
        e.Handled = true;
    }

    private void ItemsList_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (_dragSelecting) e.Handled = true;
        EndDragSelection();
    }

    private void EndDragSelection()
    {
        if (ItemsList.IsMouseCaptured) ItemsList.ReleaseMouseCapture();
        if (_selectionMarquee is not null)
            _marqueeLayer?.Remove(_selectionMarquee);
        _selectionMarquee = null;
        _marqueeLayer = null;
        _dragOrigin = null;
        _dragSelecting = false;
    }

    private sealed class SelectionMarquee(UIElement adornedElement) : Adorner(adornedElement)
    {
        private Rect _bounds;
        private static readonly Brush FillBrush = new SolidColorBrush(Color.FromArgb(45, 36, 86, 232));
        private static readonly Pen Outline = new(new SolidColorBrush(Color.FromRgb(36, 86, 232)), 1);

        public void SetBounds(Rect bounds)
        {
            _bounds = bounds;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            base.OnRender(drawingContext);
            drawingContext.DrawRectangle(FillBrush, Outline, _bounds);
        }
    }
}
