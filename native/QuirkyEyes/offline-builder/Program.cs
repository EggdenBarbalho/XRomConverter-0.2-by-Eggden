using System.Text.Json;
using MikuMikuLibrary.Archives;
using MikuMikuLibrary.Archives.Extensions;
using MikuMikuLibrary.Bones;
using MikuMikuLibrary.Databases;
using MikuMikuLibrary.IO;
using MikuMikuLibrary.Motions;
using XRomConverter.Content;

// Offline variants only. Existing motion IDs are retained for comparison;
// these files must never be installed as global replacement banks.
string candidate=args[0], source=args[1], native=args[2], output=args[3];
Directory.CreateDirectory(output);
var bones=BinaryFile.Load<BoneData>(Path.Combine(candidate,"rom/bone_data.bin"));
using var dbArchive=BinaryFile.Load<FarcArchive>(Path.Combine(native,"rob/mot_db.farc"));
var db=dbArchive.Open<MotionDatabase>(dbArchive.FileNames.Single());
bool Eye(BoneBinding b)=>b.Name.Contains("eyelid",StringComparison.OrdinalIgnoreCase)||b.Name.Contains("mabu",StringComparison.OrdinalIgnoreCase);
string Other(Motion m)=>JsonSerializer.Serialize(m.Bind().BoneBindings.Where(b=>!Eye(b)).OrderBy(b=>b.Name).ToArray());
string Eyes(Motion m)=>JsonSerializer.Serialize(m.Bind().BoneBindings.Where(Eye).OrderBy(b=>b.Name).ToArray());
var reports=new List<object>();
using var psarc=new PsarcArchive(args[4]);
if(!psarc.TryOpen("bone_data.bon",out var boneStream))throw new Exception("Source bones missing");
using var originalBonesStream=boneStream;
var sourceBones=BinaryFile.Load<BoneData>(originalBonesStream);
foreach(string ch in new[]{"MIK","RIN","LEN","LUK","KAI","MEI"}) {
 var skeleton=bones.Skeletons.Single(s=>s.Name==ch);
 using var bank=BinaryFile.Load<FarcArchive>(Path.Combine(candidate,"rom/rob/mot_"+ch+".farc"));
 using var bankStream=bank.Open(bank.FileNames.Single(),EntryStreamMode.MemoryStream);
 var set=new MotionSet();set.Load(bankStream,skeleton,db);
 var info=db.GetMotionSetInfo(ch);
 using var xbank=BinaryFile.Load<FarcArchive>(Path.Combine(source,"mot_"+ch.ToLower()+"_uniq.farc"));
 // Original source must be decoded with the original source skeleton.
 var xSkeleton=sourceBones.Skeletons.Single(s=>s.Name.Equals(ch,StringComparison.OrdinalIgnoreCase));
 var lookup=new Dictionary<string,Motion>(StringComparer.OrdinalIgnoreCase);
 foreach(string file in xbank.FileNames) {
  using var stream=xbank.Open(file,EntryStreamMode.MemoryStream);
  var m=new Motion();m.Load(stream,xSkeleton);
  lookup.Add(m.Name.Replace("_uniq_","_",StringComparison.OrdinalIgnoreCase),m);
 }
 var preserved=set.Motions.Select(Other).ToArray();
 var mapped=new List<object>();var unmatched=new List<string>();int missingBones=0;
 for(int i=0;i<set.Motions.Count;i++) {
  string name=info.Motions[i].Name;
  // Never route mouth motions; retain the explicit closed-eye (_CL) variant.
  if(!name.Contains("_FACE_",StringComparison.OrdinalIgnoreCase)){unmatched.Add(name);continue;}
  string alias=name.EndsWith("_OLD_CL",StringComparison.OrdinalIgnoreCase)?name[..^7]+"_CL":name.EndsWith("_OLD",StringComparison.OrdinalIgnoreCase)?name[..^4]:null;
  string match=lookup.ContainsKey(name)?name:alias!=null&&lookup.ContainsKey(alias)?alias:null;
  if(match==null){unmatched.Add(name);continue;}
  var target=set.Motions[i].Bind();var incoming=lookup[match].Bind();int count=0;
  foreach(var eye in incoming.BoneBindings.Where(Eye)) {
   var existing=target.BoneBindings.FirstOrDefault(b=>b.Name==eye.Name);
   if(existing==null){missingBones++;continue;}
   existing.Position=eye.Position;existing.Rotation=eye.Rotation;existing.IK=eye.IK;count++;
  }
  target.Unbind(skeleton,db);
  if(Other(set.Motions[i])!=preserved[i])throw new Exception("Non-eye channels changed: "+name);
  mapped.Add(new{target=name,source=match,eyeBones=count});
 }
 string fileOut=Path.Combine(output,"quirky_"+ch+".bin");
 var expectedEyes=set.Motions.Select(Eyes).ToArray();
 using(var stream=File.Create(fileOut))set.Save(stream,skeleton,db,leaveOpen:true);
 var roundTrip=new MotionSet();using(var stream=File.OpenRead(fileOut))roundTrip.Load(stream,skeleton,db);
 for(int i=0;i<set.Motions.Count;i++)if(Other(roundTrip.Motions[i])!=preserved[i])throw new Exception("Roundtrip changed non-eye channels: "+info.Motions[i].Name);
 for(int i=0;i<set.Motions.Count;i++)if(Eyes(roundTrip.Motions[i])!=expectedEyes[i])throw new Exception("Roundtrip changed eye curves: "+info.Motions[i].Name);
 reports.Add(new{character=ch,total=set.Motions.Count,mapped,unmatched,missingBones,nonEyeChannelsPreserved=true});
 Console.WriteLine(ch+": "+mapped.Count+" matched; "+unmatched.Count+" unmapped; "+missingBones+" missing eye bones.");
}
File.WriteAllText(Path.Combine(output,"verification.json"),JsonSerializer.Serialize(reports,new JsonSerializerOptions{WriteIndented=true}));
