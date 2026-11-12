using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ShapePath = System.Windows.Shapes.Path;
using DeskBox.Services;

namespace DeskBox.Views;

internal partial class LauncherWindow
{
    private static readonly Brush RimBrush = MakeBrush(206, 125, 147, 164);
    private static readonly Brush PlateBrush = MakeBrush(250, 27, 36, 46);
    private IReadOnlyList<LaunchSection> _wheelSections = Array.Empty<LaunchSection>();
    private IReadOnlyList<LaunchEntry> _dockEntries = Array.Empty<LaunchEntry>();
    private Size _wheelSize;
    private double _wheelIconSize;
    private double _dockIconSize;
    private Canvas? _wheelFrame;
    private Grid? _searchHub;
    private (Point Center, double Radius) _hubPosition;

    private void RenderOverview()
    {
        if (OverviewPanel.Visibility != Visibility.Visible) return;
        var size = Overview.RenderSize;
        if (size.Width < 90 || size.Height < 90) return;
        var sections = _sections.Where(section => section.Group is not null).ToArray();
        if (size == _wheelSize && _config.IconSize == _wheelIconSize && SameSections(_wheelSections, sections)) return;
        var titles = sections.Select(BuildSectorTitle).ToArray();
        foreach (var title in titles) title.Measure(new Size(120, 26));
        var pitch = Math.Clamp(_config.IconSize + 47, 80, 116);
        var layout = LauncherWheelLayout.Arrange(size, sections.Select(section => section.Items.Count).ToArray(), pitch,
            titles.Select(title => title.DesiredSize).ToArray(), _config.IconSize);
        if (layout.Radius < 45) return;
        var frame = new Canvas { Width = size.Width, Height = size.Height };
        for (var i = 0; i < layout.Sectors.Count; i++)
        {
            var sector = layout.Sectors[i];
            AddSector(frame, sections[i], layout, sector);
            titles[i].Width = sector.Title.Width;
            Place(frame, titles[i], sector.Title.TopLeft);
        }
        var hubPosition = (layout.Center, layout.HubRadius);
        if (_searchHub is null || _hubPosition != hubPosition)
        {
            if (_searchHub is not null) OverviewSearch.Children.Remove(_searchHub);
            _searchHub = AddSearchHub(layout.Center, layout.HubRadius);
            _hubPosition = hubPosition;
        }
        if (_wheelFrame is not null) Overview.Children.Remove(_wheelFrame);
        Overview.Children.Add(frame);
        _wheelFrame = frame;
        _wheelSize = size;
        _wheelIconSize = _config.IconSize;
        _wheelSections = sections;
    }

    private Button BuildSectorTitle(LaunchSection section)
    {
        var title = PanelButton(new TextBlock
        {
            Text = section.Name, TextTrimming = TextTrimming.CharacterEllipsis,
            FontWeight = FontWeights.SemiBold, FontSize = 14,
        });
        title.MaxWidth = 120;
        title.Height = 26;
        title.Padding = new Thickness(10, 0, 10, 0);
        title.Background = PlateBrush;
        title.BorderBrush = RimBrush;
        title.ToolTip = section.Name;
        title.Click += (_, _) => ShowSection(Id(section));
        if (section.Group is { IsVirtual: true } group)
        {
            title.ContextMenu = GroupMenu(group);
            WireDrop(title, group);
        }
        return title;
    }

    private void AddSector(Canvas frame, LaunchSection section, LauncherWheelLayout.Layout layout, LauncherWheelLayout.Sector sector)
    {
        var bounds = sector.Shape.Bounds;
        var offset = (Vector)bounds.TopLeft;
        Geometry Local(Geometry geometry)
        {
            var clone = geometry.Clone();
            clone.Transform = new TranslateTransform(-offset.X, -offset.Y);
            clone.Freeze();
            return clone;
        }
        var area = new Grid { Width = bounds.Width, Height = bounds.Height, Clip = Local(sector.Shape), Background = Brushes.Transparent };
        Place(frame, area, bounds.TopLeft);
        var localCenter = layout.Center - offset;
        var surface = new Grid
        {
            Background = PlateBrush, OpacityMask = CreateWheelFade(localCenter, layout.FadeStart, layout.Radius),
            IsHitTestVisible = false,
        };
        area.Children.Add(surface);
        AddBackground(surface, Overview, bounds);
        surface.Children.Add(Rim(Local(sector.Outline)));

        var content = new Canvas();
        area.Children.Add(content);
        if (section.Group is { IsVirtual: true } group)
        {
            WireDrop(area, group);
            var menu = GroupMenu(group);
            var open = new MenuItem { Header = "打开分组" };
            open.Click += (_, _) => ShowSection(Id(section));
            menu.Items.Insert(0, open);
            area.ContextMenu = menu;
        }
        var size = layout.TileSize;
        var overflow = sector.Slots.Count < section.Items.Count;
        for (var i = 0; i < sector.Slots.Count; i++)
        {
            Button tile;
            var anchor = layout.IconAnchor;
            if (overflow && i == sector.Slots.Count - 1)
            {
                tile = PanelButton($"+{section.Items.Count - i}");
                tile.Width = size.Width;
                tile.Height = size.Height;
                tile.Padding = new Thickness(0);
                tile.ToolTip = "打开完整分组";
                tile.Click += (_, _) => ShowSection(Id(section));
                anchor = size.Height / 2;
            }
            else
            {
                var item = section.Items[i];
                tile = BuildTile(item, size);
                if (section.Group is { IsVirtual: true } tileGroup) WireDrop(tile, tileGroup);
            }
            Place(content, tile, sector.Slots[i] - offset - new Vector(size.Width / 2, anchor));
        }
    }

