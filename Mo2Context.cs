using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
namespace ArmorPackSplitter;
internal sealed record Mo2Assets(string Profile, List<string> Roots,List<string> Plugins);
internal sealed record PluginChoice(string Name,string Path,bool Active)
{
    public override string ToString()=>Name+(Active?"":"  [inactive]");
}
internal static class Mo2Context
{
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]private static extern IntPtr GetModuleHandle(string moduleName);
    internal static bool IsRunningUnderMo2=>GetModuleHandle("usvfs_x64.dll")!=IntPtr.Zero||GetModuleHandle("usvfs_x86.dll")!=IntPtr.Zero;
    internal static List<PluginChoice> PluginChoices(Mo2Assets context,string dataFolder)
    {
        var providers=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        bool Plugin(string path)=>Path.GetExtension(path).ToLowerInvariant() is ".esp" or ".esm" or ".esl";
        foreach(var root in context.Roots)
            foreach(var path in Directory.EnumerateFiles(root,"*",SearchOption.TopDirectoryOnly).Where(Plugin))providers.TryAdd(Path.GetFileName(path),path);
        var vanilla=new HashSet<string>(StringComparer.OrdinalIgnoreCase){"Skyrim.esm","Update.esm","Dawnguard.esm","HearthFires.esm","Dragonborn.esm","_ResourcePack.esl"};
        if(Directory.Exists(dataFolder))
            foreach(var path in Directory.EnumerateFiles(dataFolder,"*",SearchOption.TopDirectoryOnly).Where(Plugin))
                if(vanilla.Contains(Path.GetFileName(path))||Path.GetFileName(path).StartsWith("cc",StringComparison.OrdinalIgnoreCase))providers.TryAdd(Path.GetFileName(path),path);
        var active=context.Plugins.Concat(vanilla).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return providers.Select(p=>new PluginChoice(p.Key,p.Value,active.Contains(p.Key)||p.Key.StartsWith("cc",StringComparison.OrdinalIgnoreCase))).OrderBy(p=>p.Name,StringComparer.OrdinalIgnoreCase).ToList();
    }
    private static readonly string[] GeneratedPlugins=["Requiem for the Indifferent.esp","PGPatcher.esp","PG_1.esp","DynDOLOD.esm","DynDOLOD.esp","Occlusion.esp"];
    internal static bool IsGenerated(string directory)
    {
        if(File.Exists(Path.Combine(directory,"NPC_Token.json")) || File.Exists(Path.Combine(directory,"ParallaxGen_Diff.json")))return true;
        if(GeneratedPlugins.Any(p=>File.Exists(Path.Combine(directory,p))))return true;
        var name=Path.GetFileName(directory);
        return Regex.IsMatch(name,@"(?:Synthesis|TexGen|xLODGen|DynDOLOD|ParallaxGen|Reqtificator).*Output|Output.*(?:Synthesis|TexGen|xLODGen|DynDOLOD|ParallaxGen|Reqtificator)",RegexOptions.IgnoreCase);
    }
    internal static Mo2Assets? Find(string sourcePath,string dataFolder)
    {
        foreach(var start in new[]{Path.GetDirectoryName(sourcePath),dataFolder})
        {
            for(var dir=start is null?null:new DirectoryInfo(start);dir is not null;dir=dir.Parent)
            {
                var ini=Path.Combine(dir.FullName,"ModOrganizer.ini");
                if(!File.Exists(ini))continue;
                var lines=File.ReadAllLines(ini);
                var selected=lines.FirstOrDefault(l=>l.StartsWith("selected_profile=",StringComparison.OrdinalIgnoreCase))?.Split('=',2)[1];
                if(selected is null)return null;
                if(selected.StartsWith("@ByteArray(")&&selected.EndsWith(')'))selected=selected[11..^1];
                if(selected.IndexOfAny(Path.GetInvalidFileNameChars())>=0)return null;
                var modlist=Path.Combine(dir.FullName,"profiles",selected,"modlist.txt");
                var mods=Path.Combine(dir.FullName,"mods");
                if(!File.Exists(modlist)||!Directory.Exists(mods))return null;
                var roots=File.ReadAllLines(modlist).Where(l=>l.StartsWith('+')).Select(l=>l[1..]).Where(n=>!n.EndsWith("_separator",StringComparison.OrdinalIgnoreCase))
                    .Select(n=>Path.GetFullPath(Path.Combine(mods,n)))
                    .Where(p=>p.StartsWith(mods+"\\",StringComparison.OrdinalIgnoreCase)&&Directory.Exists(p)&&!IsGenerated(p)).ToList();
                // MO2 modlist.txt is saved highest priority first.
                var pluginList=Path.Combine(dir.FullName,"profiles",selected,"plugins.txt");
                var plugins=File.Exists(pluginList)?File.ReadAllLines(pluginList).Where(l=>l.StartsWith('*')).Select(l=>l[1..].Trim()).Where(l=>l.Length>0).ToList():[];
                return new(selected,roots,plugins);
            }
        }
        return null;
    }
}
