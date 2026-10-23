using System.Numerics;
using System.Security.Cryptography;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using NiflySharp;
using NiflySharp.Blocks;
using NiflySharp.Structs;

namespace ArmorPackSplitter;

internal static class PreviewSelfTest
{
    internal static void Run(string scratch,Action<bool,string> assert)
    {
        var mod=new SkyrimMod(ModKey.FromNameAndExtension("PreviewFixture.esp"),SkyrimRelease.SkyrimSE);
        var top=mod.Armors.AddNew();top.EditorID="Top";top.Name="Fixture Top";
        var boots=mod.Armors.AddNew();boots.EditorID="Boots";boots.Name="Fixture Boots";
        var shared=mod.Armors.AddNew();shared.EditorID="SharedTop";shared.Name="Shared Top";
        var addon1=mod.ArmorAddons.AddNew();var addon2=mod.ArmorAddons.AddNew();
        top.Armature.Add(addon1.FormKey);boots.Armature.Add(addon2.FormKey);shared.Armature.Add(addon1.FormKey);
        addon1.WorldModel=new GenderedItem<Model?>(null,new Model{File=@"Preview\Top_1.nif"});
        addon2.WorldModel=new GenderedItem<Model?>(null,new Model{File=@"Preview\Boots_1.nif"});
        addon1.WeightSliderEnabled.Female=true;addon2.WeightSliderEnabled.Female=true;
        top.WorldModel=new GenderedItem<ArmorModel?>(null,new ArmorModel{Model=new Model{File=@"Preview\Inventory.nif"}});
        using var plan=new ExportPlan{Records=[top,boots,shared,addon1,addon2]};Directory.CreateDirectory(plan.Cache);
        var texture=AssetResolver.SafeCombine(plan.Cache,@"textures\preview\color.dds");Directory.CreateDirectory(Path.GetDirectoryName(texture)!);
        using(var stream=File.Create(texture))
        using(var writer=new BinaryWriter(stream))
        {
            writer.Write(0x20534444);writer.Write(124);writer.Write(0x100F);writer.Write(2);writer.Write(2);writer.Write(8);writer.Write(0);writer.Write(0);
            for(int i=0;i<11;i++)writer.Write(0);
            writer.Write(32);writer.Write(0x41);writer.Write(0);writer.Write(32);writer.Write(0x00ff0000);writer.Write(0x0000ff00);writer.Write(0x000000ff);writer.Write(0xff000000u);
            writer.Write(0x1000);for(int i=0;i<4;i++)writer.Write(0);
            for(int i=0;i<4;i++){writer.Write((byte)10);writer.Write((byte)40);writer.Write((byte)220);writer.Write((byte)0);}
        }
        Stage(@"textures\preview\color.dds",texture);
        MakeMesh(@"meshes\Preview\Top_1.nif",10,5);
        MakeMesh(@"meshes\Preview\Top_0.nif",10,3);
        MakeMesh(@"meshes\Preview\Boots_1.nif",0,0);
        MakeMesh(@"meshes\Preview\Boots_0.nif",0,-2);
        MakeMesh(@"meshes\Preview\Inventory.nif",0,9000);
        var scene=PreviewSceneReader.Read(plan,[top.FormKey,boots.FormKey],true,false);
        assert(scene.LoadedModels==2&&scene.Meshes.Count==2,"Combined preview includes both checked armor pieces"+(scene.Meshes.Count==2?"":$" (loaded={scene.LoadedModels}; meshes={scene.Meshes.Count}; {string.Join("; ",scene.Notes)})"));
        assert(scene.Meshes.Any(m=>m.Positions.Min(p=>p.Z)==15)&&scene.Meshes.Any(m=>m.Positions.Min(p=>p.Z)==0),"Combined preview preserves parent and shape transforms instead of centering pieces separately");
        assert(scene.Meshes.All(m=>!m.File.Contains("Inventory")),"Preview uses worn meshes and excludes dropped inventory models");
        assert(scene.Meshes.All(m=>m.Texture is not null)&&scene.Notes.Count==0,"Preview resolves parsed shader texture strings and decodes DDS files");
        var pixels=new byte[16];scene.Meshes[0].Texture!.CopyPixels(pixels,8,0);
        assert(pixels[0]==10&&pixels[1]==40&&pixels[2]==220&&pixels[3]==255,"Opaque material treats DDS alpha as a mask instead of making the outfit invisible");
        var transparent=PreviewSceneReader.ReadTexture(texture,true);transparent.CopyPixels(pixels,8,0);
        assert(pixels[3]==0,"Transparent materials retain their texture alpha");
        var one=PreviewSceneReader.Read(plan,[boots.FormKey],true,false);
        assert(one.LoadedModels==1&&one.Meshes.All(m=>m.Piece=="Fixture Boots"),"Unchecking a piece removes its geometry from the combined preview");
        var dedup=PreviewSceneReader.Read(plan,[top.FormKey,shared.FormKey,boots.FormKey],true,false);
        assert(dedup.Pieces==3&&dedup.LoadedModels==2,"Shared worn models are rendered once across checked pieces");
        var low=PreviewSceneReader.Read(plan,[top.FormKey,boots.FormKey],true,true);
        assert(low.Meshes.All(m=>m.File.EndsWith("_0.nif"))&&low.Meshes.Any(m=>m.Positions.Min(p=>p.Z)==13),"Low-weight preview consistently chooses every checked piece's low-weight mesh");
        var male=PreviewSceneReader.Read(plan,[top.FormKey,boots.FormKey],false,false);
        assert(male.Meshes.Count==0&&male.Notes.Count==2,"Missing gender models are explained without showing the other gender silently");
        var replacementPath=AssetResolver.SafeCombine(plan.Cache,@"textures\preview\alternate.dds");
        var replacementBytes=File.ReadAllBytes(texture);
        for(int i=128;i<replacementBytes.Length;i+=4){replacementBytes[i]=50;replacementBytes[i+1]=60;replacementBytes[i+2]=70;}
        File.WriteAllBytes(replacementPath,replacementBytes);Stage(@"textures\preview\alternate.dds",replacementPath);
        var textureSet=mod.TextureSets.AddNew();textureSet.Diffuse=@"preview\alternate.dds";plan.Records.Add(textureSet);
        var alternate=new AlternateTexture{Name="Fixture Shape",Index=0};alternate.NewTexture.SetTo(textureSet.FormKey);
        addon1.WorldModel.Female!.AlternateTextures=[alternate];
        var swapped=PreviewSceneReader.Read(plan,[top.FormKey],true,false);swapped.Meshes.Single().Texture!.CopyPixels(pixels,8,0);
        assert(pixels[0]==50&&pixels[1]==60&&pixels[2]==70,"Model-specific alternate texture sets are applied to the correct shape");
        using var canceled=new CancellationTokenSource();canceled.Cancel();
        try{PreviewSceneReader.Read(plan,[top.FormKey],true,false,canceled.Token);throw new Exception("Canceled preview completed");}
        catch(OperationCanceledException){assert(true,"Stale preview loads can be canceled after selection changes");}

        void Stage(string path,string file)=>plan.Files.Add(new(path,file,file,Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))),["preview fixture"]));
        void MakeMesh(string path,float parentZ,float shapeZ)
        {
            var nif=new NifFile();nif.Create(NiVersion.GetSSE(),true);
            var root=nif.GetRootNode();root.Translation=new Vector3(0,0,parentZ);
            var shape=new BSTriShape{Name=new NiStringRef("Fixture Shape"),Translation=new Vector3(0,0,shapeZ),Flags_ui=14,Flags_us=14};
            shape.Create(nif.Header.Version,[new(-1,0,0),new(1,0,0),new(0,0,2)],[new Triangle{V1=0,V2=1,V3=2}],[new TexCoord{U=0,V=0},new TexCoord{U=1,V=0},new TexCoord{U=.5f,V=1}],[new(0,-1,0),new(0,-1,0),new(0,-1,0)]);
            shape.IsSkinned=false;
            var set=new BSShaderTextureSet();set.Textures=[new(@"textures\preview\color.dds")];
            var setIndex=nif.AddBlock(set);
            var shader=new BSLightingShaderProperty{TextureSetRef=new NiBlockRef<BSShaderTextureSet>(setIndex),Alpha=1,UVScale=TexCoord.One};
            var shaderIndex=nif.AddBlock(shader);shape.ShaderPropertyRef=new NiBlockRef<BSShaderProperty>(shaderIndex);
            var shapeIndex=nif.AddBlock(shape);root.Children.AddBlockRef(shapeIndex);
            var full=AssetResolver.SafeCombine(plan.Cache,path);Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            if(nif.Save(full)!=0)throw new IOException("Could not save preview fixture mesh.");
            var check=new NifFile();check.Load(full);
            if(check.GetShapes().FirstOrDefault() is not BSTriShape saved||saved.VertexPositions.Count!=3)
                throw new IOException($"Invalid preview fixture: {check.GetShapes().Count()} shapes; "+string.Join("; ",check.GetShapes().Select(s=>$"{s.VertexCount} vertices, {s.TriangleCount} triangles, flags {s.Flags_ui}/{s.Flags_us}")));
            Stage(path,full);
        }
    }
}
