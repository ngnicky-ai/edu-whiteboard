using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace WhiteboardApp.Controls;

public partial class ResizableCanvasItem : UserControl
{
    public static readonly DependencyProperty InnerElementProperty =
        DependencyProperty.Register(nameof(InnerElement), typeof(UIElement), typeof(ResizableCanvasItem),
            new PropertyMetadata(null));

    public UIElement? InnerElement
    {
        get => (UIElement?)GetValue(InnerElementProperty);
        set => SetValue(InnerElementProperty, value);
    }

    public static readonly RoutedEvent SelectedEvent = EventManager.RegisterRoutedEvent(
        nameof(Selected), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(ResizableCanvasItem));

    public event RoutedEventHandler Selected
    {
        add => AddHandler(SelectedEvent, value);
        remove => RemoveHandler(SelectedEvent, value);
    }

    public static readonly RoutedEvent DeleteRequestedEvent = EventManager.RegisterRoutedEvent(
        nameof(DeleteRequested), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(ResizableCanvasItem));

    public event RoutedEventHandler DeleteRequested
    {
        add => AddHandler(DeleteRequestedEvent, value);
        remove => RemoveHandler(DeleteRequestedEvent, value);
    }

    public static readonly RoutedEvent ItemChangedEvent = EventManager.RegisterRoutedEvent(
        nameof(ItemChanged), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(ResizableCanvasItem));

    /// <summary>Raised after the item was moved or resized by the user.</summary>
    public event RoutedEventHandler ItemChanged
    {
        add => AddHandler(ItemChangedEvent, value);
        remove => RemoveHandler(ItemChangedEvent, value);
    }

    private const double MinSize = 24;
    private bool _moved;
    private Point? _dragStart;
    private bool _moving;

    public ResizableCanvasItem()
    {
        InitializeComponent();
    }

    public void SetSelected(bool selected)
    {
        SelectionBorder.BorderThickness = new Thickness(selected ? 1.5 : 0);
        ResizeThumb.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        DeleteButton.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SelectionBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        RaiseEvent(new RoutedEventArgs(SelectedEvent, this));

        // Don't start a move drag if the click landed inside an editable TextBox (the hit element is
        // usually one of its internal parts, not the TextBox itself) — let it place the caret instead.
        if (IsInsideTextBox(e.OriginalSource as DependencyObject)) return;

        _dragStart = e.GetPosition(Parent as UIElement);
        _moving = true;
        SelectionBorder.CaptureMouse();
        e.Handled = false;
    }

    private void SelectionBorder_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_moving || _dragStart is null) return;
        if (Parent is not UIElement parent) return;

        var current = e.GetPosition(parent);
        var dx = current.X - _dragStart.Value.X;
        var dy = current.Y - _dragStart.Value.Y;

        var left = Canvas.GetLeft(this);
        var top = Canvas.GetTop(this);
        if (double.IsNaN(left)) left = 0;
        if (double.IsNaN(top)) top = 0;

        Canvas.SetLeft(this, left + dx);
        Canvas.SetTop(this, top + dy);

        _dragStart = current;
        _moved |= dx != 0 || dy != 0;
    }

    private void SelectionBorder_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _moving = false;
        SelectionBorder.ReleaseMouseCapture();
        if (_moved)
        {
            _moved = false;
            RaiseEvent(new RoutedEventArgs(ItemChangedEvent, this));
        }
    }

    private void ResizeThumb_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        RaiseEvent(new RoutedEventArgs(ItemChangedEvent, this));
    }

    private static bool IsInsideTextBox(DependencyObject? element)
    {
        for (var current = element; current != null;
             current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
        {
            if (current is TextBox) return true;
            if (current is ResizableCanvasItem) return false;
        }
        return false;
    }

    private void ResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        Width = Math.Max(MinSize, ActualWidth + e.HorizontalChange);

        // Text boxes only get a new width (text re-wraps); their height keeps following the content.
        if (InnerElement is not TextBox)
        {
            Height = Math.Max(MinSize, ActualHeight + e.VerticalChange);
        }
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        RaiseEvent(new RoutedEventArgs(DeleteRequestedEvent, this));
    }
}
