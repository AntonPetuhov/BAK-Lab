using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace InternalIntegration;

// A rename preserves the NTFS file identity. An unrelated file with identical bytes
// must still be treated as a conflict, not as our interrupted publication.
internal static class FileIdentity
{
    public static string Read(string path)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Требуется Windows и файловая система с устойчивыми идентификаторами файлов.");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (!GetFileInformationByHandle(stream.SafeFileHandle, out var info))
            throw new IOException("Не удалось получить идентификатор файла", new Win32Exception(Marshal.GetLastWin32Error()));
        return $"{info.VolumeSerialNumber:X8}:{info.FileIndexHigh:X8}{info.FileIndexLow:X8}";
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out HandleInformation information);

    [StructLayout(LayoutKind.Sequential)]
    private struct HandleInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
}
