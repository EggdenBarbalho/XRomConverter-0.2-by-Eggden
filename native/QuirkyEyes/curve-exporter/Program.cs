using System.Text;
using System.Text.Json;
using MikuMikuLibrary.Archives;
using MikuMikuLibrary.Archives.Extensions;
using MikuMikuLibrary.Bones;
using MikuMikuLibrary.Databases;
using MikuMikuLibrary.IO;
using MikuMikuLibrary.Motions;

if(args[0]=="--blocks"){
 var bd=BinaryFile.Load<BoneData>(args[1]);string[] chars={"MIK","RIN","LEN","LUK","KAI","MEI"};
 var rows=new List<object>();var h=new StringBuilder("#pragma once\nnamespace quirky_blocks { struct Entry { unsigned id,offset,count; const char* name; };\n");
 foreach(var ch in chars){var sk=bd.Skeletons.Single(x=>x.Name==ch);uint offset=0;h.Append("inline constexpr Entry "+ch+"[]={\n");
 for(int i=0;i<sk.Bones.Count;i++){var b=sk.Bones[i];uint count=b.Type>=BoneType.PositionRotation?6u:3u;h.Append("{"+i+","+offset+","+count+",\""+b.Name+"\"},\n");rows.Add(new{character=ch,id=i,offset,count,name=b.Name});offset+=count;}
 h.Append("};\n");}
 h.Append("inline const Entry* table(int c){switch(c){case 0:return MIK;case 1:return RIN;case 2:return LEN;case 3:return LUK;case 4:return KAI;case 5:return MEI;default:return nullptr;}}\ninline unsigned size(int c){switch(c){case 0:return sizeof(MIK)/sizeof(Entry);case 1:return sizeof(RIN)/sizeof(Entry);case 2:return sizeof(LEN)/sizeof(Entry);case 3:return sizeof(LUK)/sizeof(Entry);case 4:return sizeof(KAI)/sizeof(Entry);case 5:return sizeof(MEI)/sizeof(Entry);default:return 0;}}\n}\n");
 File.WriteAllText(args[2],h.ToString());File.WriteAllText(args[2]+".json",JsonSerializer.Serialize(rows));return;
}
string root=args[0], native=args[1], output=args[2];
var bones=BinaryFile.Load<BoneData>(Path.Combine(args[3],"rom/bone_data.bin"));
using var farc=BinaryFile.Load<FarcArchive>(Path.Combine(native,"rob/mot_db.farc"));
var db=farc.Open<MotionDatabase>(farc.FileNames.Single());
var verification=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"verification.json")));
var records=new List<(int character,uint motion,string name,List<(string bone,KeySet[] curves)> bones)>();
string[] names={"MIK","RIN","LEN","LUK","KAI","MEI"};
for(int character=0;character<6;character++){
    string ch=names[character];var skeleton=bones.Skeletons.Single(x=>x.Name==ch);
    var eligible=verification.RootElement.EnumerateArray().Single(x=>x.GetProperty("character").GetString()==ch).GetProperty("mapped").EnumerateArray().Select(x=>x.GetProperty("target").GetString()).ToHashSet();
    var set=new MotionSet();using(var stream=File.OpenRead(Path.Combine(root,"quirky_"+ch+".bin")))set.Load(stream,skeleton,db);
    var info=db.GetMotionSetInfo(ch);
    for(int i=0;i<set.Motions.Count;i++){
        string name=info.Motions[i].Name;if(!eligible.Contains(name))continue;
        var selected=new List<(string bone,KeySet[] curves)>();
        foreach(var binding in set.Motions[i].Bind().BoneBindings.Where(x=>x.Name.Contains("eyelid",StringComparison.OrdinalIgnoreCase)||x.Name.Contains("mabu",StringComparison.OrdinalIgnoreCase))){
            var bone=skeleton.GetBone(binding.Name);var curves=new List<KeySet>();
            void Add(KeyBinding k){curves.Add(k?.X??new KeySet());curves.Add(k?.Y??new KeySet());curves.Add(k?.Z??new KeySet());}
            switch(bone.Type){case BoneType.Position:Add(binding.Position);break;case BoneType.Rotation:Add(binding.Rotation);break;case BoneType.PositionRotation:Add(binding.Position);Add(binding.Rotation);break;default:throw new Exception("Unexpected eye bone type: "+bone.Type);}
            foreach(var curve in curves){if(curve.Keys.Count>65535||curve.Keys.Any(x=>!float.IsFinite(x.Value)||!float.IsFinite(x.Tangent)))throw new Exception("Invalid curve");for(int j=1;j<curve.Keys.Count;j++)if(curve.Keys[j].Frame<=curve.Keys[j-1].Frame)throw new Exception("Unsorted curve");}
            selected.Add((binding.Name,curves.ToArray()));
        }
        if(selected.Count!=32)throw new Exception("Expected 32 eye bones: "+name);
        records.Add((character,info.Motions[i].Id,name,selected));
    }
}
Directory.CreateDirectory(output);using(var writer=new BinaryWriter(File.Create(Path.Combine(output,"quirky-curves.bin")),Encoding.UTF8)){
    writer.Write(Encoding.ASCII.GetBytes("QEY1"));writer.Write(records.Count);
    void Text(string text){byte[] bytes=Encoding.UTF8.GetBytes(text);writer.Write((ushort)bytes.Length);writer.Write(bytes);}
    foreach(var record in records){writer.Write(record.character);writer.Write(record.motion);Text(record.name);writer.Write(record.bones.Count);
        foreach(var b in record.bones){Text(b.bone);writer.Write((byte)b.curves.Length);foreach(var c in b.curves){writer.Write(c.HasTangents);writer.Write((ushort)c.Keys.Count);foreach(var k in c.Keys){writer.Write(k.Frame);writer.Write(k.Value);writer.Write(k.Tangent);}}}
    }
}
File.WriteAllText(Path.Combine(output,"curve-export.json"),JsonSerializer.Serialize(new{schema=1,motions=records.Count,characters=6,eyeBonesPerMotion=32,channels="eyelid/mabu only",originalMotionIdsPreserved=true,perCharacter=records.GroupBy(x=>x.character).Select(x=>new{character=x.Key,count=x.Count()})},new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine($"Exported {records.Count} mapped facial motions; only eye curves; no ROM or PV DB emitted.");
