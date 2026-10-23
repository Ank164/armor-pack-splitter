using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using System.Text;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Assets;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using NiflySharp;

namespace ArmorPackSplitter;

internal sealed partial class AssetResolver : IDisposable
{
    private readonly List<string> roots;
    private readonly List<(string Path, IArchiveReader Reader)> archives = [];
    private Dictionary<string, (string Archive, IArchiveFile File)>? archiveFiles;
    private readonly HashSet<string> archiveMisses=new(StringComparer.OrdinalIgnoreCase);
    private bool archivesReady;
    private readonly string plugin;
    private readonly HashSet<string> explicitRoots;
    private readonly HashSet<string> masterNames;
    private readonly Dictionary<string,int> archivePluginOrder=new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> archiveOnlyRoots=new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) {".dds", ".tga", ".nif", ".xml", ".hkx", ".tri", ".wav", ".xwm", ".fuz", ".pex"};

    public AssetResolver(string source, string data, IEnumerable<string> extras, string plugin, IEnumerable<string>? mo2Roots=null, IEnumerable<string>? masters=null,IEnumerable<string>? activePlugins=null)
    {
        this.plugin = plugin;
        explicitRoots=extras.Append(source).Where(Directory.Exists).Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        masterNames=(masters??[]).Append(plugin).Select(Path.GetFileNameWithoutExtension).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach(var active in (masters??[]).Concat(activePlugins??[]).Append(plugin).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var stem=Path.GetFileNameWithoutExtension(active);
            if(!string.IsNullOrWhiteSpace(stem))archivePluginOrder[stem]=archivePluginOrder.Count;
        }
        roots = extras.Concat(mo2Roots??[]).Concat(new[] {source,data}).Where(Directory.Exists).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var root in roots)
            if (Mo2Context.IsGenerated(root))
            {
                // MO2's virtual Data combines every enabled mod, including generated markers.
                // Resolve loose assets through the authored physical providers instead.
                if(root.Equals(Path.GetFullPath(data),StringComparison.OrdinalIgnoreCase))archiveOnlyRoots.Add(root);
                else throw new InvalidOperationException("An asset search folder contains generated output. Choose original authored assets.");
            }
    }

    public ExportPlan Build(List<IMajorRecord> records, ModKey sourceKey, Action<string>? progress)
    {
        var plan = new ExportPlan {Records=records};
        Directory.CreateDirectory(plan.Cache);
        try
        {
            var queue = new Queue<(string Path, string From)>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var record in records)
            {
                foreach (var asset in record.EnumerateAssetLinks(AssetLinkQuery.Listed))
                    if (!asset.IsNull) queue.Enqueue((asset.DataRelativePath.ToString(),AssetReason(record,asset)));
                foreach(var asset in record.EnumerateAssetLinks(AssetLinkQuery.Inferred))
                    if(!asset.IsNull && (!asset.Extension.Equals(".nif",StringComparison.OrdinalIgnoreCase)||record is ArmorAddon addon&&RequiresWeightVariant(addon,asset.DataRelativePath.ToString())))
                        queue.Enqueue((asset.DataRelativePath.ToString(),AssetReason(record,asset)));
                // Dynamic lookup in compiled Papyrus cannot be remapped safely.
                if (record is IArmorGetter armor && armor.VirtualMachineAdapter?.Scripts.Count > 0)
                    plan.ReviewIssues.Add($"{armor.EditorID}: scripted equipment requires review; dynamic script lookups cannot be guaranteed independent.");
            }
            CollectBodySlide(plan,queue);
            while(queue.TryDequeue(out var entry))
            {
                string path;
                try { path=Normalize(entry.Path); }
                catch(Exception ex) { plan.Errors.Add($"Unsafe path from {entry.From}: {ex.Message}"); continue; }
                if (path.Length==0)continue;
                if(!seen.Add(path))
                {
                    var existing=plan.Files.FirstOrDefault(f=>f.Path.Equals(path,StringComparison.OrdinalIgnoreCase));
                    if(existing is not null&&!existing.Reasons.Contains(entry.From))existing.Reasons.Add(entry.From);
                    continue;
                }
                progress?.Invoke($"Inspecting files ({seen.Count}): {path}");
                var resolved = Resolve(path);
                if (resolved is null)
                {
                    if(Path.GetExtension(path).Equals(".tri",StringComparison.OrdinalIgnoreCase)||Path.GetExtension(path).Equals(".psc",StringComparison.OrdinalIgnoreCase))
                        plan.Notes.Add(Path.GetExtension(path).Equals(".psc",StringComparison.OrdinalIgnoreCase)?$"Missing optional source script: {path} (used by {entry.From}). Compiled scripts do not require their source files.":$"Missing optional morph file: {path} (used by {entry.From}). Equipment can be exported, but this morph file will remain unavailable.");
                    else plan.ReviewIssues.Add($"Missing {path} (used by {entry.From})");
                    continue;
                }
                var (bytes, provider) = resolved.Value;
                var cached = SafeCombine(plan.Cache,path);
                Directory.CreateDirectory(Path.GetDirectoryName(cached)!);
                File.WriteAllBytes(cached,bytes);
                plan.Files.Add(new(path,provider,cached,Convert.ToHexString(SHA256.HashData(bytes)),[entry.From]));
                var ext=Path.GetExtension(path);
                if (ext.Equals(".nif",StringComparison.OrdinalIgnoreCase))
                {
                    using var stream = new MemoryStream(bytes,false);
                    var nif = new NifFile();
                    if (nif.Load(stream)!=0 || !nif.Valid || nif.HasUnknownBlocks)
                    {
                        plan.ReviewIssues.Add($"Cannot fully inspect mesh {path}; the original file is available, but its nested dependencies are unverified.");
                        continue;
                    }
                    foreach(var text in MeshAssetStrings(nif))
                        if (TryAsset(text,out var link))
                        {
                            if(text.Replace('/','\\').Contains("\\data\\",StringComparison.OrdinalIgnoreCase))plan.Notes.Add($"Author-machine path resolved under Data: {text} -> {link}. The source mesh is copied unchanged.");
                            queue.Enqueue((link,path));
                        }
                    // Weight counterparts are selected from ARMA flags above, not guessed from every mesh filename.
                    var tri=Path.ChangeExtension(path,"tri");
                    if (Exists(tri)) queue.Enqueue((tri,path));
                }
                else if (ext.Equals(".xml",StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var doc=ReadDependencyXml(bytes,out var toleratedComments);
                        if(toleratedComments)plan.Notes.Add($"Physics XML {path}: malformed comments ignored for dependency scanning. The original file is copied unchanged.");
                        foreach(var element in doc.Descendants())
                        {
                            foreach(var value in element.Attributes().Select(a=>a.Value).Concat(element.HasElements?Array.Empty<string>():new[]{element.Value}))
                                if(TryAsset(value,out var link)) queue.Enqueue((link,path));
                        }
                    }
                    catch(Exception ex) { plan.ReviewIssues.Add($"Cannot inspect XML {path}: {ex.Message}. Its original file is copied unchanged; nested dependencies need review."); }
                }
                else if (ext.Equals(".pex",StringComparison.OrdinalIgnoreCase))
                    plan.ReviewIssues.Add($"Script {path} is available and will be copied, but dynamic script dependencies need manual review.");
                if (seen.Count>30000) throw new InvalidOperationException("Asset dependency walk exceeded 30,000 files.");
            }
            plan.Notes.Add("Records are read from the opened plugin, not generated overrides.");
            if(archiveOnlyRoots.Count>0)plan.Notes.Add("MO2 virtual Data contains generated assets. Loose files are resolved from the authored mod folders; base/master archives remain available.");
            plan.Notes.Add("Detected sets are suggestions. Review the piece checklist before exporting.");
            plan.Notes.Add("External plugin references remain required masters; body, skeleton, physics engine, and runtime requirements are not bundled.");
            plan.Notes.Add("Meshes are parsed for asset references and copied byte-for-byte. Matching BodySlide projects and their source mesh/slider dependencies are included; the source showroom is not included.");
            return plan;
        }
        catch {plan.Dispose(); throw;}
    }

    internal static bool RequiresWeightVariant(ArmorAddon addon,string path)
    {
        string Canonical(string s)=>Regex.Replace(s,@"_[01]\.nif$","_#.nif",RegexOptions.IgnoreCase);
        bool Match(Model? model,bool enabled)=>enabled&&model is not null&&Canonical(model.File.DataRelativePath.ToString()).Equals(Canonical(path),StringComparison.OrdinalIgnoreCase);
        return Match(addon.WorldModel?.Male,addon.WeightSliderEnabled.Male)||Match(addon.FirstPersonModel?.Male,addon.WeightSliderEnabled.Male)
            ||Match(addon.WorldModel?.Female,addon.WeightSliderEnabled.Female)||Match(addon.FirstPersonModel?.Female,addon.WeightSliderEnabled.Female);
    }

    internal static XDocument ReadDependencyXml(byte[] bytes,out bool ignoredComments)
    {
        ignoredComments=false;
        try
        {
            using var reader=XmlReader.Create(new MemoryStream(bytes),new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null});
            return XDocument.Load(reader);
        }
        catch(XmlException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var declaration=Encoding.ASCII.GetString(bytes.Take(256).ToArray());
            var match=Regex.Match(declaration,"encoding\\s*=\\s*['\"]([^'\"]+)['\"]",RegexOptions.IgnoreCase);
            var encoding=match.Success?Encoding.GetEncoding(match.Groups[1].Value):Encoding.UTF8;
            using var textReader=new StreamReader(new MemoryStream(bytes),encoding,true);
            var original=textReader.ReadToEnd();
            var cleaned=Regex.Replace(original,@"<!--[\s\S]*?-->","");
            if(cleaned==original)throw;
            using var reader=XmlReader.Create(new StringReader(cleaned),new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null});
            var doc=XDocument.Load(reader);
            ignoredComments=true;
            return doc;
        }
    }

    private static string AssetReason(IMajorRecord record,IAssetLinkGetter asset)
    {
        var roles=new List<string>();
        var path=asset.DataRelativePath.ToString();
        void ModelRole(Model? model,string role)
        {
            if(model is not null&&Regex.Replace(model.File.DataRelativePath.ToString(),@"_[01]\.nif$","_#.nif",RegexOptions.IgnoreCase).Equals(Regex.Replace(path,@"_[01]\.nif$","_#.nif",RegexOptions.IgnoreCase),StringComparison.OrdinalIgnoreCase))roles.Add(role);
        }
        if(record is Armor armor)
        {
            ModelRole(armor.WorldModel?.Male?.Model,"inventory / dropped model (male/default)");
            ModelRole(armor.WorldModel?.Female?.Model,"inventory / dropped model (female)");
        }
        if(record is ArmorAddon addon)
        {
            ModelRole(addon.WorldModel?.Male,"male worn model");ModelRole(addon.WorldModel?.Female,"female worn model");
            ModelRole(addon.FirstPersonModel?.Male,"male first-person model");ModelRole(addon.FirstPersonModel?.Female,"female first-person model");
        }
        return (record.EditorID??record.FormKey.ToString())+": "+(roles.Count>0?string.Join("; ",roles):"record asset dependency");
    }

    private static IEnumerable<string> MeshAssetStrings(NifFile nif)
    {
        foreach(var block in nif.Blocks)
        {
            var type=block.GetType();
            foreach(var property in type.GetProperties(BindingFlags.Public|BindingFlags.Instance))
            {
                // Node and shape names may resemble filenames; only path-bearing fields count.
                bool assetField = type.Name=="BSShaderTextureSet" && property.Name=="Textures"
                    || property.Name.EndsWith("Texture",StringComparison.Ordinal) && type.Name.Contains("Shader")
                    || type.Name=="NiSourceTexture" && property.Name=="FileName"
                    || type.Name=="NiStringExtraData" && property.Name=="StringData"
                    || type.Name=="NiStringsExtraData" && property.Name=="Data";
                if(!assetField || property.GetIndexParameters().Length>0)continue;
                foreach(var value in Strings(property.GetValue(block)))yield return value;
            }
        }
    }

    // Traverse parsed path fields, never search arbitrary NIF binary bytes.
    internal static IEnumerable<string> Strings(object? root)
    {
        var visited=new HashSet<object>(ReferenceEqualityComparer.Instance);
        var stack=new Stack<(object? Value,int Depth)>();
        stack.Push((root,0));
        while(stack.TryPop(out var item))
        {
            var value=item.Value;
            if(value is null || item.Depth>20) continue;
            if(value is string s) {yield return s; continue;}
            var type=value.GetType();
            if(type.IsPrimitive || type.IsEnum || value is decimal || !visited.Add(value)) continue;
            if(value is IEnumerable enumerable)
            {
                // Geometry buffers cannot contain file paths.
                if(type.IsArray && type.GetElementType() is {} elem && (elem.IsPrimitive || elem.Namespace=="System.Numerics")) continue;
                foreach(var child in enumerable) stack.Push((child,item.Depth+1));
                continue;
            }
            if(type.Namespace?.StartsWith("NiflySharp",StringComparison.Ordinal)!=true) continue;
            foreach(var property in type.GetProperties(BindingFlags.Instance|BindingFlags.Public))
            {
                if(property.GetIndexParameters().Length>0 || !property.CanRead) continue;
                if(property.PropertyType.IsPrimitive || property.PropertyType.IsEnum) continue;
                object? child;
                try { child=property.GetValue(value); } catch {continue;}
                stack.Push((child,item.Depth+1));
            }
            foreach(var field in type.GetFields(BindingFlags.Instance|BindingFlags.Public))
                if(!field.FieldType.IsPrimitive && !field.FieldType.IsEnum) stack.Push((field.GetValue(value),item.Depth+1));
        }
    }

    internal static bool TryAsset(string text,out string path)
    {
        path=text.Trim().Trim('"').Replace('/','\\');
        if(path.Length==0 || path.Length>1024 || path.IndexOfAny(['\r','\n','\0'])>=0 || !Extensions.Contains(Path.GetExtension(path))) return false;
        var ext=Path.GetExtension(path).ToLowerInvariant();
        // Authors sometimes embed their machine's absolute Data path. Resolve only its Data-relative suffix.
        var dataIndex=path.IndexOf("\\data\\",StringComparison.OrdinalIgnoreCase);
        if(dataIndex>=0)path=path[(dataIndex+6)..];
        if(path.StartsWith("Data\\",StringComparison.OrdinalIgnoreCase))path=path[5..];
        if (!path.StartsWith("textures\\",StringComparison.OrdinalIgnoreCase) && ext is ".dds" or ".tga") path="textures\\"+path.TrimStart('\\');
        else if (!path.StartsWith("meshes\\",StringComparison.OrdinalIgnoreCase) && !path.StartsWith("SKSE\\",StringComparison.OrdinalIgnoreCase) && ext is ".nif" or ".tri" or ".hkx" or ".xml") path="meshes\\"+path.TrimStart('\\');
        return true;
    }

    public static string Normalize(string path)
    {
        path=path.Replace('/','\\').Trim();
        if(path.StartsWith("Data\\",StringComparison.OrdinalIgnoreCase)) path=path[5..];
        if(Path.IsPathRooted(path) || path.Split('\\').Any(p=>p is ".." or "." || p.Contains(':'))) throw new IOException("Asset path leaves the Data directory: "+path);
        return path;
    }
    public static string SafeCombine(string root,string path)
    {
        path=Normalize(path);
        var absolute=Path.GetFullPath(Path.Combine(root,path));
        if(!absolute.StartsWith(Path.GetFullPath(root).TrimEnd('\\')+"\\",StringComparison.OrdinalIgnoreCase)) throw new IOException("Asset path leaves the output folder.");
        return absolute;
    }
    private bool Exists(string path) => roots.Where(r=>!archiveOnlyRoots.Contains(r)).Any(r=>File.Exists(SafeCombine(r,path))) || FindArchived(path) is not null;
    private (byte[] Bytes,string Provider)? Resolve(string path)
    {
        foreach(var root in roots)
        {
            if(archiveOnlyRoots.Contains(root))continue;
            var file=SafeCombine(root,path);
            if(File.Exists(file)) return (File.ReadAllBytes(file),file);
        }
        if(FindArchived(path) is {} entry) return (entry.File.GetBytes(),entry.Archive+"::"+path);
        return null;
    }
    private (string Archive,IArchiveFile File)? FindArchived(string path)
    {
        path=Normalize(path);
        archiveFiles??=new(StringComparer.OrdinalIgnoreCase);
        if(archiveFiles.TryGetValue(path,out var known))return known;
        if(archiveMisses.Contains(path))return null;
        EnsureArchives();
        var directory=Path.GetDirectoryName(path)??"";
        var name=Path.GetFileName(path);
        foreach(var (archive,reader) in archives)
            if(reader.TryGetFolder(directory,out var folder))
            {
                var file=folder.Files.FirstOrDefault(f=>Path.GetFileName(f.Path).Equals(name,StringComparison.OrdinalIgnoreCase));
                if(file is not null){archiveFiles[path]=(archive,file);return (archive,file);}
            }
        archiveMisses.Add(path);
        return null;
    }
    private void EnsureArchives()
    {
        if(archivesReady)return;
        var candidates=new List<(string Path,int Rank)>();
        var namesSeen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var root in roots)
        {
            foreach(var path in Directory.EnumerateFiles(root,"*.bsa").OrderBy(p=>p,StringComparer.OrdinalIgnoreCase))
            {
                var name=Path.GetFileNameWithoutExtension(path);
                int rank=archivePluginOrder.Where(p=>name.Equals(p.Key,StringComparison.OrdinalIgnoreCase)||name.StartsWith(p.Key+" - ",StringComparison.OrdinalIgnoreCase)).Select(p=>p.Value).DefaultIfEmpty(-1).Max();
                if(!explicitRoots.Contains(root)&&!name.StartsWith("Skyrim - ",StringComparison.OrdinalIgnoreCase)&&rank<0)continue;
                if(namesSeen.Add(Path.GetFileName(path)))candidates.Add((path,rank));
            }
        }
        // Later plugins' archives win; duplicate archive filenames were resolved by authored MO2 mod priority.
        foreach(var (path,_) in candidates.OrderByDescending(c=>c.Rank).ThenByDescending(c=>Path.GetFileNameWithoutExtension(c.Path),StringComparer.OrdinalIgnoreCase))
        {
                var reader=Archive.CreateReader(GameRelease.SkyrimSE,path);
                archives.Add((path,reader));
        }
        archivesReady=true;
    }
    public void Dispose()
    {
        foreach(var (_,reader) in archives) (reader as IDisposable)?.Dispose();
    }
}
