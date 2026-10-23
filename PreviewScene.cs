using System.Numerics;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using NiflySharp;
using NiflySharp.Blocks;
using NiflySharp.Structs;

namespace ArmorPackSplitter;

internal sealed record PreviewMesh(string Piece,string File,string Shape,Vector3[] Positions,Vector3[] Normals,Vector2[] UVs,int[] Indices,BitmapSource? Texture,float Alpha,bool DoubleSided);
internal sealed class PreviewScene
{
    public List<PreviewMesh> Meshes {get;}=[];
    public List<string> Notes {get;}=[];
    public int RequestedModels {get;set;}
    public int LoadedModels {get;set;}
    public int Pieces {get;set;}
}

internal static class PreviewSceneReader
{
    internal static PreviewScene Read(ExportPlan plan,IEnumerable<FormKey> selected,bool female,bool lowWeight,CancellationToken cancellation=default)
    {
        var scene=new PreviewScene();
        var checkedKeys=selected.ToHashSet();
        var pieces=plan.Records.OfType<Armor>().Where(a=>checkedKeys.Contains(a.FormKey)).ToArray();
        scene.Pieces=pieces.Length;
        var addons=plan.Records.OfType<ArmorAddon>().ToDictionary(a=>a.FormKey);
        var textureSets=plan.Records.OfType<TextureSet>().ToDictionary(t=>t.FormKey);
        var files=plan.Files.ToDictionary(f=>AssetResolver.Normalize(f.Path),StringComparer.OrdinalIgnoreCase);
        var modelPaths=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var textures=new Dictionary<string,BitmapSource?>(StringComparer.OrdinalIgnoreCase);
        foreach(var armor in pieces)
        {
            cancellation.ThrowIfCancellationRequested();
            if(armor.Armature.Count==0)scene.Notes.Add($"{ExportEngine.DisplayName(armor)}: no worn model is defined.");
            foreach(var armature in armor.Armature)
            {
                if(!addons.TryGetValue(armature.FormKey,out var addon))
                {
                    scene.Notes.Add($"{ExportEngine.DisplayName(armor)}: worn model is in external master {armature.FormKey.ModKey} and cannot be previewed here.");continue;
                }
                var model=female?addon.WorldModel?.Female:addon.WorldModel?.Male;
                if(model is null||model.File.IsNull){scene.Notes.Add($"{ExportEngine.DisplayName(armor)}: no {(female?"female":"male")} worn model.");continue;}
                var path=AssetResolver.Normalize(model.File.DataRelativePath.ToString());
                var weighted=female?addon.WeightSliderEnabled.Female:addon.WeightSliderEnabled.Male;
                if(weighted)path=Regex.Replace(path,@"_[01]\.nif$",lowWeight?"_0.nif":"_1.nif",RegexOptions.IgnoreCase);
                var modelKey=path+"|"+string.Join(";",model.AlternateTextures?.Select(t=>$"{t.Index}:{t.Name}:{t.NewTexture.FormKey}")??[]);
                if(!modelPaths.Add(modelKey))continue;
                scene.RequestedModels++;
                if(!files.TryGetValue(path,out var file)){scene.Notes.Add($"Missing preview mesh: {path}");continue;}
                try
                {
                    var meshCount=scene.Meshes.Count;
                    LoadMesh(file,ExportEngine.DisplayName(armor),model,scene,files,textures,textureSets,cancellation);
                    if(scene.Meshes.Count>meshCount)scene.LoadedModels++;
                    else scene.Notes.Add($"No supported geometry in {path}.");
                }
                catch(OperationCanceledException){throw;}
                catch(Exception ex){scene.Notes.Add($"Could not preview {path}: {ex.Message}");}
            }
        }
        return scene;
    }

