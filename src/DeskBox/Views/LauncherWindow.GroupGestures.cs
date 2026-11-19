using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using DeskBox.Services;

namespace DeskBox.Views;

internal partial class LauncherWindow
{
    private Thumb? _groupGesture;

    private readonly record struct GroupDragPreview(Rect Bounds, string Caption, Action? Commit);

    private void AddGroupGestures(Border card, StackPanel content, TextBlock heading, ContentControl body,
        LaunchSection section, LauncherGridLayout layout, Canvas surface, LauncherGroupBoard board,
        Action<LauncherGroupBoard> arrange)
    {
        var group = section.Group!;
        var title = new Thumb { Style = (Style)FindResource("GroupTitleGrip"), ToolTip = "拖动分组标题以调整顺序" };
        AutomationProperties.SetName(title, $"移动分组 {group.Title}");
        var header = new Grid();
        header.Children.Add(heading);
        header.Children.Add(title);
        content.Children.Add(header);

        var frame = new Grid();
        card.Child = frame;
        frame.Children.Add(content);

        WireGroupGesture(title, card, surface, _ =>
        {
            if (!new Rect(GroupDragOverlay.RenderSize).Contains(Mouse.GetPosition(GroupDragOverlay)) ||
                group.LauncherSlot is not int slot) return null;
            var hit = board.Snap(Mouse.GetPosition(surface), slot);
            if (hit is not { } target) return null;
            Action? commit = slot == target.Slot ? null :
                () => ChangeGroupLayout(() => LauncherGroupBoard.MoveTo(_config.Boxes, group, target.Slot));
            return new GroupDragPreview(OverlayBounds(target.Bounds), group.Title, commit);
        });

        if (section.Items.Count <= 1 || group.LauncherSlot is not int groupSlot) return;
        var corner = new Thumb { Style = (Style)FindResource("GroupCornerGrip"), ToolTip = "拖动角标以调整图标行列" };
        AutomationProperties.SetName(corner, $"调整图标行列 {group.Title}");
        frame.Children.Add(corner);
        var start = new Point();
        var dragScale = 1.0;
        var originalColumns = LauncherGridLayout.GroupColumns(section);
        var previewColumns = originalColumns;

        corner.DragStarted += (_, _) =>
        {
            _groupGesture = corner;
            originalColumns = previewColumns = LauncherGridLayout.GroupColumns(section);
            start = Mouse.GetPosition(GroupDragOverlay);
            // Keep pointer coordinates stable when expanding a group changes the overview scale.
            var origin = surface.TranslatePoint(new Point(), GroupDragOverlay);
            var unit = surface.TranslatePoint(new Point(1, 0), GroupDragOverlay);
            dragScale = unit.X - origin.X;
        };
        corner.DragDelta += (_, _) =>
        {
            if (_groupGesture == corner) UpdateResize();
        };
        corner.DragCompleted += (_, e) =>
        {
            if (_groupGesture != corner) return;
            if (e.Canceled) ApplyColumns(originalColumns);
            else UpdateResize();
            _groupGesture = null;
            if (!e.Canceled && previewColumns != originalColumns)
                ChangeGroupLayout(() => group.LauncherColumns = previewColumns);
            QueueSectionRefresh();
        };

        void UpdateResize()
        {
            if (dragScale <= 0) return;
            ApplyColumns(layout.ResizeGroup(section, (Mouse.GetPosition(GroupDragOverlay) - start) / dragScale));
        }

        void ApplyColumns(int columns)
        {
            if (columns == previewColumns || body.Content is not UniformGrid tiles) return;
            previewColumns = columns;
            tiles.Columns = columns;
            tiles.Width = columns * layout.TileWidth;
            arrange(board.ResizeCard(groupSlot, layout.GroupSize(section.Items.Count, columns)));
        }

        Rect OverlayBounds(Rect bounds) => new(surface.TranslatePoint(bounds.TopLeft, GroupDragOverlay),
            surface.TranslatePoint(bounds.BottomRight, GroupDragOverlay));
    }

    private void WireGroupGesture(Thumb thumb, Border card, Canvas surface, Func<Vector, GroupDragPreview?> getPreview)
    {
        Point start = default;
        Point startInOverlay = default;
        var moved = false;
        thumb.DragStarted += (_, _) =>
        {
            _groupGesture = thumb;
            start = Mouse.GetPosition(surface);
            startInOverlay = Mouse.GetPosition(GroupDragOverlay);
            moved = false;
        };
        thumb.DragDelta += (_, _) => UpdatePreview();
        thumb.DragCompleted += (_, e) =>
        {
            if (_groupGesture != thumb) return;
            var preview = e.Canceled ? null : PreviewAtPointer();
            _groupGesture = null;
            GroupDragOutline.Visibility = Visibility.Collapsed;
            card.Opacity = 1;
            preview?.Commit?.Invoke();
            QueueSectionRefresh();
        };

        GroupDragPreview? PreviewAtPointer()
        {
            var delta = Mouse.GetPosition(GroupDragOverlay) - startInOverlay;
            moved |= Math.Abs(delta.X) >= SystemParameters.MinimumHorizontalDragDistance ||
                Math.Abs(delta.Y) >= SystemParameters.MinimumVerticalDragDistance;
            return moved ? getPreview(Mouse.GetPosition(surface) - start) : null;
        }

        void UpdatePreview()
        {
            if (_groupGesture != thumb) return;
            if (PreviewAtPointer() is not { } preview)
            {
                GroupDragOutline.Visibility = Visibility.Collapsed;
                card.Opacity = 1;
                return;
            }
            card.Opacity = 0.75;
            Canvas.SetLeft(GroupDragOutline, preview.Bounds.Left);
            Canvas.SetTop(GroupDragOutline, preview.Bounds.Top);
            GroupDragOutline.Width = preview.Bounds.Width;
            GroupDragOutline.Height = preview.Bounds.Height;
            GroupDragCaption.Text = preview.Caption;
            GroupDragOutline.Visibility = Visibility.Visible;
        }
    }
}
