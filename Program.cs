using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Archives;
using Mutagen.Bethesda.Skyrim;
namespace ArmorPackSplitter;
internal static class Program
{
    [STAThread] static void Main(string[] args)
    {
        if(args.Contains("--audit"))
        {
            try
            {
                var engine=new ExportEngine(args[1]);
                var data=Environment.GetEnvironmentVariable("ASE_DATA_FOLDER")??Settings.Load().DataFolder;
                var context=Mo2Context.Find(engine.SourcePath,data);
                using var resolver=new AssetResolver(Path.GetDirectoryName(engine.SourcePath)!,data,[],engine.Source.ModKey.ToString(),context?.Roots,engine.Source.MasterReferences.Select(m=>m.Master.ToString()),context?.Plugins);
                var rows=new List<object>();
                File.WriteAllText("audit-progress.txt","");
                foreach(var set in engine.Sets)
                {
                    try
                    {
                        using var plan=resolver.Build(engine.Closure(set.Pieces.Select(a=>a.FormKey),true),engine.Source.ModKey,null);
                        rows.Add(new {name=set.Name,total=plan.Records.Count,files=plan.Files.Count,errors=plan.Errors,issues=plan.ReviewIssues,notes=plan.Notes});
                        File.AppendAllText("audit-progress.txt",$"{set.Name}: {plan.Errors.Count} errors, {plan.ReviewIssues.Count} review issues, {plan.Files.Count} files\n");
                    }
                    catch(Exception ex){rows.Add(new{name=set.Name,errors=new[]{ex.Message}});File.AppendAllText("audit-progress.txt",set.Name+": "+ex.Message+"\n");}
                    File.WriteAllText("audit.json",System.Text.Json.JsonSerializer.Serialize(rows,new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));
                }
            }
            catch(Exception ex){File.WriteAllText("audit-error.txt",ex.ToString());Environment.ExitCode=1;}
            return;
        }
        if (args.Contains("--self-test"))
        {
            Environment.ExitCode=SelfTest.Run(args.Skip(1).FirstOrDefault());
            return;
        }
        if (args.Contains("--catalog"))
        {
            try
            {
                var engine=new ExportEngine(args[1]);
                File.WriteAllText("catalog.json",System.Text.Json.JsonSerializer.Serialize(engine.Sets.Select(s=>new {s.Name,Pieces=s.Pieces.Select(a=>new {name=ExportEngine.DisplayName(a),editorid=a.EditorID,formid=a.FormKey.ToString()})}),new System.Text.Json.JsonSerializerOptions {WriteIndented=true}));
            }
            catch(Exception ex){File.WriteAllText("catalog-error.txt",ex.ToString());Environment.ExitCode=1;}
            return;
        }
        if (args.Contains("--api"))
        {
            var types = new[] {typeof(IMajorRecord),typeof(ISkyrimMajorRecord),typeof(IArchiveReader),typeof(IArchiveFolder),typeof(IMod),typeof(IGroup)};
            File.WriteAllText("api.txt", string.Join("\n", types.Select(t => t.FullName+"\n"+string.Join("\n",t.GetMethods().Select(m=>m.ToString())))));
            return;
        }
        ApplicationConfiguration.Initialize();
        if(args.Contains("--render"))
        {
            using var form=new MainForm(null);
            form.Shown+=async(_,_)=>
            {
                try
                {
                    await form.StartupReady;
                    if(form.UsesMo2List)await form.SelectPreviewPlugin(Path.GetFileName(args[1]));else await form.LoadSource(args[1]);
                    form.SelectPreviewSet(args.Length>2?args[2]:"Red Moon");
                    var deadline=DateTime.UtcNow.AddSeconds(40);
                    while(!form.HasCheckedPlan&&DateTime.UtcNow<deadline)await Task.Delay(50);
                    if(!form.HasCheckedPlan)throw new Exception("Automatic dependency check did not finish.");
                    using var bitmap=new Bitmap(form.Width,form.Height);
                    Application.DoEvents();
                    form.DrawToBitmap(bitmap,new Rectangle(0,0,form.Width,form.Height));
                    bitmap.Save("preview.png");
                    File.WriteAllText("render-state.txt",$"MO2 selector: {form.UsesMo2List}\nPlugins: {form.PluginCount}\n"+form.DiagnosticState);
                }
                catch(Exception ex){File.WriteAllText("render-error.txt",ex+"\n"+form.DiagnosticState);Environment.ExitCode=1;}
                finally{form.Close();}
            };
            Application.Run(form);return;
        }
        Application.Run(new MainForm(args.FirstOrDefault()));
    }
}
