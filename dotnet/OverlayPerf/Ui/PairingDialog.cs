using OverlayPerf.Server;
using QRCoder;

namespace OverlayPerf.Ui;

/// <summary>Fenetre minimaliste d'appairage : QR code a scanner et adresses en clair.</summary>
public sealed class PairingDialog : Form
{
    public PairingDialog(PairingSummary summary)
    {
        Text = "Appairer un telephone";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 10f);
        Padding = new Padding(16);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        ShowInTaskbar = false;
        TopMost = true;

        var layout = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
        };
        Controls.Add(layout);

        var qr = new PictureBox { Size = new Size(320, 320), SizeMode = PictureBoxSizeMode.Zoom, Margin = new Padding(0, 0, 0, 12) };
        qr.Image = RenderQr(summary.PrimaryUrl, 320);
        layout.Controls.Add(qr);

        var intro = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(320, 0),
            Text = summary.RemoteUrl.Length > 0
                ? "Scannez ce code depuis le telephone, depuis n'importe quel reseau :"
                : "Scannez ce code depuis le telephone, sur le meme reseau local :",
            Margin = new Padding(0, 0, 0, 6),
        };
        layout.Controls.Add(intro);

        var address = new TextBox
        {
            ReadOnly = true,
            Text = summary.PrimaryUrl,
            Width = 320,
            Margin = new Padding(0, 0, 0, 6),
        };
        layout.Controls.Add(address);

        if (summary.RemoteUrl.Length > 0)
        {
            layout.Controls.Add(new Label
            {
                AutoSize = true, MaximumSize = new Size(320, 0), Margin = new Padding(0, 0, 0, 6),
                Text = $"Sur place, l'adresse locale evite le detour par Internet :\n{summary.LocalUrl}",
            });
        }
        else if (summary.Urls.Count > 1)
        {
            layout.Controls.Add(new Label
            {
                AutoSize = true, MaximumSize = new Size(320, 0), Margin = new Padding(0, 0, 0, 6),
                Text = "Autres adresses possibles :\n" + string.Join("\n", summary.Urls.Skip(1)),
            });
        }

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
        var copy = new Button { Text = "Copier l'adresse", AutoSize = true };
        copy.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(summary.PrimaryUrl);
                copy.Text = "Copiee !";
            }
            catch (Exception)
            {
                copy.Text = "Presse-papiers indisponible";
            }
        };
        var close = new Button { Text = "Fermer", AutoSize = true, DialogResult = DialogResult.OK };
        buttons.Controls.Add(copy);
        buttons.Controls.Add(close);
        layout.Controls.Add(buttons);

        var warning = new Label
        {
            AutoSize = true, MaximumSize = new Size(320, 0), ForeColor = Color.DarkOrange, Margin = new Padding(0, 10, 0, 0),
            Text = "Ce lien contient le jeton d'acces a la telemetrie de la machine : ne le diffusez pas.",
        };
        layout.Controls.Add(warning);
        AcceptButton = close;
    }

    private static Bitmap RenderQr(string text, int size)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var modules = data.ModuleMatrix;
        var count = modules.Count;
        var scale = Math.Max(1, size / (count + 2));
        var bitmap = new Bitmap((count + 2) * scale, (count + 2) * scale);
        using var g = Graphics.FromImage(bitmap);
        g.Clear(Color.White);
        using var brush = new SolidBrush(Color.Black);
        for (var y = 0; y < count; y++)
        {
            for (var x = 0; x < count; x++)
            {
                if (modules[y][x])
                {
                    g.FillRectangle(brush, (x + 1) * scale, (y + 1) * scale, scale, scale);
                }
            }
        }
        return bitmap;
    }
}
