using System.Runtime.InteropServices;

namespace LiveRecorder;

internal static class NativeOutputPicker
{
    private const int Cancelled = unchecked((int)0x800704C7);
    private const uint PickFolders = 0x20;
    private const uint ForceFileSystem = 0x40;
    private const uint PathMustExist = 0x800;
    private const uint FileMustExist = 0x1000;
    private const uint FileSystemPath = 0x80058000;

    public static string? Pick(nint owner, string initialDirectory, string title, bool pickFolder)
    {
        string path = Directory.CreateDirectory(initialDirectory).FullName;
        var dialog = (IFileDialog)new FileOpenDialog();
        try
        {
            dialog.GetOptions(out uint options);
            dialog.SetOptions(options | ForceFileSystem | PathMustExist | (pickFolder ? PickFolders : FileMustExist));
            dialog.SetTitle(title);
            Guid itemId = typeof(IShellItem).GUID;
            Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(path, nint.Zero, in itemId, out IShellItem folder));
            try { dialog.SetFolder(folder); }
            finally { Marshal.ReleaseComObject(folder); }

            int result = dialog.Show(owner);
            if (result == Cancelled)
                return null;
            Marshal.ThrowExceptionForHR(result);
            dialog.GetResult(out IShellItem selected);
            try
            {
                selected.GetDisplayName(FileSystemPath, out nint name);
                try { return Marshal.PtrToStringUni(name); }
                finally { Marshal.FreeCoTaskMem(name); }
            }
            finally { Marshal.ReleaseComObject(selected); }
        }
        finally { Marshal.ReleaseComObject(dialog); }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHCreateItemFromParsingName(string path, nint bindContext, in Guid interfaceId, out IShellItem item);

    [ComImport, Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")]
    private class FileOpenDialog { }

    [ComImport, Guid("42F85136-DB7E-439C-85F1-E4075D135FC8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileDialog
    {
        // Keep unused methods in interface order for the COM vtable.
        [PreserveSig] int Show(nint owner);
        void SetFileTypes(uint count, nint filters);
        void SetFileTypeIndex(uint index);
        void GetFileTypeIndex(out uint index);
        void Advise(nint events, out uint cookie);
        void Unadvise(uint cookie);
        void SetOptions(uint options);
        void GetOptions(out uint options);
        void SetDefaultFolder(IShellItem folder);
        void SetFolder(IShellItem folder);
        void GetFolder(out IShellItem folder);
        void GetCurrentSelection(out IShellItem item);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetFileName(out nint name);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
        void GetResult(out IShellItem item);
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(nint bindContext, in Guid handlerId, in Guid interfaceId, out nint handler);
        void GetParent(out IShellItem parent);
        void GetDisplayName(uint format, out nint name);
    }
}
