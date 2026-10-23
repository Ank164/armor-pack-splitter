using System.Text.Json;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace ArmorPackSplitter;
internal static class SelfTest
{
    public static int Run(string? realSource)
    {
        var log=new List<string>();
        var scratch=Path.Combine(Path.GetTempPath(),"ArmorPackSplitter-test-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            var source=new SkyrimMod(ModKey.FromNameAndExtension("Fixture.esp"),SkyrimRelease.SkyrimSE);
            var addon=source.ArmorAddons.AddNew();addon.EditorID="SharedAddon";
            var armor=source.Armors.AddNew();armor.EditorID="TestCuirass";armor.Name="Test Set Cuirass";armor.Armature.Add(addon.FormKey);
            var boots=source.Armors.AddNew();boots.EditorID="TestBoots";boots.Name="Test Set Boots";boots.Armature.Add(addon.FormKey);
            var unrelated=source.Armors.AddNew();unrelated.EditorID="OtherArmor";unrelated.Name="Other Armor";
            var recipe=source.ConstructibleObjects.AddNew();recipe.EditorID="RecipeTest";recipe.CreatedObject.SetTo(armor.FormKey);recipe.CreatedObjectCount=1;
            var path=Path.Combine(scratch,"Fixture.esp");
            source.BeginWrite.ToPath(path).WithLoadOrder(Array.Empty<ModKey>()).WithDataFolder(scratch).Write();
            var engine=new ExportEngine(path);
            var higher=Path.Combine(scratch,"higher");Directory.CreateDirectory(higher);File.Copy(path,Path.Combine(higher,"Fixture.esp"));
            var pickerChoices=Mo2Context.PluginChoices(new Mo2Assets("Fixture",[higher,scratch],["Fixture.esp"]),scratch);
            Assert(pickerChoices.Count(p=>p.Name=="Fixture.esp")==1&&pickerChoices.Single(p=>p.Name=="Fixture.esp").Active&&pickerChoices.Single(p=>p.Name=="Fixture.esp").Path.StartsWith(higher,StringComparison.OrdinalIgnoreCase),"MO2 selector deduplicates plugins and uses the highest authored provider");
            Assert(engine.Sets.Sum(s=>s.Pieces.Count)==3,"Every armor grouped once");
            using var plan=engine.Plan([armor.FormKey,boots.FormKey],true,scratch,[]);
            Assert(plan.Errors.Count==0,"Fixture dependency validation");
            Assert(plan.Records.Count==4,"Shared addon deduplicated; selected recipe included; unrelated armor excluded");
            var payload=Path.Combine(scratch,"payload.dds");var payloadBytes=new byte[]{1,5,9,2,4,8};File.WriteAllBytes(payload,payloadBytes);
            plan.Files.Add(new ExportFile(@"textures\fixture\payload.dds",payload,payload,Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payloadBytes)),["fixture asset"]));
            var result=engine.Export(plan,scratch,"Fixture Export");
            var written=ExportEngine.OpenMutable(Path.Combine(result,"Fixture Export.esp"));
            Assert(written.Armors.Count==2&&written.ArmorAddons.Count==1&&written.ConstructibleObjects.Count==1,"Export counts round-trip");
            Assert(written.IsSmallMaster&&written.EnumerateMajorRecords().All(r=>r.FormKey.ID is >=0x800 and <=0xFFF),"Exported ESP is ESL-flagged with compact record IDs");
            Assert(written.EnumerateMajorRecords().All(r=>r.FormKey.ModKey==written.ModKey),"All exported records own new FormIDs");
            Assert(!written.MasterReferences.Any(m=>m.Master==source.ModKey),"Source master removed");
            Assert(written.Armors.All(a=>a.Armature.All(l=>l.FormKey.ModKey==written.ModKey)),"Nested form links remapped");
            Assert(written.ConstructibleObjects.Single().CreatedObject.FormKey.ModKey==written.ModKey,"Recipe product remapped");
            var zipPath=engine.ExportZip(plan,Path.Combine(scratch,"Installable.zip"),"Zip Fixture");
            var extracted=Path.Combine(scratch,"installed");
            System.IO.Compression.ZipFile.ExtractToDirectory(zipPath,extracted);
            Assert(File.Exists(Path.Combine(extracted,"Zip Fixture.esp"))&&File.Exists(Path.Combine(extracted,"export-manifest.json")),"Installable ZIP has ESP and report at root");
            var installed=ExportEngine.OpenMutable(Path.Combine(extracted,"Zip Fixture.esp"));
            Assert(installed.Armors.Count==2&&!installed.MasterReferences.Any(m=>m.Master==source.ModKey),"ZIP extraction round-trips standalone records");
            Assert(installed.IsSmallMaster&&File.ReadAllBytes(Path.Combine(extracted,@"textures\fixture\payload.dds")).SequenceEqual(payloadBytes),"Installed ZIP retains ESL flag and asset bytes under Data-relative paths");
            try{engine.ExportZip(plan,zipPath,"Zip Fixture");throw new Exception("ZIP overwritten without explicit option");}catch(IOException){log.Add("PASS: Existing ZIP preserved by default");}
            try{engine.Export(plan,scratch,"Fixture Export");throw new Exception("Existing mod was overwritten");}catch(IOException){log.Add("PASS: Existing output refused");}
            try{AssetResolver.SafeCombine(scratch,@"..\outside.dds");throw new Exception("Traversal accepted");}catch(IOException){log.Add("PASS: Unsafe asset path refused");}
            var missingMod=new SkyrimMod(ModKey.FromNameAndExtension("MissingMesh.esp"),SkyrimRelease.SkyrimSE);
            var missingArmor=missingMod.Armors.AddNew();missingArmor.EditorID="MissingArmor";missingArmor.Name="Missing Armor";
            missingArmor.WorldModel=new Mutagen.Bethesda.Plugins.Records.GenderedItem<ArmorModel?>(null,new ArmorModel {Model=new Model {File=@"Missing\Test_1.nif"}});
            var missingMeshPath=Path.Combine(scratch,"MissingMesh.esp");
            missingMod.BeginWrite.ToPath(missingMeshPath).WithLoadOrder(Array.Empty<ModKey>()).WithDataFolder(scratch).Write();
            var missingEngine=new ExportEngine(missingMeshPath);
            using var missingPlan=missingEngine.Plan([missingArmor.FormKey],false,scratch,[]);
            Assert(missingPlan.ReviewIssues.Any(e=>e.Contains("Missing")),"Missing mesh is reported");
            try{missingEngine.Export(missingPlan,scratch,"Blocked missing");throw new Exception("Incomplete export accepted without explicit option");}catch(InvalidOperationException){log.Add("PASS: Incomplete export requires explicit option");}
            var incomplete=missingEngine.Export(missingPlan,scratch,"Incomplete fixture",allowIncomplete:true);
            using(var manifest=JsonDocument.Parse(File.ReadAllText(Path.Combine(incomplete,"export-manifest.json"))))Assert(!manifest.RootElement.GetProperty("complete").GetBoolean(),"Explicit incomplete export records omissions");
            var xml=System.Text.Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><system><!-- 中文 -- comment --><file path=\"衣服.xml\"/></system>");
            var doc=AssetResolver.ReadDependencyXml(xml,out var ignored);
            Assert(ignored&&doc.Root?.Element("file")?.Attribute("path")?.Value=="衣服.xml","Chinese XML preserved while tolerating malformed comments");
            Assert(AssetResolver.TryAsset(@"F:\Game\Data\textures\衣服\normal.dds",out var relative)&&relative==@"textures\衣服\normal.dds","Author-machine Data paths resolve safely with Unicode");
            var weighted=source.ArmorAddons.AddNew();
            weighted.WorldModel=new Mutagen.Bethesda.Plugins.Records.GenderedItem<Model?>(new Model{File=@"Test\male_1.nif"},new Model{File=@"Test\female_1.nif"});
            weighted.WeightSliderEnabled.Male=false;weighted.WeightSliderEnabled.Female=true;
            Assert(!AssetResolver.RequiresWeightVariant(weighted,@"Meshes\Test\male_0.nif"),"Disabled male weight slider does not require an invented low-weight mesh");
            Assert(AssetResolver.RequiresWeightVariant(weighted,@"Meshes\Test\female_0.nif"),"Enabled female weight slider still requires its weight pair");
            using(var issuePlan=new ExportPlan())
            {
                issuePlan.ReviewIssues.Add("Missing required fixture mesh");issuePlan.Notes.Add("Missing optional morph fixture.tri");
                var text=MainForm.PreviewText(issuePlan,1,[],false);
                Assert(text.Contains("REAL ERRORS / REQUIRED DEPENDENCIES")&&text.Contains("OPTIONAL FILES — NOT EXPORT BLOCKERS")&&text.Contains("Missing required fixture mesh")&&text.Contains("Missing optional morph fixture.tri"),"Real errors and optional details are always rendered in separate sections");
            }
            var capacitySource=new SkyrimMod(ModKey.FromNameAndExtension("Capacity.esp"),SkyrimRelease.SkyrimSE);
            for(int i=0;i<2048;i++)capacitySource.Armors.AddNew().EditorID="CapacityArmor"+i;
            var capacityPath=Path.Combine(scratch,"Capacity.esp");
            capacitySource.BeginWrite.ToPath(capacityPath).WithLoadOrder(Array.Empty<ModKey>()).WithDataFolder(scratch).Write();
            var capacityEngine=new ExportEngine(capacityPath);
            using(var capacityPlan=capacityEngine.Plan(capacityEngine.Source.Armors.Select(a=>a.FormKey),false,scratch,[]))
            {
                var capacityOutput=capacityEngine.Export(capacityPlan,scratch,"Capacity Export");
                var maxWritten=ExportEngine.OpenMutable(Path.Combine(capacityOutput,"Capacity Export.esp"));
                Assert(maxWritten.IsSmallMaster&&maxWritten.Armors.Count==2048&&maxWritten.Armors.Max(a=>a.FormKey.ID)==0xFFF,"ESL capacity boundary fits exactly 2,048 records");
                capacityPlan.Records.Add(capacityPlan.Records[0]);
                try{capacityEngine.Export(capacityPlan,scratch,"Too Large");throw new Exception("ESL overflow accepted");}catch(InvalidOperationException ex)when(ex.Message.Contains("2,048")){log.Add("PASS: ESL overflow refused before writing");}
            }
            var bsRoot=Path.Combine(scratch,@"CalienteTools\BodySlide");
            Directory.CreateDirectory(Path.Combine(bsRoot,"SliderSets"));
            Directory.CreateDirectory(Path.Combine(bsRoot,@"ShapeData\Shared"));
            File.WriteAllBytes(Path.Combine(bsRoot,@"ShapeData\Shared\Body.osd"),[1,2,3]);
            Directory.CreateDirectory(Path.Combine(bsRoot,@"ShapeData\Local"));
            File.WriteAllBytes(Path.Combine(bsRoot,@"ShapeData\Local\Input.nif"),[1]);
            File.WriteAllBytes(Path.Combine(bsRoot,@"ShapeData\Local\Local.osd"),[1]);
            File.WriteAllText(Path.Combine(bsRoot,@"SliderSets\Mixed.osp"),"""
                <SliderSetInfo version="1"><SliderSet name="Selected"><DataFolder>Local</DataFolder><SourceFile>Input.nif</SourceFile><OutputPath>meshes\Selected</OutputPath><OutputFile>Armor</OutputFile><Shape target="Body" DataFolder="Shared">Body</Shape><Slider name="Test"><Data target="Body">Body.osd\Delta</Data><Data target="Body" local="true">Local.osd\Delta</Data></Slider></SliderSet><SliderSet name="Unrelated"><OutputPath>meshes\Other</OutputPath><OutputFile>Armor</OutputFile></SliderSet></SliderSetInfo>
                """);
            using(var bsResolver=new AssetResolver(scratch,scratch,[],"Fixture.esp"))
            using(var bsPlan=new ExportPlan())
            {
                Directory.CreateDirectory(bsPlan.Cache);
                var bsQueue=new Queue<(string Path,string From)>();bsQueue.Enqueue((@"meshes\Selected\Armor_1.nif","fixture"));
                bsResolver.CollectBodySlide(bsPlan,bsQueue);
                var filtered=System.Xml.Linq.XDocument.Load(bsPlan.Files.Single().StagedPath);
                Assert(filtered.Root!.Elements("SliderSet").Single().Attribute("name")?.Value=="Selected","BodySlide matches output paths and excludes unrelated projects in the same OSP");
                Assert(bsQueue.Any(e=>e.Path.EndsWith(@"Shared\Body.osd"))&&bsQueue.Any(e=>e.Path.EndsWith(@"Local\Local.osd"))&&!bsQueue.Any(e=>e.Path.Contains("Delta")),"BodySlide resolves shared versus local slider folders and OSD dataset suffixes");
                Assert(bsQueue.Any(e=>e.Path.EndsWith(@"Local\Input.nif")),"BodySlide source mesh queued");
            }
            PreviewSelfTest.Run(scratch,Assert);
            log.Add("Fixture checks completed.");
            if(realSource is not null)
            {
                var real=new ExportEngine(realSource);
                log.Add($"REAL: {real.Source.Armors.Count} armors; {real.Sets.Count} suggested sets");
                File.WriteAllText("test-catalog.json",JsonSerializer.Serialize(real.Sets.Select(s=>new{s.Name,pieces=s.Pieces.Select(ExportEngine.DisplayName)}),new JsonSerializerOptions{WriteIndented=true}));
                var chosen=real.Sets.First(s=>s.Name.Contains(Environment.GetEnvironmentVariable("ASE_TEST_SET")??"Red Moon",StringComparison.OrdinalIgnoreCase));
                var extraRoots=(Environment.GetEnvironmentVariable("ASE_ASSET_ROOTS")??"").Split(';',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries);
                var dataFolder=Environment.GetEnvironmentVariable("ASE_DATA_FOLDER")??Settings.Load().DataFolder;
                using var realPlan=real.Plan(chosen.Pieces.Select(a=>a.FormKey),true,dataFolder,Environment.GetEnvironmentVariable("ASE_TEST_MO2")=="1"?[]:extraRoots,useMo2:Environment.GetEnvironmentVariable("ASE_TEST_MO2")=="1");
                log.Add($"REAL: {chosen.Name}: {chosen.Pieces.Count} armors, {realPlan.Records.Count} records, {realPlan.Files.Count} files; {realPlan.Errors.Count} errors");
                log.AddRange(realPlan.Errors);
                log.AddRange(realPlan.ReviewIssues);
                File.WriteAllText("test-assets.json",JsonSerializer.Serialize(realPlan.Files.Select(f=>new{f.Path,f.Source}),new JsonSerializerOptions{WriteIndented=true}));
                if(Environment.GetEnvironmentVariable("ASE_EXPECT_REVIEW")=="1")
                {
                    Assert(realPlan.Errors.Count==0&&realPlan.ReviewIssues.Count>0,"Real missing dependencies remain visible without a false hard error");
                    if(chosen.Name=="Regina")
                    {
                        Assert(realPlan.Files.Any(f=>f.Path.Equals(@"textures\putra\elvenia\cube.dds",StringComparison.OrdinalIgnoreCase)&&f.Source.Contains(".bsa::",StringComparison.OrdinalIgnoreCase)),"External active-plugin BSA dependency resolved");
                        Assert(!realPlan.ReviewIssues.Any(s=>s.Contains(@"hair_0.nif",StringComparison.OrdinalIgnoreCase)),"Disabled Regina hair weight counterpart is not reported missing");
                    }
                }
                else if(realPlan.Errors.Count==0)
                {
                    var exportName=chosen.Name+" Standalone Test "+DateTime.Now.ToString("yyyyMMdd-HHmmss");
                    var output=real.ExportZip(realPlan,Path.Combine(Environment.CurrentDirectory,"test-output",exportName+".zip"),exportName);
                    log.Add("REAL EXPORT: "+output);
                }
                else throw new InvalidOperationException("Real pack export has unresolved dependencies. See preceding errors.");
            }
            File.WriteAllLines("self-test.txt",log);
            return 0;
        }
        catch(Exception ex){log.Add(ex.ToString());File.WriteAllLines("self-test.txt",log);return 1;}
        finally{Directory.Delete(scratch,true);}
        void Assert(bool value,string message){if(!value)throw new Exception("FAIL: "+message);log.Add("PASS: "+message);}
    }
}
