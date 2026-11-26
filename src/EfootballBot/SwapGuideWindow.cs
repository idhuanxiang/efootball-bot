using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace EfootballBot;

/// <summary>
/// 「开启满级自动换人」确认弹窗（首次勾选时弹出）：
/// 无边框主题化毛玻璃外壳，展示巡检路径图与三条硬要求。点「我已验证路径，开启」返回 true；取消/关闭返回 false。
/// </summary>
public sealed class SwapGuideWindow : Window
{
    private SwapGuideWindow()
    {
        Title = "开启满级自动换人";
        Width = 760;
        Height = 760;
        MinHeight = 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        SetResourceReference(ForegroundProperty, "TextPrimary");
        SetResourceReference(FontFamilyProperty, "UiFont");
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);

        var shell = new Border
        {
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
        };
        shell.SetResourceReference(Border.BackgroundProperty, "DialogBg");
        shell.SetResourceReference(Border.BorderBrushProperty, "GlassStroke");
        if (TryFindResource("FloatShadow") is System.Windows.Media.Effects.Effect fx)
            shell.Effect = fx;

        var outer = new Grid();
        outer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(38) });
        outer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // 自绘标题栏
        var titleBar = new Grid
        {
            Margin = new Thickness(16, 0, 10, 0),
            Background = Brushes.Transparent,
        };
        titleBar.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                try { DragMove(); }
                catch (InvalidOperationException) { }
            }
        };
        var titleText = new TextBlock
        {
            Text = "开启满级自动换人",
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        titleText.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        titleBar.Children.Add(titleText);

        var closeBtn = new Button
        {
            Content = "✕",
            Width = 28,
            Height = 28,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            FontSize = 14,
            ToolTip = "关闭",
        };
        closeBtn.SetResourceReference(Control.ForegroundProperty, "TextMuted");
        closeBtn.Click += (_, _) => { DialogResult = false; Close(); };
        titleBar.Children.Add(closeBtn);
        Grid.SetRow(titleBar, 0);
        outer.Children.Add(titleBar);

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(22, 6, 22, 18),
        };
        var root = new StackPanel();

        var head = new TextBlock
        {
            Text = "开启前请先手动验证巡检路径",
            FontSize = 15,
            FontWeight = FontWeights.Bold,
        };
        head.SetResourceReference(TextBlock.ForegroundProperty, "Warning");
        root.Children.Add(head);

        var desc = new TextBlock
        {
            Text = "开启后，每 N 场比赛结束回到我的联赛主页时，会自动打开比赛计划逐个检查首发 11 人：\n" +
                   "· 满级（等级 x/x）球员：能力点没用完先自动分配，再从后备换入整体评价最低的球员；能力点已用完直接换。\n" +
                   "· 替补球员不参与自动换人流程（只换首发 11 人）。\n" +
                   "· 现阶段只能自动更换色卡，普卡无法自动培养，请切换手动模式。",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12.5,
            LineHeight = 20,
            Margin = new Thickness(0, 8, 0, 0),
        };
        desc.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        root.Children.Add(desc);

        var warnBox = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 10, 0, 0),
        };
        warnBox.SetResourceReference(Border.BackgroundProperty, "Surface2");
        var warnText = new TextBlock
        {
            Text = "★ 请先按下面的图手动验证一遍：在游戏「比赛计划」页，从门将(GK)开始按 ↑ ← ↑ → → → → → ↓ ← " +
                   "逐格移动光标，确认能依次选中全部 11 名首发。若你的阵型走不完这条路径，请勿开启本功能。",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12.5,
            LineHeight = 20,
        };
        warnText.SetResourceReference(TextBlock.ForegroundProperty, "Warning");
        warnBox.Child = warnText;
        root.Children.Add(warnBox);

        var img = new Image
        {
            Source = new System.Windows.Media.Imaging.BitmapImage(
                new Uri("pack://application:,,,/Assets/autoswap_guide.png")),
            MaxWidth = 700,
            Margin = new Thickness(0, 12, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        root.Children.Add(img);

        var pathHint = new TextBlock
        {
            Text = "路径：门将(GK) → ↑ → ← → ↑ → → → → → → → ↓ → ←（共 11 名首发，光标默认落在门将上）",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11.5,
            Margin = new Thickness(0, 6, 0, 0),
        };
        pathHint.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        root.Children.Add(pathHint);

        var btns = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };
        var cancel = new Button
        {
            Content = "取消",
            MinWidth = 90,
            Margin = new Thickness(0, 0, 10, 0),
            Style = (Style)Application.Current.Resources["GhostBtn"],
        };
        var ok = new Button
        {
            Content = "我已验证路径，开启",
            MinWidth = 156,
            Style = (Style)Application.Current.Resources["PrimaryBtn"],
        };
        ok.Click += (_, _) => { DialogResult = true; Close(); };
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        btns.Children.Add(cancel);
        btns.Children.Add(ok);
        root.Children.Add(btns);

        scroll.Content = root;
        Grid.SetRow(scroll, 1);
        outer.Children.Add(scroll);
        shell.Child = outer;
        Content = shell;

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                DialogResult = false;
                Close();
            }
        };
    }

    /// <summary>弹确认窗；true = 用户确认开启。</summary>
    public static bool Confirm(Window owner)
    {
        var w = new SwapGuideWindow { Owner = owner };
        return w.ShowDialog() == true;
    }
}
