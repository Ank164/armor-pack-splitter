using System.Diagnostics;
using System.Text.Json;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Size = System.Drawing.Size;
using System.Runtime.InteropServices;

namespace ArmorPackSplitter;
internal sealed class MainForm : Form
{
    private readonly ComboBox sets = new() {Dock=DockStyle.Fill, DropDownStyle=ComboBoxStyle.DropDownList};
    private readonly Label setLabel = new() {Text="Armor set — inferred from piece names",AutoSize=true};
    private readonly TextBox setSearch = new() {Dock=DockStyle.Fill, PlaceholderText="Find an armor set…"};
    private readonly TextBox pieceSearch = new() {Dock=DockStyle.Fill, PlaceholderText="Filter pieces by name or EditorID…"};
    private readonly CheckedListBox pieces = new() {Dock=DockStyle.Fill, CheckOnClick=true, IntegralHeight=false, BorderStyle=BorderStyle.None};
    private readonly TextBox sourceLabel = new() {Dock=DockStyle.Fill, ReadOnly=true, PlaceholderText="Open an armor pack ESP to begin"};
    private readonly ComboBox pluginPicker=new(){Dock=DockStyle.Fill,DropDownStyle=ComboBoxStyle.DropDownList,FlatStyle=FlatStyle.Flat,DrawMode=DrawMode.OwnerDrawFixed,ItemHeight=24,DropDownHeight=400,IntegralHeight=false};
    private readonly TextBox pluginFilter=new(){Dock=DockStyle.Fill,PlaceholderText="Filter MO2 plugins…"};
    private readonly List<PluginChoice> pluginChoices=[];
    private readonly bool inMo2=Mo2Context.IsRunningUnderMo2;
    private readonly TaskCompletionSource startupReady=new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task pluginLoad=Task.CompletedTask;
    private bool changingPlugins;
    private readonly TextBox data = new() {Dock=DockStyle.Fill};
    private readonly TextBox extras = new() {Dock=DockStyle.Fill, Multiline=true, ScrollBars=ScrollBars.Vertical, PlaceholderText="Optional asset folders, one per line. These override the source pack; first folder wins."};
    private readonly TextBox output = new() {Dock=DockStyle.Fill};
    private readonly TextBox modName = new() {Dock=DockStyle.Fill, PlaceholderText="Name for the new mod and ESP"};
    private readonly CheckBox recipes = new() {Text="Include crafting and tempering recipes", Checked=true, AutoSize=true};
    private readonly CheckBox showAll = new() {Text="Show every armor piece in this pack", AutoSize=true};
    private readonly CheckBox useMo2 = new() {Text="Use current MO2 profile assets",Checked=true,AutoSize=true};
    private readonly CheckBox allowIncomplete = new() {Text="Allow incomplete export (missing files / scripts)",AutoSize=true};
    private readonly CheckBox darkMode = new() {Text="Dark mode",AutoSize=true};
    private readonly System.Windows.Forms.Timer autoCheck = new() {Interval=450};
    private readonly TextBox preview = new() {Dock=DockStyle.Fill, Multiline=true, ReadOnly=true, ScrollBars=ScrollBars.Both, WordWrap=false, BackColor=Color.FromArgb(245,247,250), BorderStyle=BorderStyle.None};
    private readonly SetPreviewControl outfitPreview=new();
    private readonly Label status = new() {Dock=DockStyle.Fill, Text="Open a pack, choose a set, then check its dependencies.", AutoEllipsis=true, TextAlign=ContentAlignment.MiddleLeft};
    private readonly Button analyze = new() {Text="Check dependencies", AutoSize=true, Enabled=false};
    private readonly Button export = new() {Text="Export ZIP…", AutoSize=true, Enabled=false};
    private readonly HashSet<FormKey> selected=[];
    private ExportEngine? engine;
    private ExportPlan? plan;
    private bool refreshing;
    private bool busy;
    private readonly Panel content = new() {Dock=DockStyle.Fill};
    private readonly Settings settings = Settings.Load();

