using System.Drawing;using System.Drawing.Drawing2D;using System.Numerics;using MikuMikuLibrary.Textures.Processing;
using MikuMikuLibrary.Archives;using MikuMikuLibrary.Archives.CriMw;using MikuMikuLibrary.Archives.Extensions;using MikuMikuLibrary.Databases;using MikuMikuLibrary.IO;using MikuMikuLibrary.Sprites;
namespace XRomConverter.Target.MegaMix;
internal static class XHairstyleIcons {
 public static void Write(string root,string game,IEnumerable<(int Menu,int Module)> rows){
  string folder=Path.Combine(root,"rom/2d"),path=Path.Combine(folder,"mod_spr_db.bin");var db=BinaryFile.Load<SpriteDatabase>(path);
  var used=new HashSet<uint>();void Reserve(SpriteDatabase d){foreach(var s in d.SpriteSets){used.Add(s.Id);foreach(var x in s.Sprites)used.Add(x.Id);foreach(var x in s.Textures)used.Add(x.Id);}}
  Reserve(db);using var c=BinaryFile.Load<CpkArchive>(Path.Combine(game,"diva_main.cpk"));Reserve(c.Open<SpriteDatabase>("rom_steam/rom/2d/spr_db.bin"));
  uint next=22000000;uint Allocate(){while(used.Contains(next))next++;used.Add(next);return next++;}
  foreach(var row in rows){
   string setName=$"SPR_CMNITM_THMB{row.Menu:D3}",entry=setName.ToLowerInvariant()+".bin";
   var sourceInfo=db.SpriteSets.Single(s=>s.Name==$"SPR_SEL_MD{row.Module:D3}CMN");
   string original=Path.Combine(AppContext.BaseDirectory,"Resources/hairstyle-portraits",$"spr_sel_md{row.Module:D3}cmn.farc");
   using var source=BinaryFile.Load<FarcArchive>(File.Exists(original)?original:Path.Combine(folder,Path.ChangeExtension(sourceInfo.FileName,".farc")));
   string spriteEntry=File.Exists(original)?source.FileNames.Single(n=>n.EndsWith(".bin",StringComparison.OrdinalIgnoreCase)):sourceInfo.FileName;
   var sprites=source.Open<SpriteSet>(spriteEntry);
   var image=sprites.Sprites.FirstOrDefault(s=>s.Name=="MD_IMG")??sprites.Sprites[0];
   using var portrait=SpriteCropper.Crop(image,sprites);
   using var canvas=new Bitmap(512,512);
   using(var graphics=Graphics.FromImage(canvas)) {
    graphics.Clear(Color.Transparent);graphics.InterpolationMode=InterpolationMode.HighQualityBicubic;graphics.CompositingQuality=CompositingQuality.HighQuality;
    int cropWidth=(int)(portrait.Width*0.70f),cropHeight=Math.Min(portrait.Height,(int)(cropWidth*265f/250f));
    var crop=new Rectangle((portrait.Width-cropWidth)/2,0,cropWidth,cropHeight);
    using var framed=new Bitmap(250,265);
    using(var pg=Graphics.FromImage(framed)){pg.InterpolationMode=InterpolationMode.HighQualityBicubic;pg.DrawImage(portrait,new Rectangle(0,0,250,265),crop,GraphicsUnit.Pixel);}
    // Rounded rectangle with a gradual alpha transition inside its edges.
    const float radius=20f,fade=12f;
    for(int y=0;y<framed.Height;y++)for(int x=0;x<framed.Width;x++) {
     float qx=MathF.Abs(x+0.5f-125f)-(125f-radius),qy=MathF.Abs(y+0.5f-132.5f)-(132.5f-radius);
     float distance=MathF.Sqrt(MathF.Pow(MathF.Max(qx,0),2)+MathF.Pow(MathF.Max(qy,0),2))+MathF.Min(MathF.Max(qx,qy),0)-radius;
     float opacity=Math.Clamp(-distance/fade,0,1);opacity=opacity*opacity*(3-2*opacity);
     var pixel=framed.GetPixel(x,y);framed.SetPixel(x,y,Color.FromArgb((int)MathF.Round(pixel.A*opacity),pixel.R,pixel.G,pixel.B));
    }
    graphics.DrawImage(framed,89,142);
   }
   string preview=Path.Combine(root,"X Hairs Sprites");Directory.CreateDirectory(preview);
   using(var thumbnail=canvas.Clone(new Rectangle(2,2,408,494),canvas.PixelFormat))thumbnail.Save(Path.Combine(preview,$"{row.Menu}-{row.Module}.png"));
   canvas.RotateFlip(RotateFlipType.Rotate180FlipX);sprites.TextureSet.Textures.Clear();sprites.TextureSet.Textures.Add(TextureEncoder.EncodeYCbCrFromBitmap(canvas));
   image.Name="ITM_IMG";image.TextureIndex=0;image.X=2;image.Y=2;image.Width=408;image.Height=494;image.RectangleBegin=new Vector2(2f/512,2f/512);image.RectangleEnd=new Vector2(410f/512,496f/512);image.ResolutionMode=ResolutionMode.HDTV1080;
   sprites.Sprites.Clear();sprites.Sprites.Add(image);
   var info=db.SpriteSets.SingleOrDefault(s=>s.Name==setName);if(info==null){info=new SpriteSetInfo{Id=Allocate(),Name=setName};db.SpriteSets.Add(info);}info.FileName=entry;
   uint spriteId=info.Sprites.Count>0?info.Sprites[0].Id:Allocate(),textureId=info.Textures.Count>0?info.Textures[0].Id:Allocate();info.Sprites.Clear();info.Textures.Clear();
   info.Sprites.Add(new SpriteInfo{Name=setName+"_ITM_IMG",Id=spriteId,Index=0});info.Textures.Add(new SpriteTextureInfo{Name=$"SPRTEX_CMNITM_THMB{row.Menu:D3}_MERGE_BC5COMP_0",Id=textureId,Index=0});
   using var a=new FarcArchive();a.Add(entry,sprites);a.Save(Path.Combine(folder,Path.ChangeExtension(entry,".farc")));
  }db.Save(path);
 }
}


