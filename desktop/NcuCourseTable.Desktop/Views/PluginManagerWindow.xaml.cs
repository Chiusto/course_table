using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NcuCourseTable.Desktop.Interop;
using NcuCourseTable.Desktop.Services;
using Timetable.Plugins;

namespace NcuCourseTable.Desktop.Views;

/// <summary>插件列表：显示已载入的数据源插件，提供打开与目录入口。</summary>
public partial class PluginManagerWindow : Window
{
    private readonly App _app;

    public PluginManagerWindow()
    {
        InitializeComponent();
        _app = (App)Application.Current;
        Loaded += (_, _) =>
        {
            RefreshList();
            Motion.ScaleFadeIn(Root, 0.975, 0.34);
        };
    }

    private void RefreshList()
    {
        PluginsHost.Children.Clear();
        ErrorHost.Visibility = Visibility.Collapsed;
        ErrorList.Children.Clear();
        PluginDirText.Text = _app.PluginDir;

        bool hasPlugin = false;
        foreach (LoadedPlugin loaded in _app.Plugins)
        {
            hasPlugin = true;
            PluginsHost.Children.Add(BuildPluginCard(loaded));
        }

        EmptyHint.Visibility = hasPlugin ? Visibility.Collapsed : Visibility.Visible;

        if (_app.PluginErrors.Count > 0)
        {
            ErrorHost.Visibility = Visibility.Visible;
            foreach (PluginLoadError error in _app.PluginErrors)
            {
                ErrorList.Children.Add(new TextBlock
                {
                    Text = $"{System.IO.Path.GetFileName(error.Path)}：{error.Message}",
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = (Brush)FindResource("DangerBrush"),
                    Margin = new Thickness(0, 2, 0, 2),
                });
            }
        }
    }

    private Border BuildPluginCard(LoadedPlugin loaded)
    {
        ITimetablePlugin plugin = loaded.Instance;
        var card = new Border
        {
            Style = (Style)FindResource("Card"),
            Padding = new Thickness(16),
            Margin = new Thickness(0, 0, 0, 10),
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new Border
        {
            Width = 42,
            Height = 42,
            CornerRadius = new CornerRadius(12),
            Background = (Brush)FindResource("AccentSoftBrush"),
            VerticalAlignment = VerticalAlignment.Top,
        };
        icon.Child = new TextBlock
        {
            Text = "插",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("AccentBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(icon, 0);
        grid.Children.Add(icon);

        var text = new StackPanel { Margin = new Thickness(13, 0, 12, 0) };
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        titleRow.Children.Add(new TextBlock
        {
            Text = plugin.Name,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("LabelPrimaryBrush"),
        });
        titleRow.Children.Add(new TextBlock
        {
            Text = "  v" + plugin.Version,
            FontSize = 10.5,
            Foreground = (Brush)FindResource("LabelTertiaryBrush"),
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 1),
        });
        text.Children.Add(titleRow);
        text.Children.Add(new TextBlock
        {
            Text = plugin.Description,
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)FindResource("LabelSecondaryBrush"),
            Margin = new Thickness(0, 4, 0, 0),
            LineHeight = 17,
        });
        text.Children.Add(new TextBlock
        {
            Text = $"ID：{plugin.Id} · 已载入",
            FontSize = 10,
            Foreground = (Brush)FindResource("LabelQuaternaryBrush"),
            Margin = new Thickness(0, 5, 0, 0),
        });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var open = new Button
        {
            Content = "打开",
            Style = (Style)FindResource("BtnPrimary"),
            Padding = new Thickness(18, 6, 18, 6),
            VerticalAlignment = VerticalAlignment.Center,
        };
        open.Click += (_, _) => OpenPlugin(loaded);
        Grid.SetColumn(open, 2);
        grid.Children.Add(open);

        card.Child = grid;
        return card;
    }

    private void OpenPlugin(LoadedPlugin loaded)
    {
        try
        {
            loaded.Instance.Open(this);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                $"打开插件“{loaded.Instance.Name}”失败：\n{ex.Message}",
                "插件错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnOpenPluginDir(object sender, RoutedEventArgs e) => _app.OpenPluginsDirectory();

    private void OnOpenDataDir(object sender, RoutedEventArgs e) => _app.OpenDataDir();
}
