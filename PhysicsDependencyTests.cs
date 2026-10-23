using System.Security.Cryptography;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using NiflySharp;
using NiflySharp.Blocks;

namespace ArmorPackSplitter;

internal static class PhysicsDependencyTests
{
    internal static void Run(string scratch,Action<bool,string> assert)
    {
        var root=Path.Combine(scratch,"physics-cases");Directory.CreateDirectory(root);
        var source=new SkyrimMod(ModKey.FromNameAndExtension("PhysicsCases.esp"),SkyrimRelease.SkyrimSE);
        var groupFixture=new SkyrimMod(ModKey.FromNameAndExtension("Groups.esp"),SkyrimRelease.SkyrimSE);
        foreach(var name in new[]{"[Mystic Banha] - Boots","[Mystic Banha] - Earrings","[Mystic Dhyana] - Boots","[Mystic Dhyana] - Pendant"})groupFixture.Armors.AddNew().Name=name;
        var groups=ExportEngine.DetectSets(groupFixture.Armors.ToList());
        assert(groups.Count==2&&groups.Any(g=>g.Name=="[Mystic Banha]"&&g.Pieces.Count==2)&&groups.Any(g=>g.Name=="[Mystic Dhyana]"&&g.Pieces.Count==2),"Complete bracketed outfit names prevent Banha and Dhyana from being merged into one Mystic set");
        using var resolver=new AssetResolver(root,root,[],"PhysicsCases.esp");
        var legacy=Make("Legacy",[("HDT Havok Path",@"meshes\physics\old-single.xml",false),("HDT Havok Path",@"meshes\physics\old-list.xml",true)]);
        using(var plan=resolver.Build([legacy],source.ModKey,null))
        {
            assert(plan.ReviewIssues.Count==0&&plan.Errors.Count==0,"Missing legacy HDT-PE paths do not block Skyrim SE exports");
            assert(plan.Notes.Count(n=>n.StartsWith("Missing optional legacy HDT-PE"))==2,"Both single-string and string-list legacy paths remain visible as optional notes");
        }
        var modern=Make("Modern",[("HDT Skinned Mesh Physics Object",@"meshes\physics\active.xml",false)]);
        using(var plan=resolver.Build([modern],source.ModKey,null))
            assert(plan.ReviewIssues.Any(n=>n.Contains("active.xml")&&n.Contains("SMP physics")),"Genuine missing SMP configurations remain required dependencies");
        var shared=Make("Shared",[("HDT Havok Path",@"meshes\physics\shared.xml",false),("HDT Skinned Mesh Physics Object",@"meshes\physics\shared.xml",false)]);
        using(var plan=resolver.Build([shared],source.ModKey,null))
            assert(plan.ReviewIssues.Any(n=>n.Contains("shared.xml"))&&!plan.Notes.Any(n=>n.StartsWith("Missing optional")&&n.Contains("shared.xml")),"A required SMP reference wins when legacy metadata names the same missing file");
        var preservedPath=AssetResolver.SafeCombine(root,@"meshes\physics\preserved.xml");Directory.CreateDirectory(Path.GetDirectoryName(preservedPath)!);
        var bytes=System.Text.Encoding.UTF8.GetBytes("<legacy-hdt-pe><!-- preserved unchanged --></legacy-hdt-pe>");File.WriteAllBytes(preservedPath,bytes);
        var preserved=Make("Preserved",[("HDT Havok Path",@"meshes\physics\preserved.xml",false)]);
        using(var fresh=new AssetResolver(root,root,[],"PhysicsCases.esp"))
        using(var plan=fresh.Build([preserved],source.ModKey,null))
        {
            assert(plan.Files.Any(f=>f.Path.EndsWith("preserved.xml")&&File.ReadAllBytes(f.StagedPath).SequenceEqual(bytes)),"Available legacy physics files are preserved byte-for-byte");
            var mesh=plan.Files.Single(f=>f.Path.EndsWith("Preserved.nif"));
            assert(mesh.Hash==Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(mesh.Source))),"Legacy classification does not rewrite the source mesh");
        }
        var unknown=Make("Unknown",[("Custom Physics",@"meshes\physics\unclassified.xml",false)]);
        using(var plan=resolver.Build([unknown],source.ModKey,null))
            assert(plan.ReviewIssues.Any(n=>n.Contains("unclassified.xml")),"Unrecognized XML references are not silently downgraded to optional");

        Armor Make(string name,(string Tag,string Path,bool Plural)[] references)
        {
            var nif=new NifFile();nif.Create(NiVersion.GetSSE(),true);
            nif.GetRootNode().ExtraDataList=new NiBlockRefArray<NiExtraData>();
            foreach(var reference in references)
            {
                NiExtraData extra=reference.Plural?
                    new NiStringsExtraData{Name=new NiStringRef(reference.Tag),Data=[new(reference.Path)]}:
                    new NiStringExtraData{Name=new NiStringRef(reference.Tag),StringData=new NiStringRef(reference.Path)};
                nif.GetRootNode().ExtraDataList.AddBlockRef(nif.AddBlock(extra));
            }
            var path=AssetResolver.SafeCombine(root,@"meshes\physics\"+name+".nif");Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if(nif.Save(path)!=0)throw new IOException("Cannot write physics fixture.");
            var armor=source.Armors.AddNew();armor.EditorID=name;
            armor.WorldModel=new GenderedItem<ArmorModel?>(null,new ArmorModel{Model=new Model{File=@"physics\"+name+".nif"}});
            return armor;
        }
    }
}
