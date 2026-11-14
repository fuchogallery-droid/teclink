using System.Drawing.Drawing2D;
using TecLink.Core.Protocol;

namespace TecLink.App;

/// <summary>Full-screen window that shows the other PC's screen and controls it:
/// the mouse inside the picture and the whole keyboard go to the peer. Files and
/// folders dropped on it are sent to the peer.</summary>
public sealed class RemoteScreenForm : Form
{
    private sealed class Canvas : Control
    {
        public Canvas() { DoubleBuffered = true; ResizeRedraw = true; }
    }

    private readonly EdgeBridge _bridge;
    private readonly Canvas _canvas = new() { Dock = DockStyle.Fill, BackColor = Color.FromArgb(8, 8, 8), AllowDrop = true };
    private readonly ComboBox _displays = new() { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, Width = 190, TabStop = false };
    private readonly Label _title = new() { AutoSize = true, ForeColor = Color.FromArgb(237, 237, 237), Margin = new Padding(12, 9, 16, 0) };
    private readonly Cursor _dot;
    private Bitmap? _frame;
    private Rectangle _source; // the shown monitor in the peer's wire coordinates
    private int _display;
    private bool _closed;

    private readonly bool _local;               // showing one of THIS PC's screens (view only)
    private readonly ScreenShareHost? _localHost;
    private readonly System.Windows.Forms.Timer _barTimer = new() { Interval = 120 };
    private readonly FlowLayoutPanel _bar;

