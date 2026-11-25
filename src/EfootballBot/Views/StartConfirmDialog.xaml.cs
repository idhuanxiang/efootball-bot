using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace EfootballBot.Views;

/// <summary>
/// 开始挂机前的两件事确认（软件本体主题风格，替代原生 MessageBox）。
/// 2026-11-25 用户要求：第一次阅读时确定键需 5 秒倒计时才可点（确保读完），
/// 之后不再倒计时、直接确认。是否已阅读用 data/start_confirm_read.flag 记录（本地、无隐私数据）。
/// </summary>
public partial class StartConfirmDialog : Window
{
    private readonly DispatcherTimer _countdown = new() { Interval = TimeSpan.FromSeconds(1) };
    private int _secondsLeft;

    public StartConfirmDialog()
    {
        InitializeComponent();
        // 取消或点标题栏 X 关闭时也要停表，避免 timer 持有已关闭 Window 继续 tick 5 秒
        Closed += (_, _) => _countdown.Stop();
        if (AlreadyRead())
        {
            _countdown.Stop();
            return; // 已读过：确定键直接可用，无倒计时、无提示
        }
        // 首次：确定键 5s 倒计时，期间禁用并写明原因
        _secondsLeft = 5;
        OkBtn.IsEnabled = false;
        OkBtn.Content = "确定 (5s)";
        CountdownHint.Visibility = Visibility.Visible;
        CountdownHint.Text = "请阅读上述两点，5 秒后可确认（仅首次）";
        _countdown.Tick += (_, _) =>
        {
            _secondsLeft--;
            if (_secondsLeft > 0)
            {
                OkBtn.Content = $"确定 ({_secondsLeft}s)";
            }
            else
            {
                _countdown.Stop();
                OkBtn.IsEnabled = true;
                OkBtn.Content = "确定";
                CountdownHint.Text = "已可确认";
            }
        };
        _countdown.Start();
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        MarkRead();
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    // ★用 AppPaths.DataDir 而非 AppContext.BaseDirectory：单文件发布下 BaseDirectory 是
    //   %TEMP%\.net\<hash>\ 临时解压目录，每次启动 hash 可能变 → flag 丢失、倒计时每次都弹；
    //   AppPaths.DataDir 基于 exe 真实目录，且多开 profile 各自隔离（与项目其它数据一致）。
    private static string FlagPath => Path.Combine(
        EfootballBot.Core.System.AppPaths.DataDir, "start_confirm_read.flag");

    private static bool AlreadyRead()
    {
        try { return File.Exists(FlagPath); }
        catch { return false; }
    }

    private static void MarkRead()
    {
        try
        {
            var dir = Path.GetDirectoryName(FlagPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(FlagPath, DateTime.Now.ToString("o"));
        }
        catch { /* 标记写失败只影响下次是否再倒计时，不阻断挂机 */ }
    }
}
