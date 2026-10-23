using System.Windows.Forms.Integration;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using Mutagen.Bethesda.Plugins;
using WpfControls=System.Windows.Controls;
using WpfPoint=System.Windows.Point;
using WpfColor=System.Windows.Media.Color;
using WpfBrushes=System.Windows.Media.Brushes;

namespace ArmorPackSplitter;

internal sealed class SetPreviewControl : UserControl
{
    private readonly WpfControls.Grid surface=new();
    private readonly WpfControls.Viewport3D viewport=new(){ClipToBounds=true};
    private readonly PerspectiveCamera camera=new(){FieldOfView=38,UpDirection=new Vector3D(0,0,1)};
    private readonly ModelVisual3D visual=new();
    private readonly WpfControls.TextBlock placeholder=new(){Text="Select a set to preview the checked pieces together.",Foreground=WpfBrushes.Silver,HorizontalAlignment=System.Windows.HorizontalAlignment.Center,VerticalAlignment=System.Windows.VerticalAlignment.Center,IsHitTestVisible=false,TextWrapping=System.Windows.TextWrapping.Wrap,Margin=new System.Windows.Thickness(24)};
    private readonly ComboBox gender=new(){DropDownStyle=ComboBoxStyle.DropDownList,Width=85};
    private readonly ComboBox weight=new(){DropDownStyle=ComboBoxStyle.DropDownList,Width=108};
    private readonly Label summary=new(){Dock=DockStyle.Fill,AutoEllipsis=true,Text="Drag: rotate · Wheel: zoom · Right-drag: pan",TextAlign=ContentAlignment.MiddleLeft};
    private readonly TextBox details=new(){Dock=DockStyle.Fill,Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical,BorderStyle=BorderStyle.None,WordWrap=true};
    private readonly ElementHost host;
    private CancellationTokenSource? loading;
    private ExportPlan? plan;
    private FormKey[] selected=[];
    private long revision;
    private WpfPoint pointer;
    private bool dragging,panning;
    private Point3D center;
    private double yaw,pitch=.10,distance=250,radius=75;
    private readonly ToolTip tooltip=new();
    internal PreviewScene? Scene {get;private set;}
    internal Task Ready {get;private set;}=Task.CompletedTask;
    internal string DiagnosticState=>summary.Text+"\n"+details.Text;

