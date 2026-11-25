using System;
using System.Windows;
using System.Windows.Controls;

namespace ClipPlus;

// WPF owns item generation, pixel scrolling and container lifetime. The
// existing card interactions are built only for realized data items.
public sealed class HistoryList : ListBox
{
    internal Action<ListBoxItem, object>? PrepareRow { get; set; }
    internal Action<ListBoxItem>? ReleaseRow { get; set; }

    protected override void PrepareContainerForItemOverride(DependencyObject element, object item)
    {
        base.PrepareContainerForItemOverride(element, item);
        PrepareRow?.Invoke((ListBoxItem)element, item);
    }

    protected override void ClearContainerForItemOverride(DependencyObject element, object item)
    {
        ReleaseRow?.Invoke((ListBoxItem)element);
        base.ClearContainerForItemOverride(element, item);
    }
}
