namespace KB525CControl.Settings;

/// <summary>
/// Windows hands mouse-wheel ticks to the control under the pointer, so scrolling a settings
/// page with a combo box or a numeric field in the way would silently change that value - and
/// the change gets saved without anyone noticing. These variants ignore the wheel and scroll
/// the surrounding page instead; an open drop-down list keeps the wheel for itself.
/// </summary>
static class WheelToPage
{
    public static void Scroll(Control control, MouseEventArgs e)
    {
        if (e is HandledMouseEventArgs handled) handled.Handled = true;   // no value change

        for (var p = control.Parent; p is not null; p = p.Parent)
        {
            if (p is not ScrollableControl { AutoScroll: true } page) continue;
            if (page.VerticalScroll.Visible)
                page.AutoScrollPosition = new Point(-page.AutoScrollPosition.X, -page.AutoScrollPosition.Y - e.Delta);
            return;
        }
    }
}

public sealed class WheelSafeComboBox : ComboBox
{
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (DroppedDown) base.OnMouseWheel(e);
        else WheelToPage.Scroll(this, e);
    }
}

public sealed class WheelSafeNumericUpDown : NumericUpDown
{
    protected override void OnMouseWheel(MouseEventArgs e) => WheelToPage.Scroll(this, e);
}
