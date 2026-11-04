// WeeGleek - a tiny, 100% offline image browser + viewer for Windows 11.
//
// Uses ONLY the .NET Framework that ships with Windows (System, System.Drawing,
// System.Windows.Forms, Microsoft.VisualBasic for Recycle Bin deletes). There is no networking
// code in this file: no sockets, no HTTP, no telemetry, no licensing, no cloud APIs.
// Cloud "placeholder" files (OneDrive etc. that are not stored on this PC) are never opened,
// so browsing can't trigger a download.
//
// BROWSE MODE (folder tree | thumbnails)
//   Click folder in the tree          show its images (lazy-loaded, scanned on background threads)
//   Double-click / Enter              open the image in the viewer
//   Arrows, Home/End, PgUp/PgDn       move;  Shift / Ctrl + click or arrows: multi-select;  Ctrl+A all
//   Mouse wheel / Ctrl + wheel        scroll / change thumbnail size
//   Right-click, or F2, Ctrl+C, Ctrl+V, Del, F5   Rename, Copy, Paste, Delete (Recycle Bin), Refresh
//
// VIEW MODE (full canvas)
//   Left / Right, PgUp / PgDn, Space / Backspace, Mouse wheel   previous / next image (wraps)
//   Home / End                        first / last image in the folder
//   Ctrl + Mouse wheel, + / -         zoom (around the cursor / window centre)
//   Click and drag                    pan            Double-click   toggle fit <-> 100%
//   F or 0 = fit,  1 = actual size    R / L (Shift+R)  rotate clockwise / counter-clockwise
//   I                                 show / hide the info bar
//   Esc                               back to the browser (or leave fullscreen)
//
// Anywhere: F11 = fullscreen, Ctrl+O = open file, or drag & drop a file / folder onto the window.
//
// Written in C# 5 so it builds with the compiler included in Windows (see build.bat).

using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using VBFS = Microsoft.VisualBasic.FileIO.FileSystem;
using VBUI = Microsoft.VisualBasic.FileIO.UIOption;
using VBRecycle = Microsoft.VisualBasic.FileIO.RecycleOption;
using WinTimer = System.Windows.Forms.Timer;

// ====================================================================== helpers

static class Util
{
    public static readonly HashSet<string> Exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".bmp", ".dib", ".gif", ".tif", ".tiff" };

    // OFFLINE | RECALL_ON_OPEN | RECALL_ON_DATA_ACCESS: file content lives in the cloud, not on this PC.
    public const FileAttributes CloudMask = (FileAttributes)0x441000;

    public static float K = 1f;   // DPI scale factor

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)] static extern int StrCmpLogicalW(string a, string b);
    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] static extern int SetWindowTheme(IntPtr hwnd, string app, string ids);

    public static int NatCmp(string a, string b) { return StrCmpLogicalW(a, b); }   // Explorer-style order
    public static int S(int v) { return (int)Math.Round(v * K); }

    public static void Dark(Control c)
    {
        EventHandler apply = delegate { try { SetWindowTheme(c.Handle, "DarkMode_Explorer", null); } catch (Exception) { } };
        c.HandleCreated += apply;
        if (c.IsHandleCreated) apply(c, EventArgs.Empty);
    }

    // Run something on the UI thread from a worker; false if the window is gone.
    public static bool Post(Control host, MethodInvoker m)
    {
        try
        {
            if (host != null && host.IsHandleCreated && !host.IsDisposed) { host.BeginInvoke(m); return true; }
        }
        catch (Exception) { }
        return false;
    }

    public static string FmtSize(long b)
    {
        if (b < 1024) return b + " B";
        if (b < 1048576) return (b / 1024.0).ToString("0.#") + " KB";
        if (b < 1073741824) return (b / 1048576.0).ToString("0.##") + " MB";
        return (b / 1073741824.0).ToString("0.##") + " GB";
    }

    public static bool SameDir(string a, string b)
    {
        return a != null && b != null &&
               string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
    }

    public static byte[] ReadShared(string path)
    {
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16))
        {
            byte[] b = new byte[(int)fs.Length];
            int off = 0;
            while (off < b.Length)
            {
                int n = fs.Read(b, off, b.Length - off);
                if (n <= 0) break;
                off += n;
            }
            return b;
        }
    }

    // EXIF orientation -> quarter turns clockwise
    public static int ReadRotation(Image img)
    {
        try
        {
            if (Array.IndexOf(img.PropertyIdList, 0x0112) < 0) return 0;
            byte[] v = img.GetPropertyItem(0x0112).Value;
            switch (v[0]) { case 3: return 2; case 6: return 1; case 8: return 3; }
        }
        catch (Exception) { }
        return 0;
    }

    public static string UniqueName(string dir, string name, bool isDir)
    {
        string stem = isDir ? name : Path.GetFileNameWithoutExtension(name);
        string ext = isDir ? "" : Path.GetExtension(name);
        string p = Path.Combine(dir, name);
        for (int i = 1; File.Exists(p) || Directory.Exists(p); i++)
            p = Path.Combine(dir, stem + " - Copy" + (i > 1 ? " (" + i + ")" : "") + ext);
        return p;
    }

    public static void CopyDir(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (string f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(dst, Path.GetFileName(f)));
        foreach (string d in Directory.GetDirectories(src))
        {
            if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0) continue;   // never follow links/junctions
            CopyDir(d, Path.Combine(dst, Path.GetFileName(d)));
        }
    }
}

// ====================================================================== viewer data

sealed class Pic : IDisposable
{
    public string FullPath;
    public Image Img;
    public MemoryStream Ms;      // only kept for lazily-decoded (huge / animated) images
    public int W, H, Rot;        // Rot = quarter turns needed to honour EXIF orientation
    public long Bytes;
    public bool Anim;
    public string Err;

    public void Dispose()
    {
        if (Img != null) Img.Dispose();
        if (Ms != null) Ms.Dispose();
        Img = null; Ms = null;
    }
}

// ====================================================================== thumbnails

sealed class Item
{
    public string Path, Name;
    public long Size, Stamp;
    public bool Offline, Sel;
}

sealed class ThumbJob { public string Path; public int Size; public long Stamp; }

// LRU cache of thumbnails, bounded by memory. UI thread only.
sealed class ThumbCache
{
    sealed class E { public Bitmap B; public long Stamp; public int Size; public LinkedListNode<string> Node; }

    const long Budget = 160L * 1024 * 1024;
    readonly Dictionary<string, E> map = new Dictionary<string, E>(StringComparer.OrdinalIgnoreCase);
    readonly LinkedList<string> lru = new LinkedList<string>();
    long bytes;

    public bool Has(string path, long stamp, int size)
    {
        E e;
        return map.TryGetValue(path, out e) && e.Stamp == stamp && e.Size == size;
    }

    public Bitmap Get(string path, long stamp, int size)
    {
        E e;
        if (!map.TryGetValue(path, out e) || e.Stamp != stamp || e.Size != size) return null;
        lru.Remove(e.Node); lru.AddFirst(e.Node);
        return e.B;
    }

    public void Put(string path, long stamp, int size, Bitmap b)
    {
        E e;
        if (map.TryGetValue(path, out e)) Remove(path, e);
        e = new E { B = b, Stamp = stamp, Size = size };
        e.Node = lru.AddFirst(path);
        map[path] = e;
        bytes += (long)b.Width * b.Height * 4;
        while (bytes > Budget && lru.Count > 1) { string k = lru.Last.Value; Remove(k, map[k]); }
    }

    void Remove(string k, E e)
    {
        lru.Remove(e.Node); map.Remove(k);
        bytes -= (long)e.B.Width * e.B.Height * 4;
        e.B.Dispose();
    }

    public void Clear()
    {
        foreach (E e in map.Values) e.B.Dispose();
        map.Clear(); lru.Clear(); bytes = 0;
    }
}

// A few background threads that build thumbnails. The newest request list always wins,
// so whatever is on screen is decoded first and stale work is dropped.
sealed class ThumbService
{
    readonly object sync = new object();
    Queue<ThumbJob> queue = new Queue<ThumbJob>();
    readonly HashSet<string> running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    readonly Action<ThumbJob, Bitmap> done;

