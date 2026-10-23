using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.IO.Compression;

namespace ArmorPackSplitter;

internal sealed record ArmorSet(string Name, List<Armor> Pieces)
{
    public override string ToString() => $"{Name}  ({Pieces.Count} pieces)";
}

internal sealed class ExportEngine
{
    public string SourcePath { get; }
    public ISkyrimMod Source { get; }
    public List<ArmorSet> Sets { get; }
    private readonly Dictionary<FormKey, IMajorRecord> records;

    public ExportEngine(string path)
    {
        SourcePath = Path.GetFullPath(path);
        if(string.Equals(Path.GetFileName(Path.GetDirectoryName(SourcePath)),"Data",StringComparison.OrdinalIgnoreCase))
        {
            var context=Mo2Context.Find(SourcePath,Path.GetDirectoryName(SourcePath)!);
            var provider=context?.Roots.Select(r=>Path.Combine(r,Path.GetFileName(SourcePath))).FirstOrDefault(File.Exists);
            if(provider is not null)SourcePath=provider;
        }
        GuardSource(SourcePath);
        Source = OpenMutable(SourcePath);
        if (Source.UsingLocalization) throw new InvalidOperationException("Localized plugins are not supported yet. Open a non-localized source plugin.");
        records = Source.EnumerateMajorRecords().Cast<IMajorRecord>().ToDictionary(r => r.FormKey);
        Sets = DetectSets(Source.Armors.Where(a => !a.IsDeleted && a.FormKey.ModKey == Source.ModKey).ToList());
    }

