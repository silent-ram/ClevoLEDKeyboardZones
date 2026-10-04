using System.IO;
using System.Windows;
using ColorfulLedKeyboard.Core;

namespace ColorfulLedKeyboard.Simulator;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Any(arg => string.Equals(arg, "--probe-features", StringComparison.OrdinalIgnoreCase)))
        {
            RunProbe();
            Shutdown();
            return;
        }

        var window = new MainWindow(
            startInThreeZone: e.Args.Any(arg => string.Equals(arg, "--view-zones", StringComparison.OrdinalIgnoreCase)),
            autostart: e.Args.Any(arg => string.Equals(arg, "--autostart", StringComparison.OrdinalIgnoreCase)),
            forceLightbar: e.Args.Any(arg => string.Equals(arg, "--force-lightbar", StringComparison.OrdinalIgnoreCase)));
        MainWindow = window;
        window.Show();
    }

    /// <summary>
    /// --probe-features：调用 GET_BIOS_FEATURES_1(0x52) 并以弹窗 + 日志文件显示 16 进制结果后退出。
    /// 纯读操作，零 EC 写入（探测失败同样只读不写）。
    /// </summary>
    private static void RunProbe()
    {
        string detail;
        try
        {
            var device = new DchuKeyboardDevice(); // 真实 P/Invoke 传输
            var features = device.GetKeyboardFeatures();
            var zoneBitSet = (features & 0x00400000) != 0;
            var monoBitSet = (features & 0x40000000) != 0;
            detail =
                $"GET_BIOS_FEATURES_1 (0x52) = 0x{features:X8}" + Environment.NewLine +
                $"三区 RGB 位 (0x00400000)：{zoneBitSet}（{(zoneBitSet ? "置位" : "未置位")}）" + Environment.NewLine +
                $"白色单色位 (0x40000000)：{monoBitSet}（{(monoBitSet ? "置位" : "未置位")}）" + Environment.NewLine +
                $"判定：{(device.Has3ZoneKeyboard ? "三区 RGB 键盘" : "非三区（运行时按单区路径，分区命令被门控拒绝）")}";
        }
        catch (Exception ex)
        {
            detail =
                $"探测失败：{ex.GetType().Name}: {ex.Message}" + Environment.NewLine +
                "按非三区处理（安全默认）。" + Environment.NewLine +
                "提示：输出目录需存在 InsydeDCHU.dll（安装过本软件的机器会自动复制）。";
        }

        detail += Environment.NewLine + Environment.NewLine + "本次探测为读操作，零 EC 写入。";
        var logPath = Path.Combine(Path.GetTempPath(), "ClevoLEDKeyboardZones-probe.txt");
        try { File.WriteAllText(logPath, detail); } catch { /* 日志失败不影响弹窗 */ }
        MessageBox.Show(detail, "能力探测（--probe-features）", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
