using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace ColorfulLedKeyboard.Simulator;

/// <summary>
/// 音乐模式（绑定程序）的音频探测：枚举当前有播放会话的进程、读取会话峰值电平与
/// 系统主音量（WASAPI，模拟器与真实托盘同样运行在交互用户会话中，可以看到用户播放器）。
///
/// <para>托盘工程 AudioSessionMonitor 的极简版：无 OnSessionCreated 订阅、无播放闩锁、
/// 无定期重枚举——枚举只发生在打开音乐模式与点击"刷新"时；日常读取复用缓存的
/// AudioMeterInformation 句柄（与 AudioSessionMonitor 相同的省泄漏做法）。会话失效
/// （进程退出/会话结束）的读取返回 -1，由调用方剔除绑定。</para>
/// </summary>
internal sealed class AudioProgramProbe : IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly List<Entry> _entries = [];
    private readonly List<MMDevice> _devices = [];
    private MMDevice? _defaultDevice;
    private bool _disposed;

    public sealed class Entry(int processId, string processName, AudioSessionControl control)
    {
        private AudioMeterInformation? _meter;

        public int ProcessId { get; } = processId;
        public string ProcessName { get; } = processName;

        /// <summary>当前峰值电平 0..1；会话已失效返回 -1。</summary>
        public float ReadPeak()
        {
            try
            {
                _meter ??= control.AudioMeterInformation;
                return _meter.MasterPeakValue;
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException)
            {
                return -1f;
            }
        }

        public override string ToString() => $"{ProcessName} ({ProcessId})";
    }

    /// <summary>上次 Refresh() 得到的会话清单（只读视图）。</summary>
    public IReadOnlyList<Entry> Entries => _entries;

    /// <summary>全量重枚举所有渲染设备的活跃会话。旧清单整体废弃（含设备释放）。</summary>
    public void Refresh()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ReleaseSessions();
        try
        {
            var devices = _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            foreach (var device in devices)
            {
                try
                {
                    var manager = device.AudioSessionManager;
                    manager.RefreshSessions();
                    var sessions = manager.Sessions;
                    for (var index = 0; index < sessions.Count; index++)
                    {
                        var session = sessions[index];
                        var pid = unchecked((int)session.GetProcessID);
                        if (pid <= 0)
                        {
                            session.Dispose();
                            continue;
                        }

                        string name;
                        try
                        {
                            using var process = Process.GetProcessById(pid);
                            name = process.ProcessName;
                        }
                        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
                        {
                            name = "pid " + pid; // 进程已退出但会话尚未回收
                        }

                        _entries.Add(new Entry(pid, name, session));
                    }

                    // 设备与会话对象同生命周期：持有设备以保持会话/电平句柄有效，下次 Refresh 释放
                    _devices.Add(device);
                }
                catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or COMException)
                {
                    device.Dispose();
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or COMException)
        {
        }
    }

    /// <summary>绑定进程的峰值电平；未枚举到/会话失效返回 -1。</summary>
    public float ReadPeak(int processId)
    {
        foreach (var entry in _entries)
        {
            if (entry.ProcessId == processId)
            {
                return entry.ReadPeak();
            }
        }

        return -1f;
    }

    /// <summary>系统主音量 0..1（FollowSystemVolume 用）；读取失败按 1（不影响电平）。</summary>
    public double GetMasterVolumeScalar()
    {
        try
        {
            _defaultDevice ??= _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return _defaultDevice.AudioEndpointVolume.MasterVolumeLevelScalar;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException)
        {
            return 1d;
        }
    }

    private void ReleaseSessions()
    {
        _entries.Clear();
        foreach (var device in _devices)
        {
            try { device.Dispose(); } catch { }
        }
        _devices.Clear();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ReleaseSessions();
        try { _defaultDevice?.Dispose(); } catch { }
        _defaultDevice = null;
    }
}