    public static void GuardSource(string path)
    {
        string[] generated = ["Requiem for the Indifferent.esp", "PGPatcher.esp", "PG_1.esp", "DynDOLOD.esm", "DynDOLOD.esp", "Occlusion.esp"];
        var dir = Path.GetDirectoryName(path)!;
        if (generated.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase) ||
            File.Exists(Path.Combine(dir, "NPC_Token.json")) || File.Exists(Path.Combine(dir, "ParallaxGen_Diff.json")))
            throw new InvalidOperationException("Choose the original armor pack, rather than generated tool output.");
    }

    public static ISkyrimMod OpenMutable(string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        using var header = SkyrimMod.Create(SkyrimRelease.SkyrimSE).FromPath(path).WithLoadOrder(Array.Empty<ModKey>()).WithDataFolder(dir).Construct();
        return SkyrimMod.Create(SkyrimRelease.SkyrimSE).FromPath(path).WithLoadOrder(header.MasterReferences.Select(m => m.Master).ToArray()).WithDataFolder(dir).Mutable().Construct();
    }

    // Prefer shared name prefixes; the UI lets the user correct inferred membership.
    public static List<ArmorSet> DetectSets(List<Armor> armors)
    {
        var names = armors.ToDictionary(a => a.FormKey, a => DisplayName(a));
        var groups = new Dictionary<string, List<Armor>>(StringComparer.OrdinalIgnoreCase);
        foreach (var armor in armors)
        {
            var name = names[armor.FormKey];
            var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string? key = null;
            var bracket=Regex.Match(name,@"^\[[^\]]+\]");
            if(bracket.Success&&!GenericPrefixes.Contains(bracket.Value)&&names.Values.Count(n=>n.StartsWith(bracket.Value+" ",StringComparison.OrdinalIgnoreCase)||n.Equals(bracket.Value,StringComparison.OrdinalIgnoreCase))>=2)
                key=bracket.Value;
            for (int count = 1; key is null&&count <= Math.Min(5, words.Length); count++)
            {
                var prefix = string.Join(' ', words.Take(count));
                if (GenericPrefixes.Contains(prefix)) continue;
                var matches = names.Values.Count(n => n.Equals(prefix, StringComparison.OrdinalIgnoreCase) || n.StartsWith(prefix + " ", StringComparison.OrdinalIgnoreCase));
                if (matches >= 2) { key = prefix; break; }
            }
            key ??= Regex.Replace(name, @"\s+(armor|boots|gloves|gauntlets|helmet|heels|skirt|dress|cuirass|pants|stockings|bra|panties)(\s.*)?$", "", RegexOptions.IgnoreCase);
            if (string.IsNullOrWhiteSpace(key)) key = name;
            if (!groups.TryGetValue(key, out var list)) groups[key] = list = [];
            list.Add(armor);
        }
        return groups.Select(g =>
        {
            var common=g.Value.Select(DisplayName).First().Split(' ',StringSplitOptions.RemoveEmptyEntries).ToList();
            foreach(var name in g.Value.Select(DisplayName).Skip(1))
            {
                var tokens=name.Split(' ',StringSplitOptions.RemoveEmptyEntries);
                int count=0;
                while(count<common.Count&&count<tokens.Length&&common[count].Equals(tokens[count],StringComparison.OrdinalIgnoreCase))count++;
                common=common.Take(count).ToList();
            }
            var label=(g.Value.Count>1&&common.Count>0?string.Join(' ',common):g.Key).TrimEnd(' ','-');
            return new ArmorSet(label,g.Value.OrderBy(DisplayName).ToList());
        }).OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static readonly HashSet<string> GenericPrefixes = new(StringComparer.OrdinalIgnoreCase) {"[SMP]", "SMP", "CBBE", "3BA", "[CBBE]", "[3BA]", "[SunJeong]", "SunJeong", "BDO", "[BDO]", "Black", "White", "Red", "Blue", "Green"};
    public static string DisplayName(Armor a) => a.Name?.String is { Length: > 0 } name ? name : a.EditorID ?? a.FormKey.ToString();

    public List<IMajorRecord> Closure(IEnumerable<FormKey> selected, bool recipes)
    {
        var reached = new Dictionary<FormKey, IMajorRecord>();
        var queue = new Queue<FormKey>(selected.Distinct());
        void Walk()
        {
            while (queue.TryDequeue(out var key))
            {
                if (reached.ContainsKey(key)) continue;
                if (!records.TryGetValue(key, out var record)) throw new InvalidOperationException($"Unresolved source dependency: {key}");
                if (record.IsDeleted) throw new InvalidOperationException($"A dependency is deleted: {key}");
                if (record is ICellGetter or IWorldspaceGetter or IPlacedGetter or INpcGetter or IQuestGetter)
                    throw new InvalidOperationException($"This selection depends on {record.GetType().Name} {record.EditorID}. Exporting world or quest content is not supported.");
                reached.Add(key, record);
                foreach (var link in record.EnumerateFormLinks())
                    if (!link.FormKey.IsNull && link.FormKey.ModKey == Source.ModKey) queue.Enqueue(link.FormKey);
                if (reached.Count > 10000) throw new InvalidOperationException("Dependency walk exceeded 10,000 records.");
            }
        }
        Walk();
        if (recipes)
        {
            // Only recipes producing exported equipment; reverse references to ingredients would pull unrelated sets.
            foreach (var recipe in Source.ConstructibleObjects)
                if (recipe.FormKey.ModKey == Source.ModKey && recipe.CreatedObject is {} product && reached.ContainsKey(product.FormKey)) queue.Enqueue(recipe.FormKey);
            Walk();
        }
        return reached.Values.ToList();
    }

    public ExportPlan Plan(IEnumerable<FormKey> selected, bool recipes, string dataFolder, IEnumerable<string> extraRoots, Action<string>? progress = null, bool useMo2 = false)
    {
        var closure = Closure(selected, recipes);
        progress?.Invoke($"Tracing files for {closure.Count} records…");
        var context=useMo2?Mo2Context.Find(SourcePath,dataFolder):null;
        using var assets = new AssetResolver(Path.GetDirectoryName(SourcePath)!, dataFolder, extraRoots, Source.ModKey.FileName.ToString(),context?.Roots,Source.MasterReferences.Select(m=>m.Master.FileName.ToString()),context?.Plugins);
        var plan=assets.Build(closure, Source.ModKey, progress);
        if(context is not null)plan.Notes.Insert(0,$"Shared assets resolved through MO2 profile '{context.Profile}'; generated output folders are excluded.");
        else if(useMo2)plan.Notes.Insert(0,"No portable MO2 profile found. Assets resolved from the specified folders only.");
        return plan;
    }

    public string Export(ExportPlan plan, string parent, string name, Action<string>? progress = null, bool allowIncomplete = false)
    {
        name = name.Trim();
        if (name.Length == 0 || name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.EndsWith('.') || name.EndsWith(' '))
            throw new InvalidOperationException("Enter a valid new mod name without path separators.");
        if (plan.Errors.Count > 0) throw new InvalidOperationException("Resolve the missing or unsupported dependencies before exporting.\n" + string.Join("\n", plan.Errors.Take(12)));
        if(plan.Records.Count>2048)throw new InvalidOperationException("This selection exceeds the ESL limit of 2,048 new records. Export fewer pieces or split the selection into separate ZIPs.");
        if(plan.ReviewIssues.Count>0&&!allowIncomplete)throw new InvalidOperationException("Unresolved dependencies need review. Enable 'Allow incomplete export' to export available files with a recorded warning.\n"+string.Join("\n",plan.ReviewIssues.Take(12)));
        var output = Path.Combine(Path.GetFullPath(parent), name);
        if (Directory.Exists(output) || File.Exists(output)) throw new IOException("That output already exists. Choose a new mod name.");
        Directory.CreateDirectory(parent);
        var stage = Path.Combine(Path.GetFullPath(parent), ".ArmorPackSplitter-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            var pluginName = name + ".esp";
            var modKey = ModKey.FromNameAndExtension(pluginName);
            if (modKey == Source.ModKey) throw new InvalidOperationException("The new plugin must have a different name from the source plugin.");
            var patch = new SkyrimMod(modKey, SkyrimRelease.SkyrimSE){IsSmallMaster=true};
            var mapping = new Dictionary<FormKey, FormKey>();
            foreach (var record in plan.Records)
            {
                var group = ((IMod)patch).TryGetTopLevelGroup(record.GetType()) ?? throw new InvalidOperationException($"Unsupported nested record: {record.EditorID}");
                var copy = group.DuplicateInAsNewUntypedRecord(record);
                if(copy.FormKey.ID<0x800||copy.FormKey.ID>0xFFF)throw new InvalidOperationException("Allocated record ID is outside the compact ESL range. Export fewer pieces.");
                mapping.Add(record.FormKey, copy.FormKey);
            }
            patch.RemapLinks(mapping);
            var masters = patch.EnumerateFormLinks().Select(l => l.FormKey).Where(k => !k.IsNull && k.ModKey != modKey).Select(k => k.ModKey).Distinct().ToArray();
            if (masters.Contains(Source.ModKey)) throw new InvalidOperationException("Validation failed: a reference to the source plugin remains.");
            var order = Source.MasterReferences.Select(m => m.Master).Concat(masters).Distinct().ToArray();
            progress?.Invoke("Writing new plugin…");
            var pluginPath = Path.Combine(stage, pluginName);
            patch.BeginWrite.ToPath(pluginPath).WithLoadOrder(order).WithDataFolder(Path.GetDirectoryName(SourcePath)!).Write();
            var check = OpenMutable(pluginPath);
            if(!check.IsSmallMaster||check.EnumerateMajorRecords().Any(r=>r.FormKey.ID<0x800||r.FormKey.ID>0xFFF))throw new InvalidOperationException("The written ESP failed ESL flag or compact-ID validation.");
            if (check.MasterReferences.Any(m => m.Master == Source.ModKey)) throw new InvalidOperationException("The written ESP still requires the original pack.");
            var written = check.EnumerateMajorRecords().ToDictionary(r => r.FormKey);
            if (written.Count != mapping.Count) throw new InvalidOperationException("Record count changed during write verification.");
            foreach (var link in check.EnumerateFormLinks())
                if (link.FormKey.ModKey == modKey && !written.ContainsKey(link.FormKey)) throw new InvalidOperationException($"Dangling exported reference: {link.FormKey}");
            foreach (var asset in plan.Files)
            {
                progress?.Invoke("Copying " + asset.Path);
                var target = AssetResolver.SafeCombine(stage, asset.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(asset.StagedPath, target, false);
            }
            var report = new {
                job = "Standalone armor set export", scope = new {type="Armor", plugins=new[]{Source.ModKey.ToString()}, defined_in=true, winner_fields=false},
                total=plan.Records.Count, complete=plan.ReviewIssues.Count==0, esl_flagged=check.IsSmallMaster,
                notes=plan.Notes.Where(n=>!n.StartsWith("Missing optional",StringComparison.OrdinalIgnoreCase)),
                optional_files=plan.Notes.Where(n=>n.StartsWith("Missing optional",StringComparison.OrdinalIgnoreCase)), unresolved=plan.ReviewIssues, source=SourcePath,
                masters=check.MasterReferences.Select(m=>m.Master.ToString()),
                records=plan.Records.Select(r=>new {formid=r.FormKey.ToString(), type=r.GetType().Name, editorid=r.EditorID, name=r is IArmorGetter a ? a.Name?.String : null, winner=Source.ModKey.ToString(), exportedFormid=mapping[r.FormKey].ToString()}),
                files=plan.Files.Select(f=>new {path=f.Path, source=f.Source, sha256=f.Hash, used_by=f.Reasons})
            };
            File.WriteAllText(Path.Combine(stage,"export-manifest.json"),JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));
            File.WriteAllText(Path.Combine(stage,"README.txt"),$"{name}\r\nESL-flagged ESP with compact record IDs.\r\nExported from {Source.ModKey}. The exported ESP does not master the original pack.\r\nRequired masters: {string.Join(", ",check.MasterReferences.Select(m=>m.Master))}\r\n\r\nInstall the ZIP using MO2's Install a new mod from an archive button, then enable the mod and its ESP. This exports equipment and recipes, not the pack's showroom.\r\nBody shape, skeleton, physics engine, and other runtime requirements still apply. See export-manifest.json.\r\nTest in a new save. FormIDs differ from the original pack.\r\n\r\nOPTIONAL FILES - NOT EXPORT BLOCKERS\r\n"+string.Join("\r\n",plan.Notes.Where(n=>n.StartsWith("Missing optional",StringComparison.OrdinalIgnoreCase)))+"\r\n");
            if(plan.ReviewIssues.Count>0)File.AppendAllText(Path.Combine(stage,"README.txt"),"\r\nINCOMPLETE / UNVERIFIED EXPORT\r\nThe original ESP is not a master, but the following asset or script issues remain:\r\n"+string.Join("\r\n",plan.ReviewIssues));
            Directory.Move(stage, output);
            return output;
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
    }

    public string ExportZip(ExportPlan plan,string zipPath,string modName,Action<string>? progress=null,bool allowIncomplete=false,bool overwrite=false)
    {
        zipPath=Path.GetFullPath(zipPath);
        if(!Path.GetExtension(zipPath).Equals(".zip",StringComparison.OrdinalIgnoreCase))throw new IOException("Choose a .zip filename for the exported mod.");
        if(File.Exists(zipPath)&&!overwrite)throw new IOException("That ZIP already exists. Choose a new filename.");
        var parent=Path.GetDirectoryName(zipPath)!;
        Directory.CreateDirectory(parent);
        var tempParent=Path.GetFullPath(Path.GetTempPath());
        var working=Path.GetFullPath(Path.Combine(tempParent,"ArmorPackSplitter-package-"+Guid.NewGuid().ToString("N")));
        var temporaryZip=Path.Combine(parent,".ArmorPackSplitter-"+Guid.NewGuid().ToString("N")+".zip");
        Directory.CreateDirectory(working);
        try
        {
            var folder=Export(plan,working,modName,progress,allowIncomplete);
            progress?.Invoke("Creating installable ZIP…");
            ZipFile.CreateFromDirectory(folder,temporaryZip,CompressionLevel.Optimal,false);
            using(var archive=ZipFile.OpenRead(temporaryZip))
            {
                var expected=Directory.EnumerateFiles(folder,"*",SearchOption.AllDirectories).ToDictionary(p=>Path.GetRelativePath(folder,p).Replace('\\','/'),p=>new FileInfo(p).Length,StringComparer.OrdinalIgnoreCase);
                var entries=archive.Entries.Where(e=>!e.FullName.EndsWith('/')).ToArray();
                if(entries.Length!=expected.Count||entries.Any(e=>!expected.TryGetValue(e.FullName,out var length)||e.Length!=length))throw new IOException("ZIP validation failed: archive contents differ from the verified export.");
                if(!entries.Any(e=>e.FullName.Equals(modName.Trim()+".esp",StringComparison.OrdinalIgnoreCase)))throw new IOException("ZIP validation failed: the ESP is not at the archive root.");
            }
            File.Move(temporaryZip,zipPath,overwrite);
            return zipPath;
        }
        finally
        {
            if(File.Exists(temporaryZip))File.Delete(temporaryZip);
            if(Directory.Exists(working)&&working.StartsWith(tempParent.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)&&Path.GetFileName(working).StartsWith("ArmorPackSplitter-package-",StringComparison.Ordinal))Directory.Delete(working,true);
        }
    }
}

internal sealed record ExportFile(string Path, string Source, string StagedPath, string Hash,List<string> Reasons);
internal sealed class ExportPlan : IDisposable
{
    public List<IMajorRecord> Records { get; init; } = [];
    public List<ExportFile> Files { get; } = [];
    public List<string> Errors { get; } = [];
    public List<string> ReviewIssues { get; } = [];
    public List<string> Notes { get; } = [];
    public string Cache { get; } = Path.Combine(Path.GetTempPath(),"ArmorPackSplitter",Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(Cache)) Directory.Delete(Cache, true); }
}