    public ThumbService(int threads, Action<ThumbJob, Bitmap> done)
    {
        this.done = done;
        for (int i = 0; i < threads; i++)
        {
            var t = new Thread(Run) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "thumbs" };
            t.Start();
        }
    }

    public void Replace(List<ThumbJob> jobs)
    {
        lock (sync)
        {
            queue = new Queue<ThumbJob>();
            foreach (ThumbJob j in jobs) if (!running.Contains(j.Path)) queue.Enqueue(j);
            Monitor.PulseAll(sync);
        }
    }

    void Run()
    {
        while (true)
        {
            ThumbJob j;
            lock (sync)
            {
                while (queue.Count == 0) Monitor.Wait(sync);
                j = queue.Dequeue();
                running.Add(j.Path);
            }
            Bitmap b = null;
            try { b = Make(j.Path, j.Size); } catch (Exception) { }
            lock (sync) running.Remove(j.Path);
            try { done(j, b); } catch (Exception) { if (b != null) b.Dispose(); }
        }
    }

    static Bitmap Make(string path, int size)
    {
        if ((File.GetAttributes(path) & Util.CloudMask) != 0) return null;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16))
        using (Image img = Image.FromStream(fs, false, false))
        {
            int w = img.Width, h = img.Height, rot = Util.ReadRotation(img);
            double s = Math.Min(1.0, (double)size / Math.Max(w, h));
            int tw = Math.Max(1, (int)Math.Round(w * s)), th = Math.Max(1, (int)Math.Round(h * s));
            bool swap = (rot & 1) == 1;
            var bmp = new Bitmap(swap ? th : tw, swap ? tw : th, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            using (var ia = new ImageAttributes())
            {
                ia.SetWrapMode(WrapMode.TileFlipXY);
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.CompositingMode = CompositingMode.SourceCopy;
                g.TranslateTransform(bmp.Width / 2f, bmp.Height / 2f);
                g.RotateTransform(rot * 90f);
                g.TranslateTransform(-tw / 2f, -th / 2f);
                g.DrawImage(img, new Rectangle(0, 0, tw, th), 0, 0, w, h, GraphicsUnit.Pixel, ia);
            }
            return bmp;
        }
    }
}

// ====================================================================== small dialog / wheel routing

static class Prompt
{
    public static string Ask(IWin32Window owner, string title, string label, string text, bool selectStem)
    {
        using (var f = new Form())
        {
            f.AutoScaleMode = AutoScaleMode.None;
            f.Font = SystemFonts.MessageBoxFont;
            f.Text = title;
            f.FormBorderStyle = FormBorderStyle.FixedDialog;
            f.StartPosition = FormStartPosition.CenterParent;
            f.MaximizeBox = false; f.MinimizeBox = false; f.ShowInTaskbar = false;
            f.ClientSize = new Size(Util.S(400), Util.S(112));
            var l = new Label { Text = label, Left = Util.S(12), Top = Util.S(12), AutoSize = true };
            var t = new TextBox { Left = Util.S(12), Top = Util.S(36), Width = Util.S(376), Text = text };
            var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = Util.S(222), Top = Util.S(72), Width = Util.S(80), Height = Util.S(28) };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = Util.S(308), Top = Util.S(72), Width = Util.S(80), Height = Util.S(28) };
            f.AcceptButton = ok; f.CancelButton = cancel;
            f.Controls.Add(l); f.Controls.Add(t); f.Controls.Add(ok); f.Controls.Add(cancel);
            f.Shown += delegate
            {
                t.Focus();
                int dot = text.LastIndexOf('.');
                t.Select(0, selectStem && dot > 0 ? dot : text.Length);
            };
            return f.ShowDialog(owner) == DialogResult.OK ? t.Text.Trim() : null;
        }
    }
}

// Sends mouse-wheel input to the control under the cursor instead of the focused one,
// so hovering over the thumbnail grid or tree and scrolling just works.
sealed class WheelRouter : IMessageFilter
{
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(Point p);
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

    readonly Form owner;
    public WheelRouter(Form owner) { this.owner = owner; }

    public bool PreFilterMessage(ref Message m)
    {
        if (m.Msg != 0x020A || Form.ActiveForm != owner) return false;
        int l = unchecked((int)(long)m.LParam);
        IntPtr h = WindowFromPoint(new Point((short)(l & 0xFFFF), (short)((l >> 16) & 0xFFFF)));
        if (h == IntPtr.Zero || h == m.HWnd) return false;
        Control c = Control.FromHandle(h);
        while (c is ScrollBar && c.Parent != null) c = c.Parent;
        if (c == null || c.FindForm() != owner || c.Handle == m.HWnd) return false;
        SendMessage(c.Handle, m.Msg, m.WParam, m.LParam);
        return true;
    }
}

// ====================================================================== full-canvas viewer

sealed class Canvas : Control
{
    static readonly Color Bg = Color.FromArgb(24, 24, 24);
    const long MaxPredecodePixels = 100000000L;

    List<string> files = new List<string>();
    int idx = -1;
    readonly Dictionary<string, Pic> cache = new Dictionary<string, Pic>(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> pending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    string want, notice;
    Pic cur;

    int rot, fitMode;                 // fitMode: 0 = free, 1 = fit (never enlarge), 2 = fit (may enlarge)
    double zoom = 1, px, py;          // px,py = offset of image centre from window centre
    bool fast, showInfo = true, dragging;
    int wheelAcc;
    Point last;

    readonly WinTimer settle = new WinTimer { Interval = 150 };
    readonly ImageAttributes ia = new ImageAttributes();
    readonly Font font = new Font("Segoe UI", 9f);
    readonly Font big = new Font("Segoe UI", 12f);

    public event Action<string, int> ImageChanged;
    public string Want { get { return want; } }

    public Canvas()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = Bg; Dock = DockStyle.Fill; TabStop = true; Visible = false;
        ia.SetWrapMode(WrapMode.TileFlipXY);          // no faded edges when scaling
        settle.Tick += delegate { settle.Stop(); fast = false; Invalidate(); };
    }

    // ---------------------------------------------------------------- public API

    // Show one image right away; the folder list arrives later through SetList.
    public void ShowSingle(string path)
    {
        files = new List<string> { path };
        idx = -1;
        if (cur == null || !string.Equals(cur.FullPath, path, StringComparison.OrdinalIgnoreCase)) Clear();
        GoTo(0);
    }

    public void Open(List<string> list, int index)
    {
        files = list; idx = -1;
        if (index < 0 || index >= list.Count) return;
        if (cur == null || !string.Equals(cur.FullPath, list[index], StringComparison.OrdinalIgnoreCase)) Clear();
        GoTo(index);
    }

    public void SetList(List<string> list, string current)
    {
        files = list;
        int i = current == null ? -1 : list.FindIndex(x => string.Equals(x, current, StringComparison.OrdinalIgnoreCase));
        idx = i;
        if (i >= 0) { Prefetch(); Trim(); }
    }

    public bool HandleKey(Keys keyData)
    {
        Keys k = keyData & Keys.KeyCode;
        bool shift = (keyData & Keys.Shift) != 0;
        switch (k)
        {
            case Keys.Right: case Keys.PageDown: case Keys.Space: Step(1); return true;
            case Keys.Left: case Keys.PageUp: case Keys.Back: Step(-1); return true;
            case Keys.Home: GoTo(0); return true;
            case Keys.End: GoTo(files.Count - 1); return true;
            case Keys.Add: case Keys.Oemplus: ZoomCenter(zoom * 1.25); return true;
            case Keys.Subtract: case Keys.OemMinus: ZoomCenter(zoom / 1.25); return true;
            case Keys.F: case Keys.D0: case Keys.NumPad0: SetFit(2); return true;
            case Keys.D1: case Keys.NumPad1: ZoomCenter(1); return true;
            case Keys.R: Rotate(shift ? -1 : 1); return true;
            case Keys.L: Rotate(-1); return true;
            case Keys.I: showInfo = !showInfo; Invalidate(); return true;
        }
        return false;
    }

    // ---------------------------------------------------------------- navigation / loading

    void Step(int d)
    {
        int n = files.Count;
        if (n > 1) GoTo((((idx < 0 ? 0 : idx) + d) % n + n) % n);
    }

    void GoTo(int i)
    {
        if (i < 0 || i >= files.Count) return;
        idx = i;
        want = files[i];
        Pic p;
        if (cache.TryGetValue(want, out p)) Display(p); else Request(want);
        Prefetch();
    }

    void Prefetch()
    {
        int n = files.Count;
        if (n < 2 || idx < 0) return;
        Request(files[(idx + 1) % n]);
        Request(files[(idx + n - 1) % n]);
    }

