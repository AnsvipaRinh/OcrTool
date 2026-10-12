using System;
using System.Drawing;
using System.Windows.Forms;

namespace OcrTool.App;

internal sealed class HotkeyDialog : Form
{
    private readonly TextBox _hotkeyBox;
    private Keys _currentKey;
    private uint _currentModifiers;

    public string SelectedHotkey { get; private set; } = "";

    public HotkeyDialog(string currentHotkey)
    {
        _hotkeyBox = new TextBox
        {
            ReadOnly = true,
            Font = new Font("Segoe UI", 10),
            Width = 250,
            TabStop = true
        };
        
        _hotkeyBox.KeyDown += OnKeyDown;
        _hotkeyBox.PreviewKeyDown += OnPreviewKeyDown;
        _hotkeyBox.Enter += (s, e) => _hotkeyBox.SelectAll();

        InitializeComponent(currentHotkey);
        ParseHotkey(currentHotkey);
        UpdateHotkeyText();
    }

    private void InitializeComponent(string currentHotkey)
    {
        Text = "Change Hotkey";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        BackColor = SystemColors.Control;

        const int margin = 16;
        const int gap = 12;

        var label = new Label
        {
            Text = "Press new hotkey combination:",
            AutoSize = true,
            Font = new Font("Segoe UI", 9),
            TextAlign = ContentAlignment.MiddleCenter
        };

        var okBtn = new Button
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            Size = new Size(80, 30)
        };
        okBtn.Click += (s, e) => SelectedHotkey = _hotkeyBox.Text;

        var cancelBtn = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Size = new Size(80, 30)
        };

        var buttonsWidth = okBtn.Width + gap + cancelBtn.Width;

        // every element is centred inside this common column.
        var columnWidth = Math.Max(
            Math.Max(label.Width, _hotkeyBox.Width),
            buttonsWidth);

        label.AutoSize = false;
        label.Size = new Size(columnWidth, label.Height);
        label.TextAlign = ContentAlignment.MiddleCenter;
        _hotkeyBox.TextAlign = HorizontalAlignment.Center;

        var labelY = margin;
        var boxY = labelY + label.Height + gap;
        var buttonsY = boxY + _hotkeyBox.Height + gap;

        var columnX = margin;
        label.Location = new Point(columnX, labelY);
        _hotkeyBox.Location = new Point(columnX + (columnWidth - _hotkeyBox.Width) / 2, boxY);

        var buttonsX = columnX + (columnWidth - buttonsWidth) / 2;
        okBtn.Location = new Point(buttonsX, buttonsY);
        cancelBtn.Location = new Point(buttonsX + okBtn.Width + gap, buttonsY);

        Controls.AddRange(new Control[] { label, _hotkeyBox, okBtn, cancelBtn });

        ClientSize = new Size(
            margin + columnWidth + margin,
            buttonsY + okBtn.Height + margin);

        AcceptButton = okBtn;
        CancelButton = cancelBtn;
    }

    private void ParseHotkey(string hotkeyString)
    {
        _currentModifiers = 0;
        _currentKey = Keys.None;

        var parts = hotkeyString.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var part in parts)
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    _currentModifiers |= 0x0002;
                    break;
                case "alt":
                    _currentModifiers |= 0x0001;
                    break;
                case "shift":
                    _currentModifiers |= 0x0004;
                    break;
                case "win":
                case "windows":
                    _currentModifiers |= 0x0008;
                    break;
                default:
                    if (Enum.TryParse<Keys>(part, true, out var k))
                    {
                        _currentKey = k;
                    }
                    break;
            }
        }
    }

    private void OnPreviewKeyDown(object? sender, PreviewKeyDownEventArgs e)
    {
        e.IsInputKey = true;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        e.SuppressKeyPress = true;
        e.Handled = true;

        _currentModifiers = 0;
        _currentKey = Keys.None;

        if (e.Control) _currentModifiers |= 0x0002;
        if (e.Alt) _currentModifiers |= 0x0001;
        if (e.Shift) _currentModifiers |= 0x0004;
        
        var keyCode = e.KeyCode;
        if (keyCode == Keys.ControlKey || keyCode == Keys.Menu || keyCode == Keys.ShiftKey || keyCode == Keys.LWin || keyCode == Keys.RWin)
        {
            return;
        }
        
        _currentKey = keyCode;
        UpdateHotkeyText();
    }

    private void UpdateHotkeyText()
    {
        var parts = new System.Collections.Generic.List<string>();
        
        if ((_currentModifiers & 0x0002) != 0) parts.Add("Ctrl");
        if ((_currentModifiers & 0x0001) != 0) parts.Add("Alt");
        if ((_currentModifiers & 0x0004) != 0) parts.Add("Shift");
        if ((_currentModifiers & 0x0008) != 0) parts.Add("Win");
        
        if (_currentKey != Keys.None)
        {
            parts.Add(_currentKey.ToString());
        }

        _hotkeyBox.Text = string.Join("+", parts);
    }
}