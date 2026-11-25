using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ClipPlus.Core;

namespace ClipPlus;

public partial class PopupWindow
{
    private IReadOnlyList<ClipCategory> _categories = Array.Empty<ClipCategory>();
    private readonly Dictionary<long, Button> _categoryButtons = new();
    private long? _activeView;
    private bool _compact, _categoryTabsDirty = true, _draggingCategory;
    private Row? _openCategoryRow;
    private Grid? _categoryEditHost;
    private TextBox? _categoryEditor;
    private long? _editingCategory;
    private readonly DispatcherTimer _categoryDragTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private int _dragScrollDirection;
    private const string CategoryDragFormat = "ClipPlus.Category";

    private void InitializeCategories()
    {
        PinnedTab.Click += (_, _) => Run(() => SelectView(ClipStore.PinnedView));
        RecentTab.Click += (_, _) => Run(() => SelectView(ClipStore.RecentView));
        AddCategoryButton.Click += (_, _) => Run(() => BeginCategoryEdit(null));
        CompactButton.Click += (_, _) => Run(() =>
        {
            _compact = !_compact;
            PersistView();
            Refresh();
            FocusSearch();
        });
        CategoryNavigation.PreviewMouseWheel += (_, e) =>
        {
            if (CategoryScroll.ScrollableWidth <= 0) return;
            CategoryScroll.ScrollToHorizontalOffset(CategoryScroll.HorizontalOffset - e.Delta / 3.0);
            e.Handled = true;
        };
        CategoryScroll.DragOver += CategoryDragOver;
        CategoryScroll.DragLeave += (_, _) => _dragScrollDirection = 0;
        CategoryScroll.Drop += CategoryDrop;
        _categoryDragTimer.Tick += (_, _) =>
        {
            if (_dragScrollDirection != 0)
                CategoryScroll.ScrollToHorizontalOffset(CategoryScroll.HorizontalOffset + 12 * _dragScrollDirection);
        };
    }

    private void PersistView()
    {
        if (_activeView is long view) _saveView(view, _compact);
    }

    private void SyncCategoryBar(bool hasPins)
    {
        var categories = _store.Categories();
        if (!_categories.SequenceEqual(categories))
        {
            _categories = categories;
            _categoryTabsDirty = true;
        }
        if (_activeView is null)
        {
            _activeView = hasPins ? ClipStore.PinnedView : ClipStore.RecentView;
            PersistView();
        }
        else if (_activeView != ClipStore.RecentView &&
                 !(_activeView == ClipStore.PinnedView && hasPins) &&
                 !_categories.Any(c => c.Id == _activeView))
        {
            _activeView = ClipStore.RecentView;
            PersistView();
        }

        PinnedTab.Visibility = hasPins ? Visibility.Visible : Visibility.Collapsed;
        if (_categoryTabsDirty) RebuildCategoryTabs();
        SetTabState(PinnedTab, _activeView == ClipStore.PinnedView);
        SetTabState(RecentTab, _activeView == ClipStore.RecentView);
        foreach (var pair in _categoryButtons)
            SetTabState(pair.Value, _activeView == pair.Key);
        AddCategoryButton.IsEnabled = _categories.Count < ClipStore.CategoryCapacity;
        AddCategoryButton.ToolTip = AddCategoryButton.IsEnabled ? "Add category" :
            $"Maximum {ClipStore.CategoryCapacity} custom categories";
        var searching = !string.IsNullOrWhiteSpace(SearchBox.Text);
        CategoryNavigation.Visibility = searching ? Visibility.Collapsed : Visibility.Visible;
        SearchScope.Visibility = searching ? Visibility.Visible : Visibility.Collapsed;
        var label = _compact ? "Expand titled clips" : "Collapse titled clips";
        if (CompactButton.Content is null || AutomationProperties.GetName(CompactButton) != label)
        {
            CompactButton.Content = Glyph(_compact ? "\uE70D" : "\uE70E");
            CompactButton.ToolTip = label;
            AutomationProperties.SetName(CompactButton, label);
        }
    }

