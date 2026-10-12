using System;
using System.Drawing;
using System.Windows.Forms;

namespace OcrTool.App;

internal sealed class SelectionOverlay : Form
{
    private readonly Bitmap? _snapshot;
    private Point _startPoint;
    private Point _currentPoint;
    private bool _isSelecting;
    private readonly SolidBrush _dimBrush;
    private readonly Pen _selectionPen;
    private readonly Font _sizeFont;

    public event EventHandler<Rectangle>? SelectionCompleted;
    public event EventHandler? SelectionCancelled;

    public Bitmap? Snapshot => _snapshot;

    public SelectionOverlay(Bitmap? snapshot)
    {
        _snapshot = snapshot;
        InitializeOverlay();
        _dimBrush = new SolidBrush(Color.FromArgb(120, 0, 0, 0));
        _selectionPen = new Pen(Color.LimeGreen, 2) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
        _sizeFont = new Font("Segoe UI", 10);
    }

    private void InitializeOverlay()
    {
        var bounds = SystemInformation.VirtualScreen;
        Bounds = bounds;
        Location = bounds.Location;
        Size = bounds.Size;

        FormBorderStyle = FormBorderStyle.None;
        WindowState = FormWindowState.Normal;
        TopMost = true;
        ShowInTaskbar = false;
        BackColor = Color.Black;
        DoubleBuffered = true;
        Cursor = Cursors.Cross;

        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        Activate();
        Focus();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            _startPoint = e.Location;
            _currentPoint = e.Location;
            _isSelecting = true;
            Invalidate();
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_isSelecting)
        {
            _currentPoint = e.Location;
            Invalidate();
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && _isSelecting)
        {
            _isSelecting = false;
            var rect = GetSelectionRectangle();
            if (rect.Width > 10 && rect.Height > 10)
            {
                SelectionCompleted?.Invoke(this, rect);
            }
            else
            {
                SelectionCancelled?.Invoke(this, EventArgs.Empty);
            }
        }
        base.OnMouseUp(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape)
        {
            SelectionCancelled?.Invoke(this, EventArgs.Empty);
        }
        base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;

        if (_snapshot != null)
        {
            g.DrawImage(_snapshot, ClientRectangle);
        }
        else
        {
            g.Clear(Color.Black);
        }

        g.FillRectangle(_dimBrush, ClientRectangle);

        if (_isSelecting)
        {
            var rect = GetSelectionRectangle();
            if (rect.Width > 0 && rect.Height > 0)
            {
                if (_snapshot != null)
                {
                    var src = rect;
                    src.Intersect(new Rectangle(Point.Empty, ClientSize));
                    if (!src.IsEmpty)
                    {
                        g.DrawImage(_snapshot, src, src, GraphicsUnit.Pixel);
                    }
                }
                g.DrawRectangle(_selectionPen, rect);

                var sizeText = $"{rect.Width} x {rect.Height}";
                var textSize = g.MeasureString(sizeText, _sizeFont);
                var textPos = new PointF(rect.Right - textSize.Width - 5, rect.Top - textSize.Height - 5);
                g.FillRectangle(Brushes.Black, textPos.X - 2, textPos.Y - 2, textSize.Width + 4, textSize.Height + 4);
                g.DrawString(sizeText, _sizeFont, Brushes.LimeGreen, textPos);
            }
        }

        base.OnPaint(e);
    }

    private Rectangle GetSelectionRectangle()
    {
        int x = Math.Min(_startPoint.X, _currentPoint.X);
        int y = Math.Min(_startPoint.Y, _currentPoint.Y);
        int w = Math.Abs(_startPoint.X - _currentPoint.X);
        int h = Math.Abs(_startPoint.Y - _currentPoint.Y);
        return new Rectangle(x, y, w, h);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _dimBrush?.Dispose();
            _selectionPen?.Dispose();
            _sizeFont?.Dispose();
        }
        base.Dispose(disposing);
    }
}
