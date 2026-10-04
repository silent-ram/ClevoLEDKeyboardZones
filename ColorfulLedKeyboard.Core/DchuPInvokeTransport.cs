using System.Runtime.InteropServices;

namespace ColorfulLedKeyboard.Core;

/// <summary>
/// 生产传输实现：直通 InsydeDCHU.dll，字节序与调用方式与旧版
/// <c>DchuKeyboardDevice</c> 内嵌 P/Invoke 完全一致（BitConverter.GetBytes 为 int32 小端）。
/// </summary>
public sealed class DchuPInvokeTransport : IDchuTransport
{
    [DllImport("InsydeDCHU.dll")]
    private static extern int SetDCHU_Data(int command, byte[] buffer, int length);

    [DllImport("InsydeDCHU.dll")]
    private static extern int GetDCHU_Data_Integer(int command);

    public void SetData(int command, int args)
    {
        var payload = BitConverter.GetBytes(args);
        SetDCHU_Data(command, payload, 4);
    }

    public int GetInteger(int command) => GetDCHU_Data_Integer(command);
}