    private static void LoadMesh(ExportFile file,string piece,Model model,PreviewScene scene,Dictionary<string,ExportFile> files,Dictionary<string,BitmapSource?> textures,Dictionary<FormKey,TextureSet> textureSets,CancellationToken cancellation)
    {
        var nif=new NifFile();
        if(nif.Load(file.StagedPath)!=0||!nif.Valid)throw new IOException("The NIF could not be read.");
        if(nif.HasUnknownBlocks)scene.Notes.Add($"{file.Path}: some NIF blocks are unsupported; displaying available geometry.");
        var shapeIndex=-1;
        foreach(var shape in nif.GetShapes())
        {
            shapeIndex++;
            cancellation.ThrowIfCancellationRequested();
            // Only scene geometry; collision shapes and first-person/inventory models are excluded.
            if(((nif.Header.Version.IsSSE()?shape.Flags_ui:shape.Flags_us)&1)!=0)continue;
            var shapeName=(shape as NiObjectNET)?.Name?.String??"Shape";
            if(shapeName.Equals("VirtualGround",StringComparison.OrdinalIgnoreCase)&&nif.GetShader(shape) is null)
            {
                scene.Notes.Add("Invisible physics helper VirtualGround omitted from the visual preview.");continue;
            }
            var positions=shape is BSTriShape bs?bs.VertexPositions:shape.GeometryData?.Vertices;
            var normals=shape is BSTriShape bsNormals?bsNormals.Normals:shape.GeometryData?.Normals;
            var uvs=shape is BSTriShape bsUV?bsUV.UVs:shape.GeometryData?.UVSets;
            if(positions is null||positions.Count==0||shape.Triangles is null||shape.Triangles.Count==0)continue;
            var transform=ShapeToGlobal(nif,shape);
            var transformed=positions.Select(p=>Vector3.Transform(p,transform)).ToArray();
            if(transformed.Any(p=>!float.IsFinite(p.X)||!float.IsFinite(p.Y)||!float.IsFinite(p.Z)))throw new IOException("Non-finite mesh coordinates.");
            Matrix4x4.Invert(transform,out var inverse);
            var normalMatrix=Matrix4x4.Transpose(inverse);
            var transformedNormals=normals?.Count==positions.Count?normals.Select(n=>Vector3.Normalize(Vector3.TransformNormal(n,normalMatrix))).ToArray():[];
            var indices=new List<int>();
            foreach(var tri in shape.Triangles)
                if(tri.V1<positions.Count&&tri.V2<positions.Count&&tri.V3<positions.Count)indices.AddRange([tri.V1,tri.V2,tri.V3]);
            var shader=nif.GetShader(shape);
            var texturePath="";
            if(shader?.HasTextureSet==true)
            {
                var set=nif.GetBlock<BSShaderTextureSet>(shader.TextureSetRef);
                texturePath=AssetResolver.Strings(set?.Textures.FirstOrDefault()).FirstOrDefault()??"";
            }
            if(shader is BSEffectShaderProperty effect)texturePath=AssetResolver.Strings(effect.SourceTexture).FirstOrDefault()??texturePath;
            var alternate=model.AlternateTextures?.FirstOrDefault(a=>a.Index==shapeIndex&&(string.IsNullOrEmpty(a.Name)||a.Name.Equals(shapeName,StringComparison.OrdinalIgnoreCase)));
            if(alternate is not null)
            {
                if(textureSets.TryGetValue(alternate.NewTexture.FormKey,out var replacement)&&replacement.Diffuse is {IsNull:false} diffuse)texturePath=diffuse.DataRelativePath.ToString();
                else scene.Notes.Add($"{piece}: alternate texture for {shapeName} is in an external master; the mesh's original material is shown.");
            }
            BitmapSource? texture=null;
            if(!string.IsNullOrWhiteSpace(texturePath)&&AssetResolver.TryAsset(texturePath,out var relative))
            {
                relative=AssetResolver.Normalize(relative);
                var textureKey=relative+(shape.HasAlphaProperty?"|alpha":"|opaque");
                if(!textures.TryGetValue(textureKey,out texture))
                {
                    try
                    {
                        if(files.TryGetValue(relative,out var textureFile))texture=ReadTexture(textureFile.StagedPath,shape.HasAlphaProperty);
                        else scene.Notes.Add($"Missing preview texture: {relative}. Displaying a plain material.");
                    }
                    catch(Exception ex){scene.Notes.Add($"Texture {relative}: {ex.Message}. Displaying a plain material.");}
                    textures[textureKey]=texture;
                }
            }
            var uvScale=shader?.UVScale??TexCoord.One;
            var uvOffset=shader?.UVOffset??TexCoord.Zero;
            var coordinates=uvs?.Count==positions.Count?uvs.Select(u=>new Vector2(u.U*uvScale.U+uvOffset.U,u.V*uvScale.V+uvOffset.V)).ToArray():[];
            scene.Meshes.Add(new(piece,file.Path,shapeName,transformed,transformedNormals,coordinates,indices.ToArray(),texture,Math.Clamp(shader?.Alpha??1,0,1),shader?.DoubleSided??true));
        }
    }

