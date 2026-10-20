using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PilotLauncher.Core.Sharing;

/// <summary>
/// A folder opened once and then used by its handle. The handle names the object itself, so what has been checked is what is
/// changed: a link put in place of a folder between the check and the change cannot redirect the change. The path Windows reports
/// for the handle has every link of the way resolved, which is what the checks look at.
/// </summary>
internal static class NativeFolder
{
    private const uint ReadControl = 0x00020000;
    private const uint WriteDac = 0x00040000;
    private const uint FileReadAttributes = 0x0080;
    private const uint ShareAll = 0x7; // read | write | delete: opening must not block anybody
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x02000000; // needed to open a directory
    private const uint DaclSecurityInformation = 0x4;
    private const uint ProtectedDacl = 0x80000000;
    private const uint UnprotectedDacl = 0x20000000;
    private const int SeFileObject = 1;

    /// <param name="forChange">Also asks for the right to change the access list.</param>
    public static SafeFileHandle Open(string path, bool forChange)
    {
        var handle = CreateFileW(path, ReadControl | FileReadAttributes | (forChange ? WriteDac : 0), ShareAll, IntPtr.Zero, OpenExisting, BackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, $"Не удалось открыть папку {path}.");
        }

        return handle;
    }

    /// <summary>The real path of the opened object: no link, no short name, no substituted drive is left in it.</summary>
    public static string FinalPath(SafeFileHandle handle)
    {
        var buffer = new StringBuilder(1024);
        var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось определить настоящий путь папки.");
        if (length >= buffer.Capacity)
        {
            buffer = new StringBuilder((int)length + 1);
            length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
            if (length == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось определить настоящий путь папки.");
        }

        var path = buffer.ToString();
        if (path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal))
            return @"\\" + path[@"\\?\UNC\".Length..];
        return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[@"\\?\".Length..] : path;
    }

    /// <summary>
    /// Changes the access list of the opened folder and lets Windows pass the change on to what is inside it. Whether the list
    /// blocks inheritance from the parent stays as it was.
    /// </summary>
    public static void ChangeAccess(SafeFileHandle handle, Action<DirectorySecurity> change)
    {
        var current = ReadDacl(handle);
        var security = new DirectorySecurity();
        security.SetSecurityDescriptorBinaryForm(current, AccessControlSections.Access);
        var wasProtected = security.AreAccessRulesProtected;
        change(security);

        var descriptor = security.GetSecurityDescriptorBinaryForm();
        var pinned = GCHandle.Alloc(descriptor, GCHandleType.Pinned);
        try
        {
            if (!GetSecurityDescriptorDacl(pinned.AddrOfPinnedObject(), out var present, out var dacl, out _) || !present)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось подготовить новый список прав.");
            var rc = SetSecurityInfo(handle, SeFileObject, DaclSecurityInformation | (wasProtected ? ProtectedDacl : UnprotectedDacl), IntPtr.Zero, IntPtr.Zero, dacl, IntPtr.Zero);
            if (rc != 0)
                throw new Win32Exception((int)rc, "Не удалось изменить права папки.");
        }
        finally
        {
            pinned.Free();
        }
    }

    private static byte[] ReadDacl(SafeFileHandle handle)
    {
        GetKernelObjectSecurity(handle, DaclSecurityInformation, null, 0, out var needed);
        var buffer = new byte[needed];
        if (!GetKernelObjectSecurity(handle, DaclSecurityInformation, buffer, needed, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось прочитать права папки.");
        return buffer;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder path, uint size, uint flags);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetKernelObjectSecurity(SafeFileHandle handle, uint information, byte[]? descriptor, uint length, out uint needed);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSecurityDescriptorDacl(IntPtr descriptor, [MarshalAs(UnmanagedType.Bool)] out bool present, out IntPtr dacl, [MarshalAs(UnmanagedType.Bool)] out bool defaulted);

    [DllImport("advapi32.dll")]
    private static extern uint SetSecurityInfo(SafeFileHandle handle, int objectType, uint information, IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl);
}
