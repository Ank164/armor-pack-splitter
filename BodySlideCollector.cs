using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ArmorPackSplitter;

internal sealed partial class AssetResolver
{
    private const string BodySlideRoot = @"CalienteTools\BodySlide\";

    // Match the actual output model, never a guessed outfit/project name.
    internal static string BodySlideOutput(string path) => Regex.Replace(Normalize(path), @"(?:_[01])?\.nif$", "", RegexOptions.IgnoreCase);

    internal void CollectBodySlide(ExportPlan plan, Queue<(string Path,string From)> queue)
    {
        var outputs=queue.Where(e=>Path.GetExtension(e.Path).Equals(".nif",StringComparison.OrdinalIgnoreCase))
            .Select(e=>BodySlideOutput(e.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var outputNames=outputs.Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var availability=new Dictionary<string,bool>(StringComparer.OrdinalIgnoreCase);
        bool Available(string path)
        {
            if(!availability.TryGetValue(path,out var value))availability[path]=value=Exists(path);
            return value;
        }
        foreach(var file in ProjectFiles("SliderSets"))
        {
            try
            {
                var bytes=File.ReadAllBytes(file.Full);
                // Read text with BOM detection first so unrelated huge projects need no XML tree.
                using var textReader=new StreamReader(new MemoryStream(bytes),System.Text.Encoding.UTF8,true);
                var text=textReader.ReadToEnd();
                if(!Regex.Matches(text,@"<OutputFile\b[^>]*>([^<]+)</OutputFile>",RegexOptions.IgnoreCase).Any(m=>
                {
                    var output=m.Groups[1].Value.Trim();
                    return outputNames.Contains(Path.GetFileName(BodySlideOutput(output+(output.EndsWith(".nif",StringComparison.OrdinalIgnoreCase)?"":".nif"))));
                }))continue;
                var doc=ReadDependencyXml(bytes,out _);
                if(doc.Root?.Name.LocalName!="SliderSetInfo")continue;
                var selected=doc.Root.Elements("SliderSet").Where(s=>
                {
                    var path=s.Element("OutputPath")?.Value.Trim();
                    var name=s.Element("OutputFile")?.Value.Trim();
                    if(string.IsNullOrWhiteSpace(path)||string.IsNullOrWhiteSpace(name))return false;
                    return outputs.Contains(BodySlideOutput(path.TrimEnd('\\','/')+"\\"+name+(name.EndsWith(".nif",StringComparison.OrdinalIgnoreCase)?"":".nif")));
                }).ToArray();
                if(selected.Length==0)continue;
                var included=new List<XElement>();
                foreach(var set in selected)
                {
                    var name=set.Attribute("name")?.Value??file.Relative;
                    var dependencies=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var folder=set.Element("DataFolder")?.Value??set.Element("SetFolder")?.Value??"";
                    var source=set.Element("SourceFile")?.Value;
                    if(!string.IsNullOrWhiteSpace(source))dependencies.Add(Normalize(BodySlideRoot+@"ShapeData\"+folder+"\\"+source));
                    else plan.ReviewIssues.Add($"BodySlide project {name}: no source mesh is declared.");
                    foreach(var data in set.Descendants("Data"))
                    {
                        var dataFile=data.Value.Trim().Replace('/','\\');
                        var osd=Regex.Match(dataFile,@"^(.+?\.osd)(?:\\|$)",RegexOptions.IgnoreCase);
                        if(osd.Success)dataFile=osd.Groups[1].Value;
                        if(string.IsNullOrWhiteSpace(dataFile))continue;
                        var local=(data.Attribute("local")?.Value??"") is "true" or "1";
                        var target=data.Attribute("target")?.Value;
                        var shape=set.Elements().FirstOrDefault(s=>(s.Name.LocalName is "Shape" or "BaseShapeName")&&s.Attribute("target")?.Value==target);
                        var folders=local?new[]{folder}:(shape?.Attribute("DataFolder")?.Value??folder).Split(';');
                        var candidates=folders.Select(f=>Normalize(BodySlideRoot+@"ShapeData\"+f+"\\"+dataFile)).ToArray();
                        var chosen=candidates.FirstOrDefault(Available)??candidates[0];
                        dependencies.Add(chosen);
                    }
                    var missing=dependencies.Where(p=>!Available(p)).ToArray();
                    if(missing.Length>0)
                    {
                        plan.Notes.Add($"Missing optional BodySlide variant: {name}. Project omitted because source dependencies are unavailable: {string.Join(", ",missing)}. Prebuilt equipment remains available.");
                        continue;
                    }
                    included.Add(new XElement(set));names.Add(name);
                    foreach(var dependency in dependencies)AddDependency(dependency,name);
                }
                if(included.Count>0)StageXml(file.Relative,file.Full,new XDocument(new XElement(doc.Root.Name,doc.Root.Attributes(),included)));
            }
            catch(Exception ex){plan.ReviewIssues.Add($"Cannot inspect BodySlide project {file.Relative}: {ex.Message}");}
        }
        if(names.Count==0){plan.Notes.Add("No matching BodySlide projects were found for the selected output meshes; only prebuilt game meshes are available.");return;}
        foreach(var file in ProjectFiles("SliderGroups"))
        {
            try
            {
                var doc=ReadDependencyXml(File.ReadAllBytes(file.Full),out _);
                if(doc.Root?.Name.LocalName!="SliderGroups")continue;
                var groups=doc.Root.Elements("Group").Select(g=>new XElement(g.Name,g.Attributes(),g.Elements("Member").Where(m=>names.Contains(m.Attribute("name")?.Value??"")).Select(m=>new XElement(m)))).Where(g=>g.HasElements).ToArray();
                if(groups.Length>0)StageXml(file.Relative,file.Full,new XDocument(new XElement(doc.Root.Name,doc.Root.Attributes(),groups)));
            }
            catch(Exception ex){plan.Notes.Add($"BodySlide optional group file {file.Relative} could not be read: {ex.Message}");}
        }
        plan.Notes.Add($"BodySlide projects included ({names.Count}): "+string.Join(", ",names.Order(StringComparer.OrdinalIgnoreCase)));

        IEnumerable<(string Full,string Relative)> ProjectFiles(string category)
        {
            var filenames=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // Only this pack's project/group filenames, resolved through authored overrides.
            // Vanilla fallback models must not pull in unrelated installed body refits.
            var packFiles=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var root in explicitRoots.Where(r=>!archiveOnlyRoots.Contains(r)))
            {
                var directory=SafeCombine(root,BodySlideRoot+category);
                if(Directory.Exists(directory))foreach(var path in Directory.EnumerateFiles(directory,"*",SearchOption.AllDirectories))packFiles.Add(Normalize(Path.GetRelativePath(root,path)));
            }
            foreach(var root in roots.Where(r=>!archiveOnlyRoots.Contains(r)))
            {
                foreach(var relative in packFiles.Order(StringComparer.OrdinalIgnoreCase))
                {
                    if(filenames.Contains(relative))continue;
                    var full=SafeCombine(root,relative);
                    if(!File.Exists(full))continue;
                    if(Path.GetExtension(full).ToLowerInvariant() is not (".osp" or ".xml"))continue;
                    if(filenames.Add(relative))yield return (full,relative);
                }
            }
        }
        void AddDependency(string path,string project)
        {
            path=Normalize(path);
            if(seen.Add(path))queue.Enqueue((path,"BodySlide project "+project));
        }
        void StageXml(string relative,string provider,XDocument document)
        {
            var cached=SafeCombine(plan.Cache,relative);
            Directory.CreateDirectory(Path.GetDirectoryName(cached)!);
            document.Save(cached);
            plan.Files.Add(new(relative,provider,cached,Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(cached))),["BodySlide project/group filtered to selected output meshes"]));
        }
    }
}
