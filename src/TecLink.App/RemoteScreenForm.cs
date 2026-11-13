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

    public RemoteScreenForm(EdgeBridge bridge)
    {
        _bridge = bridge;
        Text = "TecLink — pantalla de " + bridge.RemoteName;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = Screen.FromPoint(Cursor.Position).Bounds;
        BackColor = Color.FromArgb(18, 18, 18);
        Font = new Font("Segoe UI", 9.5f);
        ShowInTaskbar = true;
        _dot = CreateDotCursor();

        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = LogicalToDeviceUnits(40), BackColor = Color.FromArgb(18, 18, 18), WrapContents = false, Padding = new Padding(0, LogicalToDeviceUnits(3), 0, 0) };
        _title.Text = "Pantalla de " + bridge.RemoteName;
        bar.Controls.Add(_title);
        var remote = bridge.RemoteDisplays;
        if (remote.Count > 1)
        {
            for (var n = 0; n < remote.Count; n++) _displays.Items.Add($"Pantalla {n + 1} · {remote[n].Width} × {remote[n].Height}");
            _displays.SelectedIndex = 0;
            _displays.BackColor = Color.FromArgb(38, 49, 42);
            _displays.ForeColor = Color.FromArgb(237, 237, 237);
            _displays.Margin = new Padding(0, 4, 12, 0);
            _displays.SelectedIndexChanged += (_, _) => { _display = _displays.SelectedIndex; Request(); _canvas.Focus(); };
            bar.Controls.Add(_displays);
        }
        bar.Controls.Add(BarButton("Enviar archivos…", (_, _) => PickFiles()));
        bar.Controls.Add(BarButton("Enviar carpeta…", (_, _) => PickFolder()));
        bar.Controls.Add(BarButton("Salir   Ctrl + Alt + Espacio", (_, _) => Close(), accent: true));
        var hint = new Label { AutoSize = true, ForeColor = Color.FromArgb(179, 189, 183), Margin = new Padding(8, 9, 0, 0), Text = "También puedes soltar archivos o carpetas sobre la pantalla." };
        bar.Controls.Add(hint);

        Controls.Add(_canvas);
        Controls.Add(bar);

        _canvas.Paint += OnPaint;
        _canvas.MouseMove += (_, e) => { if (Map(e.Location, out var p)) _bridge.ViewerMove(p.X, p.Y); };
        _canvas.MouseDown += (_, e) => Button(e, true);
        _canvas.MouseUp += (_, e) => Button(e, false);
        _canvas.MouseWheel += (_, e) => _bridge.ViewerWheel(e.Delta);
        _canvas.DragEnter += (_, e) => e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
        _canvas.DragDrop += (_, e) => { if (e.Data?.GetData(DataFormats.FileDrop) is string[] paths) _ = _bridge.Files.SendAsync(paths); };

        _bridge.ScreenFrameReceived += OnFrame;
        _bridge.ViewerCloseRequested += OnCloseRequested;
        Shown += (_, _) => { Request(); _bridge.SetViewInput(true); };
        Activated += (_, _) => _bridge.SetViewInput(true);
        Deactivate += (_, _) => _bridge.SetViewInput(false);
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

    private void Request() => _bridge.StartViewing(_display, _canvas.ClientSize.Width, _canvas.ClientSize.Height);

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
        catch (ArgumentException) { _bridge.AckFrame(); return; }
        _bridge.AckFrame();
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
        e.Graphics.InterpolationMode = picture.Size == _frame.Size ? InterpolationMode.NearestNeighbor : InterpolationMode.Bilinear;
        e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
        e.Graphics.DrawImage(_frame, picture);
        _canvas.Cursor = _dot;
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
        _bridge.ScreenFrameReceived -= OnFrame;
        _bridge.ViewerCloseRequested -= OnCloseRequested;
        _bridge.StopViewing();
        _frame?.Dispose();
        _frame = null;
        base.OnFormClosed(e);
    }
}