    internal SetPreviewControl()
    {
        Dock=DockStyle.Fill;
        viewport.Camera=camera;viewport.Children.Add(visual);
        surface.Children.Add(viewport);surface.Children.Add(placeholder);
        host=new ElementHost{Dock=DockStyle.Fill,Child=surface};
        var toolbar=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=false,Margin=Padding.Empty};
        gender.Items.AddRange(["Female","Male"]);gender.SelectedIndex=0;
        weight.Items.AddRange(["Weight 100","Weight 0"]);weight.SelectedIndex=0;
        foreach(var combo in new[]{gender,weight})
        {
            combo.DrawMode=DrawMode.OwnerDrawFixed;combo.ItemHeight=22;combo.FlatStyle=FlatStyle.Flat;
            combo.DrawItem+=(_,e)=>
            {
                using var background=new SolidBrush((e.State&DrawItemState.Selected)!=0?System.Drawing.Color.FromArgb(52,91,141):combo.BackColor);
                e.Graphics.FillRectangle(background,e.Bounds);
                TextRenderer.DrawText(e.Graphics,e.Index>=0?combo.Items[e.Index]?.ToString():combo.SelectedItem?.ToString(),combo.Font,e.Bounds,combo.ForeColor,TextFormatFlags.Left|TextFormatFlags.VerticalCenter);
            };
        }
        toolbar.Controls.Add(gender);toolbar.Controls.Add(weight);
        foreach(var (text,angle) in new[]{("Front",0.0),("Back",Math.PI)})
        {
            var button=new Button{Text=text,AutoSize=true,MinimumSize=new Size(55,28)};
            button.Click+=(_,_)=>{yaw=angle;pitch=.10;UpdateCamera();};toolbar.Controls.Add(button);
        }
        var reset=new Button{Text="Reset view",AutoSize=true,MinimumSize=new Size(80,28)};
        reset.Click+=(_,_)=>Fit();toolbar.Controls.Add(reset);
        var layout=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=4,Margin=Padding.Empty};
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute,34));layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute,26));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,44));
        layout.Controls.Add(toolbar,0,0);layout.Controls.Add(host,0,1);layout.Controls.Add(summary,0,2);layout.Controls.Add(details,0,3);Controls.Add(layout);
        gender.SelectedIndexChanged+=(_,_)=>RefreshScene();weight.SelectedIndexChanged+=(_,_)=>RefreshScene();
        surface.MouseDown+=(_,e)=>
        {
            pointer=e.GetPosition(surface);panning=e.ChangedButton==System.Windows.Input.MouseButton.Right;
            dragging=e.ChangedButton is System.Windows.Input.MouseButton.Left or System.Windows.Input.MouseButton.Right;
            if(dragging){surface.CaptureMouse();e.Handled=true;}
        };
        surface.MouseUp+=(_,_)=>{dragging=false;surface.ReleaseMouseCapture();};
        surface.LostMouseCapture+=(_,_)=>dragging=false;
        surface.MouseMove+=(_,e)=>
        {
            if(!dragging)return;
            var now=e.GetPosition(surface);var dx=now.X-pointer.X;var dy=now.Y-pointer.Y;pointer=now;
            if(panning)
            {
                var forward=camera.LookDirection;forward.Normalize();var right=Vector3D.CrossProduct(forward,camera.UpDirection);right.Normalize();
                var up=Vector3D.CrossProduct(right,forward);up.Normalize();
                center+=(-right*dx+up*dy)*(distance*.0015);
            }
            else{yaw-=dx*.01;pitch=Math.Clamp(pitch+dy*.008,-1.35,1.35);}
            UpdateCamera();
        };
        surface.MouseWheel+=(_,e)=>{distance=Math.Clamp(distance*Math.Exp(-e.Delta*.0012),radius*.2,radius*30);UpdateCamera();e.Handled=true;};
        viewport.SizeChanged+=(_,_)=>{if(Scene is not null&&!dragging)UpdateCamera();};
        SetTheme(true);UpdateCamera();
    }

    internal void SetPlan(ExportPlan value,IEnumerable<FormKey> pieces)
    {
        plan=value;selected=pieces.ToArray();RefreshScene();
    }
    internal void Clear(string message="Selection changed. Preparing the combined outfit preview…")
    {
        revision++;loading?.Cancel();plan=null;Scene=null;visual.Content=null;
        placeholder.Text=message;placeholder.Visibility=System.Windows.Visibility.Visible;
        summary.Text="Drag: rotate · Wheel: zoom · Right-drag: pan";details.Clear();
    }
    private void RefreshScene()
    {
        if(plan is null)return;
        var current=++revision;loading?.Cancel();loading=new();
        var token=loading.Token;var currentPlan=plan;var keys=selected.ToArray();var female=gender.SelectedIndex==0;var lowWeight=weight.SelectedIndex==1;
        visual.Content=null;Scene=null;placeholder.Text="Loading all checked pieces together…";placeholder.Visibility=System.Windows.Visibility.Visible;
        Ready=LoadAsync();
        async Task LoadAsync()
        {
            try
            {
                var scene=await Task.Run(()=>PreviewSceneReader.Read(currentPlan,keys,female,lowWeight,token),token);
                if(current!=revision||IsDisposed)return;
                Scene=scene;ShowScene(scene);Fit();
            }
            catch(OperationCanceledException){}
            catch(Exception ex)
            {
                if(current!=revision||IsDisposed)return;
                placeholder.Text="The 3D preview could not be loaded.";summary.Text="Preview unavailable — export checks remain separate.";details.Text=ex.Message;
            }
        }
    }

    private void ShowScene(PreviewScene scene)
    {
        var group=new Model3DGroup();
        group.Children.Add(new AmbientLight(WpfColor.FromRgb(105,105,115)));
        group.Children.Add(new DirectionalLight(WpfColor.FromRgb(220,220,220),new Vector3D(-.5,-1,-.7)));
        group.Children.Add(new DirectionalLight(WpfColor.FromRgb(100,110,135),new Vector3D(.8,1,-.3)));
        foreach(var mesh in scene.Meshes)
        {
            var geometry=new MeshGeometry3D
            {
                Positions=new Point3DCollection(mesh.Positions.Select(v=>new Point3D(v.X,v.Y,v.Z))),
                TriangleIndices=new Int32Collection(mesh.Indices),
                TextureCoordinates=new PointCollection(mesh.UVs.Select(v=>new WpfPoint(v.X,v.Y)))
            };
            if(mesh.Normals.Length==mesh.Positions.Length&&mesh.Normals.All(n=>float.IsFinite(n.X)&&float.IsFinite(n.Y)&&float.IsFinite(n.Z)))
                geometry.Normals=new Vector3DCollection(mesh.Normals.Select(n=>new Vector3D(n.X,n.Y,n.Z)));
            geometry.Freeze();
            System.Windows.Media.Brush brush=mesh.Texture is null?new SolidColorBrush(WpfColor.FromRgb(155,169,191)):
                new ImageBrush(mesh.Texture){ViewportUnits=BrushMappingMode.Absolute,Viewport=new System.Windows.Rect(0,0,1,1),TileMode=TileMode.Tile,Stretch=Stretch.Fill};
            brush.Opacity=mesh.Alpha;brush.Freeze();
            var materials=new MaterialGroup();materials.Children.Add(new DiffuseMaterial(brush));
            materials.Children.Add(new SpecularMaterial(new SolidColorBrush(WpfColor.FromArgb(255,35,35,35)),18));materials.Freeze();
            var model=new GeometryModel3D(geometry,materials){BackMaterial=mesh.DoubleSided?materials:null};model.Freeze();group.Children.Add(model);
        }
        group.Freeze();visual.Content=group;
        placeholder.Visibility=scene.Meshes.Count==0?System.Windows.Visibility.Visible:System.Windows.Visibility.Collapsed;
        if(scene.Meshes.Count==0)placeholder.Text="No worn models are available for this gender and weight.";
        summary.Text=$"{scene.Pieces} checked pieces · {scene.LoadedModels}/{scene.RequestedModels} worn meshes · {scene.Meshes.Sum(m=>m.Indices.Length/3):N0} triangles";
        details.Text="Static outfit preview · Drag to rotate · Scroll to zoom · Right-drag to pan."+
            (scene.Notes.Count>0?"\r\n"+string.Join("\r\n",scene.Notes.Distinct()):"\r\nAll checked pieces are shown together. Lighting and physics may differ in game.");
        tooltip.SetToolTip(summary,string.Join("\n",scene.Notes.Distinct()));
    }
    internal void Fit()
    {
        yaw=0;pitch=.10;
        if(Scene?.Meshes.Count>0)
        {
            var bounds=Rect3D.Empty;
            foreach(var mesh in Scene.Meshes)foreach(var p in mesh.Positions)bounds.Union(new Point3D(p.X,p.Y,p.Z));
            center=new Point3D(bounds.X+bounds.SizeX/2,bounds.Y+bounds.SizeY/2,bounds.Z+bounds.SizeZ/2);
            radius=Math.Max(1,Math.Sqrt(bounds.SizeX*bounds.SizeX+bounds.SizeY*bounds.SizeY+bounds.SizeZ*bounds.SizeZ)/2);
            var aspect=Math.Max(.25,viewport.ActualWidth/Math.Max(1,viewport.ActualHeight));
            var tanHorizontal=Math.Tan(camera.FieldOfView*Math.PI/360);
            var tanVertical=tanHorizontal/aspect;
            distance=1;
            foreach(var mesh in Scene.Meshes)foreach(var point in mesh.Positions)
            {
                var p=new Vector3D(point.X-center.X,point.Y-center.Y,point.Z-center.Z);
                var vertical=-p.Y*Math.Sin(pitch)+p.Z*Math.Cos(pitch);
                var depth=p.Y*Math.Cos(pitch)+p.Z*Math.Sin(pitch);
                distance=Math.Max(distance,Math.Max(Math.Abs(p.X)/tanHorizontal,Math.Abs(vertical)/tanVertical)+depth);
            }
            distance*=1.12;
        }
        UpdateCamera();
    }
    private void UpdateCamera()
    {
        var offset=new Vector3D(Math.Sin(yaw)*Math.Cos(pitch),Math.Cos(yaw)*Math.Cos(pitch),Math.Sin(pitch))*distance;
        camera.Position=center+offset;camera.LookDirection=-offset;
        camera.NearPlaneDistance=Math.Max(.01,radius/1000);camera.FarPlaneDistance=Math.Max(1000,distance+radius*50);
    }
    internal void SetTheme(bool dark)
    {
        surface.Background=new SolidColorBrush(dark?WpfColor.FromRgb(20,24,32):WpfColor.FromRgb(224,230,239));
        placeholder.Foreground=dark?WpfBrushes.Silver:WpfBrushes.DimGray;
    }
    internal async Task SetView(bool female,bool lowWeight)
    {
        gender.SelectedIndex=female?0:1;weight.SelectedIndex=lowWeight?1:0;
        await Ready;
    }
    internal void TurnAround(){yaw+=Math.PI;UpdateCamera();}
    internal void SaveSnapshot(string path)
    {
        surface.UpdateLayout();
        var image=new RenderTargetBitmap(Math.Max(1,(int)surface.ActualWidth),Math.Max(1,(int)surface.ActualHeight),96,96,PixelFormats.Pbgra32);
        image.Render(surface);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream=File.Create(path);encoder.Save(stream);
    }
    protected override void Dispose(bool disposing)
    {
        if(disposing){revision++;loading?.Cancel();loading?.Dispose();tooltip.Dispose();host.Child=null;}
        base.Dispose(disposing);
    }
}
