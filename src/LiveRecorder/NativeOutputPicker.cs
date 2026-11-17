using System.Runtime.InteropServices;

namespace LiveRecorder;

/// <summary>提供可指定初始目录的 Windows Shell 文件与目录选择器。</summary>
internal static class NativeOutputPicker
{
    private const int Cancelled = unchecked((int)0x800704C7);
    private const uint PickFolders = 0x20;
    private const uint ForceFileSystem = 0x40;
    private const uint PathMustExist = 0x800;
    private const uint FileMustExist = 0x1000;
    private const uint FileSystemPath = 0x80058000;

    /// <summary>打开原生选择器，返回用户选择的文件系统路径。</summary>
    /// <param name="owner">拥有选择器的主窗口句柄。</param>
    /// <param name="initialDirectory">初始目录。缺失时会尝试创建。</param>
    /// <param name="title">选择器标题。</param>
    /// <param name="pickFolder">选择目录时为 <see langword="true" />，选择文件时为 <see langword="false" />。</param>
    /// <returns>所选项的文件系统路径。用户取消时返回 <see langword="null" />。</returns>
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
            // 用户关闭选择器属于正常返回，其余失败由调用方显示。
            if (result == Cancelled)
                return null;
            Marshal.ThrowExceptionForHR(result);
            dialog.GetResult(out IShellItem selected);
            try
            {
                // 取文件系统路径供媒体工具使用，并释放 Shell 分配的字符串。
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
