using System.ComponentModel;
using System.Runtime.InteropServices;
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
    private const ushort SeDaclProtected = 0x1000;
    private const uint ModifyMask = 0x1301BF; // FileSystemRights.Modify with Synchronize
    private const uint GrantAccess = 1;
    private const uint RevokeAccess = 4; // removes the explicit entries of the trustee
    private const uint SubContainersAndObjectsInherit = 3;

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
    /// Gives <paramref name="who"/> change rights (inherited by everything inside), or takes back its explicit entries. The list is
    /// edited where the system keeps it (<c>SetEntriesInAcl</c>), the way the documentation prescribes: the entries inherited from the
    /// parent keep their flags, and nothing is copied, lost or reordered. Whether the list blocks inheritance from the parent
    /// stays as it was, and Windows passes the change on to what is already inside the folder.
    /// </summary>
    public static void ChangeAccess(SafeFileHandle handle, SecurityIdentifier who, bool grant)
    {
        var rc = GetSecurityInfo(handle, SeFileObject, DaclSecurityInformation, out _, out _, out var oldDacl, out _, out var descriptor);
        if (rc != 0)
            throw new Win32Exception((int)rc, "Не удалось прочитать права папки.");
        try
        {
            var wasProtected = GetSecurityDescriptorControl(descriptor, out var control, out _) && (control & SeDaclProtected) != 0;
            var sid = new byte[who.BinaryLength];
            who.GetBinaryForm(sid, 0);
            var pinned = GCHandle.Alloc(sid, GCHandleType.Pinned);
            try
            {
                var entry = new ExplicitAccess
                {
                    Permissions = grant ? ModifyMask : 0,
                    Mode = grant ? GrantAccess : RevokeAccess,
                    Inheritance = SubContainersAndObjectsInherit,
                    Trustee = new Trustee { Form = 0 /* the name field is a SID */, Type = 0 /* unknown kind */, Name = pinned.AddrOfPinnedObject() },
                };
                rc = SetEntriesInAclW(1, ref entry, oldDacl, out var newDacl);
                if (rc != 0)
                    throw new Win32Exception((int)rc, "Не удалось подготовить новый список прав.");
                try
                {
                    rc = SetSecurityInfo(handle, SeFileObject, DaclSecurityInformation | (wasProtected ? ProtectedDacl : UnprotectedDacl), IntPtr.Zero, IntPtr.Zero, newDacl, IntPtr.Zero);
                    if (rc != 0)
                        throw new Win32Exception((int)rc, "Не удалось изменить права папки.");
                }
                finally
                {
                    LocalFree(newDacl);
                }
            }
            finally
            {
                pinned.Free();
            }
        }
        finally
        {
            LocalFree(descriptor);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Trustee
    {
        public IntPtr MultipleTrustee;
        public int MultipleTrusteeOperation;
        public int Form;
        public int Type;
        public IntPtr Name;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExplicitAccess
    {
        public uint Permissions;
        public uint Mode;
        public uint Inheritance;
        public Trustee Trustee;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder path, uint size, uint flags);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    [DllImport("advapi32.dll")]
    private static extern uint GetSecurityInfo(SafeFileHandle handle, int objectType, uint information, out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);

    [DllImport("advapi32.dll")]
    private static extern uint SetEntriesInAclW(uint count, ref ExplicitAccess entries, IntPtr oldAcl, out IntPtr newAcl);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSecurityDescriptorControl(IntPtr descriptor, out ushort control, out uint revision);

    [DllImport("advapi32.dll")]
    private static extern uint SetSecurityInfo(SafeFileHandle handle, int objectType, uint information, IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl);
}