    private static void SetTabState(Button tab, bool active)
    {
        var status = active ? "Selected" : "";
        if (AutomationProperties.GetItemStatus(tab) == status) return;
        if (active)
        {
            tab.SetResourceReference(BackgroundProperty, "CategorySelectedBrush");
            tab.SetResourceReference(ForegroundProperty, "CategorySelectedTextBrush");
            tab.FontWeight = FontWeights.SemiBold;
        }
        else
        {
            tab.Background = System.Windows.Media.Brushes.Transparent;
            tab.BorderBrush = System.Windows.Media.Brushes.Transparent;
            tab.SetResourceReference(ForegroundProperty, "TextBrush");
            tab.FontWeight = FontWeights.Normal;
        }
        AutomationProperties.SetItemStatus(tab, status);
    }

    private void SelectView(long view)
    {
        if (_activeView != view)
        {
            _activeView = view;
            PersistView();
        }
        // Keep the tab objects alive when merely changing views, so a
        // second mouse click still reaches the same tab for inline editing.
        Refresh(false);
        RevealActiveCategory();
        FocusSearch();
    }

    private void RevealActiveCategory()
    {
        if (_activeView is long id && _categoryButtons.TryGetValue(id, out var tab))
            tab.BringIntoView();
    }

    private void RebuildCategoryTabs()
    {
        CategoryTabs.Children.Clear();
        _categoryButtons.Clear();
        foreach (var category in _categories)
        {
            var tab = new Button
            {
                Content = new TextBlock
                {
                    Text = category.Name, MaxWidth = 100, TextTrimming = TextTrimming.CharacterEllipsis,
                    FlowDirection = TextDirection(category.Name)
                },
                Style = (Style)FindResource("CategoryChip"),
                ToolTip = $"{category.Name}\nDouble-click or F2 to edit; drag or Ctrl+Left/Right to reorder"
            };
            AutomationProperties.SetName(tab, category.Name);
            SetTabState(tab, _activeView == category.Id);
            tab.Click += (_, _) => Run(() => SelectView(category.Id));
            Point start = default;
            tab.PreviewMouseLeftButtonDown += (_, e) =>
            {
                if (e.ClickCount == 2)
                {
                    e.Handled = true;
                    Run(() => BeginCategoryEdit(category.Id));
                }
                else start = e.GetPosition(CategoryTabs);
            };
            tab.PreviewMouseMove += (_, e) =>
            {
                if (e.LeftButton != MouseButtonState.Pressed || !tab.IsMouseCaptured || _draggingCategory) return;
                var position = e.GetPosition(CategoryTabs);
                if (Math.Abs(position.X - start.X) < SystemParameters.MinimumHorizontalDragDistance &&
                    Math.Abs(position.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                Run(() => DragCategory(tab, category.Id));
                e.Handled = true;
            };
            tab.PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.F2)
                {
                    e.Handled = true;
                    Run(() => BeginCategoryEdit(category.Id));
                }
                else if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && (e.Key is Key.Left or Key.Right))
                {
                    e.Handled = true;
                    Run(() =>
                    {
                        var index = _categories.ToList().FindIndex(c => c.Id == category.Id);
                        _store.MoveCategory(category.Id, index + (e.Key == Key.Left ? -1 : 1));
                        Refresh();
                        if (_categoryButtons.TryGetValue(category.Id, out var moved))
                        {
                            moved.BringIntoView();
                            moved.Focus();
                        }
                    });
                }
            };
            _categoryButtons.Add(category.Id, tab);
            CategoryTabs.Children.Add(tab);
        }
        _categoryTabsDirty = false;
    }

    private void DragCategory(Button tab, long id)
    {
        _draggingCategory = true;
        tab.ReleaseMouseCapture();
        _categoryDragTimer.Start();
        try
        {
            DragDrop.DoDragDrop(tab, new DataObject(CategoryDragFormat, id), DragDropEffects.Move);
        }
        finally
        {
            _categoryDragTimer.Stop();
            _dragScrollDirection = 0;
            _draggingCategory = false;
        }
        Refresh();
        if (_categoryButtons.TryGetValue(id, out var moved)) moved.BringIntoView();
        FocusSearch();
    }

    private void CategoryDragOver(object sender, DragEventArgs e)
    {
        e.Effects = _draggingCategory && e.Data.GetDataPresent(CategoryDragFormat) ?
            DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
        var x = e.GetPosition(CategoryScroll).X;
        _dragScrollDirection = e.Effects == DragDropEffects.None ? 0 :
            x < 18 ? -1 : x > CategoryScroll.ActualWidth - 18 ? 1 : 0;
    }

    private void CategoryDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        _dragScrollDirection = 0;
        e.Effects = DragDropEffects.None;
        if (!_draggingCategory || e.Data.GetData(CategoryDragFormat) is not long id) return;
        var from = _categories.ToList().FindIndex(c => c.Id == id);
        if (from < 0) return;
        var x = e.GetPosition(CategoryTabs).X;
        var index = 0;
        foreach (var category in _categories)
        {
            var tab = _categoryButtons[category.Id];
            var middle = tab.TranslatePoint(new Point(tab.ActualWidth / 2, 0), CategoryTabs).X;
            if (x < middle) break;
            index++;
        }
        if (index > from) index--;
        Run(() =>
        {
            _store.MoveCategory(id, index);
            e.Effects = DragDropEffects.Move;
        });
    }

    private void BeginCategoryEdit(long? id)
    {
        if (id is null && _categories.Count >= ClipStore.CategoryCapacity) return;
        EndTitle(false, false);
        ClosePalette(false);
        CloseCategoryPicker(false);
        EndCategoryEdit();
        if (_categoryTabsDirty) RebuildCategoryTabs();
        var category = _categories.FirstOrDefault(c => c.Id == id);
        if (id.HasValue && category is null) return;
        _editingCategory = id;
        var host = new Grid { Margin = new Thickness(0, 0, 3, 0) };
        host.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        host.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var editor = new TextBox
        {
            Text = category?.Name ?? "", FlowDirection = TextDirection(category?.Name ?? ""), Width = 100, MinHeight = 26, FontSize = 12,
            Padding = new Thickness(3, 1, 3, 1), ToolTip = "Enter to save; Esc to cancel"
        };
        AutomationProperties.SetName(editor, "Category name");
        host.Children.Add(editor);
        var action = IconButton(Glyph(id.HasValue ? "\uE711" : "\uE73E"), id.HasValue ? "Delete category" : "Save category");
        action.Margin = new Thickness(4, 0, 0, 0);
        Grid.SetColumn(action, 1);
        host.Children.Add(action);
        var confirming = false;
        void SetDeleteConfirmation(bool value)
        {
            confirming = value;
            action.Content = Glyph(value ? "\uE73E" : "\uE711", value);
            action.ToolTip = value ? "Confirm delete category; keep all clips" : "Delete category; keep all clips";
            AutomationProperties.SetName(action, (string)action.ToolTip);
        }
        if (id.HasValue) SetDeleteConfirmation(false);
        action.Click += (_, _) => Run(() =>
        {
            if (!id.HasValue) { SaveCategoryEdit(); return; }
            if (!confirming) { SetDeleteConfirmation(true); return; }
            _store.DeleteCategory(id.Value);
            EndCategoryEdit();
            Refresh(false);
            FocusSearch();
        });
        editor.TextChanged += (_, _) =>
        {
            editor.FlowDirection = TextDirection(editor.Text);
            if (confirming) SetDeleteConfirmation(false);
        };
        _categoryEditHost = host;
        _categoryEditor = editor;
        if (id is long editing && _categoryButtons.TryGetValue(editing, out var original))
        {
            var index = CategoryTabs.Children.IndexOf(original);
            CategoryTabs.Children.RemoveAt(index);
            CategoryTabs.Children.Insert(index, host);
        }
        else CategoryTabs.Children.Add(host);

        host.LostKeyboardFocus += (_, _) =>
        {
            if (_categoryEditHost == host) RequestInteractionReconciliation();
        };
        host.BringIntoView();
        editor.Focus();
        editor.SelectAll();
    }

    private void SaveCategoryEdit()
    {
        if (_categoryEditor is not { } editor) return;
        if (ClipStore.CategoryNameError(editor.Text, _categories, _editingCategory) is string error)
        {
            ShowStatus(error);
            editor.Focus();
            return;
        }
        if (_editingCategory is long id) _store.RenameCategory(id, editor.Text);
        else
        {
            if (_categories.Count >= ClipStore.CategoryCapacity)
            {
                ShowStatus($"Maximum {ClipStore.CategoryCapacity} custom categories.");
                return;
            }
            _activeView = _store.CreateCategory(editor.Text);
            PersistView();
        }
        EndCategoryEdit();
        Refresh(false);
        RevealActiveCategory();
        FocusSearch();
    }

    private void EndCategoryEdit()
    {
        if (_categoryEditHost is not { } host) return;
        var id = _editingCategory;
        _categoryEditor = null;
        _categoryEditHost = null;
        _editingCategory = null;
        var index = CategoryTabs.Children.IndexOf(host);
        if (index < 0) return;
        CategoryTabs.Children.RemoveAt(index);
        if (id is long original && _categoryButtons.TryGetValue(original, out var tab))
            CategoryTabs.Children.Insert(index, tab);
    }

    private Button MakeCategoryButton(ClipItem item, bool compact)
    {
        var category = _categories.FirstOrDefault(c => c.Id == item.CategoryId);
        if (category is null)
        {
            var add = IconButton(Glyph("\uE710"), "Assign category");
            add.HorizontalAlignment = HorizontalAlignment.Left;
            return add;
        }
        var label = category.Name;
        var text = new TextBlock
        {
            Text = label, FontSize = 11,
            MaxWidth = compact ? 66 : 160, TextTrimming = TextTrimming.CharacterEllipsis,
            FlowDirection = TextDirection(label), VerticalAlignment = VerticalAlignment.Center
        };
        text.SetResourceReference(ForegroundProperty, "MutedBrush");
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(text);
        var arrow = Glyph("\uE70D");
        arrow.FontSize = 9;
        arrow.Margin = new Thickness(3, 0, 0, 0);
        arrow.VerticalAlignment = VerticalAlignment.Center;
        content.Children.Add(arrow);
        var description = $"Change category: {category.Name}";
        var button = new Button
        {
            Style = (Style)FindResource("CategoryChip"), Content = content, FontSize = 11, MinHeight = 26,
            Padding = new Thickness(3, 0, 3, 0), HorizontalAlignment = HorizontalAlignment.Left,
            ToolTip = description, Margin = compact ? new Thickness(0) : new Thickness(0, 1, 0, 0)
        };

        AutomationProperties.SetName(button, description);
        return button;
    }

    private void ToggleCategoryPicker(Row row)
    {
        if (_openCategoryRow == row)
        {
            CloseCategoryPicker();
            FocusSearch();
            return;
        }
        EndTitle(false, false);
        ClosePalette(false);
        EndCategoryEdit();
        CloseCategoryPicker(false);
        ClipList.SelectedItem = row.Entry;
        _openCategoryRow = row;
        row.CategoryChoices.Children.Clear();
        void Choice(string name, long? id)
        {
            var selected = row.Item.CategoryId == id;
            var button = new Button
            {
                Content = new TextBlock
                {
                    Text = selected ? "✓ " + name : name, MaxWidth = 170,
                    TextTrimming = TextTrimming.CharacterEllipsis, FlowDirection = TextDirection(name)
                },
                Style = (Style)FindResource("CategoryChip"), Padding = new Thickness(5, 2, 5, 2),
                Margin = new Thickness(0, 0, 3, 3), ToolTip = name
            };
            SetTabState(button, selected);
            var actionName = id.HasValue ? $"Assign to {name}" : "No category";
            AutomationProperties.SetName(button, selected ? $"{actionName}, selected" : actionName);
            button.Click += (_, _) => Run(() =>
            {
                _store.SetCategory(row.Item.Id, id);
                Refresh();
                FocusSearch();
            });
            row.CategoryChoices.Children.Add(button);
        }
        Choice("No category", null);
        foreach (var category in _categories) Choice(category.Name, category.Id);
        if (_categories.Count == 0)
        {
            var hint = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(SearchBox.Text) ? "Use + above to add a category." :
                    "Clear search and use + above to add a category.",
                TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(3)
            };
            hint.SetResourceReference(ForegroundProperty, "MutedBrush");
            row.CategoryChoices.Children.Add(hint);
        }
        row.CategoryPicker.Visibility = Visibility.Visible;
        if (row.CategoryChoices.Children[0] is Button first) first.Focus();
        row.CategoryPicker.BringIntoView();
    }

    private void CloseCategoryPicker(bool processPending = true)
    {
        if (_openCategoryRow is not { } row) return;
        _openCategoryRow = null;
        row.CategoryPicker.Visibility = Visibility.Collapsed;
        if (processPending) FlushPendingHistory();
    }
}
