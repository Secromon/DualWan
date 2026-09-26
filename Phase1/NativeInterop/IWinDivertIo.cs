using System.Runtime.InteropServices;

namespace NetBinder.Service.NativeInterop;

// Injectable native boundary permits lifecycle tests without opening a real
// WinDivert handle or touching the host's network.
public interface IWinDivertIo
{
    IntPtr Open(string filter);
    bool Receive(IntPtr handle, IntPtr packet, uint length, out uint received, ref WINDIVERT_ADDRESS address);
    bool Send(IntPtr handle, IntPtr packet, uint length, out uint sent, ref WINDIVERT_ADDRESS address);
    bool Close(IntPtr handle);
    int LastError { get; }
}

internal sealed class NativeWinDivertIo : IWinDivertIo
{
    public IntPtr Open(string filter) => WinDivertNative.WinDivertOpen(filter,
        WinDivertNative.WINDIVERT_LAYER_NETWORK, 0, 0);
    public bool Receive(IntPtr handle, IntPtr packet, uint length, out uint received,
        ref WINDIVERT_ADDRESS address) => WinDivertNative.WinDivertRecv(handle, packet, length, out received, ref address);
    public bool Send(IntPtr handle, IntPtr packet, uint length, out uint sent,
        ref WINDIVERT_ADDRESS address) => WinDivertNative.WinDivertSend(handle, packet, length, out sent, ref address);
    public bool Close(IntPtr handle) => WinDivertNative.WinDivertClose(handle);
    public int LastError => Marshal.GetLastWin32Error();
}
