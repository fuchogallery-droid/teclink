using System.Windows.Forms;

namespace TecLink.App;

public sealed class SetupForm : Form
{
    public AppConfig Result { get; private set; } = new();

    private readonly TextBox _nameBox = new();
    private readonly RadioButton _roleServer = new() { Text = "Servidor (esta PC espera conexión)" };
    private readonly RadioButton _roleClient = new() { Text = "Cliente (esta PC se conecta a otra)" };
    private readonly TextBox _hostBox = new();
    private readonly TextBox _portBox = new();
    private readonly RadioButton _edgeRight = new() { Text = "Derecha (el mouse sale por mi borde derecho hacia la otra PC)" };
    private readonly RadioButton _edgeLeft = new() { Text = "Izquierda (el mouse sale por mi borde izquierdo hacia la otra PC)" };

    public SetupForm()
    {
        Text = "TecLink — configuración";
        Width = 460;
        Height = 420;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;

        var y = 15;
        AddLabel("Nombre de esta PC:", y); y += 20;
        _nameBox.SetBounds(15, y, 400, 24);
        _nameBox.Text = Environment.MachineName;
        Controls.Add(_nameBox); y += 40;

        AddLabel("Rol:", y); y += 20;
        _roleServer.SetBounds(15, y, 420, 24); _roleServer.Checked = true; Controls.Add(_roleServer); y += 26;
        _roleClient.SetBounds(15, y, 420, 24); Controls.Add(_roleClient); y += 36;

        AddLabel("IP de la otra PC (solo si esta PC es Cliente):", y); y += 20;
        _hostBox.SetBounds(15, y, 400, 24);
        Controls.Add(_hostBox); y += 40;

        AddLabel("Puerto:", y); y += 20;
        _portBox.SetBounds(15, y, 120, 24);
        _portBox.Text = Core.Networking.PeerServer.DefaultPort.ToString();
        Controls.Add(_portBox); y += 40;

        AddLabel("¿Por dónde sale el mouse hacia la otra PC?", y); y += 20;
        _edgeRight.SetBounds(15, y, 420, 24); _edgeRight.Checked = true; Controls.Add(_edgeRight); y += 26;
        _edgeLeft.SetBounds(15, y, 420, 24); Controls.Add(_edgeLeft); y += 40;

        var okButton = new Button { Text = "Guardar y conectar", DialogResult = DialogResult.OK };
        okButton.SetBounds(15, y, 180, 30);
        okButton.Click += (_, _) => SaveAndClose();
        Controls.Add(okButton);

        AcceptButton = okButton;
    }

    private void AddLabel(string text, int y)
    {
        var label = new Label { Text = text };
        label.SetBounds(15, y, 420, 20);
        Controls.Add(label);
    }

    private void SaveAndClose()
    {
        if (!int.TryParse(_portBox.Text, out var port)) port = Core.Networking.PeerServer.DefaultPort;

        Result = new AppConfig
        {
            MachineName = string.IsNullOrWhiteSpace(_nameBox.Text) ? Environment.MachineName : _nameBox.Text,
            Role = _roleServer.Checked ? PeerRole.Server : PeerRole.Client,
            PeerHost = _hostBox.Text.Trim(),
            Port = port,
            EdgeToPeer = _edgeRight.Checked ? EdgeDirection.Right : EdgeDirection.Left,
        };
        Result.Save();
        DialogResult = DialogResult.OK;
        Close();
    }
}
