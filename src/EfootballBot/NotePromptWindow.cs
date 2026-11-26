using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace EfootballBot;

/// <summary>
/// 「发现问题手动停止」的可选补充说明弹窗。
/// 无边框主题化毛玻璃样式，点「生成反馈包」= 带说明生成；点「取消」或关闭窗口 = 不生成。
/// </summary>
public sealed class NotePromptWindow : Window
{
    private readonly TextBox _box;

    public string? Note { get; private set; }

    private NotePromptWindow(string title, string hint)
    {
        Title = title;
        Width = 500;
        SizeToContent = SizeToContent.Height;
        MinWidth = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;

        SetResourceReference(ForegroundProperty, "TextPrimary");
        SetResourceReference(FontFamilyProperty, "UiFont");
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);
        FontSize = 13;

        var shell = new Border
        {
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
        };
        shell.SetResourceReference(Border.BackgroundProperty, "DialogBg");
        shell.SetResourceReference(Border.BorderBrushProperty, "GlassStroke");
        if (TryFindResource("FloatShadow") is System.Windows.Media.Effects.Effect fx)
            shell.Effect = fx;

        var outer = new StackPanel();

        // 自绘标题栏
        var titleBar = new Grid
        {
            Height = 38,
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
            Text = title,
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
        closeBtn.Click += (_, _) => { Note = null; DialogResult = false; Close(); };
        titleBar.Children.Add(closeBtn);
        outer.Children.Add(titleBar);

        var root = new Grid { Margin = new Thickness(20, 4, 20, 20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var body = new StackPanel();
        body.Children.Add(new TextBlock
        {
            Text = hint,
            Style = (Style)Application.Current.Resources["BodyText"],
        });
        _box = new TextBox
        {
            Height = 112,
            Margin = new Thickness(0, 10, 0, 0),
            Padding = new Thickness(8, 6, 8, 6),
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalContentAlignment = VerticalAlignment.Top,
            FontSize = 13,
            MaxLength = 800,
        };
        body.Children.Add(_box);
        Grid.SetRow(body, 0);
        root.Children.Add(body);

        var btns = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
        };
        var ok = new Button
        {
            Content = "生成反馈包",
            MinWidth = 112,
            Style = (Style)Application.Current.Resources["PrimaryBtn"],
        };
        var cancel = new Button
        {
            Content = "取消",
            MinWidth = 82,
            Margin = new Thickness(0, 0, 10, 0),
            Style = (Style)Application.Current.Resources["GhostBtn"],
        };
        ok.Click += (_, _) => { Note = _box.Text; DialogResult = true; Close(); };
        cancel.Click += (_, _) => { Note = null; DialogResult = false; Close(); };
        ok.IsDefault = true;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Note = null;
                DialogResult = false;
                Close();
            }
        };
        btns.Children.Add(cancel);
        btns.Children.Add(ok);
        Grid.SetRow(btns, 1);
        root.Children.Add(btns);

        outer.Children.Add(root);
        shell.Child = outer;
        Content = shell;

        Loaded += (_, _) => _box.Focus();
    }

    /// <summary>弹出补充说明窗口。返回说明文本（可能为空串）；null = 用户取消生成反馈包。</summary>
    public static string? Ask(Window owner, string hint)
    {
        var w = new NotePromptWindow("问题反馈（可选补充说明）", hint) { Owner = owner };
        return w.ShowDialog() == true ? (w.Note ?? "") : null;
    }
}
