// BitChroma - lightweight screen color identifier / picker for Windows.
// Single-file WinForms app, compiles with the in-box .NET Framework csc.exe.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace BitChroma
{
    static class Native
    {
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
        [DllImport("user32.dll")] public static extern uint GetDpiForSystem();
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X; public int Y; }

        public static Point CursorPos()
        {
            POINT p;
            if (GetCursorPos(out p)) return new Point(p.X, p.Y);
            return Control.MousePosition;
        }

        public static float ScaleFactor()
        {
            try { return GetDpiForSystem() / 96f; } catch { return 1f; }
        }

        public static void MakeDpiAware()
        {
            // Per-monitor v2 keeps every screen coordinate in true physical pixels,
            // which is what makes the picked pixel exact on scaled displays.
            try { if (SetProcessDpiAwarenessContext(new IntPtr(-4))) return; } catch { }
            try { SetProcessDPIAware(); } catch { }
        }
    }

    static class Col
    {
        public static string Hex(Color c) { return string.Format("#{0:X2}{1:X2}{2:X2}", c.R, c.G, c.B); }
        public static string Rgb(Color c) { return string.Format("rgb({0}, {1}, {2})", c.R, c.G, c.B); }

        public static string Hsl(Color c)
        {
            double h, s, l;
            ToHsl(c, out h, out s, out l);
            return string.Format("hsl({0}, {1}%, {2}%)",
                Math.Round(h), Math.Round(s * 100), Math.Round(l * 100));
        }

        public static void ToHsl(Color c, out double h, out double s, out double l)
        {
            double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            double d = max - min;
            l = (max + min) / 2.0;
            if (d < 1e-9) { h = 0; s = 0; return; }
            s = l > 0.5 ? d / (2.0 - max - min) : d / (max + min);
            if (max == r) h = ((g - b) / d + (g < b ? 6 : 0));
            else if (max == g) h = (b - r) / d + 2;
            else h = (r - g) / d + 4;
            h *= 60;
        }

        public static double Luma(Color c)
        {
            return (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
        }

        public static Color Ink(Color bg)
        {
            return Luma(bg) > 0.58 ? Color.FromArgb(20, 20, 24) : Color.FromArgb(245, 245, 250);
        }

        public static bool TryParse(string text, out Color c)
        {
            c = Color.Black;
            if (text == null) return false;
            string t = text.Trim().Replace(" ", "");
            if (t.StartsWith("#")) t = t.Substring(1);
            if (t.Length == 3) t = new string(new char[] { t[0], t[0], t[1], t[1], t[2], t[2] });
            if (t.Length != 6) return false;
            int v;
            if (!int.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v)) return false;
            c = Color.FromArgb((v >> 16) & 0xFF, (v >> 8) & 0xFF, v & 0xFF);
            return true;
        }
    }

    // Fullscreen snapshot overlay: freeze the desktop, magnify, click a pixel.
    class PickOverlay : Form
    {
        static readonly int[] Zooms = { 4, 6, 8, 12, 16, 24, 32 };

        readonly Bitmap shot;
        readonly Rectangle vb;      // virtual screen bounds, physical pixels
        readonly float S;
        readonly int magSize, infoH, gap;
        readonly Font infoFont, hintFont;

        int zoomIx = 3;
        Point cur;                  // screen coords
        Rectangle hintRect;

        public Color Result;
        public bool Picked;

        public PickOverlay(Bitmap snapshot, Rectangle bounds, float scale, int zoom)
        {
            shot = snapshot; vb = bounds; S = scale;
            for (int i = 0; i < Zooms.Length; i++) if (Zooms[i] == zoom) zoomIx = i;

            magSize = (int)Math.Round(196 * S);
            infoH = (int)Math.Round(28 * S);
            gap = (int)Math.Round(26 * S);
            infoFont = new Font("Consolas", 14f * S, FontStyle.Bold, GraphicsUnit.Pixel);
            hintFont = new Font("Segoe UI", 12f * S, GraphicsUnit.Pixel);

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = vb;
            ShowInTaskbar = false;
            TopMost = true;
            KeyPreview = true;
            Cursor = Cursors.Cross;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer, true);
            cur = Native.CursorPos();

            Rectangle pb = Screen.PrimaryScreen.Bounds;
            int hw = (int)Math.Round(600 * S), hh = (int)Math.Round(30 * S);
            hintRect = new Rectangle(pb.Left - vb.Left + (pb.Width - hw) / 2,
                                     pb.Top - vb.Top + (int)Math.Round(18 * S), hw, hh);
        }

        public int Zoom { get { return Zooms[zoomIx]; } }

        protected override void OnShown(EventArgs e) { base.OnShown(e); Activate(); Focus(); }

        Point ToClient(Point screenPt)
        {
            return new Point(screenPt.X - vb.X, screenPt.Y - vb.Y);
        }

        Color PixelAt(Point screenPt)
        {
            Point c = ToClient(screenPt);
            if (c.X < 0 || c.Y < 0 || c.X >= shot.Width || c.Y >= shot.Height) return Color.Black;
            return shot.GetPixel(c.X, c.Y);
        }

        int Cells()
        {
            int n = Math.Max(3, magSize / Zoom);
            if (n % 2 == 0) n++;
            return n;
        }

        Rectangle MagRect(Point screenPt)
        {
            Point c = ToClient(screenPt);
            int total = magSize + infoH;
            int x = c.X + gap, y = c.Y + gap;
            if (x + magSize > ClientSize.Width) x = c.X - gap - magSize;
            if (y + total > ClientSize.Height) y = c.Y - gap - total;
            if (x < 0) x = 0;
            if (y < 0) y = 0;
            return new Rectangle(x, y, magSize, total);
        }

        void Redraw(Point oldPt, Point newPt)
        {
            Invalidate(Rectangle.Inflate(MagRect(oldPt), 6, 6));
            Invalidate(Rectangle.Inflate(MagRect(newPt), 6, 6));
            Update();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Point p = new Point(e.X + vb.X, e.Y + vb.Y);
            if (p == cur) return;
            Point old = cur; cur = p;
            Redraw(old, cur);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            int ix = zoomIx + (e.Delta > 0 ? 1 : -1);
            if (ix < 0) ix = 0;
            if (ix >= Zooms.Length) ix = Zooms.Length - 1;
            if (ix == zoomIx) return;
            zoomIx = ix;
            Redraw(cur, cur);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left) Pick();
            else Cancel();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            switch (e.KeyCode)
            {
                case Keys.Escape: Cancel(); break;
                case Keys.Enter:
                case Keys.Space: Pick(); break;
                case Keys.Left: Nudge(-1, 0); break;
                case Keys.Right: Nudge(1, 0); break;
                case Keys.Up: Nudge(0, -1); break;
                case Keys.Down: Nudge(0, 1); break;
                case Keys.Oemplus:
                case Keys.Add: Bump(1); break;
                case Keys.OemMinus:
                case Keys.Subtract: Bump(-1); break;
            }
            e.Handled = true;
        }

        void Bump(int d)
        {
            int ix = Math.Max(0, Math.Min(Zooms.Length - 1, zoomIx + d));
            if (ix == zoomIx) return;
            zoomIx = ix; Redraw(cur, cur);
        }

        void Nudge(int dx, int dy)
        {
            Point p = new Point(cur.X + dx, cur.Y + dy);
            Native.SetCursorPos(p.X, p.Y);
            Point old = cur; cur = p;
            Redraw(old, cur);
        }

        void Pick()
        {
            Result = PixelAt(cur);
            Picked = true;
            DialogResult = DialogResult.OK;
            Close();
        }

        void Cancel()
        {
            Picked = false;
            DialogResult = DialogResult.Cancel;
            Close();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Rectangle clip = e.ClipRectangle;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.SmoothingMode = SmoothingMode.None;
            g.DrawImage(shot, clip, clip, GraphicsUnit.Pixel);

            if (clip.IntersectsWith(hintRect)) DrawHint(g);
            DrawMagnifier(g);
        }

        void DrawHint(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var b = new SolidBrush(Color.FromArgb(205, 18, 18, 22)))
            using (var path = Rounded(hintRect, (int)Math.Round(8 * S)))
                g.FillPath(b, path);
            using (var b = new SolidBrush(Color.FromArgb(235, 235, 240)))
            {
                var sf = new StringFormat();
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                g.DrawString("Click to pick   .   Wheel = zoom   .   Arrows = nudge 1px   .   Esc or right-click = cancel",
                    hintFont, b, hintRect, sf);
            }
            g.SmoothingMode = SmoothingMode.None;
        }

        static GraphicsPath Rounded(Rectangle r, int rad)
        {
            var p = new GraphicsPath();
            int d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        void DrawMagnifier(Graphics g)
        {
            Rectangle outer = MagRect(cur);
            Rectangle view = new Rectangle(outer.X, outer.Y, magSize, magSize);
            Rectangle info = new Rectangle(outer.X, outer.Bottom - infoH, magSize, infoH);

            int n = Cells();
            int half = n / 2;
            float px = (float)magSize / n;
            Point c = ToClient(cur);
            Rectangle src = new Rectangle(c.X - half, c.Y - half, n, n);

            using (var bg = new SolidBrush(Color.FromArgb(26, 26, 30))) g.FillRectangle(bg, view);

            Rectangle isrc = Rectangle.Intersect(src, new Rectangle(0, 0, shot.Width, shot.Height));
            if (isrc.Width > 0 && isrc.Height > 0)
            {
                var dst = new RectangleF(view.X + (isrc.X - src.X) * px, view.Y + (isrc.Y - src.Y) * px,
                                         isrc.Width * px, isrc.Height * px);
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(shot, dst, new RectangleF(isrc.X, isrc.Y, isrc.Width, isrc.Height), GraphicsUnit.Pixel);
            }

            if (px >= 6f)
            {
                using (var grid = new Pen(Color.FromArgb(46, 0, 0, 0)))
                    for (int i = 1; i < n; i++)
                    {
                        float o = i * px;
                        g.DrawLine(grid, view.X + o, view.Y, view.X + o, view.Bottom);
                        g.DrawLine(grid, view.X, view.Y + o, view.Right, view.Y + o);
                    }
            }

            // target cell
            var cell = new RectangleF(view.X + half * px, view.Y + half * px, px, px);
            using (var pw = new Pen(Color.White, Math.Max(1f, 2f * S)))
            using (var pk = new Pen(Color.Black, Math.Max(1f, 2f * S)))
            {
                g.DrawRectangle(pk, cell.X - 1, cell.Y - 1, cell.Width + 2, cell.Height + 2);
                g.DrawRectangle(pw, cell.X, cell.Y, cell.Width, cell.Height);
            }

            Color picked = PixelAt(cur);
            using (var b = new SolidBrush(picked)) g.FillRectangle(b, info);
            using (var pen = new Pen(Color.FromArgb(235, 20, 20, 24), Math.Max(1f, 2f * S)))
                g.DrawRectangle(pen, outer.X, outer.Y, outer.Width - 1, outer.Height - 1);
            using (var pen = new Pen(Color.FromArgb(120, 255, 255, 255), 1f))
                g.DrawRectangle(pen, outer.X + 1, outer.Y + 1, outer.Width - 3, outer.Height - 3);

            using (var b = new SolidBrush(Col.Ink(picked)))
            {
                var sf = new StringFormat();
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.DrawString(Col.Hex(picked) + "   " + cur.X + "," + cur.Y, infoFont, b, info, sf);
                g.SmoothingMode = SmoothingMode.None;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { infoFont.Dispose(); hintFont.Dispose(); }
            base.Dispose(disposing);
        }
    }

    class MainForm : Form
    {
        const int HOTKEY_ID = 0xBC01;
        const int WM_HOTKEY = 0x0312;
        const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2;
        const int MAX_HISTORY = 12;

        static readonly Color BgDark = Color.FromArgb(30, 31, 36);
        static readonly Color BgCtl = Color.FromArgb(44, 46, 54);
        static readonly Color BgBtn = Color.FromArgb(56, 59, 70);
        static readonly Color Accent = Color.FromArgb(88, 130, 220);
        static readonly Color Fg = Color.FromArgb(232, 232, 238);
        static readonly Color FgDim = Color.FromArgb(150, 152, 162);
        const string IdleHint = "Ctrl+Alt+P picks from anywhere on screen.";

        readonly float S;
        Color current = Color.FromArgb(255, 209, 102);
        readonly List<Color> history = new List<Color>();
        int zoom = 12;
        bool picking, startPick, hotkeyOk;
        bool pendingAuto = true, pendingTop, pendingHide = true;

        Panel swatch, histPanel;
        TextBox txHex, txRgb, txHsl;
        CheckBox cbAuto, cbTop, cbHide;
        Label lbStatus;
        System.Windows.Forms.Timer statusTimer;
        readonly string settingsPath;

        int Sc(double v) { return (int)Math.Round(v * S); }

        public MainForm(bool pickOnStart)
        {
            S = Native.ScaleFactor();
            startPick = pickOnStart;
            settingsPath = Path.Combine(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BitChroma"),
                "settings.ini");
            LoadSettings();
            BuildUi();
            ApplyColor(current, false, false);
        }

        void BuildUi()
        {
            Text = "BitChroma";
            BackColor = BgDark;
            ForeColor = Fg;
            Font = new Font("Segoe UI", 12f * S, GraphicsUnit.Pixel);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(Sc(340), Sc(378));
            KeyPreview = true;
            Icon = MakeIcon();

            int pad = Sc(12), w = Sc(316);

            swatch = new Panel();
            swatch.Bounds = new Rectangle(pad, pad, w, Sc(76));
            swatch.Cursor = Cursors.Hand;
            swatch.Paint += PaintSwatch;
            swatch.Click += delegate { Copy(Col.Hex(current)); };
            Controls.Add(swatch);

            int y = Sc(98);
            txHex = AddRow("HEX", y, false); y += Sc(32);
            txRgb = AddRow("RGB", y, true); y += Sc(32);
            txHsl = AddRow("HSL", y, true); y += Sc(38);

            var bPick = new Button();
            bPick.Text = "Pick color   (Ctrl+Alt+P)";
            bPick.Bounds = new Rectangle(pad, y, Sc(200), Sc(34));
            bPick.FlatStyle = FlatStyle.Flat;
            bPick.BackColor = Accent;
            bPick.ForeColor = Color.White;
            bPick.TabStop = false;
            bPick.FlatAppearance.BorderSize = 0;
            bPick.Click += delegate { StartPick(); };
            Controls.Add(bPick);

            var bTune = MakeButton("Fine-tune...", new Rectangle(pad + Sc(208), y, Sc(108), Sc(34)));
            bTune.Click += delegate { FineTune(); };
            Controls.Add(bTune);

            y += Sc(44);
            cbAuto = AddCheck("Auto-copy hex on pick", pad, y, Sc(180));
            cbTop = AddCheck("Always on top", pad + Sc(180), y, Sc(136));
            y += Sc(24);
            cbHide = AddCheck("Hide this window while picking", pad, y, Sc(240));
            cbTop.CheckedChanged += delegate { TopMost = cbTop.Checked; };

            y += Sc(28);
            var lbRecent = new Label();
            lbRecent.Text = "RECENT";
            lbRecent.Bounds = new Rectangle(pad, y, Sc(120), Sc(16));
            lbRecent.ForeColor = FgDim;
            lbRecent.Font = new Font("Segoe UI", 10f * S, FontStyle.Bold, GraphicsUnit.Pixel);
            Controls.Add(lbRecent);

            y += Sc(20);
            histPanel = new Panel();
            histPanel.Bounds = new Rectangle(pad, y, w, Sc(26));
            Controls.Add(histPanel);

            y += Sc(32);
            lbStatus = new Label();
            lbStatus.Bounds = new Rectangle(pad, y, w, Sc(18));
            lbStatus.ForeColor = FgDim;
            lbStatus.Text = IdleHint;
            Controls.Add(lbStatus);

            statusTimer = new System.Windows.Forms.Timer();
            statusTimer.Interval = 2200;
            statusTimer.Tick += delegate
            {
                statusTimer.Stop();
                lbStatus.ForeColor = FgDim;
                lbStatus.Text = IdleHint;
            };

            RebuildHistory();
        }

        Button MakeButton(string text, Rectangle b)
        {
            var btn = new Button();
            btn.Text = text;
            btn.Bounds = b;
            btn.FlatStyle = FlatStyle.Flat;
            btn.BackColor = BgBtn;
            btn.ForeColor = Fg;
            btn.TabStop = false;
            btn.FlatAppearance.BorderSize = 0;
            btn.FlatAppearance.MouseOverBackColor = Color.FromArgb(72, 76, 90);
            return btn;
        }

        CheckBox AddCheck(string text, int x, int y, int w)
        {
            var cb = new CheckBox();
            cb.Text = text;
            cb.Bounds = new Rectangle(x, y, w, Sc(20));
            cb.ForeColor = Fg;
            cb.FlatStyle = FlatStyle.Flat;
            cb.TabStop = false;
            Controls.Add(cb);
            return cb;
        }

        TextBox AddRow(string label, int y, bool readOnly)
        {
            int pad = Sc(12);
            var lb = new Label();
            lb.Text = label;
            lb.Bounds = new Rectangle(pad, y + Sc(6), Sc(38), Sc(18));
            lb.ForeColor = FgDim;
            lb.Font = new Font("Segoe UI", 10f * S, FontStyle.Bold, GraphicsUnit.Pixel);
            Controls.Add(lb);

            var tb = new TextBox();
            tb.Bounds = new Rectangle(pad + Sc(42), y, Sc(196), Sc(26));
            tb.BackColor = BgCtl;
            tb.ForeColor = Fg;
            tb.BorderStyle = BorderStyle.FixedSingle;
            tb.ReadOnly = readOnly;
            tb.Font = new Font("Consolas", 14f * S, GraphicsUnit.Pixel);
            tb.Enter += delegate { tb.SelectAll(); };
            Controls.Add(tb);

            var btn = MakeButton("Copy", new Rectangle(pad + Sc(244), y, Sc(72), Sc(26)));
            btn.Click += delegate { Copy(tb.Text); };
            Controls.Add(btn);
            return tb;
        }

        void PaintSwatch(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Rectangle r = swatch.ClientRectangle;
            using (var b = new SolidBrush(current)) g.FillRectangle(b, r);
            using (var p = new Pen(Color.FromArgb(70, 255, 255, 255)))
                g.DrawRectangle(p, 0, 0, r.Width - 1, r.Height - 1);
            using (var b = new SolidBrush(Col.Ink(current)))
            using (var f = new Font("Segoe UI", 30f * S, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var fs = new Font("Segoe UI", 11f * S, GraphicsUnit.Pixel))
            {
                var sf = new StringFormat();
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                g.DrawString(Col.Hex(current), f, b, new RectangleF(0, 0, r.Width, r.Height - Sc(14)), sf);
                g.DrawString("click to copy", fs, b, new RectangleF(0, r.Height - Sc(20), r.Width, Sc(16)), sf);
            }
        }

        void RebuildHistory()
        {
            histPanel.Controls.Clear();
            int size = Sc(24), gap = Sc(4);
            for (int i = 0; i < history.Count && i < MAX_HISTORY; i++)
            {
                Color captured = history[i];
                var p = new Panel();
                p.Bounds = new Rectangle(i * (size + gap), 0, size, size);
                p.BackColor = captured;
                p.Cursor = Cursors.Hand;
                p.BorderStyle = BorderStyle.FixedSingle;
                p.Click += delegate { ApplyColor(captured, false, cbAuto.Checked); };
                var tip = new ToolTip();
                tip.SetToolTip(p, Col.Hex(captured));
                histPanel.Controls.Add(p);
            }
        }

        void ApplyColor(Color c, bool addToHistory, bool copyHex)
        {
            current = c;
            txHex.Text = Col.Hex(c);
            txRgb.Text = Col.Rgb(c);
            txHsl.Text = Col.Hsl(c);
            swatch.Invalidate();

            if (addToHistory)
            {
                history.RemoveAll(delegate (Color h) { return h.ToArgb() == c.ToArgb(); });
                history.Insert(0, c);
                while (history.Count > MAX_HISTORY) history.RemoveAt(history.Count - 1);
                RebuildHistory();
            }
            if (copyHex) Copy(Col.Hex(c));
        }

        void Copy(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            for (int i = 0; i < 5; i++)
            {
                try { Clipboard.SetText(text); Status("Copied  " + text, true); return; }
                catch { Thread.Sleep(40); }
            }
            Status("Clipboard busy - try again", false);
        }

        void Status(string msg, bool good)
        {
            lbStatus.Text = msg;
            lbStatus.ForeColor = good ? Color.FromArgb(126, 208, 140) : Color.FromArgb(226, 130, 130);
            statusTimer.Stop();
            statusTimer.Start();
        }

        void StartPick()
        {
            if (picking) return;
            picking = true;
            bool hidden = cbHide.Checked;
            try
            {
                if (hidden)
                {
                    Opacity = 0;
                    Application.DoEvents();
                    Thread.Sleep(90);   // let the compositor drop our window before the snapshot
                }

                Rectangle vb = SystemInformation.VirtualScreen;
                using (Bitmap shot = GrabScreen(vb))
                using (var ov = new PickOverlay(shot, vb, S, zoom))
                {
                    ov.ShowDialog();
                    zoom = ov.Zoom;
                    if (ov.Picked)
                    {
                        Opacity = 1;
                        ApplyColor(ov.Result, true, cbAuto.Checked);
                    }
                }
            }
            finally
            {
                Opacity = 1;
                picking = false;
                try { Activate(); } catch { }
            }
        }

        static Bitmap GrabScreen(Rectangle vb)
        {
            var bmp = new Bitmap(vb.Width, vb.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
                g.CopyFromScreen(vb.Left, vb.Top, 0, 0, vb.Size, CopyPixelOperation.SourceCopy);
            return bmp;
        }

        void FineTune()
        {
            using (var dlg = new ColorDialog())
            {
                dlg.Color = current;          // seeded with the pixel you just picked
                dlg.FullOpen = true;
                dlg.AnyColor = true;
                var custom = new int[16];
                for (int i = 0; i < custom.Length; i++)
                {
                    Color c = i < history.Count ? history[i] : current;
                    custom[i] = c.R | (c.G << 8) | (c.B << 16);
                }
                dlg.CustomColors = custom;
                if (dlg.ShowDialog(this) == DialogResult.OK)
                    ApplyColor(dlg.Color, true, cbAuto.Checked);
            }
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            cbAuto.Checked = pendingAuto;
            cbTop.Checked = pendingTop;
            cbHide.Checked = pendingHide;
            TopMost = pendingTop;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            hotkeyOk = Native.RegisterHotKey(Handle, HOTKEY_ID, MOD_CONTROL | MOD_ALT, 0x50 /* P */);
            if (!hotkeyOk) Status("Ctrl+Alt+P is taken by another app", false);
            if (startPick) { startPick = false; BeginInvoke(new MethodInvoker(StartPick)); }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Control && e.KeyCode == Keys.C && !(ActiveControl is TextBox))
            {
                Copy(Col.Hex(current));
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.F2)
            {
                StartPick();
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Enter && ActiveControl == txHex)
            {
                Color c;
                if (Col.TryParse(txHex.Text, out c)) ApplyColor(c, true, false);
                else Status("Not a valid hex color", false);
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Escape)
            {
                Close();
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HOTKEY_ID) { StartPick(); return; }
            base.WndProc(ref m);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (hotkeyOk) Native.UnregisterHotKey(Handle, HOTKEY_ID);
            SaveSettings();
            base.OnFormClosing(e);
        }

        void LoadSettings()
        {
            var vals = new Dictionary<string, string>();
            try
            {
                if (File.Exists(settingsPath))
                {
                    foreach (string line in File.ReadAllLines(settingsPath))
                    {
                        int ix = line.IndexOf('=');
                        if (ix > 0) vals[line.Substring(0, ix).Trim()] = line.Substring(ix + 1).Trim();
                    }
                }
            }
            catch { }

            pendingAuto = !vals.ContainsKey("autocopy") || vals["autocopy"] == "1";
            pendingTop = vals.ContainsKey("ontop") && vals["ontop"] == "1";
            pendingHide = !vals.ContainsKey("hide") || vals["hide"] == "1";
            if (vals.ContainsKey("zoom")) int.TryParse(vals["zoom"], out zoom);
            if (zoom < 4) zoom = 12;
            if (vals.ContainsKey("current"))
            {
                Color c;
                if (Col.TryParse(vals["current"], out c)) current = c;
            }
            if (vals.ContainsKey("history"))
            {
                foreach (string h in vals["history"].Split(','))
                {
                    Color c;
                    if (Col.TryParse(h, out c)) history.Add(c);
                }
            }
        }

        void SaveSettings()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(settingsPath));
                var sb = new StringBuilder();
                sb.AppendLine("autocopy=" + (cbAuto.Checked ? "1" : "0"));
                sb.AppendLine("ontop=" + (cbTop.Checked ? "1" : "0"));
                sb.AppendLine("hide=" + (cbHide.Checked ? "1" : "0"));
                sb.AppendLine("zoom=" + zoom);
                sb.AppendLine("current=" + Col.Hex(current));
                var parts = new List<string>();
                foreach (Color c in history) parts.Add(Col.Hex(c));
                sb.AppendLine("history=" + string.Join(",", parts.ToArray()));
                File.WriteAllText(settingsPath, sb.ToString());
            }
            catch { }
        }

        static Icon MakeIcon()
        {
            using (var bmp = new Bitmap(32, 32))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    using (var b = new SolidBrush(Color.FromArgb(255, 209, 102))) g.FillRectangle(b, 2, 2, 14, 14);
                    using (var b = new SolidBrush(Color.FromArgb(88, 130, 220))) g.FillRectangle(b, 16, 2, 14, 14);
                    using (var b = new SolidBrush(Color.FromArgb(126, 208, 140))) g.FillRectangle(b, 2, 16, 14, 14);
                    using (var b = new SolidBrush(Color.FromArgb(226, 110, 130))) g.FillRectangle(b, 16, 16, 14, 14);
                    using (var p = new Pen(Color.FromArgb(235, 255, 255, 255), 2f)) g.DrawRectangle(p, 11, 11, 10, 10);
                }
                return Icon.FromHandle(bmp.GetHicon());
            }
        }
    }

    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            Native.MakeDpiAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            bool pick = args.Length > 0 && (args[0] == "-p" || args[0] == "--pick" || args[0] == "/p");
            Application.Run(new MainForm(pick));
        }
    }
}