    /// <param name="local">true = one of this PC's own screens; false = a screen of the other PC (controllable).</param>
    /// <param name="screenId">The monitor to show (its id); null = the first.</param>
    /// <param name="target">The monitor of THIS PC the window covers; null = the one with the mouse.</param>
    public RemoteScreenForm(EdgeBridge bridge, bool local = false, string? screenId = null, Screen? target = null)
    {
        _bridge = bridge; _local = local;
        if (local) _localHost = new ScreenShareHost();
        Text = local ? "TecLink — pantalla completa" : "TecLink — pantalla de " + bridge.RemoteName;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = (target ?? Screen.FromPoint(Cursor.Position)).Bounds;
        BackColor = Color.FromArgb(18, 18, 18);
        Font = new Font("Segoe UI", 9.5f);
        ShowInTaskbar = true;
        _dot = CreateDotCursor();

        // The picture owns the whole monitor; the bar floats over it and only shows when the mouse touches the top edge.
        var bar = _bar = new FlowLayoutPanel { Left = 0, Top = 0, Width = Width, Height = LogicalToDeviceUnits(44), BackColor = Color.FromArgb(18, 18, 18), WrapContents = false, Padding = new Padding(0, LogicalToDeviceUnits(3), 0, 0), Visible = false, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
        var remote = local ? Screen.AllScreens.Select((s, n) => new TecLink.Core.DisplayInfo(s.DeviceName, s.Bounds.Left, s.Bounds.Top, s.Bounds.Width, s.Bounds.Height)).ToList() : bridge.RemoteDisplays.ToList();
        _display = Math.Max(0, remote.FindIndex(d => d.Id == screenId));
        _title.Text = local ? "Pantalla completa" : "Pantalla de " + bridge.RemoteName;
        bar.Controls.Add(_title);
        if (remote.Count > 1)
        {
            for (var n = 0; n < remote.Count; n++) _displays.Items.Add($"Pantalla {n + 1} · {remote[n].Width} × {remote[n].Height}");
            _displays.SelectedIndex = _display;
            _displays.BackColor = Color.FromArgb(38, 49, 42);
            _displays.ForeColor = Color.FromArgb(237, 237, 237);
            _displays.Margin = new Padding(0, 4, 12, 0);
            _displays.SelectedIndexChanged += (_, _) => { _display = _displays.SelectedIndex; Request(); _canvas.Focus(); };
            bar.Controls.Add(_displays);
        }
        if (!local)
        {
            bar.Controls.Add(BarButton("Enviar archivos…", (_, _) => PickFiles()));
            bar.Controls.Add(BarButton("Enviar carpeta…", (_, _) => PickFolder()));
        }
        bar.Controls.Add(BarButton(local ? "Salir de pantalla completa" : "Salir   Ctrl + Alt + Espacio", (_, _) => Close(), accent: true));
        var hint = new Label { AutoSize = true, ForeColor = Color.FromArgb(179, 189, 183), Margin = new Padding(8, 9, 0, 0), Text = local ? "Solo se ve: para trabajar en ella, usa su monitor." : "También puedes soltar archivos o carpetas sobre la pantalla." };
        bar.Controls.Add(hint);

        _canvas.Dock = DockStyle.Fill;
        Controls.Add(bar);
        Controls.Add(_canvas);
        bar.BringToFront();
        _barTimer.Tick += (_, _) =>
        {
            var y = PointToClient(Cursor.Position).Y; var inside = ClientRectangle.Contains(PointToClient(Cursor.Position));
            if (inside && y <= 3 && !bar.Visible) { bar.Visible = true; bar.BringToFront(); }
            else if (bar.Visible && (!inside || y > bar.Height + 12) && !_displays.DroppedDown) bar.Visible = false;
        };
        _barTimer.Start();
        // This window must never appear inside the live pictures of the room.
        HandleCreated += (_, _) => TecLink.App.Native.Win32.SetWindowDisplayAffinity(Handle, TecLink.App.Native.Win32.WDA_EXCLUDEFROMCAPTURE);

        _canvas.Paint += OnPaint;
        if (!local)
        {
            _canvas.MouseMove += (_, e) => { if (Map(e.Location, out var p)) _bridge.ViewerMove(p.X, p.Y); };
            _canvas.MouseDown += (_, e) => Button(e, true);
            _canvas.MouseUp += (_, e) => Button(e, false);
            _canvas.MouseWheel += (_, e) => _bridge.ViewerWheel(e.Delta);
        }
        KeyPreview = true;
        KeyDown += (_, e) => { if (local && e.KeyCode == Keys.Escape) Close(); };
        _canvas.DragEnter += (_, e) => e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
        _canvas.DragDrop += (_, e) => { if (e.Data?.GetData(DataFormats.FileDrop) is string[] paths) _ = _bridge.Files.SendAsync(paths); };

        if (!local) { _bridge.ScreenFrameReceived += OnFrame; _bridge.ViewerCloseRequested += OnCloseRequested; }
        Shown += (_, _) => { Request(); if (!local) _bridge.SetViewInput(true); _canvas.Focus(); };
        if (!local) { Activated += (_, _) => _bridge.SetViewInput(true); Deactivate += (_, _) => _bridge.SetViewInput(false); }
        // The stream is sized to the real monitor: if this window changes size, ask again so the picture stays sharp.
        Resize += (_, _) => { if (IsHandleCreated && Visible) Request(); };
    }

    private Button BarButton(string text, EventHandler click, bool accent = false)
    {
        var button = new Button
        {
            Text = text, AutoSize = true, FlatStyle = FlatStyle.Flat, TabStop = false, Margin = new Padding(0, 2, 8, 0),
            BackColor = accent ? Color.FromArgb(0, 255, 102) : Color.FromArgb(38, 49, 42),
            ForeColor = accent ? Color.FromArgb(8, 8, 8) : Color.FromArgb(237, 237, 237),
            Padding = new Padding(8, 2, 8, 2),
        };
        button.FlatAppearance.BorderColor = accent ? Color.FromArgb(0, 255, 102) : Color.FromArgb(85, 95, 87);
        button.Click += click;
        return button;
    }

    private void Request()
    {
        if (!_local) { _bridge.StartViewing(_display, _canvas.ClientSize.Width, _canvas.ClientSize.Height); return; }
        _localHost!.Start(_display, _canvas.ClientSize.Width, _canvas.ClientSize.Height, message => { OnFrame(message); return Task.CompletedTask; });
    }

    private void PickFiles()
    {
        using var dialog = new OpenFileDialog { Multiselect = true, Title = "Enviar archivos a " + _bridge.RemoteName };
        if (dialog.ShowDialog(this) == DialogResult.OK) _ = _bridge.Files.SendAsync(dialog.FileNames);
    }

    private void PickFolder()
    {
        using var dialog = new FolderBrowserDialog { Description = "Enviar una carpeta completa a " + _bridge.RemoteName, UseDescriptionForTitle = true };
        if (dialog.ShowDialog(this) == DialogResult.OK) _ = _bridge.Files.SendAsync(new[] { dialog.SelectedPath });
    }

    // Network thread: decode here, hand the finished bitmap to the UI thread.
    private void OnFrame(WireMessage message)
    {
        if (_closed) return;
        Bitmap bitmap;
        try
        {
            using var stream = new MemoryStream(message.Data);
            using var decoded = Image.FromStream(stream);
            bitmap = new Bitmap(decoded);
        }
        catch (ArgumentException) { AckFrame(); return; }
        AckFrame();
        try
        {
            BeginInvoke(() =>
            {
                if (_closed || message.Index != _display) { bitmap.Dispose(); return; }
                _frame?.Dispose();
                _frame = bitmap;
                _source = new Rectangle(message.Left, message.Top, message.Width, message.Height);
                _canvas.Invalidate();
            });
        }
        catch (InvalidOperationException) { bitmap.Dispose(); }
    }

    private void AckFrame() { if (_local) _localHost?.Ack(); else _bridge.AckFrame(); }

    private Rectangle Picture()
    {
        if (_frame is null) return Rectangle.Empty;
        var area = _canvas.ClientSize;
        var ratio = Math.Min((double)area.Width / _frame.Width, (double)area.Height / _frame.Height);
        var size = new Size((int)(_frame.Width * ratio), (int)(_frame.Height * ratio));
        return new Rectangle((area.Width - size.Width) / 2, (area.Height - size.Height) / 2, size.Width, size.Height);
    }

    private void OnPaint(object? sender, PaintEventArgs e)
    {
        if (_frame is null)
        {
            TextRenderer.DrawText(e.Graphics, _bridge.CanUseExtras ? $"Conectando con la pantalla de {_bridge.RemoteName}…" : "La otra PC no está conectada.",
                Font, _canvas.ClientRectangle, Color.FromArgb(179, 189, 183), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }
        var picture = Picture();
        e.Graphics.InterpolationMode = picture.Size == _frame.Size ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
        e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        e.Graphics.DrawImage(_frame, picture);
        _canvas.Cursor = _local ? Cursors.Default : _dot;
    }

    private bool Map(Point local, out Point wire)
    {
        wire = Point.Empty;
        var picture = Picture();
        if (picture.Width <= 0 || picture.Height <= 0 || _source.Width <= 0) return false;
        var x = Math.Clamp(local.X - picture.Left, 0, picture.Width - 1);
        var y = Math.Clamp(local.Y - picture.Top, 0, picture.Height - 1);
        wire = new Point(_source.Left + (int)((long)x * _source.Width / picture.Width), _source.Top + (int)((long)y * _source.Height / picture.Height));
        return true;
    }

    private void Button(MouseEventArgs e, bool down)
    {
        var kind = e.Button switch
        {
            MouseButtons.Left => MouseButtonKind.Left,
            MouseButtons.Right => MouseButtonKind.Right,
            MouseButtons.Middle => MouseButtonKind.Middle,
            _ => (MouseButtonKind)0,
        };
        if (kind == 0) return;
        if (Map(e.Location, out var p)) _bridge.ViewerMove(p.X, p.Y);
        _bridge.ViewerButton(kind, down);
    }

    private void OnCloseRequested() { if (!_closed) Close(); }

    private static Cursor CreateDotCursor()
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var ring = new Pen(Color.Black, 1.5f);
            g.FillEllipse(Brushes.White, 13, 13, 6, 6);
            g.DrawEllipse(ring, 13, 13, 6, 6);
        }
        return new Cursor(bitmap.GetHicon());
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _closed = true;
        _barTimer.Stop(); _barTimer.Dispose();
        if (_local) _localHost?.Dispose();
        else
        {
            _bridge.ScreenFrameReceived -= OnFrame;
            _bridge.ViewerCloseRequested -= OnCloseRequested;
            _bridge.StopViewing();
        }
        _frame?.Dispose();
        _frame = null;
        base.OnFormClosed(e);
    }
}