    internal static Matrix4x4 Transform(Matrix33 rotation,Vector3 translation,float scale)
    {
        // NIF matrices use column vectors; System.Numerics uses row vectors.
        return new Matrix4x4(rotation.M11*scale,rotation.M21*scale,rotation.M31*scale,0,
            rotation.M12*scale,rotation.M22*scale,rotation.M32*scale,0,
            rotation.M13*scale,rotation.M23*scale,rotation.M33*scale,0,
            translation.X,translation.Y,translation.Z,1);
    }

    private static Matrix4x4 ShapeToGlobal(NifFile nif,INiShape shape)
    {
        if(shape.HasSkinInstance&&nif.GetBlock(shape.SkinInstanceRef) is NiSkinInstance skin&&nif.GetBlock<NiSkinData>(skin.Data) is {} data)
        {
            var t=data.SkinTransform;
            // SkinTransform maps global bind coordinates into shape coordinates.
            if(!Matrix4x4.Invert(Transform(t.Rotation,t.Translation,t.Scale),out var inverse))throw new IOException("Singular skin transform.");
            return inverse;
        }
        var transform=Transform(shape.Rotation,shape.Translation,shape.Scale);
        var parent=nif.GetParentNode(shape);
        var seen=new HashSet<NiNode>();
        while(parent is not null&&seen.Add(parent))
        {
            transform*=Transform(parent.Rotation,parent.Translation,parent.Scale);
            parent=nif.GetParentNode(parent);
        }
        return transform;
    }

    internal static BitmapSource ReadTexture(string path,bool useAlpha)
    {
        BitmapSource source;
        if(Path.GetExtension(path).ToLowerInvariant() is ".dds" or ".tga")
        {
            using var image=Pfim.Pfimage.FromFile(path);
            if(image.Compressed)image.Decompress();
            var format=image.Format switch
            {
                Pfim.ImageFormat.Rgba32=>PixelFormats.Bgra32,
                Pfim.ImageFormat.Rgb24=>PixelFormats.Bgr24,
                Pfim.ImageFormat.Rgb8=>PixelFormats.Gray8,
                Pfim.ImageFormat.R5g5b5a1 or Pfim.ImageFormat.R5g5b5=>PixelFormats.Bgr555,
                Pfim.ImageFormat.R5g6b5=>PixelFormats.Bgr565,
                _=>throw new IOException("Unsupported DDS pixel format "+image.Format)
            };
            // DDS alpha channels often contain specular masks on opaque Skyrim materials.
            if(!useAlpha&&format==PixelFormats.Bgra32)
                for(int row=0;row<image.Height;row++)for(int x=0;x<image.Width;x++)image.Data[row*image.Stride+x*4+3]=255;
            source=BitmapSource.Create(image.Width,image.Height,96,96,format,null,image.Data,image.Stride);
        }
        else
        {
            using var stream=File.OpenRead(path);
            source=BitmapFrame.Create(stream,BitmapCreateOptions.None,BitmapCacheOption.OnLoad);
        }
        // Large texture packs should not fill the preview's working set with 8K images.
        var ratio=1024.0/Math.Max(source.PixelWidth,source.PixelHeight);
        if(ratio<1)
        {
            var scaled=new FormatConvertedBitmap(new TransformedBitmap(source,new ScaleTransform(ratio,ratio)),PixelFormats.Bgra32,null,0);
            var stride=scaled.PixelWidth*4;var bytes=new byte[stride*scaled.PixelHeight];scaled.CopyPixels(bytes,stride,0);
            source=BitmapSource.Create(scaled.PixelWidth,scaled.PixelHeight,96,96,PixelFormats.Bgra32,null,bytes,stride);
        }
        source.Freeze();return source;
    }
}
