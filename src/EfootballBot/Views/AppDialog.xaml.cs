using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using EfootballBot.Core.Config;
using EfootballBot.Core.UI;

namespace EfootballBot.Views;

/// <summary>
/// 统一无边框深色毛玻璃弹窗，替代系统原生 MessageBox，与软件本体视觉世界保持一致。
/// </summary>
public partial class AppDialog : Window
{
    public AppDialog()
    {
        InitializeComponent();
        ApplyShellBackdrop();
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

    public static void Info(Window? owner, string title, string message, string okText = "确定")
        => ShowCore(owner, title, message, okText, cancelText: null, isWarn: false, danger: false);

    public static void Warn(Window? owner, string title, string message, string okText = "确定")
        => ShowCore(owner, title, message, okText, cancelText: null, isWarn: true, danger: false);

    public static bool Confirm(
        Window? owner,
        string title,
        string message,
        string okText = "确定",
        string cancelText = "取消",
        bool danger = false)
        => ShowCore(owner, title, message, okText, cancelText, isWarn: danger, danger: danger);

    private static bool ShowCore(
        Window? owner,
        string title,
        string message,
        string okText,
        string? cancelText,
        bool isWarn,
        bool danger)
    {
        var dlg = new AppDialog
        {
            Title = title,
        };
        if (owner is not null && owner.IsLoaded)
            dlg.Owner = owner;
        else
            dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        dlg.TxtTitle.Text = title;
        dlg.TxtMessage.Text = message;
        dlg.OkBtn.Content = okText;
        dlg.OkBtn.IsDefault = !danger;

        if (!string.IsNullOrEmpty(cancelText))
        {
            dlg.CancelBtn.Content = cancelText;
            dlg.CancelBtn.Visibility = Visibility.Visible;
        }

        if (isWarn)
        {
            var resKey = danger ? "Danger" : "Warning";
            if (dlg.TryFindResource(resKey) is Brush dotBrush)
                dlg.TitleDot.Fill = dotBrush;
        }

        if (danger && dlg.TryFindResource("Danger") is Brush dangerBrush)
        {
            dlg.OkBtn.Background = dangerBrush;
            dlg.OkBtn.BorderBrush = dangerBrush;
        }

        return dlg.ShowDialog() == true;
    }

    private void ApplyShellBackdrop()
    {
        try
        {
            var cfg = AppConfig.Load();
            bool glass = ThemeManager.IsGlassWorld(cfg.Appearance.World);
            if (TryFindResource("DialogBg") is SolidColorBrush dlgBrush)
            {
                var c = dlgBrush.Color;
                var fb = new SolidColorBrush(Color.FromArgb(glass ? (byte)0xDC : (byte)0xF2, c.R, c.G, c.B));
                fb.Freeze();
                Shell.Background = fb;
                return;
            }
            var fallback = new SolidColorBrush(Color.FromArgb(glass ? (byte)0xDC : (byte)0xF2, 0x15, 0x13, 0x1E));
            fallback.Freeze();
            Shell.Background = fallback;
        }
        catch
        {
            // 读取配置失败时保留 XAML 默认 DialogBg
        }
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try { DragMove(); }
            catch (InvalidOperationException) { }
        }
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