    public MainForm(string? initial)
    {
        Text="Armor Pack Splitter "+typeof(MainForm).Assembly.GetName().Version?.ToString(3);
        Width=1400; Height=980; MinimumSize=new Size(1120,820);
        StartPosition=FormStartPosition.CenterScreen;
        Font=new Font("Segoe UI",10);
        BackColor=Color.White;
        Icon=Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        sets.DrawMode=DrawMode.OwnerDrawFixed;sets.ItemHeight=24;
        sets.FlatStyle=FlatStyle.Flat;
        sets.DrawItem+=(_,e)=>
        {
            using var background=new SolidBrush((e.State&DrawItemState.Selected)!=0?Color.FromArgb(52,91,141):sets.BackColor);
            e.Graphics.FillRectangle(background,e.Bounds);
            var text=e.Index>=0?sets.Items[e.Index]?.ToString():sets.SelectedItem?.ToString()??"Choose a set…";
            TextRenderer.DrawText(e.Graphics!,text,sets.Font,e.Bounds,sets.ForeColor,TextFormatFlags.Left|TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis);
            e.DrawFocusRectangle();
        };
        pluginPicker.DrawItem+=(_,e)=>
        {
            using var background=new SolidBrush((e.State&DrawItemState.Selected)!=0?Color.FromArgb(52,91,141):pluginPicker.BackColor);
            e.Graphics.FillRectangle(background,e.Bounds);
            var text=e.Index>=0?pluginPicker.Items[e.Index]?.ToString():pluginPicker.SelectedItem?.ToString()??"Select a plugin from the MO2 profile…";
            TextRenderer.DrawText(e.Graphics,text,pluginPicker.Font,e.Bounds,pluginPicker.ForeColor,TextFormatFlags.Left|TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis);
        };
        var layout=new TableLayoutPanel {Dock=DockStyle.Fill,Padding=new Padding(20),ColumnCount=1,RowCount=7};
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute,40));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute,44));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute,100));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute,64));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute,44));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute,36));
        layout.Controls.Add(Row(new Label {Text="Export an armor set to its own mod",Font=new Font(Font.FontFamily,18,FontStyle.Bold),Dock=DockStyle.Fill},darkMode),0,0);
        var open=new Button {Text="Browse for plugin…",AutoSize=true,Visible=!inMo2};
        var sourceRow=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2};
        sourceRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));sourceRow.ColumnStyles.Add(new ColumnStyle(inMo2?SizeType.Absolute:SizeType.AutoSize,inMo2?300:0));
        if(inMo2){sourceRow.Controls.Add(pluginPicker,0,0);sourceRow.Controls.Add(pluginFilter,1,0);}
        else{sourceRow.Controls.Add(sourceLabel,0,0);sourceRow.Controls.Add(open,1,0);}
        layout.Controls.Add(sourceRow,0,1);
        var assets=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=2,RowCount=2};
        assets.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,55)); assets.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,45));
        assets.RowStyles.Add(new RowStyle(SizeType.Absolute,28)); assets.RowStyles.Add(new RowStyle(SizeType.Percent,100));
        assets.Controls.Add(new Label {Text="Game Data folder (for shared and base assets)",AutoSize=true},0,0);
        assets.Controls.Add(new Label {Text="Additional asset folders (optional)",AutoSize=true},1,0);
        var dataPanel=new TableLayoutPanel {Dock=DockStyle.Fill,RowCount=2,ColumnCount=1};
        dataPanel.RowStyles.Add(new RowStyle(SizeType.Absolute,36)); dataPanel.RowStyles.Add(new RowStyle(SizeType.Percent,100));
        var browseData=new Button {Text="Browse…",AutoSize=true};
        dataPanel.Controls.Add(Row(data,browseData),0,0);
        dataPanel.Controls.Add(new Label {Text="Run through MO2 to use its virtual Data folder.",AutoSize=true,ForeColor=Color.DimGray},0,1);
        assets.Controls.Add(dataPanel,0,1); assets.Controls.Add(extras,1,1);
        layout.Controls.Add(assets,0,2);
        var split=new SplitContainer {Dock=DockStyle.Fill,Size=new Size(1320,550),SplitterDistance=400,Panel1MinSize=300,Panel2MinSize=570};
        var left=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=1,RowCount=6};
        left.RowStyles.Add(new RowStyle(SizeType.Absolute,26)); left.RowStyles.Add(new RowStyle(SizeType.Absolute,36));
        left.RowStyles.Add(new RowStyle(SizeType.Absolute,36)); left.RowStyles.Add(new RowStyle(SizeType.Absolute,32));
        left.RowStyles.Add(new RowStyle(SizeType.Absolute,36)); left.RowStyles.Add(new RowStyle(SizeType.Percent,100));
        left.Controls.Add(setLabel,0,0);
        left.Controls.Add(setSearch,0,1); left.Controls.Add(sets,0,2); left.Controls.Add(showAll,0,3); left.Controls.Add(pieceSearch,0,4);left.Controls.Add(pieces,0,5);
        split.Panel1.Controls.Add(left);
        var right=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=1,RowCount=2,Padding=new Padding(12,0,0,0)};
        right.RowStyles.Add(new RowStyle(SizeType.Absolute,26)); right.RowStyles.Add(new RowStyle(SizeType.Percent,100));
        var previewSplit=new SplitContainer{Dock=DockStyle.Fill,Orientation=Orientation.Horizontal,Size=new Size(850,550),SplitterDistance=400,Panel1MinSize=180,Panel2MinSize=100};
        previewSplit.Panel1.Controls.Add(outfitPreview);
        var notes=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=2};
        notes.RowStyles.Add(new RowStyle(SizeType.Absolute,26));notes.RowStyles.Add(new RowStyle(SizeType.Percent,100));
        notes.Controls.Add(new Label{Text="Dependencies and export notes",AutoSize=true,Font=new Font(Font,FontStyle.Bold)},0,0);notes.Controls.Add(preview,0,1);
        previewSplit.Panel2.Controls.Add(notes);
        right.Controls.Add(new Label {Text="3D set preview — all checked pieces together",AutoSize=true,Font=new Font(Font,FontStyle.Bold)},0,0);right.Controls.Add(previewSplit,0,1);
        split.Panel2.Controls.Add(right); layout.Controls.Add(split,0,3);
        var options=new FlowLayoutPanel {Dock=DockStyle.Fill,WrapContents=true};options.Controls.Add(recipes);options.Controls.Add(useMo2);options.Controls.Add(allowIncomplete);layout.Controls.Add(options,0,4);
        var destination=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=4};
        destination.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,95)); destination.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,58));destination.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));destination.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,42));
        destination.Controls.Add(new Label {Text="ZIP folder:",AutoSize=true,Padding=new Padding(0,6,0,0)},0,0);destination.Controls.Add(output,1,0);
        var browseOutput=new Button {Text="Browse…",AutoSize=true};destination.Controls.Add(browseOutput,2,0);destination.Controls.Add(modName,3,0);
        layout.Controls.Add(destination,0,5);
        var bottom=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=3};
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.Controls.Add(status,0,0);bottom.Controls.Add(analyze,1,0);bottom.Controls.Add(export,2,0);layout.Controls.Add(bottom,0,6);
        content.Controls.Add(layout);Controls.Add(content);
        data.Text=settings.DataFolder; output.Text=settings.OutputFolder; extras.Text=settings.ExtraFolders;
        preview.Text="Select a set from the dropdown.\r\n\r\nReview its checked pieces. Use ‘Show every armor piece’ to add pieces from another group.\r\n\r\nCheck dependencies before exporting. Missing files or unsupported dependencies must be resolved first.\r\n\r\nThe original pack can be disabled after a successful export. Shared body, skeleton, physics and other listed requirements still apply.";
        open.Click+=async(_,_)=>{using var dialog=new OpenFileDialog {Filter="Skyrim plugins|*.esp;*.esm;*.esl",Title="Open original armor pack"};if(dialog.ShowDialog(this)==DialogResult.OK) await LoadSource(dialog.FileName);};
        browseData.Click+=(_,_)=>BrowseFolder(data,"Select Skyrim Data folder");
        browseOutput.Click+=(_,_)=>BrowseFolder(output,"Choose where to save exported ZIPs");
        pluginFilter.TextChanged+=(_,_)=>FilterPlugins();
        pluginPicker.SelectedIndexChanged+=(_,_)=>{if(!changingPlugins&&!busy&&pluginPicker.SelectedItem is PluginChoice choice)pluginLoad=LoadSource(choice.Path);};
        sets.SelectedIndexChanged+=(_,_)=>ChooseSet();setSearch.TextChanged+=(_,_)=>FilterSets();
        pieceSearch.TextChanged+=(_,_)=>RefreshPieces();showAll.CheckedChanged+=(_,_)=>RefreshPieces();
        pieces.ItemCheck+=(_,e)=>{if(refreshing)return;var piece=((PieceItem)pieces.Items[e.Index]).Armor;if(e.NewValue==CheckState.Checked)selected.Add(piece.FormKey);else selected.Remove(piece.FormKey);InvalidatePlan();};
        recipes.CheckedChanged+=(_,_)=>InvalidatePlan(); data.TextChanged+=(_,_)=>InvalidatePlan();extras.TextChanged+=(_,_)=>InvalidatePlan();
        useMo2.CheckedChanged+=(_,_)=>InvalidatePlan();
        allowIncomplete.CheckedChanged+=(_,_)=>RenderPlan();
        autoCheck.Tick+=async(_,_)=>{autoCheck.Stop();if(!busy&&engine is not null&&selected.Count>0&&Directory.Exists(data.Text))await Analyze();};
        darkMode.CheckedChanged+=(_,_)=>ApplyTheme();
        darkMode.Checked=settings.DarkMode;ApplyTheme();
        HandleCreated+=(_,_)=>ApplyTheme();
        analyze.Click+=async(_,_)=>await Analyze(); export.Click+=async(_,_)=>await Export();
        FormClosing+=(_,e)=>{if(busy)e.Cancel=true;};
        FormClosed+=(_,_)=>{autoCheck.Dispose();plan?.Dispose();settings.DataFolder=data.Text;settings.OutputFolder=output.Text;settings.ExtraFolders=extras.Text;settings.DarkMode=darkMode.Checked;settings.Save();};
        AllowDrop=true;DragEnter+=(_,e)=>{if(!busy&&e.Data?.GetDataPresent(DataFormats.FileDrop)==true)e.Effect=DragDropEffects.Copy;};
        DragDrop+=async(_,e)=>{if(!busy && e.Data?.GetData(DataFormats.FileDrop) is string[] files && files.FirstOrDefault() is {} file)await LoadSource(file);};
        Shown+=async(_,_)=>
        {
            try{await InitializeSession(initial);}
            catch(Exception ex){status.Text="Could not load MO2 plugin list.";preview.Text=ex.Message;}
            finally{startupReady.TrySetResult();}
        };
    }
    private static TableLayoutPanel Row(Control field,Control button)
    {
        var row=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=2,Margin=Padding.Empty};row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));row.Controls.Add(field,0,0);row.Controls.Add(button,1,0);return row;
    }
    private void BrowseFolder(TextBox field,string title){using var d=new FolderBrowserDialog {Description=title,UseDescriptionForTitle=true,SelectedPath=Directory.Exists(field.Text)?field.Text:""};if(d.ShowDialog(this)==DialogResult.OK)field.Text=d.SelectedPath;}
    private async Task InitializeSession(string? initial)
    {
        if(!inMo2){if(initial is not null&&File.Exists(initial))await LoadSource(initial);return;}
        var context=Mo2Context.Find(Application.ExecutablePath,data.Text);
        if(context is null)throw new InvalidOperationException("MO2 is running, but its profile could not be located from the app and Data folders.");
        status.Text=$"Loading plugins from MO2 profile '{context.Profile}'…";
        SetBusy(true);
        var gameData=data.Text;
        try{pluginChoices.AddRange(await Task.Run(()=>Mo2Context.PluginChoices(context,gameData)));FilterPlugins();}
        finally{SetBusy(false);}
        status.Text=$"MO2 profile '{context.Profile}' · {pluginChoices.Count} plugins — select an armor pack.";
        var requested=initial is not null?Path.GetFileName(initial):settings.LastPlugin;
        var choice=pluginChoices.FirstOrDefault(p=>p.Name.Equals(requested,StringComparison.OrdinalIgnoreCase));
        if(choice is not null){pluginPicker.SelectedItem=choice;await pluginLoad;}
    }
    private void FilterPlugins()
    {
        var prior=(pluginPicker.SelectedItem as PluginChoice)?.Name;
        changingPlugins=true;
        try
        {
            pluginPicker.BeginUpdate();pluginPicker.Items.Clear();
            foreach(var choice in pluginChoices.Where(p=>p.Name.Contains(pluginFilter.Text,StringComparison.OrdinalIgnoreCase)))pluginPicker.Items.Add(choice);
            pluginPicker.SelectedItem=pluginPicker.Items.Cast<PluginChoice>().FirstOrDefault(p=>p.Name==prior);
        }
        finally{pluginPicker.EndUpdate();changingPlugins=false;}
    }
    internal async Task LoadSource(string path)
    {
        autoCheck.Stop();outfitPreview.Clear("Opening armor pack…");plan?.Dispose();plan=null;engine=null;selected.Clear();
        refreshing=true;
        try{sets.Items.Clear();pieces.Items.Clear();}finally{refreshing=false;}
        preview.Text="Loading plugin…";
        SetBusy(true);
        try
        {
            var loaded=await Task.Run(()=>new ExportEngine(path));
            engine=loaded; sourceLabel.Text=loaded.SourcePath; selected.Clear(); InvalidatePlan();
            settings.LastPlugin=Path.GetFileName(loaded.SourcePath);
            setSearch.Clear();FilterSets();status.Text=$"{loaded.Source.Armors.Count} armor pieces · {loaded.Sets.Count} suggested sets";
        }
        catch(Exception ex){MessageBox.Show(this,ex.Message,"Could not open pack",MessageBoxButtons.OK,MessageBoxIcon.Error);}
        finally{SetBusy(false);}
    }
    private void FilterSets()
    {
        if(engine is null)return;
        var prior=(sets.SelectedItem as ArmorSet)?.Name;
        refreshing=true;
        try {sets.Items.Clear();foreach(var set in engine.Sets.Where(s=>s.Name.Contains(setSearch.Text,StringComparison.OrdinalIgnoreCase)))sets.Items.Add(set);var index=sets.Items.Cast<ArmorSet>().ToList().FindIndex(s=>s.Name==prior);sets.SelectedIndex=index>=0?index:sets.Items.Count>0?0:-1;}
        finally{refreshing=false;}
        ChooseSet();
    }
    private void ChooseSet()
    {
        if(refreshing)return;
        selected.Clear();
        if(sets.SelectedItem is ArmorSet set){foreach(var piece in set.Pieces)selected.Add(piece.FormKey);modName.Text=string.Concat(set.Name.Select(c=>Path.GetInvalidFileNameChars().Contains(c)?'_':c)).Trim()+" Standalone";}
        setLabel.Text=sets.SelectedItem is ArmorSet current?$"Armor set: {current.Name} ({current.Pieces.Count} pieces)":"Armor set — inferred from piece names";
        InvalidatePlan();RefreshPieces();
    }
    private void RefreshPieces()
    {
        if(engine is null)return;
        var scope=showAll.Checked?engine.Source.Armors.ToList():(sets.SelectedItem as ArmorSet)?.Pieces??[];
        refreshing=true;pieces.BeginUpdate();
        try{pieces.Items.Clear();foreach(var armor in scope.Where(a=>!a.IsDeleted && (ExportEngine.DisplayName(a)+" "+a.EditorID).Contains(pieceSearch.Text,StringComparison.OrdinalIgnoreCase)).OrderBy(ExportEngine.DisplayName))pieces.Items.Add(new PieceItem(armor),selected.Contains(armor.FormKey));}
        finally{pieces.EndUpdate();refreshing=false;}
    }
    private void InvalidatePlan()
    {
        autoCheck.Stop();
        outfitPreview.Clear(selected.Count==0?"Check armor pieces to preview them together.":"Selection changed. Preparing the combined outfit preview…");
        plan?.Dispose();plan=null;export.Enabled=false;analyze.Enabled=!busy&&engine is not null&&selected.Count>0;
        if(engine is not null){status.Text=$"{selected.Count} pieces selected — checking dependencies shortly…";preview.Text="Selection changed. Dependencies will be checked automatically.";if(selected.Count>0)autoCheck.Start();}
    }
    private void SetBusy(bool value){busy=value;content.Enabled=!value;UseWaitCursor=value;if(!value){analyze.Enabled=engine is not null&&selected.Count>0;export.Enabled=CanExport();}}
    private bool CanExport()=>plan is not null&&plan.Errors.Count==0&&(plan.ReviewIssues.Count==0||allowIncomplete.Checked);
    internal async Task Analyze()
    {
        if(engine is null || selected.Count==0)return;
        autoCheck.Stop();
        if(!Directory.Exists(data.Text)){MessageBox.Show(this,"Choose the game Data folder first.");return;}
        var keys=selected.ToArray();var dataFolder=data.Text;var folders=extras.Lines.Where(l=>!string.IsNullOrWhiteSpace(l)).Select(l=>l.Trim()).ToArray();
        if(folders.Any(f=>!Directory.Exists(f))){MessageBox.Show(this,"An additional asset folder does not exist. Correct its path first.");return;}
        var includeRecipes=recipes.Checked;var includeMo2=useMo2.Checked;var progress=new Progress<string>(s=>status.Text=s);SetBusy(true);
        try
        {
            plan?.Dispose();plan=null;
            plan=await Task.Run(()=>engine.Plan(keys,includeRecipes,dataFolder,folders,s=>((IProgress<string>)progress).Report(s),includeMo2));
            RenderPlan();
            outfitPreview.SetPlan(plan,keys);
        }
        catch(Exception ex){preview.Text=ex.ToString();status.Text="Dependency check failed.";}
        finally{SetBusy(false);}
    }
    private void RenderPlan()
    {
        if(plan is null||engine is null)return;
        var masters=plan.Records.SelectMany(r=>r.EnumerateFormLinks()).Where(l=>!l.FormKey.IsNull&&l.FormKey.ModKey!=engine.Source.ModKey).Select(l=>l.FormKey.ModKey.ToString()).Distinct();
        preview.Text=PreviewText(plan,selected.Count,masters,allowIncomplete.Checked);
        status.Text=plan.Errors.Count>0?"Export blocked by an unsafe or unsupported dependency.":plan.ReviewIssues.Count>0?$"{plan.ReviewIssues.Count} unresolved dependencies — see preview.":"Dependencies checked. Ready to export.";
        export.Enabled=!busy&&CanExport();
    }
    internal static string PreviewText(ExportPlan plan,int pieces,IEnumerable<string> masters,bool incomplete)
    {
        var optional=plan.Notes.Where(n=>n.StartsWith("Missing optional",StringComparison.OrdinalIgnoreCase)).ToList();
        var general=plan.Notes.Except(optional).ToList();
        return $"{pieces} selected armor pieces\r\n{plan.Records.Count} total records\r\n{plan.Files.Count} files to include\r\nOutput: ESL-flagged ESP in an installable ZIP\r\n\r\nRequired masters:\r\n{string.Join("\r\n",masters)}\r\n\r\n"+
            "REAL ERRORS / REQUIRED DEPENDENCIES\r\n"+(plan.Errors.Count+plan.ReviewIssues.Count==0?"None.\r\n":string.Join("\r\n",plan.Errors.Concat(plan.ReviewIssues))+"\r\n")+
            (plan.Errors.Count>0?"Export blocked by unsafe or unsupported data.\r\n":plan.ReviewIssues.Count>0?(incomplete?"Available files will be exported; unresolved issues will be recorded.\r\n":"Review the issues or enable ‘Allow incomplete export’ to include the available files.\r\n"):"")+"\r\n"+
            "OPTIONAL FILES — NOT EXPORT BLOCKERS\r\n"+(optional.Count==0?"None.\r\n":string.Join("\r\n",optional)+"\r\n")+"\r\n"+
            "GENERAL NOTES\r\n"+string.Join("\r\n",general)+"\r\n\r\nFiles and why they are included:\r\n"+string.Join("\r\n\r\n",plan.Files.Select(f=>f.Path+"\r\n  Used by: "+string.Join("; ",f.Reasons)+"\r\n  From: "+f.Source));
    }
    private async Task Export()
    {
        if(engine is null||plan is null||!CanExport())return;
        if(!Directory.Exists(output.Text)){MessageBox.Show(this,"Choose an existing parent output folder first.");return;}
        var name=modName.Text;
        using var dialog=new SaveFileDialog{Title="Export installable mod ZIP",Filter="Mod archive (*.zip)|*.zip",DefaultExt="zip",AddExtension=true,FileName=name+".zip",InitialDirectory=output.Text,OverwritePrompt=true};
        if(dialog.ShowDialog(this)!=DialogResult.OK)return;
        var zipPath=dialog.FileName;var permitIncomplete=allowIncomplete.Checked;SetBusy(true);
        try
        {
            var progress=new Progress<string>(s=>status.Text=s);
            var result=await Task.Run(()=>engine.ExportZip(plan,zipPath,name,s=>((IProgress<string>)progress).Report(s),permitIncomplete,overwrite:true));
            output.Text=Path.GetDirectoryName(result)!;
            status.Text="Export complete: "+result;
            Process.Start(new ProcessStartInfo("explorer.exe",$"/select,\"{result}\""){UseShellExecute=true});
            MessageBox.Show(this,$"Created {result}\n\nInstall this ZIP using MO2's ‘Install a new mod from an archive’ button. The included ESP is ESL-flagged and has no master dependency on the original pack.\n\n"+(plan.ReviewIssues.Count>0?$"{plan.ReviewIssues.Count} unresolved asset or script issues remain. Read the export report before using it.\n\n":"")+"Shared body, skeleton, physics and listed master requirements still apply.","Export complete");
        }
        catch(Exception ex){MessageBox.Show(this,ex.Message,"Export failed",MessageBoxButtons.OK,MessageBoxIcon.Error);}
        finally{SetBusy(false);}
    }
    private void ApplyTheme()
    {
        var dark=darkMode.Checked;
        var surface=dark?Color.FromArgb(24,27,34):Color.White;
        var field=dark?Color.FromArgb(34,39,49):Color.White;
        var ink=dark?Color.FromArgb(231,234,240):Color.FromArgb(28,32,40);
        void Paint(Control control)
        {
            control.ForeColor=ink;
            control.BackColor=control is TextBox or ComboBox or CheckedListBox?field:surface;
            if(control is Button button){button.FlatStyle=FlatStyle.Flat;button.BackColor=dark?Color.FromArgb(46,55,69):Color.FromArgb(240,243,247);button.FlatAppearance.BorderColor=dark?Color.FromArgb(76,87,105):Color.FromArgb(185,193,205);}
            foreach(Control child in control.Controls)Paint(child);
        }
        Paint(this);
        preview.BackColor=dark?Color.FromArgb(29,33,41):Color.FromArgb(245,247,250);
        outfitPreview.SetTheme(dark);
        if(IsHandleCreated){int value=dark?1:0;DwmSetWindowAttribute(Handle,20,ref value,sizeof(int));}
        Invalidate(true);
    }
    [DllImport("dwmapi.dll")]private static extern int DwmSetWindowAttribute(IntPtr window,int attribute,ref int value,int size);
    private sealed record PieceItem(Armor Armor){public override string ToString()=>ExportEngine.DisplayName(Armor)+"  ["+Armor.EditorID+"]";}
    internal void SelectPreviewSet(string name)
    {
        foreach(var item in sets.Items.Cast<ArmorSet>())if(item.Name.Equals(name,StringComparison.OrdinalIgnoreCase)){sets.SelectedItem=item;return;}
        throw new InvalidOperationException("Preview set not found: "+name);
    }
    internal bool HasCheckedPlan=>plan is not null&&!busy;
    internal Task PreviewReady=>outfitPreview.Ready;
    internal void SavePreviewSnapshot(string path)=>outfitPreview.SaveSnapshot(path);
    internal PreviewScene? PreviewScene=>outfitPreview.Scene;
    internal async Task VerifyPreviewInteractions()
    {
        var original=selected.Count;
        if(original<2)throw new InvalidOperationException("Preview interaction check requires a multi-piece set.");
        var index=Enumerable.Range(0,pieces.Items.Count).First(i=>pieces.GetItemChecked(i));
        pieces.SetItemChecked(index,false);
        await WaitForPreview();
        if(outfitPreview.Scene?.Pieces!=original-1)throw new Exception("Unchecking a piece did not refresh the combined scene.");
        outfitPreview.SaveSnapshot("preview-piece-removed.png");
        pieces.SetItemChecked(index,true);await WaitForPreview();
        if(outfitPreview.Scene?.Pieces!=original)throw new Exception("Restoring a piece did not refresh the combined scene.");
        await outfitPreview.SetView(true,true);outfitPreview.SaveSnapshot("preview-weight-0.png");
        await outfitPreview.SetView(false,false);outfitPreview.SaveSnapshot("preview-male.png");
        await outfitPreview.SetView(true,false);outfitPreview.TurnAround();outfitPreview.SaveSnapshot("preview-back.png");outfitPreview.Fit();
        File.WriteAllText("preview-interactions.txt","PASS: UI piece removal and restoration refresh the combined scene.\nPASS: Female weight 0, male, and back views rendered.\n"+outfitPreview.DiagnosticState);
        async Task WaitForPreview()
        {
            var deadline=DateTime.UtcNow.AddSeconds(60);
            while(!HasCheckedPlan&&DateTime.UtcNow<deadline)await Task.Delay(50);
            if(!HasCheckedPlan)throw new TimeoutException("Selection dependency scan did not finish.");
            await outfitPreview.Ready;
        }
    }
    internal string DiagnosticState=>status.Text+"\n3D: "+outfitPreview.DiagnosticState+"\n"+preview.Text;
    internal Task StartupReady=>startupReady.Task;
    internal bool UsesMo2List=>inMo2;
    internal int PluginCount=>pluginChoices.Count;
    internal async Task SelectPreviewPlugin(string filename)
    {
        pluginFilter.Clear();
        pluginPicker.SelectedItem=pluginChoices.FirstOrDefault(p=>p.Name.Equals(filename,StringComparison.OrdinalIgnoreCase))??throw new InvalidOperationException("Plugin not in MO2 selector: "+filename);
        await pluginLoad;
    }
}
internal sealed class Settings
{
    public string DataFolder {get;set;}=DetectDataFolder();
    public string OutputFolder {get;set;}=Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    public string ExtraFolders {get;set;}="";
    public bool DarkMode {get;set;}=true;
    public string LastPlugin {get;set;}="";
    internal static string DetectDataFolder()
    {
        foreach(var start in new[]{Environment.CurrentDirectory,AppContext.BaseDirectory})
            for(var directory=new DirectoryInfo(start);directory is not null;directory=directory.Parent)
                foreach(var relative in new[]{"Data",Path.Combine("Stock Game","Data")})
                {
                    var candidate=Path.Combine(directory.FullName,relative);
                    if(File.Exists(Path.Combine(candidate,"Skyrim.esm")))return candidate;
                }
        foreach(var view in new[]{Microsoft.Win32.RegistryView.Registry32,Microsoft.Win32.RegistryView.Registry64})
        {
            try
            {
                using var root=Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine,view);
                using var key=root.OpenSubKey(@"SOFTWARE\Bethesda Softworks\Skyrim Special Edition");
                if(key?.GetValue("Installed Path") is string installed)
                {
                    var candidate=Path.Combine(installed,"Data");
                    if(File.Exists(Path.Combine(candidate,"Skyrim.esm")))return candidate;
                }
            }
            catch(System.Security.SecurityException){}
        }
        return "";
    }
    private static string FilePath=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ArmorPackSplitter","settings.json");
    public static Settings Load(){try{return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath))??new();}catch{return new();}}
    public void Save(){Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);File.WriteAllText(FilePath,JsonSerializer.Serialize(this));}
}
