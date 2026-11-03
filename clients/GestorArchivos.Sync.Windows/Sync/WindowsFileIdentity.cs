using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
namespace GestorArchivos.Sync.Windows.Sync;
internal static class WindowsFileIdentity
{
    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInfo
    {
        public ulong VolumeSerialNumber;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] FileId;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int kind, out FileIdInfo info, uint size);
    internal static string? TryGet(string path, bool directory)
    {
        if (!OperatingSystem.IsWindows()) return null;
        using var handle = CreateFileW(path, 0x80, 7, IntPtr.Zero, 3, directory ? 0x02000000u : 0u, IntPtr.Zero);
        if (handle.IsInvalid || !GetFileInformationByHandleEx(handle, 18, out var info, 24) || info.FileId is null) return null;
        return info.VolumeSerialNumber.ToString("X16") + ":" + Convert.ToHexString(info.FileId);
    }
}
