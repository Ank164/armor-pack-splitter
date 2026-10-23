namespace ArmorPackSplitter;

// A small tab strip without the native TabControl's light frame or minimum header widths.
internal sealed class PreviewTabs : UserControl
{
    private readonly TableLayoutPanel strip=new(){Dock=DockStyle.Top,Height=34,RowCount=1,Margin=Padding.Empty};
    private readonly Panel pages=new(){Dock=DockStyle.Fill};
    private readonly Dictionary<Control,Button> buttons=[];
    private Control? selected;
    private bool dark=true;
    internal event EventHandler? SelectedTabChanged;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Control? SelectedTab
    {
        get=>selected;
        set
        {
            if(value is null||value==selected||!buttons.ContainsKey(value))return;
            selected=value;
            foreach(var page in buttons.Keys)page.Visible=page==value;
            value.BringToFront();SetTheme(dark);SelectedTabChanged?.Invoke(this,EventArgs.Empty);
        }
    }
    internal PreviewTabs(){Dock=DockStyle.Fill;Controls.Add(pages);Controls.Add(strip);}
    internal void AddPage(Control page)
    {
        var button=new Button{Text=page.Text,Dock=DockStyle.Fill,Margin=Padding.Empty,FlatStyle=FlatStyle.Flat,AutoEllipsis=true,AccessibleRole=AccessibleRole.PageTab,UseVisualStyleBackColor=false};
        button.FlatAppearance.BorderSize=0;
        button.Click+=(_,_)=>SelectedTab=page;
        page.TextChanged+=(_,_)=>button.Text=page.Text;
        page.Dock=DockStyle.Fill;page.Visible=false;pages.Controls.Add(page);buttons.Add(page,button);
        strip.ColumnCount=buttons.Count;strip.ColumnStyles.Clear();
        foreach(var _ in buttons)strip.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100f/buttons.Count));
        strip.Controls.Add(button,buttons.Count-1,0);
        if(selected is null)SelectedTab=page;
    }
    internal void SetTheme(bool useDark)
    {
        dark=useDark;
        foreach(var (page,button) in buttons)
        {
            button.BackColor=dark?(page==selected?Color.FromArgb(46,55,69):Color.FromArgb(24,27,34)):(page==selected?Color.White:Color.FromArgb(230,234,240));
            button.ForeColor=dark?Color.FromArgb(231,234,240):Color.FromArgb(28,32,40);
            button.FlatAppearance.MouseOverBackColor=dark?Color.FromArgb(60,70,85):Color.FromArgb(213,224,238);
        }
    }
}