    private static Brush CreateWheelFade(Point center, double start, double radius)
    {
        var fade = new RadialGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute, Center = center, GradientOrigin = center,
            RadiusX = radius, RadiusY = radius,
        };
        fade.GradientStops.Add(new GradientStop(Colors.White, start / radius));
        for (var i = 1; i <= 4; i++)
        {
            var t = i / 4.0;
            var alpha = 1 - t * t * (3 - 2 * t);
            fade.GradientStops.Add(new GradientStop(Color.FromArgb((byte)Math.Round(alpha * 255), 255, 255, 255),
                (start + (radius - start) * t) / radius));
        }
        fade.Freeze();
        return fade;
    }

    private Grid AddSearchHub(Point center, double radius)
    {
        var shape = new EllipseGeometry(new Point(radius, radius), radius, radius);
        var glyph = Geometry.Parse("M 21,12 A 9,9 0 1 1 3,12 A 9,9 0 1 1 21,12 M 19,19 L 27,27").Clone();
        glyph.Transform = new TranslateTransform(radius - 15, radius - 15);
        var drawing = new DrawingGroup();
        using (var context = drawing.Open())
        {
            context.DrawGeometry(PlateBrush, new Pen(RimBrush, 2), shape);
            context.DrawGeometry(null, new Pen(TextBrush, 2.4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, glyph);
        }
        var brush = new DrawingBrush(drawing)
        {
            ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, 2 * radius, 2 * radius),
            Stretch = Stretch.Fill,
        };
        brush.Freeze();
        var hub = new Grid { Width = 2 * radius, Height = 2 * radius, Clip = shape, Background = brush };
        var origin = center - new Vector(radius, radius);
        var button = PanelButton("");
        button.ToolTip = "搜索（Ctrl+F）";
        button.Padding = new Thickness(0);
        System.Windows.Automation.AutomationProperties.SetName(button, "搜索");
        button.Click += (_, _) => OpenSearch();
        hub.Children.Add(button);
        Place(OverviewSearch, hub, origin);
        return hub;
    }

    private void RenderDock()
    {
        var entries = _sections.FirstOrDefault(section => Id(section) == "desktop")?.Items ?? Array.Empty<LaunchEntry>();
        if (_dockIconSize == _config.IconSize && _dockEntries.SequenceEqual(entries)) return;
        var tiles = entries.Select(item => BuildTile(item, new Size(74, 72))).ToArray();
        DockIcons.Children.Clear();
        foreach (var tile in tiles) DockIcons.Children.Add(tile);
        _dockIconSize = _config.IconSize;
        _dockEntries = entries;
    }

    private void SizeDetail()
    {
        var size = MainArea.RenderSize;
        var radius = Math.Min(size.Width, size.Height) / 2 - 2;
        if (radius <= 0) return;
        var shape = new EllipseGeometry(new Point(size.Width / 2, size.Height / 2), radius, radius);
        DetailPlate.Clip = shape;
        if (DetailPlate.Children.Count > 1) DetailPlate.Children.RemoveAt(1);
        DetailPlate.Children.Add(Rim(shape));
        DetailContent.Width = radius * 1.56;
        DetailContent.Height = radius * 1.18;
    }

    private void SizeDock()
    {
        if (DockPlate.ActualWidth < 1 || DockPlate.ActualHeight < 1) return;
        DockPlate.Clip = new RectangleGeometry(new Rect(DockPlate.RenderSize), 22, 22);
    }

    private static ShapePath Rim(Geometry geometry) => new()
    {
        Data = geometry, Stroke = RimBrush, StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round, IsHitTestVisible = false,
    };

    private static void Place(Canvas canvas, FrameworkElement element, Point origin)
    {
        Canvas.SetLeft(element, origin.X);
        Canvas.SetTop(element, origin.Y);
        canvas.Children.Add(element);
    }
}
