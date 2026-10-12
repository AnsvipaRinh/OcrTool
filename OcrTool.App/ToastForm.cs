using System;
using System.Drawing;
using System.Windows.Forms;

namespace OcrTool.App;

internal sealed class ToastForm : Form
{
    private readonly System.Windows.Forms.Timer _timer;
    private readonly Label _titleLabel;
    private readonly Label _messageLabel;

    public ToastForm(string title, string message)
    {
        _titleLabel = new Label
        {
            Text = title,
            Font = new Font("Segoe UI", 10, FontStyle.Bold),
            ForeColor = Color.White,
            AutoSize = true,
            Location = new Point(12, 10)
        };

        _messageLabel = new Label
        {
            Text = message,
            Font = new Font("Segoe UI", 9),
            ForeColor = Color.LightGray,
            AutoSize = true,
            MaximumSize = new Size(280, 0),
            Location = new Point(12, 35)
        };

        _timer = new System.Windows.Forms.Timer { Interval = 2500 };
        _timer.Tick += (s, e) => Close();

        InitializeComponent();
    }

    private void InitializeComponent()
    {
        Size = new Size(320, 80);
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(40, 40, 40);
        
        var screen = Screen.PrimaryScreen;
        var x = screen.WorkingArea.Right - Width - 20;
        var y = screen.WorkingArea.Bottom - Height - 20;
        Location = new Point(x, y);

        Controls.Add(_titleLabel);
        Controls.Add(_messageLabel);

        _timer.Start();

        Paint += (s, e) =>
        {
            using var pen = new Pen(Color.DodgerBlue, 2);
            e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        };
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _timer.Start();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer?.Dispose();
        }
        base.Dispose(disposing);
    }
}