    bool IsNear(string p)
    {
        int n = files.Count;
        if (idx < 0 || n == 0) return false;
        for (int d = -1; d <= 1; d++)
            if (string.Equals(files[((idx + d) % n + n) % n], p, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    void Request(string path)
    {
        if (cache.ContainsKey(path) || pending.Contains(path)) return;
        pending.Add(path);
        Control host = FindForm();
        ThreadPool.QueueUserWorkItem(delegate
        {
            Pic p = Decode(path);
            if (!Util.Post(host, delegate { Loaded(p); })) p.Dispose();
        });
    }

    void Loaded(Pic p)
    {
        pending.Remove(p.FullPath);
        if (!IsNear(p.FullPath) || cache.ContainsKey(p.FullPath)) { p.Dispose(); return; }
        cache[p.FullPath] = p;
        if (string.Equals(p.FullPath, want, StringComparison.OrdinalIgnoreCase)) Display(p);
    }

    void StopAnim()
    {
        if (cur != null && cur.Anim && cur.Img != null)
        {
            try { ImageAnimator.StopAnimate(cur.Img, OnFrame); } catch (Exception) { }
        }
    }

    void Clear() { StopAnim(); cur = null; notice = null; Invalidate(); }

    void Display(Pic p)
    {
        StopAnim();
        cur = p;
        notice = p.Err != null ? "Can't open this file\n" + Path.GetFileName(p.FullPath) + "\n" + p.Err : null;
        rot = p.Rot;
        fitMode = 1;
        fast = false;
        if (p.Anim && p.Img != null) ImageAnimator.Animate(p.Img, OnFrame);
        Refit();
        Trim();
        Invalidate();
        var h = ImageChanged;
        if (h != null) h(p.FullPath, idx);
    }

    void OnFrame(object s, EventArgs e)
    {
        try { BeginInvoke(new MethodInvoker(Invalidate)); } catch (Exception) { }
    }

    // keep only the current picture and its two neighbours in memory
    void Trim()
    {
        var drop = new List<string>();
        foreach (KeyValuePair<string, Pic> kv in cache)
            if (kv.Value != cur && !IsNear(kv.Key)) drop.Add(kv.Key);
        foreach (string k in drop) { cache[k].Dispose(); cache.Remove(k); }
    }

    // runs on worker threads
    static Pic Decode(string path)
    {
        var p = new Pic { FullPath = path };
        Image img = null; MemoryStream ms = null;
        try
        {
            if ((File.GetAttributes(path) & Util.CloudMask) != 0)
                throw new IOException("This file is a cloud placeholder that isn't stored on this PC, so it was not opened. WeeGleek never goes online.");
            byte[] bytes = Util.ReadShared(path);             // one fast sequential read, no lock afterwards
            p.Bytes = bytes.Length;
            ms = new MemoryStream(bytes, false);
            img = Image.FromStream(ms, false, false);          // skip colour management + validation = faster
            p.W = img.Width; p.H = img.Height;
            p.Rot = Util.ReadRotation(img);

            bool anim = false;
            try { anim = ImageAnimator.CanAnimate(img); } catch (Exception) { }

            if (!anim && (long)p.W * p.H <= MaxPredecodePixels)
            {
                // Decode now, off the UI thread, into the format GDI+ draws fastest.
                var bmp = new Bitmap(p.W, p.H, PixelFormat.Format32bppPArgb);
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.CompositingMode = CompositingMode.SourceCopy;
                    g.InterpolationMode = InterpolationMode.NearestNeighbor;
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    g.DrawImage(img, new Rectangle(0, 0, p.W, p.H), 0, 0, p.W, p.H, GraphicsUnit.Pixel);
                }
                img.Dispose(); ms.Dispose();
                p.Img = bmp;
            }
            else { p.Img = img; p.Ms = ms; p.Anim = anim; }    // animated GIF or gigantic image: keep lazy
        }
        catch (Exception ex)
        {
            if (img != null) img.Dispose();
            if (ms != null) ms.Dispose();
            p.Img = null; p.Ms = null;
            p.Err = ex is OutOfMemoryException ? "Unsupported, damaged or too large." : ex.Message;
        }
        return p;
    }

    // ---------------------------------------------------------------- view maths

    bool HasImage { get { return cur != null && cur.Img != null; } }
    int RW { get { return !HasImage ? 1 : ((rot & 1) == 0 ? cur.W : cur.H); } }
    int RH { get { return !HasImage ? 1 : ((rot & 1) == 0 ? cur.H : cur.W); } }

    void Refit()
    {
        if (!HasImage) return;
        if (fitMode == 0) { Clamp(); return; }
        double cw = ClientSize.Width, ch = ClientSize.Height;
        if (cw < 1 || ch < 1) return;
        double z = Math.Min(cw / RW, ch / RH);
        if (fitMode == 1 && z > 1) z = 1;
        zoom = z; px = py = 0;
    }

    void Clamp()
    {
        if (!HasImage) return;
        double mx = Math.Max(0, (RW * zoom - ClientSize.Width) / 2);
        double my = Math.Max(0, (RH * zoom - ClientSize.Height) / 2);
        px = Math.Max(-mx, Math.Min(mx, px));
        py = Math.Max(-my, Math.Min(my, py));
    }

    // Low-quality interpolation while the user is interacting, high quality once things settle.
    void Interact()
    {
        fast = true;
        settle.Stop(); settle.Start();
        Invalidate();
    }

    void SetFit(int mode)
    {
        if (!HasImage) return;
        fitMode = mode; fast = false;
        Refit(); Invalidate();
    }

    void ZoomTo(double nz, double sx, double sy)
    {
        if (!HasImage) return;
        nz = Math.Max(0.01, Math.Min(64, nz));
        double cx = ClientSize.Width / 2.0, cy = ClientSize.Height / 2.0, k = nz / zoom;
        px = (sx - cx) - ((sx - cx) - px) * k;       // keeps the point under the cursor fixed
        py = (sy - cy) - ((sy - cy) - py) * k;
        zoom = nz; fitMode = 0;
        Clamp(); Interact();
    }

    void ZoomCenter(double nz) { ZoomTo(nz, ClientSize.Width / 2.0, ClientSize.Height / 2.0); }

    void Rotate(int d)
    {
        if (!HasImage) return;
        rot = (rot + d + 4) & 3;
        px = py = 0;
        Refit(); Invalidate();
    }

    // ---------------------------------------------------------------- painting

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(Bg);

        if (HasImage)
        {
            bool pixelPerfect = Math.Abs(zoom - 1) < 1e-6 || zoom >= 3;
            g.InterpolationMode = pixelPerfect ? InterpolationMode.NearestNeighbor
                                : fast ? InterpolationMode.Bilinear : InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.TranslateTransform((float)(ClientSize.Width / 2.0 + px), (float)(ClientSize.Height / 2.0 + py));
            g.RotateTransform(rot * 90f);
            g.ScaleTransform((float)zoom, (float)zoom);
            g.TranslateTransform(-cur.W / 2f, -cur.H / 2f);
            if (cur.Anim) ImageAnimator.UpdateFrames(cur.Img);
            g.DrawImage(cur.Img, new Rectangle(0, 0, cur.W, cur.H), 0, 0, cur.W, cur.H, GraphicsUnit.Pixel, ia);
            g.ResetTransform();
            if (showInfo) DrawInfo(g);
        }

        if (notice != null)
        {
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString(notice, big, Brushes.Gray, new RectangleF(20, 0, ClientSize.Width - 40, ClientSize.Height), sf);
        }
    }

    void DrawInfo(Graphics g)
    {
        string s = Path.GetFileName(cur.FullPath) + "    " + cur.W + " x " + cur.H + "    " + Util.FmtSize(cur.Bytes)
                 + "    " + (int)Math.Round(zoom * 100) + "%    " + (idx + 1) + " / " + files.Count;
        float k = g.DpiY / 96f;
        int h = (int)(font.GetHeight(g) + 10 * k);
        var bar = new Rectangle(0, ClientSize.Height - h, ClientSize.Width, h);
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        using (var b = new SolidBrush(Color.FromArgb(170, 0, 0, 0))) g.FillRectangle(b, bar);
        using (var sf = new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
            g.DrawString(s, font, Brushes.Gainsboro, new RectangleF(bar.X + 10 * k, bar.Y, bar.Width - 20 * k, bar.Height), sf);
    }

    // ---------------------------------------------------------------- input

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible) { Refit(); Invalidate(); }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Refit();
        Interact();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if ((ModifierKeys & Keys.Control) != 0)
        {
            ZoomTo(zoom * Math.Pow(1.2, e.Delta / 120.0), e.X, e.Y);
            return;
        }
        wheelAcc += e.Delta;                               // accumulate so touchpads don't skip images
        while (Math.Abs(wheelAcc) >= 120)
        {
            Step(wheelAcc > 0 ? -1 : 1);
            wheelAcc -= wheelAcc > 0 ? 120 : -120;
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left && HasImage) { dragging = true; last = e.Location; Cursor = Cursors.SizeAll; }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!dragging) return;
        px += e.X - last.X; py += e.Y - last.Y;
        last = e.Location;
        Clamp(); Interact();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        dragging = false; Cursor = Cursors.Default;
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (!HasImage) return;
        if (Math.Abs(zoom - 1) < 1e-6) SetFit(2); else ZoomTo(1, e.X, e.Y);
    }
}

// ====================================================================== thumbnail grid

// Owner-drawn, virtualized grid: only the visible cells are painted, so 100,000 files cost no more than 100.
sealed class ThumbGrid : Control
{
    static readonly int[] TileSizes = { 96, 128, 160, 200, 256 };
    static readonly Color Bg = Color.FromArgb(24, 24, 24);

    public event Action<int> OpenRequested;
    public event Action<Point, int> ContextRequested;      // client point, hit index (-1 = empty space)
    public event Action SelectionChanged;
    public event Action<string, string> Loaded;            // folder, error message (null = ok)

    public string Dir, Error;
    public bool Scanning;
    public List<Item> Items = new List<Item>();
    public List<string> Paths = new List<string>();
    public int FocusIndex { get { return fidx; } }

