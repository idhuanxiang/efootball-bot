using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace EfootballBot;

/// <summary>
/// 热键记录弹窗：无边框主题化样式，按下任意键（可含修饰键）或鼠标键即记录，Esc 取消。
/// 结果写入 <see cref="Code"/>/<see cref="Modifiers"/>/<see cref="IsMouse"/>。
/// </summary>
public sealed class HotkeyRecordWindow : Window
{
    /// <summary>记录到的键码（键盘 VK 或鼠标键码 1左/2右/4中/5X1/6X2）。</summary>
    public int Code { get; private set; }
    /// <summary>修饰键掩码（Alt=1 Ctrl=2 Shift=4 Win=8）。</summary>
    public int Modifiers { get; private set; }
    /// <summary>是否鼠标键。</summary>
    public bool IsMouse { get; private set; }
    /// <summary>是否成功记录（false=取消）。</summary>
    public bool Recorded { get; private set; }

    private readonly Grid _titleBar;
    private readonly TextBlock _badgeText;

    public HotkeyRecordWindow(string title)
    {
        Title = title;
        Width = 420;
        SizeToContent = SizeToContent.Height;
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

        var shell = new Border
        {
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
        };
        shell.SetResourceReference(Border.BackgroundProperty, "DialogBg");
        shell.SetResourceReference(Border.BorderBrushProperty, "GlassStroke");
        if (TryFindResource("FloatShadow") is System.Windows.Media.Effects.Effect fx)
            shell.Effect = fx;

        var layout = new StackPanel();

        // 自绘标题栏
        _titleBar = new Grid
        {
            Height = 38,
            Margin = new Thickness(16, 0, 10, 0),
            Background = Brushes.Transparent,
        };
        _titleBar.MouseLeftButtonDown += (_, e) =>
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
        _titleBar.Children.Add(titleText);

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
        closeBtn.Click += (_, _) => Close();
        _titleBar.Children.Add(closeBtn);
        layout.Children.Add(_titleBar);

        var body = new StackPanel { Margin = new Thickness(20, 4, 20, 20) };
        var hint = new TextBlock
        {
            Text = "请按下要记录的按键组合（键盘任意键或鼠标键，可含 Ctrl / Shift / Alt / Win）",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12.5,
            LineHeight = 19,
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        body.Children.Add(hint);

        var badge = new Border
        {
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 14, 14, 14),
            Margin = new Thickness(0, 12, 0, 10),
        };
        badge.SetResourceReference(Border.BackgroundProperty, "InsetBg");
        badge.SetResourceReference(Border.BorderBrushProperty, "Divider");
        _badgeText = new TextBlock
        {
            Text = "等待按键输入…",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _badgeText.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        badge.Child = _badgeText;
        body.Children.Add(badge);

        var escHint = new TextBlock
        {
            Text = "按 Esc 取消",
            FontSize = 11.5,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        escHint.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        body.Children.Add(escHint);

        layout.Children.Add(body);
        shell.Child = layout;
        Content = shell;

        PreviewKeyDown += OnKey;
        PreviewKeyUp += (_, _) => UpdateModifierBadge();
        PreviewMouseDown += OnMouse;
    }

    private void UpdateModifierBadge()
    {
        var parts = new List<string>();
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) parts.Add("Ctrl");
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) parts.Add("Shift");
        if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0) parts.Add("Alt");
        if ((Keyboard.Modifiers & ModifierKeys.Windows) != 0) parts.Add("Win");
        _badgeText.Text = parts.Count > 0
            ? string.Join(" + ", parts) + " + …"
            : "等待按键输入…";
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape) { Close(); return; }

        // 单独的修饰键不作为主键，仅实时更新徽章提示
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            UpdateModifierBadge();
            return;
        }
        // 方向/编辑键不适合
        if (key is Key.Tab or Key.Left or Key.Right or Key.Up or Key.Down or Key.Back or Key.Delete)
            return;

        int mods = 0;
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) mods |= 2;
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) mods |= 4;
        if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0) mods |= 1;
        if ((Keyboard.Modifiers & ModifierKeys.Windows) != 0) mods |= 8;

        Code = KeyInterop.VirtualKeyFromKey(key);
        Modifiers = mods;
        IsMouse = false;
        Recorded = true;
        Close();
    }

    private void OnMouse(object sender, MouseButtonEventArgs e)
    {
        // 点击自绘标题栏（拖拽/关闭按钮）时不录制鼠标按键
        if (_titleBar.IsMouseOver) return;

        e.Handled = true;
        Code = e.ChangedButton switch
        {
            MouseButton.Left => 1,
            MouseButton.Right => 2,
            MouseButton.Middle => 4,
            MouseButton.XButton1 => 5,
            MouseButton.XButton2 => 6,
            _ => 0,
        };
        if (Code == 0) return;

        int mods = 0;
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) mods |= 2;
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) mods |= 4;
        if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0) mods |= 1;
        if ((Keyboard.Modifiers & ModifierKeys.Windows) != 0) mods |= 8;

        Modifiers = mods;
        IsMouse = true;
        Recorded = true;
        Close();
    }
}
