using System.Runtime.InteropServices;

namespace CNM.UI;

/// <summary>
/// 系统「选择文件夹」窗口：用 Explorer 那套现代版 IFileOpenDialog（左侧导航栏、地址栏都能用）。
/// 万一这台机器上 COM 不给面子，退回 WinForms 的老式树状框 —— 宁可难看，也不能点了按钮没反应。
/// 用户取消 → 返回 null。
/// </summary>
internal static class FolderPicker
{
    /// <summary>最近一次没走通的地方（自检用：CNM.exe --pick-folder 会把它打出来）</summary>
    public static string LastNote { get; private set; } = "";

    public static string Pick(IWin32Window owner, string title = "选择文件夹", string initial = "")
    {
        LastNote = "";
        try
        {
            return Modern(owner, title, initial);
        }
        catch (Exception ex)
        {
            LastNote = "现代版失败，退回老式框：" + ex.GetType().Name + " " + ex.Message;
            return Legacy(owner, title, initial);
        }
    }

    // ---------- 现代版 ----------

    private const uint FOS_PICKFOLDERS = 0x00000020;
    private const uint FOS_FORCEFILESYSTEM = 0x00000040;
    private const uint SIGDN_FILESYSPATH = 0x80058000;

    /// <summary>把「起始路径」收拾成一个存在的目录；给文件就取它的所在目录</summary>
    private static string Normalize(string p)
    {
        var s = (p ?? "").Trim().Trim('"');
        if (s.Length == 0) return null;
        try
        {
            if (File.Exists(s)) s = Path.GetDirectoryName(s) ?? s;
            if (Directory.Exists(s)) return s;
            var parent = Path.GetDirectoryName(s);
            if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent)) return parent;   // 手打了一半的路径也尽量靠近
        }
        catch { }
        return null;
    }

    private static string Modern(IWin32Window owner, string title, string initial)
    {
        var dlg = (IFileOpenDialog)new FileOpenDialogRCW();   // 拿不到就抛 → 外面退回老框
        try
        {
            dlg.GetOptions(out var opt);
            dlg.SetOptions(opt | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM);
            if (!string.IsNullOrWhiteSpace(title)) dlg.SetTitle(title);

            var dir = Normalize(initial);
            if (dir != null)
            {
                // 首选：SetFolder（把 shell 项设为默认目录）
                var done = false;
                try
                {
                    var iid = typeof(IShellItem).GUID;
                    SHCreateItemFromParsingName(dir, IntPtr.Zero, ref iid, out var item);
                    if (item != null)
                    {
                        dlg.SetFolder(item);
                        Marshal.ReleaseComObject(item);
                        done = true;
                    }
                }
                catch (Exception ex) { LastNote = "SetFolder 没成功：" + ex.Message; }

                // 备选：文件夹选择框里直接填目录路径 —— shell 自己那个「选择文件夹」就是这个行为
                if (!done)
                {
                    try
                    {
                        dlg.SetFileName(dir);
                        if (LastNote.Length == 0) LastNote = "用 SetFileName 定的起始目录";
                    }
                    catch (Exception ex) { LastNote += " SetFileName 也失败：" + ex.Message; }
                }
            }

            int hr = dlg.Show(owner != null ? owner.Handle : IntPtr.Zero);
            if (hr != 0) return null;      // 取消（0x800704C7）或出错

            dlg.GetResult(out var picked);
            try
            {
                picked.GetDisplayName(SIGDN_FILESYSPATH, out var path);
                return string.IsNullOrWhiteSpace(path) ? null : path;
            }
            finally { if (picked != null) Marshal.ReleaseComObject(picked); }
        }
        finally { Marshal.ReleaseComObject(dlg); }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        string pszPath, IntPtr pbc, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);

    [ComImport, Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")]
    private class FileOpenDialogRCW { }

    [ComImport, Guid("42F85136-DB7E-439C-85F1-E4075D135FC8"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileDialog
    {
        [PreserveSig] int Show(IntPtr parent);
        void SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);
        void SetFileTypeIndex(uint iFileType);
        void GetFileTypeIndex(out uint piFileType);
        void Advise(IntPtr pfde, out uint pdwCookie);
        void Unadvise(uint dwCookie);
        void SetOptions(uint fos);
        void GetOptions(out uint pfos);
        void SetDefaultFolder(IShellItem psi);
        void SetFolder(IShellItem psi);
        void GetFolder(out IShellItem ppsi);
        void GetCurrentSelection(out IShellItem ppsi);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
        void GetResult(out IShellItem ppsi);
        void AddPlace(IShellItem psi, int fdap);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
        void Close(int hr);
        void SetClientGuid(ref Guid guid);
        void ClearClientData();
        void SetFilter(IntPtr pFilter);
    }

    [ComImport, Guid("D57C7288-D4AD-4768-BE02-9D969532D960"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOpenDialog : IFileDialog
    {
        void GetResults(out IntPtr ppenum);
        void GetSelectedItems(out IntPtr ppss);
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int piOrder);
    }

    // ---------- 老式兜底 ----------

    private static string Legacy(IWin32Window owner, string title, string initial)
    {
        try
        {
            using var fbd = new FolderBrowserDialog { Description = title, ShowNewFolderButton = true };
            var dir = Normalize(initial);
            if (dir != null) fbd.SelectedPath = dir;
            return fbd.ShowDialog(owner) == DialogResult.OK ? fbd.SelectedPath : null;
        }
        catch { return null; }
    }
}