    readonly VScrollBar vs = new VScrollBar();
    readonly ThumbCache cache = new ThumbCache();
    readonly ThumbService svc;
    readonly Dictionary<string, long> failed = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
    readonly WinTimer repaintTimer = new WinTimer { Interval = 40 };
    readonly Font font = new Font("Segoe UI", 9f);
    readonly Font big = new Font("Segoe UI", 12f);
    readonly SolidBrush selBrush = new SolidBrush(Color.FromArgb(90, 60, 130, 230));
    readonly Pen selPen = new Pen(Color.FromArgb(60, 130, 230));
    readonly SolidBrush placeBrush = new SolidBrush(Color.FromArgb(40, 40, 40));

    int tileIdx = 2, tile, pad, cellW, cellH, cols = 1, total, scrollY, anc = -1, fidx = -1;
    volatile int scanGen;
    bool setting, repaintPending;
    Control host;

    public ThumbGrid()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = Bg; TabStop = true; Dock = DockStyle.Fill;
        vs.Dock = DockStyle.Right;
        vs.ValueChanged += delegate { if (setting) return; scrollY = vs.Value; Schedule(); Invalidate(); };
        Controls.Add(vs);
        Util.Dark(vs);
        svc = new ThumbService(Math.Max(2, Math.Min(4, Environment.ProcessorCount / 2)), OnThumb);
        repaintTimer.Tick += delegate { repaintTimer.Stop(); repaintPending = false; Invalidate(); };
        Relayout();
    }

    // ---------------------------------------------------------------- folder scan (background thread)

    public void Load(string dir, string selectPath, int hint)
    {
        int gen = ++scanGen;
        Scanning = true;
        host = FindForm();
        Control h = host;
        ThreadPool.QueueUserWorkItem(delegate
        {
            var list = new List<Item>();
            string err = null;
            try
            {
                foreach (FileInfo fi in new DirectoryInfo(dir).EnumerateFiles())
                {
                    if (gen != scanGen) return;                       // superseded by a newer request
                    FileAttributes a = fi.Attributes;
                    if ((a & FileAttributes.Hidden) != 0 || !Util.Exts.Contains(fi.Extension)) continue;
                    list.Add(new Item
                    {
                        Path = fi.FullName, Name = fi.Name, Size = fi.Length,
                        Stamp = fi.LastWriteTimeUtc.Ticks ^ fi.Length,
                        Offline = (a & Util.CloudMask) != 0
                    });
                }
                list.Sort(delegate (Item x, Item y) { return Util.NatCmp(x.Name, y.Name); });
            }
            catch (Exception ex) { err = ex.Message; list.Clear(); }
            if (gen != scanGen) return;
            Util.Post(h, delegate { Apply(gen, dir, list, err, selectPath, hint); });
        });
    }

    void Apply(int gen, string dir, List<Item> list, string err, string selectPath, int hint)
    {
        if (gen != scanGen) return;
        Scanning = false;
        Dir = dir; Items = list; Error = err;
        Paths = new List<string>(list.Count);
        foreach (Item it in list) Paths.Add(it.Path);
        anc = fidx = -1;
        int sel = selectPath == null ? -1 : IndexOfPath(selectPath, -1);
        if (sel < 0 && hint >= 0 && list.Count > 0) sel = Math.Min(hint, list.Count - 1);
        if (!Util.SameDir(dir, lastDir)) scrollY = 0;
        lastDir = dir;
        Relayout();
        if (sel >= 0) SelectOnly(sel); else Changed();
        Schedule();
        Invalidate();
        var h = Loaded;
        if (h != null) h(dir, err);
    }
    string lastDir;

    public int IndexOfPath(string path, int hint)
    {
        if (hint >= 0 && hint < Items.Count && string.Equals(Items[hint].Path, path, StringComparison.OrdinalIgnoreCase)) return hint;
        return Items.FindIndex(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase));
    }

    // ---------------------------------------------------------------- thumbnails

    void Schedule()
    {
        host = FindForm();
        int n = Items.Count;
        var jobs = new List<ThumbJob>();
        if (n > 0 && cellH > 0)
        {
            int v0 = (scrollY / cellH) * cols;
            int v1 = Math.Min(n - 1, ((scrollY + ClientSize.Height) / cellH + 1) * cols - 1);
            int p0 = Math.Max(0, v0 - 2 * cols), p1 = Math.Min(n - 1, v1 + 3 * cols);
            for (int i = v0; i <= v1; i++) Want(i, jobs);             // what's on screen first
            for (int i = v1 + 1; i <= p1; i++) Want(i, jobs);         // then a little ahead
            for (int i = v0 - 1; i >= p0; i--) Want(i, jobs);         // then a little behind
        }
        svc.Replace(jobs);
    }

    void Want(int i, List<ThumbJob> jobs)
    {
        Item it = Items[i];
        if (it.Offline || cache.Has(it.Path, it.Stamp, tile)) return;
        long fs;
        if (failed.TryGetValue(it.Path, out fs) && fs == it.Stamp) return;
        jobs.Add(new ThumbJob { Path = it.Path, Size = tile, Stamp = it.Stamp });
    }

    void OnThumb(ThumbJob j, Bitmap b)    // worker thread
    {
        if (!Util.Post(host, delegate { Thumbed(j, b); }) && b != null) b.Dispose();
    }

    void Thumbed(ThumbJob j, Bitmap b)
    {
        if (b == null) failed[j.Path] = j.Stamp;
        else if (j.Size != tile) b.Dispose();
        else cache.Put(j.Path, j.Stamp, j.Size, b);
        if (!repaintPending) { repaintPending = true; repaintTimer.Start(); }   // coalesce repaints
    }

    // ---------------------------------------------------------------- layout / scrolling

    void Relayout()
    {
        tile = Util.S(TileSizes[tileIdx]); pad = Util.S(8);
        cellW = tile + 2 * pad; cellH = pad + tile + pad / 2 + font.Height + pad;
        int W = Math.Max(1, ClientSize.Width - vs.Width), H = Math.Max(1, ClientSize.Height);
        cols = Math.Max(1, (W - pad) / cellW);
        int rows = (Items.Count + cols - 1) / cols;
        total = rows * cellH + pad;
        scrollY = Math.Max(0, Math.Min(scrollY, Math.Max(0, total - H)));
        setting = true;
        try
        {
            vs.Minimum = 0;
            vs.SmallChange = Math.Max(1, cellH / 3);
            vs.LargeChange = H;
            vs.Maximum = Math.Max(total, 1) - 1;
            vs.Enabled = total > H;
            vs.Value = Math.Min(scrollY, vs.Maximum);
        }
        finally { setting = false; }
    }

    void SetScroll(int v)
    {
        v = Math.Max(0, Math.Min(Math.Max(0, total - ClientSize.Height), v));
        if (v == scrollY) return;
        scrollY = v;
        setting = true;
        try { vs.Value = Math.Min(v, vs.Maximum); } finally { setting = false; }
        Schedule(); Invalidate();
    }

    void EnsureVisible(int i)
    {
        if (i < 0 || i >= Items.Count) return;
        int top = (i / cols) * cellH + pad / 2, bottom = top + cellH;
        if (top < scrollY) SetScroll(top - pad / 2);
        else if (bottom > scrollY + ClientSize.Height) SetScroll(bottom - ClientSize.Height + pad / 2);
    }

    void ChangeTile(int dir)
    {
        int n = tileIdx + dir;
        if (n < 0 || n >= TileSizes.Length) return;
        int firstIdx = (scrollY / Math.Max(1, cellH)) * cols;
        tileIdx = n;
        cache.Clear();
        Relayout();
        SetScroll((firstIdx / cols) * cellH);
        Schedule(); Invalidate();
    }

    // ---------------------------------------------------------------- selection

    public List<int> SelectedIndices()
    {
        var r = new List<int>();
        for (int i = 0; i < Items.Count; i++) if (Items[i].Sel) r.Add(i);
        return r;
    }

    void Changed()
    {
        Invalidate();
        var h = SelectionChanged;
        if (h != null) h();
    }

    void ClearAll() { foreach (Item it in Items) it.Sel = false; }

    public void SelectOnly(int i)
    {
        if (i < 0 || i >= Items.Count) return;
        ClearAll();
        Items[i].Sel = true;
        anc = fidx = i;
        EnsureVisible(i);
        Changed();
    }

    void SelectRange(int a, int b)
    {
        ClearAll();
        for (int i = Math.Min(a, b); i <= Math.Max(a, b); i++) Items[i].Sel = true;
        fidx = b;
        EnsureVisible(b);
        Changed();
    }

    int HitTest(Point p)
    {
        int lm = pad / 2;
        if (p.X < lm || p.Y < 0) return -1;
        int c = (p.X - lm) / cellW;
        if (c >= cols) return -1;
        int i = ((p.Y + scrollY) / cellH) * cols + c;
        return i >= 0 && i < Items.Count ? i : -1;
    }

    // ---------------------------------------------------------------- painting

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(Bg);
        if (Items.Count == 0)
        {
            string m = Scanning ? "Loading..." : Error != null ? "Can't open this folder\n" + Error
                     : Dir == null ? "Select a folder on the left" : "No images in this folder";
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString(m, big, Brushes.Gray, new RectangleF(0, 0, ClientSize.Width - vs.Width, ClientSize.Height), sf);
            return;
        }
        int lm = pad / 2;
        int r0 = scrollY / cellH, r1 = (scrollY + ClientSize.Height) / cellH;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        for (int r = r0; r <= r1; r++)
            for (int c = 0; c < cols; c++)
            {
                int i = r * cols + c;
                if (i >= Items.Count) return;
                var rc = new Rectangle(lm + c * cellW, r * cellH - scrollY + pad / 2, cellW, cellH);
                if (rc.IntersectsWith(e.ClipRectangle)) DrawCell(g, i, rc);
            }
    }

    void DrawCell(Graphics g, int i, Rectangle rc)
    {
        Item it = Items[i];
        if (it.Sel)
        {
            var sel = new Rectangle(rc.X + 1, rc.Y + 1, rc.Width - 3, rc.Height - 3);
            g.FillRectangle(selBrush, sel);
            g.DrawRectangle(selPen, sel);
        }
        if (i == fidx && Focused) ControlPaint.DrawFocusRectangle(g, new Rectangle(rc.X + 3, rc.Y + 3, rc.Width - 7, rc.Height - 7));

        var box = new Rectangle(rc.X + pad, rc.Y + pad, tile, tile);
        Bitmap b = it.Offline ? null : cache.Get(it.Path, it.Stamp, tile);
        if (b != null)
            g.DrawImage(b, box.X + (tile - b.Width) / 2, box.Y + (tile - b.Height) / 2, b.Width, b.Height);
        else
        {
            int s = tile * 3 / 4;
            var ph = new Rectangle(box.X + (tile - s) / 2, box.Y + (tile - s) / 2, s, s);
            g.FillRectangle(placeBrush, ph);
            string t = it.Offline ? "cloud-only\n(not on this PC)" : failed.ContainsKey(it.Path) ? "?" : "";
            if (t.Length > 0)
                TextRenderer.DrawText(g, t, font, ph, Color.Gray, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
        var tr = new Rectangle(rc.X + 2, box.Bottom + pad / 2, rc.Width - 4, font.Height + 2);
        TextRenderer.DrawText(g, it.Name, font, tr, it.Sel ? Color.White : Color.Gainsboro,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
    }

    // ---------------------------------------------------------------- input

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (!Visible) return;
        Relayout();
        if (fidx >= 0) EnsureVisible(fidx);
        Schedule(); Invalidate();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Relayout(); Schedule(); Invalidate();
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if ((ModifierKeys & Keys.Control) != 0) { ChangeTile(e.Delta > 0 ? 1 : -1); return; }
        SetScroll(scrollY - (int)(e.Delta * cellH * 0.6 / 120.0));
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        int i = HitTest(e.Location);
        bool ctrl = (ModifierKeys & Keys.Control) != 0, shift = (ModifierKeys & Keys.Shift) != 0;
        if (e.Button == MouseButtons.Right) { if (i >= 0 && !Items[i].Sel) SelectOnly(i); return; }
        if (e.Button != MouseButtons.Left) return;
        if (i < 0) { if (!ctrl && !shift) { ClearAll(); Changed(); } return; }
        if (shift && anc >= 0) SelectRange(anc, i);
        else if (ctrl) { Items[i].Sel = !Items[i].Sel; anc = fidx = i; Changed(); }
        else SelectOnly(i);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Right) return;
        var h = ContextRequested;
        if (h != null) h(e.Location, HitTest(e.Location));
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        int i = HitTest(e.Location);
        var h = OpenRequested;
        if (e.Button == MouseButtons.Left && i >= 0 && h != null) h(i);
    }

    protected override bool IsInputKey(Keys keyData)
    {
        switch (keyData & Keys.KeyCode)
        {
            case Keys.Left: case Keys.Right: case Keys.Up: case Keys.Down:
            case Keys.Home: case Keys.End: case Keys.PageUp: case Keys.PageDown: case Keys.Return:
                return true;
        }
        return base.IsInputKey(keyData);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        int n = Items.Count;
        if (n == 0) return;
        int f = fidx < 0 ? 0 : fidx, t = f;
        int page = Math.Max(1, ClientSize.Height / cellH) * cols;
        switch (e.KeyCode)
        {
            case Keys.Left: t = f - 1; break;
            case Keys.Right: t = f + 1; break;
            case Keys.Up: t = f - cols; break;
            case Keys.Down: t = f + cols; break;
            case Keys.Home: t = 0; break;
            case Keys.End: t = n - 1; break;
            case Keys.PageUp: t = f - page; break;
            case Keys.PageDown: t = f + page; break;
            case Keys.Return:
                if (fidx >= 0 && OpenRequested != null) OpenRequested(fidx);
                e.Handled = true; return;
            case Keys.A:
                if (e.Control) { foreach (Item it in Items) it.Sel = true; Changed(); e.Handled = true; }
                return;
            default: return;
        }
        t = Math.Max(0, Math.Min(n - 1, t));
        if (e.Shift) SelectRange(anc < 0 ? f : anc, t); else SelectOnly(t);
        e.Handled = true;
    }
}

// ====================================================================== main window

sealed class MainForm : Form
{
    const string Dummy = "\u0001dummy";

    Panel browse;
    Splitter splitter;
    TreeView tree;
    ThumbGrid grid;
    Canvas canvas;
    Label status;
    ImageList icons;
    TreeNode pcNode;
    ContextMenuStrip gridMenu, treeMenu;
    ToolStripMenuItem gOpen, gRename, gCopy, gPaste, gDelete, tRename, tCopy, tPaste, tDelete, tNew;

    readonly Dictionary<TreeNode, List<Action>> waiting = new Dictionary<TreeNode, List<Action>>();
    readonly Dictionary<TreeNode, string> driveDefault = new Dictionary<TreeNode, string>();
    readonly WinTimer navTimer = new WinTimer { Interval = 180 };
    string navPath, startArg;
    int revealToken;
    bool viewing, suppress, full;
    Control lastPane;
    FormBorderStyle oldStyle; FormWindowState oldState; Rectangle oldBounds;

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);

    public MainForm(string arg)
    {
        startArg = arg;
        AutoScaleMode = AutoScaleMode.None;
        Text = "WeeGleek";
        BackColor = Color.FromArgb(24, 24, 24);
        MinimumSize = new Size(Util.S(560), Util.S(360));
        Rectangle wa = Screen.PrimaryScreen.WorkingArea;
        ClientSize = new Size(wa.Width * 8 / 10, wa.Height * 8 / 10);
        StartPosition = FormStartPosition.CenterScreen;

        icons = new ImageList { ColorDepth = ColorDepth.Depth32Bit, ImageSize = new Size(Util.S(16), Util.S(16)) };
        for (int k = 0; k < 3; k++) icons.Images.Add(DrawIcon(k, Util.S(16)));

        tree = new TreeView
        {
            Dock = DockStyle.Left, Width = Util.S(280), BorderStyle = BorderStyle.None,
            BackColor = Color.FromArgb(32, 32, 32), ForeColor = Color.Gainsboro,
            HideSelection = false, ShowLines = false, FullRowSelect = true, HotTracking = true,
            ItemHeight = Util.S(22), Indent = Util.S(16), ImageList = icons, Font = new Font("Segoe UI", 9.5f)
        };
        Util.Dark(tree);
        tree.BeforeExpand += delegate (object s, TreeViewCancelEventArgs e) { EnsureLoaded(e.Node, null); };
        tree.AfterSelect += OnTreeSelect;
        tree.NodeMouseClick += OnTreeClick;
        tree.GotFocus += delegate { lastPane = tree; };

        grid = new ThumbGrid();
        grid.GotFocus += delegate { lastPane = grid; };
        grid.OpenRequested += OpenIndex;
        grid.ContextRequested += ShowGridMenu;
        grid.SelectionChanged += UpdateStatus;
        grid.Loaded += OnFolderLoaded;
        lastPane = grid;

        canvas = new Canvas();
        canvas.ImageChanged += OnImageChanged;

        // browse pane: [tree | splitter | grid]; the fill control is added first, edge-docked ones after
        browse = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(24, 24, 24) };
        splitter = new Splitter
        {
            Dock = DockStyle.Left, Width = Util.S(5), MinSize = Util.S(140), MinExtra = Util.S(200),
            BackColor = Color.FromArgb(52, 52, 52)
        };
        browse.Controls.Add(grid);
        browse.Controls.Add(splitter);
        browse.Controls.Add(tree);

        status = new Label
        {
            Dock = DockStyle.Bottom, Height = Util.S(24), TextAlign = ContentAlignment.MiddleLeft,
            BackColor = Color.FromArgb(38, 38, 38), ForeColor = Color.Gainsboro, Font = new Font("Segoe UI", 9f)
        };

        // fill-docked controls first, edge-docked ones after
        Controls.Add(browse);
        Controls.Add(canvas);
        Controls.Add(status);

        BuildMenus();
        BuildRoots();

        navTimer.Tick += delegate { navTimer.Stop(); if (navPath != null) ShowFolder(navPath, null, -1, false); };

        AllowDrop = true;
        foreach (Control c in new Control[] { this, tree, grid, canvas })
        {
            c.AllowDrop = true;
            c.DragEnter += OnDragEnterFiles;
            c.DragDrop += OnDropFiles;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try { int on = 1; DwmSetWindowAttribute(Handle, 20, ref on, 4); } catch (Exception) { }   // dark title bar
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Application.AddMessageFilter(new WheelRouter(this));
        pcNode.Expand();
        LabelDrives();
        if (!string.IsNullOrEmpty(startArg)) OpenImage(startArg);
        else
        {
            TreeNode q = null;
            foreach (TreeNode n in pcNode.Nodes) if (n.Name == "quick" && n.Text == "Pictures") q = n;
            if (q == null && pcNode.Nodes.Count > 0) q = pcNode.Nodes[pcNode.Nodes.Count - 1];
            if (q != null) tree.SelectedNode = q;
            grid.Focus();
        }
    }

    // ================================================================ folder tree (lazy, async)

    static Bitmap DrawIcon(int kind, int s)
    {
        var b = new Bitmap(s, s, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(b))
        {
            float u = s / 16f;
            if (kind == 0)        // folder
                using (var back = new SolidBrush(Color.FromArgb(214, 160, 40)))
                using (var front = new SolidBrush(Color.FromArgb(246, 202, 84)))
                {
                    g.FillRectangle(back, 1 * u, 3 * u, 6 * u, 3 * u);
                    g.FillRectangle(back, 1 * u, 4 * u, 14 * u, 9 * u);
                    g.FillRectangle(front, 1 * u, 6 * u, 14 * u, 7 * u);
                }
            else if (kind == 1)   // drive
                using (var body = new SolidBrush(Color.FromArgb(170, 176, 186)))
                using (var led = new SolidBrush(Color.FromArgb(80, 200, 100)))
                {
                    g.FillRectangle(body, 1 * u, 5 * u, 14 * u, 7 * u);
                    g.FillRectangle(led, 11 * u, 9 * u, 2 * u, 1.5f * u);
                }
            else                  // this PC
                using (var screen = new SolidBrush(Color.FromArgb(70, 140, 230)))
                using (var stand = new SolidBrush(Color.FromArgb(170, 176, 186)))
                {
                    g.FillRectangle(screen, 2 * u, 2 * u, 12 * u, 8 * u);
                    g.FillRectangle(stand, 6 * u, 11 * u, 4 * u, 2 * u);
                    g.FillRectangle(stand, 4 * u, 13 * u, 8 * u, 1.5f * u);
                }
        }
        return b;
    }

    static TreeNode DummyNode() { return new TreeNode("") { Name = Dummy }; }

    TreeNode MakeNode(string path, string text, string name, int icon)
    {
        var n = new TreeNode(text) { Tag = path, Name = name, ImageIndex = icon, SelectedImageIndex = icon };
        n.Nodes.Add(DummyNode());
        return n;
    }

    void BuildRoots()
    {
        pcNode = new TreeNode("This PC") { Name = "pc", ImageIndex = 2, SelectedImageIndex = 2 };
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[][] quick =
        {
            new[] { "Desktop", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory) },
            new[] { "Documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) },
            new[] { "Downloads", Path.Combine(home, "Downloads") },
            new[] { "Pictures", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures) }
        };
        foreach (string[] q in quick)
            if (!string.IsNullOrEmpty(q[1]) && Directory.Exists(q[1])) pcNode.Nodes.Add(MakeNode(q[1], q[0], "quick", 0));

        foreach (DriveInfo d in DriveInfo.GetDrives())
        {
            string kind;
            if (d.DriveType == DriveType.Fixed) kind = "Local Disk";
            else if (d.DriveType == DriveType.Removable) kind = "Removable Disk";
            else if (d.DriveType == DriveType.CDRom) kind = "CD/DVD Drive";
            else continue;                                       // no network or virtual drives
            string root = d.Name;
            TreeNode n = MakeNode(root, kind + " (" + root.TrimEnd('\\') + ")", "drive", 1);
            driveDefault[n] = kind;
            pcNode.Nodes.Add(n);
        }
        tree.Nodes.Add(pcNode);
    }

    // volume labels can be slow to query (card readers, optical drives), so fetch them off-thread
    void LabelDrives()
    {
        var nodes = new List<TreeNode>();
        foreach (TreeNode n in pcNode.Nodes) if (n.Name == "drive") nodes.Add(n);
        ThreadPool.QueueUserWorkItem(delegate
        {
            foreach (TreeNode n in nodes)
            {
                string root = (string)n.Tag, label = "";
                try { var d = new DriveInfo(root); if (d.IsReady) label = d.VolumeLabel; } catch (Exception) { }
                if (label.Length == 0) continue;
                string text = label + " (" + root.TrimEnd('\\') + ")";
                TreeNode nn = n;
                Util.Post(this, delegate { nn.Text = text; });
            }
        });
    }

    static List<string> ListDirs(string path)
    {
        var r = new List<string>();
        try
        {
            foreach (DirectoryInfo d in new DirectoryInfo(path).EnumerateDirectories())
            {
                if ((d.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                r.Add(d.FullName);
            }
            r.Sort(delegate (string a, string b) { return Util.NatCmp(Path.GetFileName(a), Path.GetFileName(b)); });
        }
        catch (Exception) { }                                    // access denied, disc removed, ...
        return r;
    }

    static bool IsUnloaded(TreeNode n) { return n.Nodes.Count == 1 && n.Nodes[0].Name == Dummy; }

    // Fill a node's children on a worker thread; callbacks run on the UI thread afterwards.
    void EnsureLoaded(TreeNode n, Action done)
    {
        if (!IsUnloaded(n)) { if (done != null) done(); return; }
        List<Action> w;
        if (waiting.TryGetValue(n, out w)) { if (done != null) w.Add(done); return; }
        w = new List<Action>();
        if (done != null) w.Add(done);
        waiting[n] = w;
        n.Nodes[0].Text = "Loading...";
        string path = (string)n.Tag;
        ThreadPool.QueueUserWorkItem(delegate
        {
            List<string> dirs = ListDirs(path);
            Util.Post(this, delegate { Populate(n, dirs); });
        });
    }

    void Populate(TreeNode n, List<string> dirs)
    {
        if (!waiting.ContainsKey(n)) return;                     // node was reloaded meanwhile
        tree.BeginUpdate();
        n.Nodes.Clear();
        foreach (string d in dirs) n.Nodes.Add(MakeNode(d, Path.GetFileName(d), "dir", 0));
        tree.EndUpdate();
        List<Action> w = waiting[n];
        waiting.Remove(n);
        foreach (Action a in w) a();
    }

    void ReloadNode(TreeNode n)
    {
        bool exp = n.IsExpanded;
        waiting.Remove(n);
        n.Nodes.Clear();
        n.Nodes.Add(DummyNode());
        if (exp) n.Expand();
    }

    TreeNode FindDrive(string root)
    {
        foreach (TreeNode n in pcNode.Nodes)
            if (n.Name == "drive" && Util.SameDir((string)n.Tag, root)) return n;
        return null;
    }

    TreeNode FindLoadedNode(string path)
    {
        string root = Path.GetPathRoot(path);
        TreeNode cur = root == null ? null : FindDrive(root);
        if (cur == null) return null;
        if (Util.SameDir(root, path)) return cur;
        foreach (string part in path.Substring(root.Length).Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (IsUnloaded(cur)) return null;
            TreeNode next = null;
            foreach (TreeNode c in cur.Nodes)
                if (string.Equals(c.Text, part, StringComparison.OrdinalIgnoreCase)) { next = c; break; }
            if (next == null) return null;
            cur = next;
        }
        return cur;
    }

    // Expand and highlight a folder in the tree (asynchronously, one level at a time).
    void RevealInTree(string dir)
    {
        int token = ++revealToken;
        try
        {
            dir = Path.GetFullPath(dir);
            string root = Path.GetPathRoot(dir);
            TreeNode n = FindDrive(root);
            if (n == null) return;
            string[] parts = dir.Substring(root.Length).Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            RevealStep(n, parts, 0, token);
        }
        catch (Exception) { }
    }

    void RevealStep(TreeNode n, string[] parts, int i, int token)
    {
        if (token != revealToken) return;
        if (i == parts.Length) { SelectQuiet(n); return; }
        EnsureLoaded(n, delegate
        {
            if (token != revealToken) return;
            n.Expand();
            TreeNode next = null;
            foreach (TreeNode c in n.Nodes)
                if (string.Equals(c.Text, parts[i], StringComparison.OrdinalIgnoreCase)) { next = c; break; }
            if (next == null) { SelectQuiet(n); return; }
            RevealStep(next, parts, i + 1, token);
        });
    }

    void SelectQuiet(TreeNode n)
    {
        suppress = true;
        try { tree.SelectedNode = n; n.EnsureVisible(); }
        finally { suppress = false; }
    }

    void OnTreeSelect(object s, TreeViewEventArgs e)
    {
        if (suppress) return;
        string p = NodePath(e.Node);
        if (p == null) return;
        if (e.Action == TreeViewAction.ByKeyboard) { navPath = p; navTimer.Stop(); navTimer.Start(); }   // debounce arrow-key scrubbing
        else ShowFolder(p, null, -1, false);
    }

    static string NodePath(TreeNode n) { return n == null ? null : n.Tag as string; }

    static bool IsRootPath(string p) { return Util.SameDir(Path.GetPathRoot(p), p); }

    // ================================================================ browsing <-> viewing

    void ShowFolder(string dir, string select, int hint, bool force)
    {
        if (dir == null) return;
        if (!force && select != null && !grid.Scanning && Util.SameDir(dir, grid.Dir))
        {
            int i = grid.IndexOfPath(select, -1);
            if (i >= 0) { grid.SelectOnly(i); SyncCanvas(); return; }
        }
        grid.Load(dir, select, hint);
        UpdateStatus();
    }

    void OnFolderLoaded(string dir, string err)
    {
        UpdateStatus();
        if (!viewing) Text = dir + " - WeeGleek";
        SyncCanvas();
    }

    // hand the viewer the (sorted) list of the folder once the grid has scanned it
    void SyncCanvas()
    {
        string w = canvas.Want;
        if (w == null || grid.Dir == null) return;
        if (Util.SameDir(Path.GetDirectoryName(w), grid.Dir)) canvas.SetList(grid.Paths, w);
    }

    void OpenImage(string path)
    {
        try
        {
            path = Path.GetFullPath(path);
            if (Directory.Exists(path))
            {
                SetViewing(false);
                ShowFolder(path, null, -1, false);
                RevealInTree(path);
                return;
            }
            if (!File.Exists(path)) return;
            string dir = Path.GetDirectoryName(path);
            canvas.ShowSingle(path);                       // show it immediately, before the folder scan finishes
            SetViewing(true);
            ShowFolder(dir, path, -1, false);              // grid + tree follow in the background
            RevealInTree(dir);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "WeeGleek"); }
    }

    void OpenIndex(int i)
    {
        if (i < 0 || i >= grid.Paths.Count) return;
        canvas.Open(grid.Paths, i);
        SetViewing(true);
    }

    void OnImageChanged(string path, int index)
    {
        if (viewing) Text = Path.GetFileName(path) + " - WeeGleek";
        if (grid.Dir != null && Util.SameDir(Path.GetDirectoryName(path), grid.Dir))
        {
            int i = grid.IndexOfPath(path, index);
            if (i >= 0) grid.SelectOnly(i);                // keep the browser's selection in step with the viewer
        }
    }

    void SetViewing(bool v)
    {
        viewing = v;
        SuspendLayout();
        if (v) { canvas.Visible = true; browse.Visible = false; status.Visible = false; }
        else { browse.Visible = true; status.Visible = true; canvas.Visible = false; }
        ResumeLayout(true);
        if (v) canvas.Focus();
        else { grid.Focus(); lastPane = grid; Text = (grid.Dir ?? "WeeGleek") + " - WeeGleek"; UpdateStatus(); }
    }

    void UpdateStatus()
    {
        if (grid.Scanning) { status.Text = "  Loading folder..."; return; }
        if (grid.Error != null) { status.Text = "  " + grid.Error; return; }
        List<int> sel = grid.SelectedIndices();
        long bytes = 0;
        foreach (int i in sel) bytes += grid.Items[i].Size;
        string s = "  " + grid.Items.Count + (grid.Items.Count == 1 ? " image" : " images");
        if (sel.Count > 0) s += "   |   " + sel.Count + " selected (" + Util.FmtSize(bytes) + ")";
        status.Text = s + "   |   " + grid.Dir;
    }

    // ================================================================ menus & file operations

    static ToolStripMenuItem AddEntry(ContextMenuStrip m, string text, string keys, EventHandler h)
    {
        var mi = new ToolStripMenuItem(text) { ShortcutKeyDisplayString = keys };
        mi.Click += h;
        m.Items.Add(mi);
        return mi;
    }

    void BuildMenus()
    {
        gridMenu = new ContextMenuStrip();
        gOpen = AddEntry(gridMenu, "Open", "Enter", delegate { OpenIndex(grid.FocusIndex); });
        gridMenu.Items.Add(new ToolStripSeparator());
        gRename = AddEntry(gridMenu, "Rename", "F2", delegate { DoRename(false); });
        gCopy = AddEntry(gridMenu, "Copy", "Ctrl+C", delegate { DoCopy(false); });
        gPaste = AddEntry(gridMenu, "Paste", "Ctrl+V", delegate { DoPaste(false); });
        gridMenu.Items.Add(new ToolStripSeparator());
        gDelete = AddEntry(gridMenu, "Delete (to Recycle Bin)", "Del", delegate { DoDelete(false); });
        gridMenu.Items.Add(new ToolStripSeparator());
        AddEntry(gridMenu, "Refresh", "F5", delegate { ReloadAll(); });

        treeMenu = new ContextMenuStrip();
        tNew = AddEntry(treeMenu, "New Folder", "", delegate { DoNewFolder(); });
        treeMenu.Items.Add(new ToolStripSeparator());
        tRename = AddEntry(treeMenu, "Rename", "F2", delegate { DoRename(true); });
        tCopy = AddEntry(treeMenu, "Copy", "Ctrl+C", delegate { DoCopy(true); });
        tPaste = AddEntry(treeMenu, "Paste", "Ctrl+V", delegate { DoPaste(true); });
        treeMenu.Items.Add(new ToolStripSeparator());
        tDelete = AddEntry(treeMenu, "Delete (to Recycle Bin)", "Del", delegate { DoDelete(true); });
        treeMenu.Items.Add(new ToolStripSeparator());
        AddEntry(treeMenu, "Refresh", "F5", delegate { ReloadAll(); });
    }

    static bool ClipHasFiles()
    {
        try { return Clipboard.ContainsFileDropList(); } catch (Exception) { return false; }
    }

    void ShowGridMenu(Point p, int hit)
    {
        lastPane = grid;
        bool on = hit >= 0;
        gOpen.Enabled = on;
        gRename.Enabled = on && grid.SelectedIndices().Count == 1;
        gCopy.Enabled = on;
        gDelete.Enabled = on;
        gPaste.Enabled = grid.Dir != null && ClipHasFiles();
        gridMenu.Show(grid, p);
    }

    void OnTreeClick(object s, TreeNodeMouseClickEventArgs e)
    {
        if (e.Button != MouseButtons.Right) return;
        tree.SelectedNode = e.Node;
        lastPane = tree;
        string p = NodePath(e.Node);
        bool real = p != null, plain = real && !IsRootPath(p);
        tNew.Enabled = real;
        tRename.Enabled = plain;
        tCopy.Enabled = plain;
        tDelete.Enabled = plain;
        tPaste.Enabled = real && ClipHasFiles();
        treeMenu.Show(tree, e.Location);
    }

    void ReloadAll()
    {
        TreeNode n = tree.SelectedNode;
        if (NodePath(n) != null) ReloadNode(n);
        if (grid.Dir != null) ShowFolder(grid.Dir, null, -1, true);
    }

    string ValidateName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.EndsWith(".") || name.EndsWith(" "))
            return "That name isn't valid. It can't be empty, end with a dot or space, or contain any of  \\ / : * ? \" < > |";
        return null;
    }

    void Warn(string msg) { MessageBox.Show(this, msg, "WeeGleek", MessageBoxButtons.OK, MessageBoxIcon.Warning); }

    void DoRename(bool inTree)
    {
        if (inTree)
        {
            TreeNode n = tree.SelectedNode;
            string p = NodePath(n);
            if (p == null || IsRootPath(p)) return;
            string nn = Prompt.Ask(this, "Rename folder", "New name:", n.Text, false);
            if (nn == null || nn == n.Text) return;
            string bad = ValidateName(nn);
            if (bad != null) { Warn(bad); return; }
            string dest = Path.Combine(Path.GetDirectoryName(p), nn);
            if (!string.Equals(dest, p, StringComparison.OrdinalIgnoreCase) && (Directory.Exists(dest) || File.Exists(dest))) { Warn("An item with that name already exists."); return; }
            try { Directory.Move(p, dest); }
            catch (Exception ex) { Warn(ex.Message); return; }
            string gd = grid.Dir;
            n.Text = nn; n.Tag = dest;
            ReloadNode(n);
            if (gd != null && (Util.SameDir(gd, p) || gd.StartsWith(p.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)))
                ShowFolder(dest + gd.Substring(p.TrimEnd('\\').Length), null, -1, true);
            return;
        }
        List<int> sel = grid.SelectedIndices();
        if (sel.Count != 1) return;
        Item it = grid.Items[sel[0]];
        string name = Prompt.Ask(this, "Rename", "New name:", it.Name, true);
        if (name == null || name == it.Name) return;
        string err = ValidateName(name);
        if (err != null) { Warn(err); return; }
        string target = Path.Combine(Path.GetDirectoryName(it.Path), name);
        if (!string.Equals(target, it.Path, StringComparison.OrdinalIgnoreCase) && (File.Exists(target) || Directory.Exists(target))) { Warn("A file with that name already exists."); return; }
        try { File.Move(it.Path, target); }
        catch (Exception ex) { Warn(ex.Message); return; }
        ShowFolder(grid.Dir, target, -1, true);
    }

    void DoCopy(bool inTree)
    {
        var list = new StringCollection();
        if (inTree)
        {
            string p = NodePath(tree.SelectedNode);
            if (p == null || IsRootPath(p)) return;
            list.Add(p);
        }
        else foreach (int i in grid.SelectedIndices()) list.Add(grid.Items[i].Path);
        if (list.Count == 0) return;
        try
        {
            var d = new DataObject();
            d.SetFileDropList(list);                                // real Explorer-compatible clipboard format
            Clipboard.SetDataObject(d, true, 5, 50);
            status.Text = "  Copied " + list.Count + (list.Count == 1 ? " item" : " items") + " to the clipboard";
        }
        catch (Exception ex) { Warn(ex.Message); }
    }

    void DoPaste(bool inTree)
    {
        string dest = inTree ? NodePath(tree.SelectedNode) : grid.Dir;
        if (dest == null || !ClipHasFiles()) return;
        var srcs = new List<string>();
        foreach (string s in Clipboard.GetFileDropList()) srcs.Add(s);
        if (srcs.Count == 0) return;
        status.Text = "  Copying " + srcs.Count + (srcs.Count == 1 ? " item" : " items") + "...";
        ThreadPool.QueueUserWorkItem(delegate            // copying never blocks the UI
        {
            var errs = new List<string>();
            foreach (string s in srcs)
            {
                try
                {
                    bool isDir = Directory.Exists(s);
                    if (!isDir && !File.Exists(s)) continue;
                    string name = Path.GetFileName(s.TrimEnd('\\', '/'));
                    if (name.Length == 0) continue;
                    if (isDir && (Util.SameDir(dest, s) || dest.StartsWith(s.TrimEnd('\\', '/') + "\\", StringComparison.OrdinalIgnoreCase)))
                    { errs.Add(name + ": a folder can't be copied into itself."); continue; }
                    string target = Util.UniqueName(dest, name, isDir);   // never overwrites anything
                    if (isDir) Util.CopyDir(s, target); else File.Copy(s, target);
                }
                catch (Exception ex) { errs.Add(Path.GetFileName(s) + ": " + ex.Message); }
            }
            Util.Post(this, delegate { PasteDone(dest, errs); });
        });
    }

    void PasteDone(string dest, List<string> errs)
    {
        if (Util.SameDir(dest, grid.Dir)) ShowFolder(dest, null, -1, true); else UpdateStatus();
        TreeNode n = FindLoadedNode(dest);
        if (n != null && !IsUnloaded(n)) ReloadNode(n);
        if (errs.Count > 0)
        {
            if (errs.Count > 8) { errs = errs.GetRange(0, 8); errs.Add("..."); }
            Warn("Some items could not be copied:\n\n" + string.Join("\n", errs));
        }
    }

    void DoDelete(bool inTree)
    {
        var paths = new List<string>();
        int hint = -1;
        if (inTree)
        {
            string p = NodePath(tree.SelectedNode);
            if (p == null || IsRootPath(p)) return;
            paths.Add(p);
        }
        else
        {
            List<int> sel = grid.SelectedIndices();
            if (sel.Count > 0) hint = sel[0];
            foreach (int i in sel) paths.Add(grid.Items[i].Path);
        }
        if (paths.Count == 0) return;
        string msg = paths.Count == 1 ? "Move \"" + Path.GetFileName(paths[0]) + "\" to the Recycle Bin?"
                                      : "Move these " + paths.Count + " items to the Recycle Bin?";
        if (MessageBox.Show(this, msg, "Delete", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

        Cursor = Cursors.WaitCursor;
        try
        {
            foreach (string p in paths)
            {
                if (Directory.Exists(p)) VBFS.DeleteDirectory(p, VBUI.OnlyErrorDialogs, VBRecycle.SendToRecycleBin);
                else VBFS.DeleteFile(p, VBUI.OnlyErrorDialogs, VBRecycle.SendToRecycleBin);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Warn(ex.Message); }
        finally { Cursor = Cursors.Default; }

        if (inTree)
        {
            TreeNode parent = tree.SelectedNode.Parent;
            string pp = NodePath(parent);
            if (parent != null && pp != null)
            {
                SelectQuiet(parent);
                ReloadNode(parent);
                ShowFolder(pp, null, -1, true);
                return;
            }
        }
        if (grid.Dir != null) ShowFolder(grid.Dir, null, hint, true);
    }

    void DoNewFolder()
    {
        TreeNode n = tree.SelectedNode;
        string p = NodePath(n);
        if (p == null) return;
        try
        {
            string target = Path.Combine(p, "New folder");
            for (int i = 2; Directory.Exists(target) || File.Exists(target); i++) target = Path.Combine(p, "New folder (" + i + ")");
            Directory.CreateDirectory(target);
            ReloadNode(n);
            n.Expand();
        }
        catch (Exception ex) { Warn(ex.Message); }
    }

    // ================================================================ open dialog, drag & drop, keys, fullscreen

    void OpenDialog()
    {
        using (var d = new OpenFileDialog())
        {
            d.Filter = "Images|*.jpg;*.jpeg;*.jpe;*.jfif;*.png;*.bmp;*.dib;*.gif;*.tif;*.tiff|All files|*.*";
            if (grid.Dir != null) d.InitialDirectory = grid.Dir;
            if (d.ShowDialog(this) == DialogResult.OK) OpenImage(d.FileName);
        }
    }

    void OnDragEnterFiles(object s, DragEventArgs e)
    {
        e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
    }

    void OnDropFiles(object s, DragEventArgs e)
    {
        string[] f = e.Data.GetData(DataFormats.FileDrop) as string[];
        if (f != null && f.Length > 0) OpenImage(f[0]);
    }

    void ToggleFull()
    {
        if (!full)
        {
            Screen sc = Screen.FromControl(this);
            oldStyle = FormBorderStyle; oldState = WindowState;
            oldBounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            full = true;
            WindowState = FormWindowState.Normal;
            FormBorderStyle = FormBorderStyle.None;
            Bounds = sc.Bounds;
        }
        else
        {
            full = false;
            FormBorderStyle = oldStyle;
            WindowState = FormWindowState.Normal;
            Bounds = oldBounds;
            WindowState = oldState;
        }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        Keys k = keyData & Keys.KeyCode;
        bool ctrl = (keyData & Keys.Control) != 0;
        if (k == Keys.F11) { ToggleFull(); return true; }
        if (ctrl && k == Keys.O) { OpenDialog(); return true; }

        if (viewing)
        {
            if (k == Keys.Enter && !ctrl) { ToggleFull(); return true; }
            if (k == Keys.Escape) { if (full) ToggleFull(); else SetViewing(false); return true; }
            if (canvas.HandleKey(keyData)) return true;
        }
        else
        {
            bool tr = lastPane == tree;
            switch (k)
            {
                case Keys.F2: DoRename(tr); return true;
                case Keys.Delete: DoDelete(tr); return true;
                case Keys.F5: ReloadAll(); return true;
                case Keys.C: if (ctrl) { DoCopy(tr); return true; } break;
                case Keys.V: if (ctrl) { DoPaste(tr); return true; } break;
                case Keys.Escape: if (full) { ToggleFull(); return true; } break;
            }
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }
}

static class Program
{
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);

    static bool reporting;

    // Shows any unexpected error in a message box (Ctrl+C in the box copies the text).
    // Nothing is written to disk and nothing is sent anywhere.
    static void Report(Exception ex)
    {
        if (reporting) return;                       // don't stack dialogs if a repaint keeps failing
        reporting = true;
        try
        {
            MessageBox.Show("WeeGleek hit an unexpected error:\n\n" + (ex == null ? "(unknown)" : ex.ToString()),
                            "WeeGleek", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (Exception) { }
        reporting = false;
    }

    [STAThread]
    static void Main(string[] args)
    {
        try
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate (object s, ThreadExceptionEventArgs e) { Report(e.Exception); };
            AppDomain.CurrentDomain.UnhandledException += delegate (object s, UnhandledExceptionEventArgs e) { Report(e.ExceptionObject as Exception); };

            // Real pixels on high-DPI screens (no blurry bitmap scaling by Windows).
            try { SetProcessDpiAwarenessContext(new IntPtr(-4)); }
            catch (Exception) { try { SetProcessDPIAware(); } catch (Exception) { } }
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) Util.K = g.DpiX / 96f;
            Application.EnableVisualStyles();
            Application.Run(new MainForm(args.Length > 0 ? args[0] : null));
        }
        catch (Exception ex) { Report(ex); }
    }
}
