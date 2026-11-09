using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using XRomConverter.Analysis;
using XRomConverter.Ids;
using FirstLib.FirstRead;
using FirstLib.FirstRead.Common;
using MikuMikuLibrary.Archives;
using MikuMikuLibrary.Archives.CriMw;
using MikuMikuLibrary.Archives.Extensions;
using MikuMikuLibrary.Bones;
using MikuMikuLibrary.Databases;
using MikuMikuLibrary.Extensions;
using MikuMikuLibrary.Hashes;
using MikuMikuLibrary.IO;
using MikuMikuLibrary.Materials;
using MikuMikuLibrary.Motions;
using MikuMikuLibrary.Objects;
using MikuMikuLibrary.Objects.Extra;
using MikuMikuLibrary.Objects.Extra.Blocks;
using MikuMikuLibrary.Objects.Extra.Parameters;
using MikuMikuLibrary.Objects.Processing;
using MikuMikuLibrary.Parameters;
using MikuMikuLibrary.Parameters.Extensions;
using MikuMikuLibrary.Sprites;
using MikuMikuLibrary.Textures;
using MikuMikuLibrary.Textures.Processing;
using XRomConverter.Content;
using XRomConverter.Target.MegaMix;

namespace XRomConverter;

internal enum OsageSettingPartType
{
	LEFT,
	RIGHT,
	LONG_C
}

internal class OsageSettingParameter
{
	public int EXF;
	public OsageSettingPartType Parts;
	public string Root;

	public OsageSettingParameter()
	{
		Root = "";
	}
}

internal class OsageSettingCategory
{
	public string Name;
	public List<OsageSettingParameter> Osg;

	public OsageSettingCategory()
	{
		Name = "";
		Osg = new List<OsageSettingParameter>();
	}
}

internal class OsageSettingObject
{
	public string Category;
	public string Name;

	public OsageSettingObject()
	{
		Category = "";
		Name = "";
	}
}

internal class OsageSetting
{
	public List<OsageSettingCategory> Categories;
	public List<OsageSettingObject> Objects;

	public OsageSetting()
	{
		Categories = new List<OsageSettingCategory>();
		Objects = new List<OsageSettingObject>();
	}
}

internal class Program
{
	private static readonly IReadOnlyDictionary<short, string> sKnownModuleNames = new Dictionary<short, string>
	{
		[78] = "Sakine Meiko Original",
		[95] = "Akita Neru Original",
		[96] = "Yowane Haku Original",
		[97] = "Kasane Teto Original",
		[195] = "School Club Girl",
		[196] = "Gothic Purple",
		[197] = "Nostalgia",
		[242] = "M.S.J",
		[244] = "Akita Neru Swimwear",
		[245] = "Yowane Haku Swimwear",
		[246] = "Sakine Meiko Swimwear",
		[247] = "Kasane Teto Swimwear"
	};

	private static List<Item> MergeRepeatedItems(IEnumerable<Item> items, string context)
	{
		List<Item> list = new List<Item>();
		foreach (IGrouping<short, Item> item2 in from x in items
			group x by x.ItemNumber)
		{
			Item item = item2.First();
			string firstSignature = GetItemSignature(item);
			if (item2.Skip(1).Any((Item x) => GetItemSignature(x) != firstSignature))
			{
				throw new InvalidDataException($"Conflicting item {item2.Key} across selected sources ({context}).");
			}
			list.Add(item);
		}
		return list;
	}

	private static string GetItemSignature(Item item)
	{
		return string.Join("|", item.Attribute, item.U04, item.U06, item.U07, item.Type, item.U09, item.DestID, item.SubID, item.U0F, item.U10, item.U14, string.Join(",", item.Objects.Select((ItemObject x) => $"{x.ObjectID}:{x.RPK}")), string.Join(",", item.TextureChanges.Select((TextureChange x) => $"{x.OriginalTextureID}:{x.ReplacementTextureID}")), string.Join(",", item.ObjectSets));
	}

	private static OsageSkinParameter CloneSkinParameterTemplate(OsageSkinParameter template)
	{
		// Round-trip through the real serializer so nested BOC/collision/node data is
		// copied as well. The shared template is never inserted into an object result.
		var source = new OsageSkinParameterSet { Format = BinaryFormat.DT };
		source.Parameters.Add(template);
		using var stream = new MemoryStream();
		source.Save(stream, leaveOpen: true);
		stream.Position = 0;
		return BinaryFile.Load<OsageSkinParameterSet>(stream).Parameters.Single();
	}

	private static void RemoveUnsupportedExtraCharacterContent(List<ModuleInfo> modules, List<List<Item>> characterItems, List<List<Costume>> characterCostumes)
	{
		if (characterItems.Count > 10 && characterCostumes.Count > 10)
		{
			int num = modules.RemoveAll((ModuleInfo x) => x.Character == CharacterType.EXT);
			int count = characterItems[10].Count;
			int count2 = characterCostumes[10].Count;
			characterItems[10].Clear();
			characterCostumes[10].Clear();
			if (num > 0 || count > 0 || count2 > 0)
			{
				Console.WriteLine("[CONTENT] Skipping unsupported EXT character content " + $"({num} module(s), {count} item(s), " + $"{count2} costume(s)).");
			}
		}
	}

	private static void Main(string[] args)
	{
        if((args.Length==2||args.Length==3)&&args[0]=="audit-osage-modules"){OsageModuleCoverage.Write(args[1],args.Length==3?args[2]:null);return;}
        if(args.Length==3&&args[0]=="propagate-osage-categories"){OsageCategoryPropagation.Write(args[1],args[2]);return;}
        if(args.Length==3&&args[0]=="inspect-pv-osage-inputs"){
            foreach(var package in new[]{"diva_main.cpk","diva_dlc00.cpk"}){using var cpk=BinaryFile.Load<CpkArchive>(Path.Combine(args[1],package));Directory.CreateDirectory(args[2]);
            foreach(var name in cpk.FileNames.Where(n=>n.EndsWith("pv_db.txt",StringComparison.OrdinalIgnoreCase)||(n.Contains("004",StringComparison.OrdinalIgnoreCase)&&n.Contains("mothead",StringComparison.OrdinalIgnoreCase))||n.EndsWith("mothead_PV219.bin",StringComparison.OrdinalIgnoreCase)||n.EndsWith("pv_219_nochart.dsc",StringComparison.OrdinalIgnoreCase)||n.EndsWith("pv_004_nochart.dsc",StringComparison.OrdinalIgnoreCase))){using var input=cpk.Open(name,EntryStreamMode.MemoryStream);using var output=File.Create(Path.Combine(args[2],Path.GetFileName(name)));input.CopyTo(output);Console.WriteLine(name);}}return;
        }
        if(args.Length==3&&args[0]=="inspect-native-skp"){
            using var cpk=BinaryFile.Load<CpkArchive>(Path.Combine(args[1],"diva_main.cpk"));Directory.CreateDirectory(args[2]);
            foreach(var name in cpk.FileNames.Where(n=>n.Contains("skin_param")&&(n.Contains("sp_skp")||n.Contains("osage_setting")))){using var input=cpk.Open(name,EntryStreamMode.MemoryStream);using var output=File.Create(Path.Combine(args[2],Path.GetFileName(name)));input.CopyTo(output);Console.WriteLine(name);}return;
        }
        if(args.Length==3&&args[0]=="inspect-skp-archive"){
            using var archive=BinaryFile.Load<FarcArchive>(args[1]);
            Directory.CreateDirectory(args[2]);
            foreach(var name in archive.FileNames){using var input=archive.Open(name,EntryStreamMode.MemoryStream);using var output=File.Create(Path.Combine(args[2],Path.GetFileName(name)));input.CopyTo(output);}
            Console.WriteLine($"Extracted {archive.FileNames.Count()} entries.");return;
        }

        if(args.Length==4&&args[0]=="sad-pv206-brows"){Pv236BrowFallback.Run(args[1],args[2],args[3],"MIK_FACE_SAD",3480,3600);return;}
        if(args.Length==6&&args[0]=="retarget-pv236-brows-b"){PvBrowAudit.Retarget(args[1],args[2],args[3],args[4],args[5],new[]{236});return;}
        if(args.Length==6&&args[0]=="retarget-pv-brows-b"){PvBrowAudit.Retarget(args[1],args[2],args[3],args[4],args[5]);return;}
        if(args.Length==4&&args[0]=="fallback-pv236-brows"){Pv236BrowFallback.Run(args[1],args[2],args[3]);return;}
        if(args.Length==4&&args[0]=="audit-pv-brows"){PvBrowAudit.Run(args[1],args[2],args[3]);return;}
        if(args.Length==4&&args[0]=="audit-reference-pvs"){ClosedEyeMotionAudit.ReferencePvs(args[1],args[2],args[3]);return;}
        if(args.Length==5&&args[0]=="audit-mouths"){PspSurpriseSmileReplacer.Audit(args[1],args[2],args[3],args[4]);return;}
        if(args.Length==3&&args[0]=="replace-psp-surprise-smile"){PspSurpriseSmileReplacer.Write(args[1],args[2]);return;}
        if(args.Length==3&&args[0]=="restore-closed-eyes"){ClosedEyeRestorer.Write(args[1],args[2]);return;}
        if(args.Length>0&&args[0]=="audit-closed-eyes"){Environment.ExitCode=ClosedEyeMotionAudit.Run(args);return;}
        if(args.Length==4&&args[0]=="migrate-pdx-model-only"){XExclusiveAccessories.RemoveLegacyRegistrations(args[1],args[2]);XExclusiveAccessories.Write(args[1],args[3]);return;}
        if(args.Length==4&&args[0]=="restore-rin-default-head-items"){RinDefaultHeadItems.Write(args[1],args[2]);using var native=BinaryFile.Load<CpkArchive>(Path.Combine(args[3],"diva_main.cpk"));NativeHairReplaceWriter.Write(args[1],native,native.Open<ObjectDatabase>("rom_steam/rom/objset/obj_db.bin"),Enumerable.Range(0,1000).Select(i=>(short)i));return;}
        if(args.Length==3&&args[0]=="restore-x-exclusive-accessories"){XExclusiveAccessories.Write(args[1],args[2]);return;}
        if(args.Length==3&&args[0]=="replace-native-accessories"){NativeAccessoryReplaceWriter.Write(args[1],args[2]);return;}
        if(args.Length==3&&args[0]=="restore-native-secondary-accessories"){SecondaryAccessoryCompatibility.Write(args[1],args[2]);return;}
		if (args.Length == 3 && args[0] == "polish-noble-natural") {
            HeadAdornmentTransfer.Write(args[1],new HashSet<string>(StringComparer.OrdinalIgnoreCase){"mikitm057_x","mikitm060_x"});
            using var native=BinaryFile.Load<CpkArchive>(Path.Combine(args[2],"diva_main.cpk"));
            NativeHairReplaceWriter.Write(args[1],native,native.Open<ObjectDatabase>("rom_steam/rom/objset/obj_db.bin"),Enumerable.Range(0,1000).Select(i=>(short)i));return;
        }
		if(args.Length==3&&args[0]=="polish-x-original-hairstyles") {
            HeadAdornmentTransfer.Write(args[1],new HashSet<string>(StringComparer.OrdinalIgnoreCase){"mikitm134_x","mikitm137_x","mikitm138_x","mikitm136_x","mikitm143_x","mikitm144_x","lenitm107_x","lenitm111_x","lenitm114_x","meiitm108_x","mikitm152_x"});
            XOriginalHairstyles.Write(args[1],args[2]);return;
        }
        if (args.Length == 3 && args[0] == "register-x-hairstyles") { XOriginalHairstyles.Write(args[1],args[2]); return; }
		if(args.Length==3&&args[0]=="refresh-angel-native-hair"){using var game=BinaryFile.Load<CpkArchive>(Path.Combine(args[2],"diva_main.cpk"));NativeHairReplaceWriter.Write(args[1],game,game.Open<ObjectDatabase>("rom_steam/rom/objset/obj_db.bin"),new short[]{212});return;}
        if(args.Length==3&&args[0]=="repair-summer-memories") {
            HeadAdornmentTransfer.Write(args[1],new HashSet<string>(StringComparer.OrdinalIgnoreCase){"mikitm010_x"});
            using var game=BinaryFile.Load<CpkArchive>(Path.Combine(args[2],"diva_main.cpk"));
            NativeHairReplaceWriter.Write(args[1],game,game.Open<ObjectDatabase>("rom_steam/rom/objset/obj_db.bin"),new short[]{9});return;
        }
        if(args.Length==2&&args[0]=="repair-angel-sealily"){HeadAdornmentTransfer.RepairAngelSeaLily(args[1]);return;}
        if (args.Length == 2 && args[0] == "audit-head-adornments") {
			HeadAdornmentTransfer.Audit(args[1]);
			return;
		}
		if (args.Length == 3 && args[0] == "attach-head-adornments") {
			HeadAdornmentTransfer.Audit(args[1]);
			HeadAdornmentTransfer.Write(args[1]);
            HeadSlotAccessoryOwnership.Write(args[1]);
			using var game = BinaryFile.Load<CpkArchive>(Path.Combine(args[2], "diva_main.cpk"));
			NativeHairReplaceWriter.Write(args[1], game, game.Open<ObjectDatabase>("rom_steam/rom/objset/obj_db.bin"), Enumerable.Range(0,1000).Select(i=>(short)i));
			return;
		}
		if (args.Length == 3 && args[0] == "attach-default-miku-headphones") {
			DefaultMikuHeadphones.Write(args[1]);
			using var game = BinaryFile.Load<CpkArchive>(Path.Combine(args[2], "diva_main.cpk"));
			NativeHairReplaceWriter.Write(args[1], game, game.Open<ObjectDatabase>("rom_steam/rom/objset/obj_db.bin"), new short[]{0});
			return;
		}
		if (args.Length == 2 && args[0] == "move-head-accessories") {
			HeadAccessoryMigrator.Write(args[1]); return;
		}
		if (args.Length == 3 && args[0] == "refresh-native-hair") {
			using var game = BinaryFile.Load<CpkArchive>(Path.Combine(args[2], "diva_main.cpk"));
			NativeHairReplaceWriter.Write(args[1], game, game.Open<ObjectDatabase>("rom_steam/rom/objset/obj_db.bin"), Enumerable.Range(0,1000).Select(i=>(short)i));
			return;
		}
		if (args.Length > 0 && args[0] == "attach-recommended-modules")
		{
			Environment.ExitCode = RecommendedModulesWriter.Run(args);
			return;
		}
		if (args.Length > 0 && args[0] == "build-comparison-mod")
		{
			Environment.ExitCode = ComparisonModBuilder.Run(args);
			return;
		}
		if (args.Length > 0 && args[0] == "rebuild-miku-pv-brows")
		{
			Environment.ExitCode = MikuPvBrowRebuildCommand.Run(args);
			return;
		}
		if (args.Length > 0 && args[0] == "rebuild-miku-generic-face")
		{
			Environment.ExitCode = GenericFaceRebuildCommand.Run(args);
			return;
		}
		if (args.Length > 0 && args[0] == "import-rob-reference")
		{
			Environment.ExitCode = RobReferenceImportCommand.Run(args);
			return;
		}
		if (args.Length > 0 && args[0] == "rebuild-p4-runtime-reference")
		{
			Environment.ExitCode = P4RuntimeReferenceCommand.Run(args);
			return;
		}
		if (SkinReferenceStagingWriterCommand.IsRequested(args))
		{
			Environment.ExitCode = SkinReferenceStagingWriterCommand.Run(args);
			return;
		}
		if (XMotionFamilyInventoryCommand.IsRequested(args))
		{
			Environment.ExitCode = XMotionFamilyInventoryCommand.Run(args);
			return;
		}
		if (MegaMixModuleReferenceCommand.IsRequested(args))
		{
			Environment.ExitCode = MegaMixModuleReferenceCommand.Run(args);
			return;
		}
		if (ObjectSkinProvenanceCommand.IsRequested(args))
		{
			Environment.ExitCode = ObjectSkinProvenanceCommand.Run(args);
			return;
		}
		if (args.Length > 0 && args[0] == "test-hair-root-isolation")
		{
			Environment.ExitCode = HairRootIsolationTest.Run(args);
			return;
		}
		if (args.Length > 0 && args[0] == "audit-hair-root-motion")
		{
			Environment.ExitCode = HairRootMotionAudit.Run(args);
			return;
		}
		if (ObjectSetPairInspectionCommand.IsRequested(args))
		{
			Environment.ExitCode = ObjectSetPairInspectionCommand.Run(args);
			return;
		}
		if (CpkModuleCorpusCommand.IsRequested(args))
		{
			Environment.ExitCode = CpkModuleCorpusCommand.Run(args);
			return;
		}
		if (P4ReferenceComparisonCommand.IsRequested(args))
		{
			Environment.ExitCode = P4ReferenceComparisonCommand.Run(args);
			return;
		}
		if (MegaMixStagingWriterCommand.IsRequested(args))
		{
			Environment.ExitCode = MegaMixStagingWriterCommand.Run(args);
			return;
		}
		if (PhysicalOccupancyCommand.IsRequested(args))
		{
			Environment.ExitCode = PhysicalOccupancyCommand.Run(args);
			return;
		}
		if (ModuleCandidateValidationCommand.IsRequested(args))
		{
			Environment.ExitCode = ModuleCandidateValidationCommand.Run(args);
			return;
		}
		if (IsolatedModuleStageCommand.IsRequested(args))
		{
			Environment.ExitCode = IsolatedModuleStageCommand.Run(args);
			return;
		}
		if (MetadataOccupancyCommand.IsRequested(args))
		{
			Environment.ExitCode = MetadataOccupancyCommand.Run(args);
			return;
		}
		if (PhysicalStageCompositionCommand.IsRequested(args))
		{
			Environment.ExitCode = PhysicalStageCompositionCommand.Run(args);
			return;
		}
		if (ReferenceObjectSetStagingWriterCommand.IsRequested(args))
		{
			Environment.ExitCode = ReferenceObjectSetStagingWriterCommand.Run(args);
			return;
		}
		if (CharacterItemLinkValidationCommand.IsRequested(args))
		{
			Environment.ExitCode = CharacterItemLinkValidationCommand.Run(args);
			return;
		}
		if (ModuleDatabaseValidationCommand.IsRequested(args))
		{
			Environment.ExitCode = ModuleDatabaseValidationCommand.Run(args);
			return;
		}
		if (ContentInspectionCommand.IsRequested(args))
		{
			Environment.ExitCode = ContentInspectionCommand.Run(args);
			return;
		}
		if (InstallationIdScannerCommand.IsRequested(args))
		{
			Environment.ExitCode = InstallationIdScannerCommand.Run(args);
			return;
		}
		if (LoaderPriorityResolverCommand.IsRequested(args))
		{
			Environment.ExitCode = LoaderPriorityResolverCommand.Run(args);
			return;
		}
		if (InstallationBindingPlannerCommand.IsRequested(args))
		{
			Environment.ExitCode = InstallationBindingPlannerCommand.Run(args);
			return;
		}
		if (ModuleBuildValidator.IsRequested(args))
		{
			Environment.ExitCode = ModuleBuildValidator.Run(args);
			return;
		}
		if (ModuleAnalysisCommand.IsRequested(args))
		{
			Environment.ExitCode = ModuleAnalysisCommand.Run(args);
			return;
		}
		if (!CliOptions.TryParse(args, out CliOptions options, out string error))
		{
			Console.WriteLine(error);
			CliOptions.PrintUsage();
			return;
		}
		if (!Path.Exists(args[2]))
		{
			Directory.CreateDirectory(args[2]);
		}
		if (!Path.Exists(Path.Combine(args[2], "rom")))
		{
			Directory.CreateDirectory(args[2]);
		}
		if (!Path.Exists(Path.Combine(args[2], Path.Combine("rom", "2d"))))
		{
			Directory.CreateDirectory(Path.Combine(args[2], Path.Combine("rom", "2d")));
		}
		if (!Path.Exists(Path.Combine(args[2], Path.Combine("rom", "lang2"))))
		{
			Directory.CreateDirectory(Path.Combine(args[2], Path.Combine("rom", "lang2")));
		}
		if (!Path.Exists(Path.Combine(args[2], Path.Combine("rom", "objset"))))
		{
			Directory.CreateDirectory(Path.Combine(args[2], Path.Combine("rom", "objset")));
		}
		if (!Path.Exists(Path.Combine(args[2], Path.Combine("rom", Path.Combine("rom", "rob")))))
		{
			Directory.CreateDirectory(Path.Combine(args[2], Path.Combine("rom", "rob")));
		}
		if (!Path.Exists(Path.Combine(args[2], Path.Combine("rom", "skin_param"))))
		{
			Directory.CreateDirectory(Path.Combine(args[2], Path.Combine("rom", "skin_param")));
		}
		if (!Path.Exists(Path.Combine(args[2], "rom_ps4")))
		{
			Directory.CreateDirectory(Path.Combine(args[2], "rom_ps4"));
		}
		if (!Path.Exists(Path.Combine(args[2], Path.Combine("rom_ps4", "rom"))))
		{
			Directory.CreateDirectory(Path.Combine(args[2], Path.Combine("rom_ps4", "rom")));
		}
		if (!Path.Exists(Path.Combine(args[2], "rom_ps4_dlc")))
		{
			Directory.CreateDirectory(Path.Combine(args[2], "rom_ps4_dlc"));
		}
		if (!Path.Exists(Path.Combine(args[2], Path.Combine("rom_ps4_dlc", "rom"))))
		{
			Directory.CreateDirectory(Path.Combine(args[2], Path.Combine("rom_ps4_dlc", "rom")));
		}
		if (!Path.Exists(Path.Combine(args[2], "rom_switch")))
		{
			Directory.CreateDirectory(Path.Combine(args[2], "rom_switch"));
		}
		if (!Path.Exists(Path.Combine(args[2], Path.Combine("rom_switch", "rom"))))
		{
			Directory.CreateDirectory(Path.Combine(args[2], Path.Combine("rom_switch", "rom")));
		}
		if (!args.Contains("--module-id"))
		using (FarcArchive farcArchive = new FarcArchive())
		{
			MemoryStream memoryStream = new MemoryStream();
			memoryStream.WriteByte(35);
			for (int i = 0; i < 10; i++)
			{
				CharacterType characterType = (CharacterType)i;
				farcArchive.Add(characterType.ToString().ToLower() + "itm_tbl.txt", memoryStream, leaveOpen: true);
			}
			new MemoryStream();
			farcArchive.Save(Path.Combine(args[2], "rom_ps4", "rom", "chritm_prop.farc"));
			File.Copy(Path.Combine(args[2], "rom_ps4", "rom", "chritm_prop.farc"), Path.Combine(args[2], "rom_ps4_dlc", "rom", "mdata_chritm_prop.farc"), overwrite: true);
			File.Copy(Path.Combine(args[2], "rom_ps4", "rom", "chritm_prop.farc"), Path.Combine(args[2], "rom_switch", "rom", "chritm_prop.farc"), overwrite: true);
		}
		StreamWriter streamWriter = File.CreateText(Path.Combine(args[2], "config.toml"));
		streamWriter.WriteLine("enabled = true");
		streamWriter.WriteLine("include = [\".\"]");
		streamWriter.WriteLine("name = \"Project DIVA X Rob Conversion\"");
		streamWriter.WriteLine("author = \"XRomConverter by thatrandomlurker; parameters borrowed from MFMK by skyth\"");
		streamWriter.Close();
		ContentSource contentSource = null;
		List<ContentSource> list = new List<ContentSource>();
		try
		{
			contentSource = ContentSource.Load("base", options.BaseRomPath);
			for (int j = 0; j < options.DlcRomPaths.Count; j++)
			{
				list.Add(ContentSource.Load($"dlc[{j + 1}]", options.DlcRomPaths[j]));
			}
		}
		catch (Exception ex)
		{
			contentSource?.Dispose();
			foreach (ContentSource item5 in list)
			{
				item5.Dispose();
			}
			Console.WriteLine("Failed to load Project DIVA X content: " + ex.Message);
			return;
		}
		ContentSource contentSource2 = contentSource;
		using (contentSource2)
		{
			using (new ContentSourceGroup(list))
			{
				if (list.Any((ContentSource x) => x.FirstRead.AddonContentPackage.PackageType != ACPKType.DLC))
				{
					Console.WriteLine("At least one --dlc source is not marked as DLC in firstread.bin.");
					return;
				}
				IReadOnlyList<ContentSource> readOnlyList = ((list.Count == 0) ? new ContentSource[1] { contentSource2 } : ((!options.IncludeBaseContent) ? ((IReadOnlyList<ContentSource>)list) : ((IReadOnlyList<ContentSource>)new ContentSource[1] { contentSource2 }.Concat(list).ToArray())));
				List<ACCT> activeContainers = readOnlyList.SelectMany((ContentSource x) => x.Containers).ToList();
				if (activeContainers.Count == 0)
				{
					Console.WriteLine("The selected content contains no containers.");
					return;
				}
				ContentResolver contentResolver = new ContentResolver(list.Concat(new ContentSource[1] { contentSource2 }).ToArray());
				Console.WriteLine($"[CONTENT] Base: package {contentSource2.FirstRead.AddonContentPackage.PackageID}, {contentSource2.Containers.Count} container(s), PSARC: {contentSource2.HasPsarc}, included: {readOnlyList.Contains(contentSource2)}");
				foreach (ContentSource item6 in list)
				{
					ADCT firstRead = item6.FirstRead;
					Console.WriteLine($"[CONTENT] {item6.Id}: {firstRead.AddonContentPackage.Description.Trim()} (package {firstRead.AddonContentPackage.PackageID}), {firstRead.AddonContentContainers.Count} container(s)");
				}
				if (activeContainers.Any((ACCT x) => x.Paths.Any((ACCTPath y) => y.FileName.EndsWith("emcs"))))
				{
					Console.WriteLine("XHD is currently unsupported, Please select a valid Project Diva X rom folder with unpacked data.");
					return;
				}
				Dictionary<uint, string> objsetHashNameMap = new Dictionary<uint, string>();
				Dictionary<uint, string> objectHashNameMap = new Dictionary<uint, string>();
				Dictionary<uint, string> textureHashNameMap = new Dictionary<uint, string>();
				objectHashNameMap.Add(MurmurHash.Calculate("NULL"), "NULL");
				List<ModuleInfo> modules = activeContainers.SelectMany((ACCT x) => x.ParameterDatabases).SelectMany((PRDB x) => x.ModuleTable.Modules).ToList();
				Dictionary<short, short> sourceModuleIds = modules.ToDictionary(x => x.ModuleID, x => x.ModuleID);
				List<CustomizeItemInfo> list2 = activeContainers.SelectMany((ACCT x) => x.ParameterDatabases).SelectMany((PRDB x) => x.CustomizeItemTable.CustomizeItems).ToList();
				List<STRA> stringArrays = list.Concat(new ContentSource[1] { contentSource2 }).Where(((IEnumerable<ContentSource>)readOnlyList).Contains<ContentSource>).SelectMany((ContentSource x) => x.Containers)
					.SelectMany((ACCT x) => x.StringArrays)
					.ToList();
				List<Item> list3;
				List<List<Item>> list4;
				try
				{
					list3 = MergeRepeatedItems(activeContainers.SelectMany((ACCT x) => x.RobDatabases).SelectMany((RBDB x) => x.CustomizeItems), "shared customize items");
					list4 = (from num21 in Enumerable.Range(0, 11)
						select MergeRepeatedItems((from x in activeContainers.SelectMany((ACCT x) => x.RobDatabases)
							where x.CharacterItemTables.Count > num21
							select x).SelectMany((RBDB x) => x.CharacterItemTables[num21].Items), $"{num21} items")).ToList();
				}
				catch (InvalidDataException ex2)
				{
					Console.WriteLine(ex2.Message);
					return;
				}
				List<List<Costume>> characterCostumes = (from num21 in Enumerable.Range(0, 11)
					select (from x in (from x in activeContainers.SelectMany((ACCT x) => x.RobDatabases)
							where x.CharacterItemTables.Count > num21
							select x).SelectMany((RBDB x) => x.CharacterItemTables[num21].Costumes)
						group x by x.CostumeID into x
						select x.Last()).ToList()).ToList();
				RemoveUnsupportedExtraCharacterContent(modules, list4, characterCostumes);
				int moduleFilterIndex = Array.FindIndex(args, x => x.Equals("--module-id", StringComparison.OrdinalIgnoreCase));
				bool includeBaseHead = args.Contains("--include-base-head", StringComparer.OrdinalIgnoreCase);
				if (moduleFilterIndex >= 0)
				{
					if (moduleFilterIndex + 1 >= args.Length || !short.TryParse(args[moduleFilterIndex + 1], out short selectedModuleId))
						throw new ArgumentException("--module-id requires a numeric Diva X module ID.");
					var selectedModules = modules.Where(x => x.ModuleID == selectedModuleId).ToList();
					if (selectedModules.Count != 1)
						throw new InvalidDataException($"Expected exactly one source module {selectedModuleId}, found {selectedModules.Count}.");
					modules = selectedModules;
					for (int selectedCharacter = 0; selectedCharacter < characterCostumes.Count; selectedCharacter++)
					{
						var allowedCos = modules.Where(x => (int)x.Character == selectedCharacter).Select(x => x.CosID).ToHashSet();
						characterCostumes[selectedCharacter] = characterCostumes[selectedCharacter]
							.Where(x => allowedCos.Contains(x.CostumeID)).ToList();
						var allowedItems = characterCostumes[selectedCharacter].SelectMany(x => x.Parts)
							.Where(x => x.ItemNumber != 0).Select(x => x.ItemNumber).ToHashSet();
						list4[selectedCharacter] = list4[selectedCharacter]
							.Where(x => allowedItems.Contains(x.ItemNumber)).ToList();
					}
					int targetIdIndex = Array.FindIndex(args, x => x.Equals("--target-id", StringComparison.OrdinalIgnoreCase));
					if (targetIdIndex >= 0)
					{
						if (targetIdIndex + 1 >= args.Length || !short.TryParse(args[targetIdIndex + 1], out short targetModuleId))
							throw new ArgumentException("--target-id requires a numeric target module ID.");
						sourceModuleIds[targetModuleId] = selectedModuleId;
						modules[0].ModuleID = targetModuleId;
					}
					int targetCosIndex = Array.FindIndex(args, x => x.Equals("--target-cos-id", StringComparison.OrdinalIgnoreCase));
					if (targetCosIndex >= 0)
					{
						if (targetCosIndex + 1 >= args.Length || !short.TryParse(args[targetCosIndex + 1], out short targetCosId))
							throw new ArgumentException("--target-cos-id requires a numeric target costume ID.");
						modules[0].CosID = targetCosId;
						foreach (Costume costume in characterCostumes[(int)modules[0].Character]) costume.CostumeID = targetCosId;
					}
					int targetItemsIndex = Array.FindIndex(args, x => x.Equals("--target-items", StringComparison.OrdinalIgnoreCase));
					if (targetItemsIndex >= 0)
					{
						if (targetItemsIndex + 1 >= args.Length) throw new ArgumentException("--target-items requires SOURCE:TARGET pairs.");
						var itemBindings = args[targetItemsIndex + 1].Split(',', StringSplitOptions.RemoveEmptyEntries)
							.Select(x => x.Split(':')).ToDictionary(x => short.Parse(x[0]), x => short.Parse(x[1]));
						foreach (Item item in list4[(int)modules[0].Character])
							if (itemBindings.TryGetValue(item.ItemNumber, out short targetItem)) item.ItemNumber = targetItem;
						foreach (Costume costume in characterCostumes[(int)modules[0].Character])
							foreach (CostumePart part in costume.Parts)
								if (itemBindings.TryGetValue(part.ItemNumber, out short targetItem)) part.ItemNumber = targetItem;
					}
				}
				string text = string.Join(", ", from x in modules
					group x by x.ModuleID into x
					where x.Count() > 1
					select x.Key);
				if (text.Length > 0)
				{
					Console.WriteLine("Duplicate module ID(s) across selected sources: " + text);
					return;
				}
				HashSet<uint> referencedObjectSetIds = list4.SelectMany((List<Item> x) => x).Concat(list3).SelectMany((Item x) => x.ObjectSets)
					.ToHashSet();
                // Hair selections use item identities independently of module/COS identities.
                // Plan aliases while source module IDs and costume IDs are still intact.
                if (!args.Contains("--target-items", StringComparer.OrdinalIgnoreCase) &&
                    !args.Contains("--target-id", StringComparer.OrdinalIgnoreCase) &&
                    !args.Contains("--target-cos-id", StringComparer.OrdinalIgnoreCase))
                    HairIdentityRemapper.Apply(modules, list4, characterCostumes);
                // Apply all final identities simultaneously: native IDs may overlap source IDs.
                if (!args.Contains("--target-id", StringComparer.OrdinalIgnoreCase) && !args.Contains("--target-cos-id", StringComparer.OrdinalIgnoreCase))
                {
                    using var idMap = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Resources", "modules", "x-original-id-map.json")));
                    var bindings = idMap.RootElement.EnumerateArray().ToDictionary(x => x.GetProperty("sourceModule").GetInt16(), x => (Id: x.GetProperty("module").GetInt16(), Cos: x.GetProperty("cos").GetInt16()));
                    var assignments = modules.Select(m => (Module: m, Source: m.ModuleID, SourceCos: m.CosID,
                        Target: bindings.GetValueOrDefault(m.ModuleID, (Id: m.ModuleID, Cos: m.CosID)))).ToArray();
                    if (assignments.Select(x => x.Target.Id).Distinct().Count() != assignments.Length)
                        throw new InvalidDataException("Final module identities collide.");
                    var costumeMaps = assignments.GroupBy(x => (int)x.Module.Character).ToDictionary(g => g.Key,
                        g => g.GroupBy(x => x.SourceCos).ToDictionary(c => c.Key, c => c.Select(x => x.Target.Cos).Distinct().Single()));
                    foreach (var group in costumeMaps)
                    {
                        var changes = characterCostumes[group.Key].Where(c => group.Value.ContainsKey(c.CostumeID)).Select(c => (Costume: c, Target: group.Value.GetValueOrDefault(c.CostumeID, c.CostumeID))).ToArray();
                        if (changes.Select(x => x.Target).Distinct().Count() != changes.Length)
                            throw new InvalidDataException("Final costume identities collide.");
                        foreach (var change in changes) change.Costume.CostumeID = change.Target;
                        characterCostumes[group.Key] = changes.Select(x => x.Costume).ToList();
                    }
                    sourceModuleIds.Clear();
                    foreach (var a in assignments)
                    {
                        a.Module.ModuleID = a.Target.Id; a.Module.CosID = a.Target.Cos;
                        sourceModuleIds[a.Target.Id] = a.Source;
                    }
                }
				List<string> list5 = readOnlyList.SelectMany((ContentSource x) => x.EnumerateObjectSetFiles()).Distinct<string>(StringComparer.OrdinalIgnoreCase).ToList();
				if (!readOnlyList.Contains(contentSource2))
				{
					string[] collection = new string[10] { "mikitm000.farc", "rinitm000.farc", "lenitm000.farc", "lukitm000.farc", "neritm000.farc", "hakitm000.farc", "kaiitm000.farc", "meiitm000.farc", "sakitm000.farc", "tetitm000.farc" };
					list5.AddRange(collection);
					list5.AddRange(from x in contentSource2.EnumerateObjectSetFiles()
						where referencedObjectSetIds.Contains(MurmurHash.Calculate(Path.GetFileNameWithoutExtension(x)))
						select x);
				}
				foreach (string item7 in list5.Distinct<string>(StringComparer.OrdinalIgnoreCase))
				{
					ContentSource source2;
					using Stream source = contentResolver.OpenRequired(item7, out source2);
					using FarcArchive archive = BinaryFile.Load<FarcArchive>(source);
					string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(item7);
					Console.WriteLine("[CONTENT] " + item7 + " <- " + source2.Id);
					ObjectDatabase objectDatabase = archive.Open<ObjectDatabase>(fileNameWithoutExtension + ".osi");
					TextureDatabase textureDatabase = archive.Open<TextureDatabase>(fileNameWithoutExtension + ".txi");
					if (!objsetHashNameMap.ContainsKey(objectDatabase.ObjectSets[0].Id))
					{
						objsetHashNameMap.Add(objectDatabase.ObjectSets[0].Id, objectDatabase.ObjectSets[0].Name);
					}
					foreach (ObjectInfo @object in objectDatabase.ObjectSets[0].Objects)
					{
						if (!objectHashNameMap.ContainsKey(@object.Id))
						{
							objectHashNameMap.Add(@object.Id, @object.Name);
						}
					}
					foreach (TextureInfo texture in textureDatabase.Textures)
					{
						if (!textureHashNameMap.ContainsKey(texture.Id))
						{
							textureHashNameMap.Add(texture.Id, texture.Name);
						}
					}
				}
				int characterIndex;
				for (characterIndex = 0; characterIndex < list4.Count; characterIndex++)
				{
					HashSet<short> hashSet = (from x in modules
						where x.Character == (CharacterType)characterIndex
						select x.CosID).ToHashSet();
					HashSet<short> orphanCostumes = new HashSet<short>();
					List<Item> list6 = new List<Item>();
					foreach (Item item in list4[characterIndex])
					{
						if (!item.ObjectSets.Any((uint x) => !objsetHashNameMap.ContainsKey(x)))
						{
							list6.Add(item);
							continue;
						}
						int stringId = int.Parse($"190{characterIndex:D2}{item.ItemNumber:D4}");
						string? obj = stringArrays.Select((STRA x) => x.GetStringById(stringId)).FirstOrDefault((string x) => x.Length > 0) ?? "";
						List<short> list7 = (from x in characterCostumes[characterIndex]
							where x.Parts.Any((CostumePart y) => y.ItemNumber == item.ItemNumber)
							select x.CostumeID).ToList();
						bool flag = list7.Any(hashSet.Contains);
						if (obj.Length == 0 && !flag)
						{
							foreach (short item8 in list7)
							{
								orphanCostumes.Add(item8);
							}
							Console.WriteLine($"[CONTENT] Ignoring orphan {characterIndex} item {item.ItemNumber} with unresolved object set(s).");
						}
						else
						{
							list6.Add(item);
						}
					}
					list4[characterIndex] = list6;
					characterCostumes[characterIndex] = characterCostumes[characterIndex].Where((Costume x) => !orphanCostumes.Contains(x.CostumeID)).ToList();
				}
				referencedObjectSetIds = list4.SelectMany((List<Item> x) => x).Concat(list3).SelectMany((Item x) => x.ObjectSets)
					.ToHashSet();
				ContentSource source6;
				if (args.Any((string x) => x.Equals("--inspect", StringComparison.OrdinalIgnoreCase)))
				{
					using (Stream source3 = contentSource2.OpenRequired("bone_data.bon"))
					{
						BinaryFile.Load<BoneData>(source3);
					}
					string[] array = new string[10] { "mot_mik.farc", "mot_rin.farc", "mot_len.farc", "mot_luk.farc", "mot_ner.farc", "mot_hak.farc", "mot_kai.farc", "mot_mei.farc", "mot_sak.farc", "mot_tet.farc" };
					foreach (string fileName in array)
					{
						using Stream source4 = contentSource2.OpenRequired(fileName);
						using (BinaryFile.Load<FarcArchive>(source4))
						{
						}
					}
					List<string> list8 = (from x in activeContainers.SelectMany((ACCT x) => x.Paths)
						select x.FileName into x
						where x.EndsWith(".osp", StringComparison.OrdinalIgnoreCase)
						select x).Distinct<string>(StringComparer.OrdinalIgnoreCase).ToList();
					foreach (string item9 in list8)
					{
						using Stream source5 = contentResolver.OpenRequired(item9, out source6);
						new OsageSkinParameterSet().Load(source5);
					}
					List<string> list9 = activeContainers.SelectMany((ACCT x) => x.Auth2DFlist).Distinct<string>(StringComparer.OrdinalIgnoreCase).ToList();
					foreach (string item10 in list9)
					{
						using Stream source7 = contentResolver.OpenRequired(item10, out source6);
						using (BinaryFile.Load<FarcArchive>(source7))
						{
						}
					}
					List<uint> list10 = referencedObjectSetIds.Where((uint x) => !objsetHashNameMap.ContainsKey(x)).ToList();
					if (list10.Count > 0)
					{
						Console.WriteLine("[INSPECT] Missing object set ID(s): " + string.Join(", ", list10));
						foreach (uint missingObjectSet in list10)
						{
							List<string> values = list4.SelectMany((List<Item> items, int num21) => items.Where((Item x) => x.ObjectSets.Contains(missingObjectSet)).Select(delegate(Item x)
							{
								int stringId2 = int.Parse($"190{num21:D2}{x.ItemNumber:D4}");
								string value6 = stringArrays.Select((STRA y) => y.GetStringById(stringId2)).FirstOrDefault((string y) => y.Length > 0) ?? "";
								string value7 = string.Join(", ", characterCostumes[num21].Where((Costume y) => y.Parts.Any((CostumePart z) => z.ItemNumber == x.ItemNumber)).SelectMany(delegate(Costume y)
								{
									List<ModuleInfo> list13 = modules.Where((ModuleInfo z) => z.Character == (CharacterType)num21 && z.CosID == y.CostumeID).ToList();
									return (list13.Count == 0) ? new string[1] { $"COS_{y.CostumeID + 1}" } : list13.Select(delegate(ModuleInfo z)
									{
										string value8 = stringArrays.Select((STRA a) => a.GetStringById(10010000 + z.ModuleID)).FirstOrDefault((string a) => a.Length > 0) ?? "";
										return $"module {z.ModuleID} '{value8}' (COS_{y.CostumeID + 1})";
									});
								}));
								return $"{num21} item {x.ItemNumber} ('{value6}', costumes: [{value7}])";
							})).Concat(list3.Where((Item x) => x.ObjectSets.Contains(missingObjectSet)).Select(delegate(Item x)
							{
								int stringId2 = int.Parse($"19099{x.ItemNumber:D4}");
								string value6 = stringArrays.Select((STRA y) => y.GetStringById(stringId2)).FirstOrDefault((string y) => y.Length > 0) ?? "";
								return $"shared customize item {x.ItemNumber} ('{value6}')";
							})).ToList();
							Console.WriteLine($"[INSPECT] Missing object set {missingObjectSet} referenced by: " + string.Join(", ", values));
						}
					}
					Console.WriteLine($"[INSPECT] Success: {modules.Count} module(s), {readOnlyList.SelectMany((ContentSource x) => x.EnumerateObjectSetFiles()).Distinct<string>(StringComparer.OrdinalIgnoreCase).Count()} DLC/base object set file(s), {referencedObjectSetIds.Count} referenced object set ID(s), {list8.Count} skin parameter file(s), {list9.Count} sprite archive(s), {list10.Count} unresolved.");
					if (list10.Count > 0)
					{
						Environment.ExitCode = 2;
					}
					return;
				}
				CpkArchive archive2 = BinaryFile.Load<CpkArchive>(Path.Combine(args[1], "diva_main.cpk"));
				BoneData boneData = archive2.Open<BoneData>("rom/bone_data.bin");
				ObjectDatabase objectDatabase2 = archive2.Open<ObjectDatabase>("rom_steam/rom/objset/obj_db.bin");
				archive2.Open<TextureDatabase>("rom_steam/rom/objset/tex_db.bin");
				MotionDatabase mmMotionData = archive2.Open<FarcArchive>("rom_ps4/rom/rob/mot_db.farc").Open<MotionDatabase>("mot_db.bin");
				SpriteDatabase spriteDatabase = archive2.Open<SpriteDatabase>("rom_steam/rom/2d/spr_db.bin");
				using Stream source8 = contentSource2.OpenRequired("bone_data.bon");
				BoneData boneData2 = BinaryFile.Load<BoneData>(source8);
				Dictionary<int, string> dictionary = new Dictionary<int, string>
				{
					{ 0, "MIKU" },
					{ 1, "RIN" },
					{ 2, "LEN" },
					{ 3, "LUKA" },
					{ 4, "NERU" },
					{ 5, "HAKU" },
					{ 6, "KAITO" },
					{ 7, "MEIKO" },
					{ 8, "SAKINE" },
					{ 9, "TETO" },
					{ 10, "MIKU" }
				};
				Dictionary<string, List<string>> dictionary2 = new Dictionary<string, List<string>>
				{
					{
						"j_cloth_skirt_b_000_wj",
						new List<string> { "j_cloth_skirt_b_000_wj" }
					},
					{
						"j_cloth_skirt_f_000_wj",
						new List<string> { "j_cloth_skirt_f_000_wj" }
					},
					{
						"j_cloth_skirt_l_02_000_wj",
						new List<string> { "j_cloth_skirt_l_02_000_wj" }
					},
					{
						"j_cloth_skirt_l_04_000_wj",
						new List<string> { "j_cloth_skirt_l_04_000_wj" }
					},
					{
						"j_cloth_skirt_l_06_000_wj",
						new List<string> { "j_cloth_skirt_l_06_000_wj" }
					},
					{
						"j_cloth_skirt_r_02_000_wj",
						new List<string> { "j_cloth_skirt_r_02_000_wj" }
					},
					{
						"j_cloth_skirt_r_04_000_wj",
						new List<string> { "j_cloth_skirt_r_04_000_wj" }
					},
					{
						"j_cloth_skirt_r_06_000_wj",
						new List<string> { "j_cloth_skirt_r_06_000_wj" }
					}
				};
				Dictionary<string, List<string>> dictionary3 = new Dictionary<string, List<string>>
				{
					{
						"j_cloth_long_f_000_wj",
						new List<string> { "j_cloth_long_f_000_wj", "j_cloth_long_f_001_wj", "j_cloth_long_f_002_wj" }
					},
					{
						"j_cloth_long_b_000_wj",
						new List<string> { "j_cloth_long_b_000_wj", "j_cloth_long_b_001_wj", "j_cloth_long_b_002_wj" }
					},
					{
						"j_cloth_long_l_01_000_wj",
						new List<string> { "j_cloth_long_l_01_000_wj", "j_cloth_long_l_01_001_wj", "j_cloth_long_l_01_002_wj" }
					},
					{
						"j_cloth_long_l_02_000_wj",
						new List<string> { "j_cloth_long_l_02_000_wj", "j_cloth_long_l_02_001_wj", "j_cloth_long_l_02_002_wj" }
					},
					{
						"j_cloth_long_l_03_000_wj",
						new List<string> { "j_cloth_long_l_03_000_wj", "j_cloth_long_l_03_001_wj", "j_cloth_long_l_03_002_wj" }
					},
					{
						"j_cloth_long_r_01_000_wj",
						new List<string> { "j_cloth_long_r_01_000_wj", "j_cloth_long_r_01_001_wj", "j_cloth_long_r_01_002_wj" }
					},
					{
						"j_cloth_long_r_02_000_wj",
						new List<string> { "j_cloth_long_r_02_000_wj", "j_cloth_long_r_02_001_wj", "j_cloth_long_r_02_002_wj" }
					},
					{
						"j_cloth_long_r_03_000_wj",
						new List<string> { "j_cloth_long_r_03_000_wj", "j_cloth_long_r_03_001_wj", "j_cloth_long_r_03_002_wj" }
					}
				};
				Dictionary<string, List<string>> dictionary4 = new Dictionary<string, List<string>>
				{
					{
						"j_hair_twin_l_000_wj",
						new List<string> { "j_hair_twin_l_000_wj", "j_hair_twin_l_001_wj", "j_hair_twin_l_002_wj", "j_hair_twin_l_003_wj", "j_hair_twin_l_004_wj", "j_hair_twin_l_005_wj" }
					},
					{
						"j_hair_twin_r_000_wj",
						new List<string> { "j_hair_twin_r_000_wj", "j_hair_twin_r_001_wj", "j_hair_twin_r_002_wj", "j_hair_twin_r_003_wj", "j_hair_twin_r_004_wj", "j_hair_twin_r_005_wj" }
					}
				};
				Dictionary<string, List<string>> dictionary5 = new Dictionary<string, List<string>>
				{
					{
						"j_hair_long_c_000_wj",
						new List<string> { "j_hair_long_c_000_wj", "j_hair_long_c_001_wj", "j_hair_long_c_002_wj", "j_hair_long_c_003_wj", "j_hair_long_c_004_wj" }
					},
					{
						"j_hair_long_l_01_000_wj",
						new List<string> { "j_hair_long_l_01_000_wj", "j_hair_long_l_01_001_wj", "j_hair_long_l_01_002_wj" }
					},
					{
						"j_hair_long_r_01_000_wj",
						new List<string> { "j_hair_long_r_01_000_wj", "j_hair_long_r_01_001_wj", "j_hair_long_r_01_002_wj" }
					}
				};
				new Dictionary<string, string>
				{
					{ "j_cloth_skirt_b_000_wj", "kl_kosi_etc_wj" },
					{ "j_cloth_skirt_f_000_wj", "kl_kosi_etc_wj" },
					{ "j_cloth_skirt_l_02_000_wj", "kl_kosi_etc_wj" },
					{ "j_cloth_skirt_l_04_000_wj", "kl_kosi_etc_wj" },
					{ "j_cloth_skirt_l_06_000_wj", "kl_kosi_etc_wj" },
					{ "j_cloth_skirt_r_02_000_wj", "kl_kosi_etc_wj" },
					{ "j_cloth_skirt_r_04_000_wj", "kl_kosi_etc_wj" },
					{ "j_cloth_skirt_r_06_000_wj", "kl_kosi_etc_wj" },
					{ "j_cloth_long_b_000_wj", "kl_kosi_etc_wj" },
					{ "j_cloth_long_b_001_wj", "j_cloth_long_b_000_wj" },
					{ "j_cloth_long_b_002_wj", "j_cloth_long_b_001_wj" },
					{ "j_cloth_long_f_000_wj", "kl_kosi_etc_wj" },
					{ "j_cloth_long_f_001_wj", "j_cloth_long_f_000_wj" },
					{ "j_cloth_long_f_002_wj", "j_cloth_long_f_001_wj" },
					{ "j_cloth_long_l_01_000_wj", "kl_kosi_etc_wj" },
					{ "j_cloth_long_l_01_001_wj", "j_cloth_long_l_01_000_wj" },
					{ "j_cloth_long_l_01_002_wj", "j_cloth_long_l_01_001_wj" },
					{ "j_cloth_long_l_02_000_wj", "kl_kosi_etc_wj" },
					{ "j_cloth_long_l_02_001_wj", "j_cloth_long_l_02_000_wj" },
					{ "j_cloth_long_l_02_002_wj", "j_cloth_long_l_02_001_wj" },
					{ "j_cloth_long_l_03_000_wj", "kl_kosi_etc_wj" },
					{ "j_cloth_long_l_03_001_wj", "j_cloth_long_l_03_000_wj" },
					{ "j_cloth_long_l_03_002_wj", "j_cloth_long_l_03_001_wj" },
					{ "j_cloth_long_r_01_000_wj", "kl_kosi_etc_wj" },
					{ "j_cloth_long_r_01_001_wj", "j_cloth_long_r_01_000_wj" },
					{ "j_cloth_long_r_01_002_wj", "j_cloth_long_r_01_001_wj" },
					{ "j_cloth_long_r_02_000_wj", "kl_kosi_etc_wj" },
					{ "j_cloth_long_r_02_001_wj", "j_cloth_long_r_02_000_wj" },
					{ "j_cloth_long_r_02_002_wj", "j_cloth_long_r_02_001_wj" },
					{ "j_cloth_long_r_03_000_wj", "kl_kosi_etc_wj" },
					{ "j_cloth_long_r_03_001_wj", "j_cloth_long_r_03_000_wj" },
					{ "j_cloth_long_r_03_002_wj", "j_cloth_long_r_03_001_wj" },
					{ "j_hair_twin_l_000_wj", "j_kao_wj" },
					{ "j_hair_twin_l_001_wj", "j_hair_twin_l_000_wj" },
					{ "j_hair_twin_l_002_wj", "j_hair_twin_l_001_wj" },
					{ "j_hair_twin_l_003_wj", "j_hair_twin_l_002_wj" },
					{ "j_hair_twin_l_004_wj", "j_hair_twin_l_003_wj" },
					{ "j_hair_twin_l_005_wj", "j_hair_twin_l_004_wj" },
					{ "j_hair_twin_r_000_wj", "j_kao_wj" },
					{ "j_hair_twin_r_001_wj", "j_hair_twin_r_000_wj" },
					{ "j_hair_twin_r_002_wj", "j_hair_twin_r_001_wj" },
					{ "j_hair_twin_r_003_wj", "j_hair_twin_r_002_wj" },
					{ "j_hair_twin_r_004_wj", "j_hair_twin_r_003_wj" },
					{ "j_hair_twin_r_005_wj", "j_hair_twin_r_004_wj" },
					{ "j_hair_long_c_000_wj", "j_kao_wj" },
					{ "j_hair_long_c_001_wj", "j_hair_long_c_000_wj" },
					{ "j_hair_long_c_002_wj", "j_hair_long_c_001_wj" },
					{ "j_hair_long_c_003_wj", "j_hair_long_c_002_wj" },
					{ "j_hair_long_c_004_wj", "j_hair_long_c_003_wj" },
					{ "j_hair_long_l_01_000_wj", "j_kao_wj" },
					{ "j_hair_long_l_01_001_wj", "j_hair_long_l_01_000_wj" },
					{ "j_hair_long_l_01_002_wj", "j_hair_long_l_01_001_wj" },
					{ "j_hair_long_r_01_000_wj", "j_kao_wj" },
					{ "j_hair_long_r_01_001_wj", "j_hair_long_r_01_000_wj" },
					{ "j_hair_long_r_01_002_wj", "j_hair_long_r_01_001_wj" }
				};
				Dictionary<string, float> dictionary6 = new Dictionary<string, float>
				{
					{ "j_cloth_skirt_f_000_wj", 0.22828364f },
					{ "j_cloth_skirt_b_000_wj", 0.3169026f },
					{ "j_cloth_skirt_l_02_000_wj", 0.19258642f },
					{ "j_cloth_skirt_l_04_000_wj", 0.26696336f },
					{ "j_cloth_skirt_l_06_000_wj", 0.23032296f },
					{ "j_cloth_skirt_r_02_000_wj", 0.19258642f },
					{ "j_cloth_skirt_r_04_000_wj", 0.26696336f },
					{ "j_cloth_skirt_r_06_000_wj", 0.23032296f },
					{ "j_cloth_long_f_000_wj", 0.20000039f },
					{ "j_cloth_long_f_001_wj", 0.22999932f },
					{ "j_cloth_long_f_002_wj", 0.22999932f },
					{ "j_cloth_long_b_000_wj", 0.1800004f },
					{ "j_cloth_long_b_001_wj", 0.1999994f },
					{ "j_cloth_long_b_002_wj", 0.1999994f },
					{ "j_cloth_long_l_01_000_wj", 0.20000039f },
					{ "j_cloth_long_l_01_001_wj", 0.22999938f },
					{ "j_cloth_long_l_01_002_wj", 0.22999938f },
					{ "j_cloth_long_l_02_000_wj", 0.20000042f },
					{ "j_cloth_long_l_02_001_wj", 0.21999964f },
					{ "j_cloth_long_l_02_002_wj", 0.21999964f },
					{ "j_cloth_long_l_03_000_wj", 0.18000035f },
					{ "j_cloth_long_l_03_001_wj", 0.19999975f },
					{ "j_cloth_long_l_03_002_wj", 0.19999975f },
					{ "j_cloth_long_r_01_000_wj", 0.20000039f },
					{ "j_cloth_long_r_01_001_wj", 0.22999938f },
					{ "j_cloth_long_r_01_002_wj", 0.22999938f },
					{ "j_cloth_long_r_02_000_wj", 0.20000042f },
					{ "j_cloth_long_r_02_001_wj", 0.21999964f },
					{ "j_cloth_long_r_02_002_wj", 0.21999964f },
					{ "j_cloth_long_r_03_000_wj", 0.18000035f },
					{ "j_cloth_long_r_03_001_wj", 0.19999975f },
					{ "j_cloth_long_r_03_002_wj", 0.19999975f },
					{ "j_hair_twin_l_000_wj", 0.222f },
					{ "j_hair_twin_l_001_wj", 0.222f },
					{ "j_hair_twin_l_002_wj", 0.222f },
					{ "j_hair_twin_l_003_wj", 0.222f },
					{ "j_hair_twin_l_004_wj", 0.222f },
					{ "j_hair_twin_l_005_wj", 0.222f },
					{ "j_hair_twin_r_000_wj", 0.222f },
					{ "j_hair_twin_r_001_wj", 0.222f },
					{ "j_hair_twin_r_002_wj", 0.222f },
					{ "j_hair_twin_r_003_wj", 0.222f },
					{ "j_hair_twin_r_004_wj", 0.222f },
					{ "j_hair_twin_r_005_wj", 0.222f },
					{ "j_hair_long_c_000_wj", 0.17000431f },
					{ "j_hair_long_c_001_wj", 0.15999728f },
					{ "j_hair_long_c_002_wj", 0.22000293f },
					{ "j_hair_long_c_003_wj", 0.17999971f },
					{ "j_hair_long_c_004_wj", 0.17999971f },
					{ "j_hair_long_l_01_000_wj", 0.120002985f },
					{ "j_hair_long_l_01_001_wj", 0.19999824f },
					{ "j_hair_long_l_01_002_wj", 0.19999824f },
					{ "j_hair_long_r_01_000_wj", 0.120002985f },
					{ "j_hair_long_r_01_001_wj", 0.19999824f },
					{ "j_hair_long_r_01_002_wj", 0.19999824f }
				};
				Dictionary<string, OsageSkinParameter> dictionary7 = new Dictionary<string, OsageSkinParameter>();
				OsageSkinParameter obj2 = new OsageSkinParameter
				{
					Name = "c_cloth_skirt_b_osg",
					AirResistance = 0.9f
				};
				List<OsageCollisionParameter> collisions = obj2.Collisions;
				OsageCollisionParameter osageCollisionParameter = new OsageCollisionParameter();
				osageCollisionParameter.Bone0.Name = "j_momo_r_wj";
				osageCollisionParameter.Bone0.Position = new Vector3(0.25f, -1f / 64f, 0.003906f);
				osageCollisionParameter.Bone1.Name = "j_momo_l_wj";
				osageCollisionParameter.Bone1.Position = new Vector3(0.25f, -1f / 64f, -0.003906f);
				osageCollisionParameter.Radius = 0.050781f;
				osageCollisionParameter.Type = 2u;
				collisions.Add(osageCollisionParameter);
				List<OsageCollisionParameter> collisions2 = obj2.Collisions;
				OsageCollisionParameter osageCollisionParameter2 = new OsageCollisionParameter();
				osageCollisionParameter2.Bone0.Name = "j_momo_r_wj";
				osageCollisionParameter2.Bone0.Position = new Vector3(5f / 32f, -1f / 64f, 0.003906f);
				osageCollisionParameter2.Bone1.Name = "j_momo_l_wj";
				osageCollisionParameter2.Bone1.Position = new Vector3(5f / 32f, -1f / 64f, -0.003906f);
				osageCollisionParameter2.Radius = 0.050781f;
				osageCollisionParameter2.Type = 2u;
				collisions2.Add(osageCollisionParameter2);
				List<OsageCollisionParameter> collisions3 = obj2.Collisions;
				OsageCollisionParameter osageCollisionParameter3 = new OsageCollisionParameter();
				osageCollisionParameter3.Bone0.Name = "kl_kosi_etc_wj";
				osageCollisionParameter3.Bone0.Position = new Vector3(0.035156f, -9f / 64f, 1f / 32f);
				osageCollisionParameter3.Bone1.Name = "kl_kosi_etc_wj";
				osageCollisionParameter3.Bone1.Position = new Vector3(0.035156f, -9f / 64f, -1f / 32f);
				osageCollisionParameter3.Radius = 0.105469f;
				osageCollisionParameter3.Type = 2u;
				collisions3.Add(osageCollisionParameter3);
				List<OsageCollisionParameter> collisions4 = obj2.Collisions;
				OsageCollisionParameter osageCollisionParameter4 = new OsageCollisionParameter();
				osageCollisionParameter4.Bone0.Name = "j_momo_r_wj";
				osageCollisionParameter4.Bone0.Position = new Vector3(3f / 32f, 0f, 0f);
				osageCollisionParameter4.Bone1.Name = "j_momo_l_wj";
				osageCollisionParameter4.Bone1.Position = new Vector3(3f / 32f, 0f, 0f);
				osageCollisionParameter4.Radius = 0.089844f;
				osageCollisionParameter4.Type = 2u;
				collisions4.Add(osageCollisionParameter4);
				List<OsageCollisionParameter> collisions5 = obj2.Collisions;
				OsageCollisionParameter osageCollisionParameter5 = new OsageCollisionParameter();
				osageCollisionParameter5.Bone0.Name = "kl_kosi_etc_wj";
				osageCollisionParameter5.Bone0.Position = new Vector3(1f / 32f, -0.1875f, 0f);
				osageCollisionParameter5.Bone1.Name = "kl_kosi_etc_wj";
				osageCollisionParameter5.Bone1.Position = new Vector3(0f, 0f, 0f);
				osageCollisionParameter5.Radius = 3f / 32f;
				osageCollisionParameter5.Type = 2u;
				collisions5.Add(osageCollisionParameter5);
				List<OsageCollisionParameter> collisions6 = obj2.Collisions;
				OsageCollisionParameter osageCollisionParameter6 = new OsageCollisionParameter();
				osageCollisionParameter6.Bone0.Name = "j_momo_r_wj";
				osageCollisionParameter6.Bone0.Position = new Vector3(0.058594f, -1f / 64f, 0.003906f);
				osageCollisionParameter6.Bone1.Name = "j_momo_l_wj";
				osageCollisionParameter6.Bone1.Position = new Vector3(0.058594f, -1f / 64f, -0.003906f);
				osageCollisionParameter6.Radius = 0.089844f;
				osageCollisionParameter6.Type = 2u;
				collisions6.Add(osageCollisionParameter6);
				List<OsageCollisionParameter> collisions7 = obj2.Collisions;
				OsageCollisionParameter osageCollisionParameter7 = new OsageCollisionParameter();
				osageCollisionParameter7.Bone0.Name = "j_momo_r_wj";
				osageCollisionParameter7.Bone0.Position = new Vector3(3f / 32f, -0.011719f, -0.007813f);
				osageCollisionParameter7.Bone1.Name = "j_momo_r_wj";
				osageCollisionParameter7.Bone1.Position = new Vector3(0.25f, 0f, 0f);
				osageCollisionParameter7.Radius = 5f / 64f;
				osageCollisionParameter7.Type = 2u;
				collisions7.Add(osageCollisionParameter7);
				List<OsageCollisionParameter> collisions8 = obj2.Collisions;
				OsageCollisionParameter osageCollisionParameter8 = new OsageCollisionParameter();
				osageCollisionParameter8.Bone0.Name = "j_momo_l_wj";
				osageCollisionParameter8.Bone0.Position = new Vector3(3f / 32f, -0.011719f, 0.007813f);
				osageCollisionParameter8.Bone1.Name = "j_momo_l_wj";
				osageCollisionParameter8.Bone1.Position = new Vector3(0.25f, 0f, 0f);
				osageCollisionParameter8.Radius = 5f / 64f;
				osageCollisionParameter8.Type = 2u;
				collisions8.Add(osageCollisionParameter8);
				obj2.CollisionRadius = 0f;
				obj2.CollisionType = 1u;
				obj2.Force = 0.015f;
				obj2.ForceGain = 0.9f;
				obj2.Friction = 1f;
				obj2.HingeY = 90f;
				obj2.HingeZ = 90f;
				obj2.InitRotationY = 0f;
				obj2.InitRotationZ = 0f;
				obj2.MoveCancel = 0f;
				obj2.Nodes.Add(new OsageNodeParameter
				{
					Radius = 0.03f,
					HingeYMax = 180f,
					HingeYMin = -180f,
					HingeZMax = 180f,
					HingeZMin = -180f,
					InertialCancel = 0f,
					Weight = 1f
				});
				obj2.RotationY = 0f;
				obj2.RotationZ = 0f;
				obj2.Stiffness = 0f;
				obj2.WindAffection = 0.5f;
				dictionary7.Add("j_cloth_skirt_b_000_wj", obj2);
				OsageSkinParameter obj3 = new OsageSkinParameter
				{
					Name = "c_cloth_skirt_f_osg",
					AirResistance = 0.9f,
					Bocs = 
					{
						new OsageBocParameter
						{
							StNode = 0u,
							EdNode = 0u,
							EdRoot = "c_cloth_skirt_l_02_osg"
						},
						new OsageBocParameter
						{
							StNode = 0u,
							EdNode = 0u,
							EdRoot = "c_cloth_skirt_r_02_osg"
						},
						new OsageBocParameter
						{
							StNode = 1u,
							EdNode = 1u,
							EdRoot = "c_cloth_skirt_l_02_osg"
						},
						new OsageBocParameter
						{
							StNode = 1u,
							EdNode = 1u,
							EdRoot = "c_cloth_skirt_r_02_osg"
						}
					}
				};
				List<OsageCollisionParameter> collisions9 = obj3.Collisions;
				OsageCollisionParameter osageCollisionParameter9 = new OsageCollisionParameter();
				osageCollisionParameter9.Bone0.Name = "j_momo_r_wj";
				osageCollisionParameter9.Bone0.Position = new Vector3(0.25f, -1f / 64f, 0.003906f);
				osageCollisionParameter9.Bone1.Name = "j_momo_l_wj";
				osageCollisionParameter9.Bone1.Position = new Vector3(0.25f, -1f / 64f, -0.003906f);
				osageCollisionParameter9.Radius = 0.050781f;
				osageCollisionParameter9.Type = 2u;
				collisions9.Add(osageCollisionParameter9);
				List<OsageCollisionParameter> collisions10 = obj3.Collisions;
				OsageCollisionParameter osageCollisionParameter10 = new OsageCollisionParameter();
				osageCollisionParameter10.Bone0.Name = "j_momo_r_wj";
				osageCollisionParameter10.Bone0.Position = new Vector3(5f / 32f, -1f / 64f, 0.003906f);
				osageCollisionParameter10.Bone1.Name = "j_momo_l_wj";
				osageCollisionParameter10.Bone1.Position = new Vector3(5f / 32f, -1f / 64f, -0.003906f);
				osageCollisionParameter10.Radius = 0.050781f;
				osageCollisionParameter10.Type = 2u;
				collisions10.Add(osageCollisionParameter10);
				List<OsageCollisionParameter> collisions11 = obj3.Collisions;
				OsageCollisionParameter osageCollisionParameter11 = new OsageCollisionParameter();
				osageCollisionParameter11.Bone0.Name = "kl_kosi_etc_wj";
				osageCollisionParameter11.Bone0.Position = new Vector3(0.035156f, -9f / 64f, 1f / 32f);
				osageCollisionParameter11.Bone1.Name = "kl_kosi_etc_wj";
				osageCollisionParameter11.Bone1.Position = new Vector3(0.035156f, -9f / 64f, -1f / 32f);
				osageCollisionParameter11.Radius = 0.105469f;
				osageCollisionParameter11.Type = 2u;
				collisions11.Add(osageCollisionParameter11);
				List<OsageCollisionParameter> collisions12 = obj3.Collisions;
				OsageCollisionParameter osageCollisionParameter12 = new OsageCollisionParameter();
				osageCollisionParameter12.Bone0.Name = "j_momo_r_wj";
				osageCollisionParameter12.Bone0.Position = new Vector3(3f / 32f, 0f, 0f);
				osageCollisionParameter12.Bone1.Name = "j_momo_l_wj";
				osageCollisionParameter12.Bone1.Position = new Vector3(3f / 32f, 0f, 0f);
				osageCollisionParameter12.Radius = 0.089844f;
				osageCollisionParameter12.Type = 2u;
				collisions12.Add(osageCollisionParameter12);
				List<OsageCollisionParameter> collisions13 = obj3.Collisions;
				OsageCollisionParameter osageCollisionParameter13 = new OsageCollisionParameter();
				osageCollisionParameter13.Bone0.Name = "kl_kosi_etc_wj";
				osageCollisionParameter13.Bone0.Position = new Vector3(1f / 32f, -0.1875f, 0f);
				osageCollisionParameter13.Bone1.Name = "kl_kosi_etc_wj";
				osageCollisionParameter13.Bone1.Position = new Vector3(0f, 0f, 0f);
				osageCollisionParameter13.Radius = 3f / 32f;
				osageCollisionParameter13.Type = 2u;
				collisions13.Add(osageCollisionParameter13);
				List<OsageCollisionParameter> collisions14 = obj3.Collisions;
				OsageCollisionParameter osageCollisionParameter14 = new OsageCollisionParameter();
				osageCollisionParameter14.Bone0.Name = "j_momo_r_wj";
				osageCollisionParameter14.Bone0.Position = new Vector3(0.058594f, -1f / 64f, 0.003906f);
				osageCollisionParameter14.Bone1.Name = "j_momo_l_wj";
				osageCollisionParameter14.Bone1.Position = new Vector3(0.058594f, -1f / 64f, -0.003906f);
				osageCollisionParameter14.Radius = 0.089844f;
				osageCollisionParameter14.Type = 2u;
				collisions14.Add(osageCollisionParameter14);
				List<OsageCollisionParameter> collisions15 = obj3.Collisions;
				OsageCollisionParameter osageCollisionParameter15 = new OsageCollisionParameter();
				osageCollisionParameter15.Bone0.Name = "j_momo_r_wj";
				osageCollisionParameter15.Bone0.Position = new Vector3(3f / 32f, -0.011719f, -0.007813f);
				osageCollisionParameter15.Bone1.Name = "j_momo_r_wj";
				osageCollisionParameter15.Bone1.Position = new Vector3(0.25f, 0f, 0f);
				osageCollisionParameter15.Radius = 5f / 64f;
				osageCollisionParameter15.Type = 2u;
				collisions15.Add(osageCollisionParameter15);
				List<OsageCollisionParameter> collisions16 = obj3.Collisions;
				OsageCollisionParameter osageCollisionParameter16 = new OsageCollisionParameter();
				osageCollisionParameter16.Bone0.Name = "j_momo_l_wj";
				osageCollisionParameter16.Bone0.Position = new Vector3(3f / 32f, -0.011719f, 0.007813f);
				osageCollisionParameter16.Bone1.Name = "j_momo_l_wj";
				osageCollisionParameter16.Bone1.Position = new Vector3(0.25f, 0f, 0f);
				osageCollisionParameter16.Radius = 5f / 64f;
				osageCollisionParameter16.Type = 2u;
				collisions16.Add(osageCollisionParameter16);
				obj3.CollisionRadius = 0f;
				obj3.CollisionType = 1u;
				obj3.Force = 0.015f;
				obj3.ForceGain = 0.9f;
				obj3.Friction = 1f;
				obj3.HingeY = 90f;
				obj3.HingeZ = 90f;
				obj3.InitRotationY = 0f;
				obj3.InitRotationZ = 0f;
				obj3.MoveCancel = 0f;
				obj3.Nodes.Add(new OsageNodeParameter
				{
					Radius = 0.03f,
					HingeYMax = 180f,
					HingeYMin = -180f,
					HingeZMax = 180f,
					HingeZMin = -180f,
					InertialCancel = 0f,
					Weight = 1f
				});
				obj3.RotationY = 0f;
				obj3.RotationZ = 0f;
				obj3.Stiffness = 0f;
				obj3.WindAffection = 0.5f;
				dictionary7.Add("j_cloth_skirt_f_000_wj", obj3);
				OsageSkinParameter obj4 = new OsageSkinParameter
				{
					Name = "c_cloth_skirt_l_02_osg",
					AirResistance = 0.9f,
					Bocs = 
					{
						new OsageBocParameter
						{
							StNode = 0u,
							EdNode = 0u,
							EdRoot = "c_cloth_skirt_l_04_osg"
						},
						new OsageBocParameter
						{
							StNode = 1u,
							EdNode = 1u,
							EdRoot = "c_cloth_skirt_l_04_osg"
						}
					}
				};
				List<OsageCollisionParameter> collisions17 = obj4.Collisions;
				OsageCollisionParameter osageCollisionParameter17 = new OsageCollisionParameter();
				osageCollisionParameter17.Bone0.Name = "j_momo_r_wj";
				osageCollisionParameter17.Bone0.Position = new Vector3(0.25f, -1f / 64f, 0.003906f);
				osageCollisionParameter17.Bone1.Name = "j_momo_l_wj";
				osageCollisionParameter17.Bone1.Position = new Vector3(0.25f, -1f / 64f, -0.003906f);
				osageCollisionParameter17.Radius = 0.050781f;
				osageCollisionParameter17.Type = 2u;
				collisions17.Add(osageCollisionParameter17);
				List<OsageCollisionParameter> collisions18 = obj4.Collisions;
				OsageCollisionParameter osageCollisionParameter18 = new OsageCollisionParameter();
				osageCollisionParameter18.Bone0.Name = "j_momo_r_wj";
				osageCollisionParameter18.Bone0.Position = new Vector3(5f / 32f, -1f / 64f, 0.003906f);
				osageCollisionParameter18.Bone1.Name = "j_momo_l_wj";
				osageCollisionParameter18.Bone1.Position = new Vector3(5f / 32f, -1f / 64f, -0.003906f);
				osageCollisionParameter18.Radius = 0.050781f;
				osageCollisionParameter18.Type = 2u;
				collisions18.Add(osageCollisionParameter18);
				List<OsageCollisionParameter> collisions19 = obj4.Collisions;
				OsageCollisionParameter osageCollisionParameter19 = new OsageCollisionParameter();
				osageCollisionParameter19.Bone0.Name = "kl_kosi_etc_wj";
				osageCollisionParameter19.Bone0.Position = new Vector3(0.035156f, -9f / 64f, 1f / 32f);
				osageCollisionParameter19.Bone1.Name = "kl_kosi_etc_wj";
				osageCollisionParameter19.Bone1.Position = new Vector3(0.035156f, -9f / 64f, -1f / 32f);
				osageCollisionParameter19.Radius = 0.105469f;
				osageCollisionParameter19.Type = 2u;
				collisions19.Add(osageCollisionParameter19);
				List<OsageCollisionParameter> collisions20 = obj4.Collisions;
				OsageCollisionParameter osageCollisionParameter20 = new OsageCollisionParameter();
				osageCollisionParameter20.Bone0.Name = "j_momo_r_wj";
				osageCollisionParameter20.Bone0.Position = new Vector3(3f / 32f, 0f, 0f);
				osageCollisionParameter20.Bone1.Name = "j_momo_l_wj";
				osageCollisionParameter20.Bone1.Position = new Vector3(3f / 32f, 0f, 0f);
				osageCollisionParameter20.Radius = 0.089844f;
				osageCollisionParameter20.Type = 2u;
				collisions20.Add(osageCollisionParameter20);
				List<OsageCollisionParameter> collisions21 = obj4.Collisions;
				OsageCollisionParameter osageCollisionParameter21 = new OsageCollisionParameter();
				osageCollisionParameter21.Bone0.Name = "kl_kosi_etc_wj";
				osageCollisionParameter21.Bone0.Position = new Vector3(1f / 32f, -0.1875f, 0f);
				osageCollisionParameter21.Bone1.Name = "kl_kosi_etc_wj";
				osageCollisionParameter21.Bone1.Position = new Vector3(0f, 0f, 0f);
				osageCollisionParameter21.Radius = 3f / 32f;
				osageCollisionParameter21.Type = 2u;
				collisions21.Add(osageCollisionParameter21);
				List<OsageCollisionParameter> collisions22 = obj4.Collisions;
				OsageCollisionParameter osageCollisionParameter22 = new OsageCollisionParameter();
				osageCollisionParameter22.Bone0.Name = "j_momo_r_wj";
				osageCollisionParameter22.Bone0.Position = new Vector3(0.058594f, -1f / 64f, 0.003906f);
				osageCollisionParameter22.Bone1.Name = "j_momo_l_wj";
				osageCollisionParameter22.Bone1.Position = new Vector3(0.058594f, -1f / 64f, -0.003906f);
				osageCollisionParameter22.Radius = 0.089844f;
				osageCollisionParameter22.Type = 2u;
				collisions22.Add(osageCollisionParameter22);
				List<OsageCollisionParameter> collisions23 = obj4.Collisions;
				OsageCollisionParameter osageCollisionParameter23 = new OsageCollisionParameter();
				osageCollisionParameter23.Bone0.Name = "j_momo_r_wj";
				osageCollisionParameter23.Bone0.Position = new Vector3(3f / 32f, -0.011719f, -0.007813f);
				osageCollisionParameter23.Bone1.Name = "j_momo_r_wj";
				osageCollisionParameter23.Bone1.Position = new Vector3(0.25f, 0f, 0f);
				osageCollisionParameter23.Radius = 5f / 64f;
				osageCollisionParameter23.Type = 2u;
				collisions23.Add(osageCollisionParameter23);
				List<OsageCollisionParameter> collisions24 = obj4.Collisions;
				OsageCollisionParameter osageCollisionParameter24 = new OsageCollisionParameter();
				osageCollisionParameter24.Bone0.Name = "j_momo_l_wj";
				osageCollisionParameter24.Bone0.Position = new Vector3(3f / 32f, -0.011719f, 0.007813f);
				osageCollisionParameter24.Bone1.Name = "j_momo_l_wj";
				osageCollisionParameter24.Bone1.Position = new Vector3(0.25f, 0f, 0f);
				osageCollisionParameter24.Radius = 5f / 64f;
				osageCollisionParameter24.Type = 2u;
				collisions24.Add(osageCollisionParameter24);
				obj4.CollisionRadius = 0f;
				obj4.CollisionType = 1u;
				obj4.Force = 0.015f;
				obj4.ForceGain = 0.9f;
				obj4.Friction = 1f;
				obj4.HingeY = 90f;
				obj4.HingeZ = 90f;
				obj4.InitRotationY = 0f;
				obj4.InitRotationZ = 0f;
				obj4.MoveCancel = 0f;
				obj4.Nodes.Add(new OsageNodeParameter
				{
					Radius = 0.03f,
					HingeYMax = 180f,
					HingeYMin = -180f,
					HingeZMax = 180f,
					HingeZMin = -180f,
					InertialCancel = 0f,
					Weight = 1f
				});
				obj4.RotationY = 0f;
				obj4.RotationZ = 0f;
				obj4.Stiffness = 0f;
				obj4.WindAffection = 0.5f;
				dictionary7.Add("j_cloth_skirt_l_02_000_wj", obj4);
				OsageSkinParameter obj5 = new OsageSkinParameter
				{
					Name = "c_cloth_skirt_l_04_osg",
					AirResistance = 0.9f,
					Bocs = 
					{
						new OsageBocParameter
						{
							StNode = 0u,
							EdNode = 0u,
							EdRoot = "c_cloth_skirt_l_06_osg"
						},
						new OsageBocParameter
						{
							StNode = 1u,
							EdNode = 1u,
							EdRoot = "c_cloth_skirt_l_06_osg"
						}
					}
				};
				List<OsageCollisionParameter> collisions25 = obj5.Collisions;
				OsageCollisionParameter osageCollisionParameter25 = new OsageCollisionParameter();
				osageCollisionParameter25.Bone0.Name = "j_momo_r_wj";
				osageCollisionParameter25.Bone0.Position = new Vector3(0.25f, -1f / 64f, 0.003906f);
				osageCollisionParameter25.Bone1.Name = "j_momo_l_wj";
				osageCollisionParameter25.Bone1.Position = new Vector3(0.25f, -1f / 64f, -0.003906f);
				osageCollisionParameter25.Radius = 0.050781f;
				osageCollisionParameter25.Type = 2u;
				collisions25.Add(osageCollisionParameter25);
				List<OsageCollisionParameter> collisions26 = obj5.Collisions;
				OsageCollisionParameter osageCollisionParameter26 = new OsageCollisionParameter();
				osageCollisionParameter26.Bone0.Name = "j_momo_r_wj";
				osageCollisionParameter26.Bone0.Position = new Vector3(5f / 32f, -1f / 64f, 0.003906f);
				osageCollisionParameter26.Bone1.Name = "j_momo_l_wj";
				osageCollisionParameter26.Bone1.Position = new Vector3(5f / 32f, -1f / 64f, -0.003906f);
				osageCollisionParameter26.Radius = 0.050781f;
				osageCollisionParameter26.Type = 2u;
				collisions26.Add(osageCollisionParameter26);
				List<OsageCollisionParameter> collisions27 = obj5.Collisions;
				OsageCollisionParameter osageCollisionParameter27 = new OsageCollisionParameter();
				osageCollisionParameter27.Bone0.Name = "kl_kosi_etc_wj";
				osageCollisionParameter27.Bone0.Position = new Vector3(0.035156f, -9f / 64f, 1f / 32f);
				osageCollisionParameter27.Bone1.Name = "kl_kosi_etc_wj";
				osageCollisionParameter27.Bone1.Position = new Vector3(0.035156f, -9f / 64f, -1f / 32f);
				osageCollisionParameter27.Radius = 0.105469f;
				osageCollisionParameter27.Type = 2u;
				collisions27.Add(osageCollisionParameter27);
				List<OsageCollisionParameter> collisions28 = obj5.Collisions;
				OsageCollisionParameter osageCollisionParameter28 = new OsageCollisionParameter();
				osageCollisionParameter28.Bone0.Name = "j_momo_r_wj";
				osageCollisionParameter28.Bone0.Position = new Vector3(3f / 32f, 0f, 0f);
				osageCollisionParameter28.Bone1.Name = "j_momo_l_wj";
				osageCollisionParameter28.Bone1.Position = new Vector3(3f / 32f, 0f, 0f);
				osageCollisionParameter28.Radius = 0.089844f;
				osageCollisionParameter28.Type = 2u;
				collisions28.Add(osageCollisionParameter28);
				List<OsageCollisionParameter> collisions29 = obj5.Collisions;
				OsageCollisionParameter osageCollisionParameter29 = new OsageCollisionParameter();
				osageCollisionParameter29.Bone0.Name = "kl_kosi_etc_wj";
				osageCollisionParameter29.Bone0.Position = new Vector3(1f / 32f, -0.1875f, 0f);
				osageCollisionParameter29.Bone1.Name = "kl_kosi_etc_wj";
				osageCollisionParameter29.Bone1.Position = new Vector3(0f, 0f, 0f);
				osageCollisionParameter29.Radius = 3f / 32f;
				osageCollisionParameter29.Type = 2u;
				collisions29.Add(osageCollisionParameter29);
				List<OsageCollisionParameter> collisions30 = obj5.Collisions;
				OsageCollisionParameter osageCollisionParameter30 = new OsageCollisionParameter();
				osageCollisionParameter30.Bone0.Name = "j_momo_r_wj";
				osageCollisionParameter30.Bone0.Position = new Vector3(0.058594f, -1f / 64f, 0.003906f);
				osageCollisionParameter30.Bone1.Name = "j_momo_l_wj";
				osageCollisionParameter30.Bone1.Position = new Vector3(0.058594f, -1f / 64f, -0.003906f);
				osageCollisionParameter30.Radius = 0.089844f;
				osageCollisionParameter30.Type = 2u;
				collisions30.Add(osageCollisionParameter30);
				List<OsageCollisionParameter> collisions31 = obj5.Collisions;
				OsageCollisionParameter osageCollisionParameter31 = new OsageCollisionParameter();
				osageCollisionParameter31.Bone0.Name = "j_momo_r_wj";
				osageCollisionParameter31.Bone0.Position = new Vector3(3f / 32f, -0.011719f, -0.007813f);
				osageCollisionParameter31.Bone1.Name = "j_momo_r_wj";
				osageCollisionParameter31.Bone1.Position = new Vector3(0.25f, 0f, 0f);
				osageCollisionParameter31.Radius = 5f / 64f;
				osageCollisionParameter31.Type = 2u;
				collisions31.Add(osageCollisionParameter31);
				List<OsageCollisionParameter> collisions32 = obj5.Collisions;
				OsageCollisionParameter osageCollisionParameter32 = new OsageCollisionParameter();
				osageCollisionParameter32.Bone0.Name = "j_momo_l_wj";
				osageCollisionParameter32.Bone0.Position = new Vector3(3f / 32f, -0.011719f, 0.007813f);
				osageCollisionParameter32.Bone1.Name = "j_momo_l_wj";
				osageCollisionParameter32.Bone1.Position = new Vector3(0.25f, 0f, 0f);
				osageCollisionParameter32.Radius = 5f / 64f;
				osageCollisionParameter32.Type = 2u;
				collisions32.Add(osageCollisionParameter32);
				obj5.CollisionRadius = 0f;
				obj5.CollisionType = 1u;
				obj5.Force = 0.015f;
				obj5.ForceGain = 0.9f;
				obj5.Friction = 1f;
				obj5.HingeY = 90f;
				obj5.HingeZ = 90f;
				obj5.InitRotationY = 0f;
				obj5.InitRotationZ = 0f;
				obj5.MoveCancel = 0f;
				obj5.Nodes.Add(new OsageNodeParameter
				{
					Radius = 0.03f,
					HingeYMax = 180f,
					HingeYMin = -180f,
					HingeZMax = 180f,
					HingeZMin = -180f,
					InertialCancel = 0f,
					Weight = 1f
				});
				obj5.RotationY = 0f;
				obj5.RotationZ = 0f;
				obj5.Stiffness = 0f;
				obj5.WindAffection = 0.5f;
				dictionary7.Add("j_cloth_skirt_l_04_000_wj", obj5);
				OsageSkinParameter obj6 = new OsageSkinParameter
				{
					Name = "c_cloth_skirt_l_06_osg",
					AirResistance = 0.9f,
					Bocs = 
					{
						new OsageBocParameter
						{
							StNode = 0u,
							EdNode = 0u,
							EdRoot = "c_cloth_skirt_b_osg"
						},
						new OsageBocParameter
						{
							StNode = 1u,
							EdNode = 1u,
							EdRoot = "c_cloth_skirt_b_osg"
						}
					}
				};
				List<OsageCollisionParameter> collisions33 = obj6.Collisions;
				OsageCollisionParameter osageCollisionParameter33 = new OsageCollisionParameter();
				osageCollisionParameter33.Bone0.Name = "j_momo_r_wj";
				osageCollisionParameter33.Bone0.Position = new Vector3(0.25f, -1f / 64f, 0.003906f);
				osageCollisionParameter33.Bone1.Name = "j_momo_l_wj";
				osageCollisionParameter33.Bone1.Position = new Vector3(0.25f, -1f / 64f, -0.003906f);
				osageCollisionParameter33.Radius = 0.050781f;
				osageCollisionParameter33.Type = 2u;
				collisions33.Add(osageCollisionParameter33);
				List<OsageCollisionParameter> collisions34 = obj6.Collisions;
				OsageCollisionParameter osageCollisionParameter34 = new OsageCollisionParameter();
				osageCollisionParameter34.Bone0.Name = "j_momo_r_wj";
				osageCollisionParameter34.Bone0.Position = new Vector3(5f / 32f, -1f / 64f, 0.003906f);
				osageCollisionParameter34.Bone1.Name = "j_momo_l_wj";
				osageCollisionParameter34.Bone1.Position = new Vector3(5f / 32f, -1f / 64f, -0.003906f);
				osageCollisionParameter34.Radius = 0.050781f;
				osageCollisionParameter34.Type = 2u;
				collisions34.Add(osageCollisionParameter34);
				List<OsageCollisionParameter> collisions35 = obj6.Collisions;
				OsageCollisionParameter osageCollisionParameter35 = new OsageCollisionParameter();
				osageCollisionParameter35.Bone0.Name = "kl_kosi_etc_wj";
				osageCollisionParameter35.Bone0.Position = new Vector3(0.035156f, -9f / 64f, 1f / 32f);
				osageCollisionParameter35.Bone1.Name = "kl_kosi_etc_wj";
				osageCollisionParameter35.Bone1.Position = new Vector3(0.035156f, -9f / 64f, -1f / 32f);
				osageCollisionParameter35.Radius = 0.105469f;
				osageCollisionParameter35.Type = 2u;
				collisions35.Add(osageCollisionParameter35);
				List<OsageCollisionParameter> collisions36 = obj6.Collisions;
				OsageCollisionParameter osageCollisionParameter36 = new OsageCollisionParameter();
				osageCollisionParameter36.Bone0.Name = "j_momo_r_wj";
				osageCollisionParameter36.Bone0.Position = new Vector3(3f / 32f, 0f, 0f);
				osageCollisionParameter36.Bone1.Name = "j_momo_l_wj";
				osageCollisionParameter36.Bone1.Position = new Vector3(3f / 32f, 0f, 0f);
				osageCollisionParameter36.Radius = 0.089844f;
				osageCollisionParameter36.Type = 2u;
				collisions36.Add(osageCollisionParameter36);
				List<OsageCollisionParameter> collisions37 = obj6.Collisions;
				OsageCollisionParameter osageCollisionParameter37 = new OsageCollisionParameter();
				osageCollisionParameter37.Bone0.Name = "kl_kosi_etc_wj";
				osageCollisionParameter37.Bone0.Position = new Vector3(1f / 32f, -0.1875f, 0f);
				osageCollisionParameter37.Bone1.Name = "kl_kosi_etc_wj";
				osageCollisionParameter37.Bone1.Position = new Vector3(0f, 0f, 0f);
				osageCollisionParameter37.Radius = 3f / 32f;
				osageCollisionParameter37.Type = 2u;
				collisions37.Add(osageCollisionParameter37);
				List<OsageCollisionParameter> collisions38 = obj6.Collisions;
				OsageCollisionParameter osageCollisionParameter38 = new OsageCollisionParameter();
				osageCollisionParameter38.Bone0.Name = "j_momo_r_wj";
				osageCollisionParameter38.Bone0.Position = new Vector3(0.058594f, -1f / 64f, 0.003906f);
				osageCollisionParameter38.Bone1.Name = "j_momo_l_wj";
				osageCollisionParameter38.Bone1.Position = new Vector3(0.058594f, -1f / 64f, -0.003906f);
				osageCollisionParameter38.Radius = 0.089844f;
				osageCollisionParameter38.Type = 2u;
				collisions38.Add(osageCollisionParameter38);
				List<OsageCollisionParameter> collisions39 = obj6.Collisions;
				OsageCollisionParameter osageCollisionParameter39 = new OsageCollisionParameter();
				osageCollisionParameter39.Bone0.Name = "j_momo_r_wj";
				osageCollisionParameter39.Bone0.Position = new Vector3(3f / 32f, -0.011719f, -0.007813f);
				osageCollisionParameter39.Bone1.Name = "j_momo_r_wj";
				osageCollisionParameter39.Bone1.Position = new Vector3(0.25f, 0f, 0f);
				osageCollisionParameter39.Radius = 5f / 64f;
				osageCollisionParameter39.Type = 2u;
				collisions39.Add(osageCollisionParameter39);
				List<OsageCollisionParameter> collisions40 = obj6.Collisions;
				OsageCollisionParameter osageCollisionParameter40 = new OsageCollisionParameter();
				osageCollisionParameter40.Bone0.Name = "j_momo_l_wj";
				osageCollisionParameter40.Bone0.Position = new Vector3(3f / 32f, -0.011719f, 0.007813f);
				osageCollisionParameter40.Bone1.Name = "j_momo_l_wj";
				osageCollisionParameter40.Bone1.Position = new Vector3(0.25f, 0f, 0f);
				osageCollisionParameter40.Radius = 5f / 64f;
				osageCollisionParameter40.Type = 2u;
				collisions40.Add(osageCollisionParameter40);
				obj6.CollisionRadius = 0f;
				obj6.CollisionType = 1u;
				obj6.Force = 0.015f;
				obj6.ForceGain = 0.9f;
				obj6.Friction = 1f;
				obj6.HingeY = 90f;
				obj6.HingeZ = 90f;
				obj6.InitRotationY = 0f;
				obj6.InitRotationZ = 0f;
				obj6.MoveCancel = 0f;
				obj6.Nodes.Add(new OsageNodeParameter
				{
					Radius = 0.03f,
					HingeYMax = 180f,
					HingeYMin = -180f,
					HingeZMax = 180f,
					HingeZMin = -180f,
					InertialCancel = 0f,
					Weight = 1f
				});
				obj6.RotationY = 0f;
				obj6.RotationZ = 0f;
				obj6.Stiffness = 0f;
				obj6.WindAffection = 0.5f;
				dictionary7.Add("j_cloth_skirt_l_06_000_wj", obj6);
				OsageSkinParameter obj7 = new OsageSkinParameter
				{
					Name = "c_cloth_skirt_r_02_osg",
					AirResistance = 0.9f,
					Bocs = 
					{
						new OsageBocParameter
						{
							StNode = 0u,
							EdNode = 0u,
							EdRoot = "c_cloth_skirt_r_04_osg"
						},
						new OsageBocParameter
						{
							StNode = 1u,
							EdNode = 1u,
							EdRoot = "c_cloth_skirt_r_04_osg"
						}
					}
				};
				List<OsageCollisionParameter> collisions41 = obj7.Collisions;
				OsageCollisionParameter osageCollisionParameter41 = new OsageCollisionParameter();
				osageCollisionParameter41.Bone0.Name = "j_momo_r_wj";
				osageCollisionParameter41.Bone0.Position = new Vector3(0.25f, -1f / 64f, 0.003906f);
				osageCollisionParameter41.Bone1.Name = "j_momo_l_wj";
				osageCollisionParameter41.Bone1.Position = new Vector3(0.25f, -1f / 64f, -0.003906f);
				osageCollisionParameter41.Radius = 0.050781f;
				osageCollisionParameter41.Type = 2u;
				collisions41.Add(osageCollisionParameter41);
				List<OsageCollisionParameter> collisions42 = obj7.Collisions;
				OsageCollisionParameter osageCollisionParameter42 = new OsageCollisionParameter();
				osageCollisionParameter42.Bone0.Name = "j_momo_r_wj";
				osageCollisionParameter42.Bone0.Position = new Vector3(5f / 32f, -1f / 64f, 0.003906f);
				osageCollisionParameter42.Bone1.Name = "j_momo_l_wj";
				osageCollisionParameter42.Bone1.Position = new Vector3(5f / 32f, -1f / 64f, -0.003906f);
				osageCollisionParameter42.Radius = 0.050781f;
				osageCollisionParameter42.Type = 2u;
				collisions42.Add(osageCollisionParameter42);
				List<OsageCollisionParameter> collisions43 = obj7.Collisions;
				OsageCollisionParameter osageCollisionParameter43 = new OsageCollisionParameter();
				osageCollisionParameter43.Bone0.Name = "kl_kosi_etc_wj";
				osageCollisionParameter43.Bone0.Position = new Vector3(0.035156f, -9f / 64f, 1f / 32f);
				osageCollisionParameter43.Bone1.Name = "kl_kosi_etc_wj";
				osageCollisionParameter43.Bone1.Position = new Vector3(0.035156f, -9f / 64f, -1f / 32f);
				osageCollisionParameter43.Radius = 0.105469f;
				osageCollisionParameter43.Type = 2u;
				collisions43.Add(osageCollisionParameter43);
				List<OsageCollisionParameter> collisions44 = obj7.Collisions;
				OsageCollisionParameter osageCollisionParameter44 = new OsageCollisionParameter();
				osageCollisionParameter44.Bone0.Name = "j_momo_r_wj";
				osageCollisionParameter44.Bone0.Position = new Vector3(3f / 32f, 0f, 0f);
				osageCollisionParameter44.Bone1.Name = "j_momo_l_wj";
				osageCollisionParameter44.Bone1.Position = new Vector3(3f / 32f, 0f, 0f);
				osageCollisionParameter44.Radius = 0.089844f;
				osageCollisionParameter44.Type = 2u;
				collisions44.Add(osageCollisionParameter44);
				List<OsageCollisionParameter> collisions45 = obj7.Collisions;
				OsageCollisionParameter osageCollisionParameter45 = new OsageCollisionParameter();
				osageCollisionParameter45.Bone0.Name = "kl_kosi_etc_wj";
				osageCollisionParameter45.Bone0.Position = new Vector3(1f / 32f, -0.1875f, 0f);
				osageCollisionParameter45.Bone1.Name = "kl_kosi_etc_wj";
				osageCollisionParameter45.Bone1.Position = new Vector3(0f, 0f, 0f);
				osageCollisionParameter45.Radius = 3f / 32f;
				osageCollisionParameter45.Type = 2u;
				collisions45.Add(osageCollisionParameter45);
				List<OsageCollisionParameter> collisions46 = obj7.Collisions;
				OsageCollisionParameter osageCollisionParameter46 = new OsageCollisionParameter();
				osageCollisionParameter46.Bone0.Name = "j_momo_r_wj";
				osageCollisionParameter46.Bone0.Position = new Vector3(0.058594f, -1f / 64f, 0.003906f);
				osageCollisionParameter46.Bone1.Name = "j_momo_l_wj";
				osageCollisionParameter46.Bone1.Position = new Vector3(0.058594f, -1f / 64f, -0.003906f);
				osageCollisionParameter46.Radius = 0.089844f;
				osageCollisionParameter46.Type = 2u;
				collisions46.Add(osageCollisionParameter46);
				List<OsageCollisionParameter> collisions47 = obj7.Collisions;
				OsageCollisionParameter osageCollisionParameter47 = new OsageCollisionParameter();
				osageCollisionParameter47.Bone0.Name = "j_momo_r_wj";
				osageCollisionParameter47.Bone0.Position = new Vector3(3f / 32f, -0.011719f, -0.007813f);
				osageCollisionParameter47.Bone1.Name = "j_momo_r_wj";
				osageCollisionParameter47.Bone1.Position = new Vector3(0.25f, 0f, 0f);
				osageCollisionParameter47.Radius = 5f / 64f;
				osageCollisionParameter47.Type = 2u;
				collisions47.Add(osageCollisionParameter47);
				List<OsageCollisionParameter> collisions48 = obj7.Collisions;
				OsageCollisionParameter osageCollisionParameter48 = new OsageCollisionParameter();
				osageCollisionParameter48.Bone0.Name = "j_momo_l_wj";
				osageCollisionParameter48.Bone0.Position = new Vector3(3f / 32f, -0.011719f, 0.007813f);
				osageCollisionParameter48.Bone1.Name = "j_momo_l_wj";
				osageCollisionParameter48.Bone1.Position = new Vector3(0.25f, 0f, 0f);
				osageCollisionParameter48.Radius = 5f / 64f;
				osageCollisionParameter48.Type = 2u;
				collisions48.Add(osageCollisionParameter48);
				obj7.CollisionRadius = 0f;
				obj7.CollisionType = 1u;
				obj7.Force = 0.015f;
				obj7.ForceGain = 0.9f;
				obj7.Friction = 1f;
				obj7.HingeY = 90f;
				obj7.HingeZ = 90f;
				obj7.InitRotationY = 0f;
				obj7.InitRotationZ = 0f;
				obj7.MoveCancel = 0f;
				obj7.Nodes.Add(new OsageNodeParameter
				{
					Radius = 0.03f,
					HingeYMax = 180f,
					HingeYMin = -180f,
					HingeZMax = 180f,
					HingeZMin = -180f,
					InertialCancel = 0f,
					Weight = 1f
				});
				obj7.RotationY = 0f;
				obj7.RotationZ = 0f;
				obj7.Stiffness = 0f;
				obj7.WindAffection = 0.5f;
				dictionary7.Add("j_cloth_skirt_r_02_000_wj", obj7);
				OsageSkinParameter obj8 = new OsageSkinParameter
				{
					Name = "c_cloth_skirt_r_04_osg",
					AirResistance = 0.9f,
					Bocs = 
					{
						new OsageBocParameter
						{
							StNode = 0u,
							EdNode = 0u,
							EdRoot = "c_cloth_skirt_l_06_osg"
						},
						new OsageBocParameter
						{
							StNode = 1u,
							EdNode = 1u,
							EdRoot = "c_cloth_skirt_r_06_osg"
						}
					}
				};
				List<OsageCollisionParameter> collisions49 = obj8.Collisions;
				OsageCollisionParameter osageCollisionParameter49 = new OsageCollisionParameter();
				osageCollisionParameter49.Bone0.Name = "j_momo_r_wj";
				osageCollisionParameter49.Bone0.Position = new Vector3(0.25f, -1f / 64f, 0.003906f);
				osageCollisionParameter49.Bone1.Name = "j_momo_l_wj";
				osageCollisionParameter49.Bone1.Position = new Vector3(0.25f, -1f / 64f, -0.003906f);
				osageCollisionParameter49.Radius = 0.050781f;
				osageCollisionParameter49.Type = 2u;
				collisions49.Add(osageCollisionParameter49);
				List<OsageCollisionParameter> collisions50 = obj8.Collisions;
				OsageCollisionParameter osageCollisionParameter50 = new OsageCollisionParameter();
				osageCollisionParameter50.Bone0.Name = "j_momo_r_wj";
				osageCollisionParameter50.Bone0.Position = new Vector3(5f / 32f, -1f / 64f, 0.003906f);
				osageCollisionParameter50.Bone1.Name = "j_momo_l_wj";
				osageCollisionParameter50.Bone1.Position = new Vector3(5f / 32f, -1f / 64f, -0.003906f);
				osageCollisionParameter50.Radius = 0.050781f;
				osageCollisionParameter50.Type = 2u;
				collisions50.Add(osageCollisionParameter50);
				List<OsageCollisionParameter> collisions51 = obj8.Collisions;
				OsageCollisionParameter osageCollisionParameter51 = new OsageCollisionParameter();
				osageCollisionParameter51.Bone0.Name = "kl_kosi_etc_wj";
				osageCollisionParameter51.Bone0.Position = new Vector3(0.035156f, -9f / 64f, 1f / 32f);
				osageCollisionParameter51.Bone1.Name = "kl_kosi_etc_wj";
				osageCollisionParameter51.Bone1.Position = new Vector3(0.035156f, -9f / 64f, -1f / 32f);
				osageCollisionParameter51.Radius = 0.105469f;
				osageCollisionParameter51.Type = 2u;
				collisions51.Add(osageCollisionParameter51);
				List<OsageCollisionParameter> collisions52 = obj8.Collisions;
				OsageCollisionParameter osageCollisionParameter52 = new OsageCollisionParameter();
				osageCollisionParameter52.Bone0.Name = "j_momo_r_wj";
				osageCollisionParameter52.Bone0.Position = new Vector3(3f / 32f, 0f, 0f);
				osageCollisionParameter52.Bone1.Name = "j_momo_l_wj";
				osageCollisionParameter52.Bone1.Position = new Vector3(3f / 32f, 0f, 0f);
				osageCollisionParameter52.Radius = 0.089844f;
				osageCollisionParameter52.Type = 2u;
				collisions52.Add(osageCollisionParameter52);
				List<OsageCollisionParameter> collisions53 = obj8.Collisions;
				OsageCollisionParameter osageCollisionParameter53 = new OsageCollisionParameter();
				osageCollisionParameter53.Bone0.Name = "kl_kosi_etc_wj";
				osageCollisionParameter53.Bone0.Position = new Vector3(1f / 32f, -0.1875f, 0f);
				osageCollisionParameter53.Bone1.Name = "kl_kosi_etc_wj";
				osageCollisionParameter53.Bone1.Position = new Vector3(0f, 0f, 0f);
				osageCollisionParameter53.Radius = 3f / 32f;
				osageCollisionParameter53.Type = 2u;
				collisions53.Add(osageCollisionParameter53);
				List<OsageCollisionParameter> collisions54 = obj8.Collisions;
				OsageCollisionParameter osageCollisionParameter54 = new OsageCollisionParameter();
				osageCollisionParameter54.Bone0.Name = "j_momo_r_wj";
				osageCollisionParameter54.Bone0.Position = new Vector3(0.058594f, -1f / 64f, 0.003906f);
				osageCollisionParameter54.Bone1.Name = "j_momo_l_wj";
				osageCollisionParameter54.Bone1.Position = new Vector3(0.058594f, -1f / 64f, -0.003906f);
				osageCollisionParameter54.Radius = 0.089844f;
				osageCollisionParameter54.Type = 2u;
				collisions54.Add(osageCollisionParameter54);
				List<OsageCollisionParameter> collisions55 = obj8.Collisions;
				OsageCollisionParameter osageCollisionParameter55 = new OsageCollisionParameter();
				osageCollisionParameter55.Bone0.Name = "j_momo_r_wj";
				osageCollisionParameter55.Bone0.Position = new Vector3(3f / 32f, -0.011719f, -0.007813f);
				osageCollisionParameter55.Bone1.Name = "j_momo_r_wj";
				osageCollisionParameter55.Bone1.Position = new Vector3(0.25f, 0f, 0f);
				osageCollisionParameter55.Radius = 5f / 64f;
				osageCollisionParameter55.Type = 2u;
				collisions55.Add(osageCollisionParameter55);
				List<OsageCollisionParameter> collisions56 = obj8.Collisions;
				OsageCollisionParameter osageCollisionParameter56 = new OsageCollisionParameter();
				osageCollisionParameter56.Bone0.Name = "j_momo_l_wj";
				osageCollisionParameter56.Bone0.Position = new Vector3(3f / 32f, -0.011719f, 0.007813f);
				osageCollisionParameter56.Bone1.Name = "j_momo_l_wj";
				osageCollisionParameter56.Bone1.Position = new Vector3(0.25f, 0f, 0f);
				osageCollisionParameter56.Radius = 5f / 64f;
				osageCollisionParameter56.Type = 2u;
				collisions56.Add(osageCollisionParameter56);
				obj8.CollisionRadius = 0f;
				obj8.CollisionType = 1u;
				obj8.Force = 0.015f;
				obj8.ForceGain = 0.9f;
				obj8.Friction = 1f;
				obj8.HingeY = 90f;
				obj8.HingeZ = 90f;
				obj8.InitRotationY = 0f;
				obj8.InitRotationZ = 0f;
				obj8.MoveCancel = 0f;
				obj8.Nodes.Add(new OsageNodeParameter
				{
					Radius = 0.03f,
					HingeYMax = 180f,
					HingeYMin = -180f,
					HingeZMax = 180f,
					HingeZMin = -180f,
					InertialCancel = 0f,
					Weight = 1f
				});
				obj8.RotationY = 0f;
				obj8.RotationZ = 0f;
				obj8.Stiffness = 0f;
				obj8.WindAffection = 0.5f;
				dictionary7.Add("j_cloth_skirt_r_04_000_wj", obj8);
				OsageSkinParameter obj9 = new OsageSkinParameter
				{
					Name = "c_cloth_skirt_r_06_osg",
					AirResistance = 0.9f,
					Bocs = 
					{
						new OsageBocParameter
						{
							StNode = 0u,
							EdNode = 0u,
							EdRoot = "c_cloth_skirt_b_osg"
						},
						new OsageBocParameter
						{
							StNode = 1u,
							EdNode = 1u,
							EdRoot = "c_cloth_skirt_b_osg"
						}
					}
				};
				List<OsageCollisionParameter> collisions57 = obj9.Collisions;
				OsageCollisionParameter osageCollisionParameter57 = new OsageCollisionParameter();
				osageCollisionParameter57.Bone0.Name = "j_momo_r_wj";
				osageCollisionParameter57.Bone0.Position = new Vector3(0.25f, -1f / 64f, 0.003906f);
				osageCollisionParameter57.Bone1.Name = "j_momo_l_wj";
				osageCollisionParameter57.Bone1.Position = new Vector3(0.25f, -1f / 64f, -0.003906f);
				osageCollisionParameter57.Radius = 0.050781f;
				osageCollisionParameter57.Type = 2u;
				collisions57.Add(osageCollisionParameter57);
				List<OsageCollisionParameter> collisions58 = obj9.Collisions;
				OsageCollisionParameter osageCollisionParameter58 = new OsageCollisionParameter();
				osageCollisionParameter58.Bone0.Name = "j_momo_r_wj";
				osageCollisionParameter58.Bone0.Position = new Vector3(5f / 32f, -1f / 64f, 0.003906f);
				osageCollisionParameter58.Bone1.Name = "j_momo_l_wj";
				osageCollisionParameter58.Bone1.Position = new Vector3(5f / 32f, -1f / 64f, -0.003906f);
				osageCollisionParameter58.Radius = 0.050781f;
				osageCollisionParameter58.Type = 2u;
				collisions58.Add(osageCollisionParameter58);
				List<OsageCollisionParameter> collisions59 = obj9.Collisions;
				OsageCollisionParameter osageCollisionParameter59 = new OsageCollisionParameter();
				osageCollisionParameter59.Bone0.Name = "kl_kosi_etc_wj";
				osageCollisionParameter59.Bone0.Position = new Vector3(0.035156f, -9f / 64f, 1f / 32f);
				osageCollisionParameter59.Bone1.Name = "kl_kosi_etc_wj";
				osageCollisionParameter59.Bone1.Position = new Vector3(0.035156f, -9f / 64f, -1f / 32f);
				osageCollisionParameter59.Radius = 0.105469f;
				osageCollisionParameter59.Type = 2u;
				collisions59.Add(osageCollisionParameter59);
				List<OsageCollisionParameter> collisions60 = obj9.Collisions;
				OsageCollisionParameter osageCollisionParameter60 = new OsageCollisionParameter();
				osageCollisionParameter60.Bone0.Name = "j_momo_r_wj";
				osageCollisionParameter60.Bone0.Position = new Vector3(3f / 32f, 0f, 0f);
				osageCollisionParameter60.Bone1.Name = "j_momo_l_wj";
				osageCollisionParameter60.Bone1.Position = new Vector3(3f / 32f, 0f, 0f);
				osageCollisionParameter60.Radius = 0.089844f;
				osageCollisionParameter60.Type = 2u;
				collisions60.Add(osageCollisionParameter60);
				List<OsageCollisionParameter> collisions61 = obj9.Collisions;
				OsageCollisionParameter osageCollisionParameter61 = new OsageCollisionParameter();
				osageCollisionParameter61.Bone0.Name = "kl_kosi_etc_wj";
				osageCollisionParameter61.Bone0.Position = new Vector3(1f / 32f, -0.1875f, 0f);
				osageCollisionParameter61.Bone1.Name = "kl_kosi_etc_wj";
				osageCollisionParameter61.Bone1.Position = new Vector3(0f, 0f, 0f);
				osageCollisionParameter61.Radius = 3f / 32f;
				osageCollisionParameter61.Type = 2u;
				collisions61.Add(osageCollisionParameter61);
				List<OsageCollisionParameter> collisions62 = obj9.Collisions;
				OsageCollisionParameter osageCollisionParameter62 = new OsageCollisionParameter();
				osageCollisionParameter62.Bone0.Name = "j_momo_r_wj";
				osageCollisionParameter62.Bone0.Position = new Vector3(0.058594f, -1f / 64f, 0.003906f);
				osageCollisionParameter62.Bone1.Name = "j_momo_l_wj";
				osageCollisionParameter62.Bone1.Position = new Vector3(0.058594f, -1f / 64f, -0.003906f);
				osageCollisionParameter62.Radius = 0.089844f;
				osageCollisionParameter62.Type = 2u;
				collisions62.Add(osageCollisionParameter62);
				List<OsageCollisionParameter> collisions63 = obj9.Collisions;
				OsageCollisionParameter osageCollisionParameter63 = new OsageCollisionParameter();
				osageCollisionParameter63.Bone0.Name = "j_momo_r_wj";
				osageCollisionParameter63.Bone0.Position = new Vector3(3f / 32f, -0.011719f, -0.007813f);
				osageCollisionParameter63.Bone1.Name = "j_momo_r_wj";
				osageCollisionParameter63.Bone1.Position = new Vector3(0.25f, 0f, 0f);
				osageCollisionParameter63.Radius = 5f / 64f;
				osageCollisionParameter63.Type = 2u;
				collisions63.Add(osageCollisionParameter63);
				List<OsageCollisionParameter> collisions64 = obj9.Collisions;
				OsageCollisionParameter osageCollisionParameter64 = new OsageCollisionParameter();
				osageCollisionParameter64.Bone0.Name = "j_momo_l_wj";
				osageCollisionParameter64.Bone0.Position = new Vector3(3f / 32f, -0.011719f, 0.007813f);
				osageCollisionParameter64.Bone1.Name = "j_momo_l_wj";
				osageCollisionParameter64.Bone1.Position = new Vector3(0.25f, 0f, 0f);
				osageCollisionParameter64.Radius = 5f / 64f;
				osageCollisionParameter64.Type = 2u;
				collisions64.Add(osageCollisionParameter64);
				obj9.CollisionRadius = 0f;
				obj9.CollisionType = 1u;
				obj9.Force = 0.015f;
				obj9.ForceGain = 0.9f;
				obj9.Friction = 1f;
				obj9.HingeY = 90f;
				obj9.HingeZ = 90f;
				obj9.InitRotationY = 0f;
				obj9.InitRotationZ = 0f;
				obj9.MoveCancel = 0f;
				obj9.Nodes.Add(new OsageNodeParameter
				{
					Radius = 0.03f,
					HingeYMax = 180f,
					HingeYMin = -180f,
					HingeZMax = 180f,
					HingeZMin = -180f,
					InertialCancel = 0f,
					Weight = 1f
				});
				obj9.RotationY = 0f;
				obj9.RotationZ = 0f;
				obj9.Stiffness = 0f;
				obj9.WindAffection = 0.5f;
				dictionary7.Add("j_cloth_skirt_r_06_000_wj", obj9);
				OsageSkinParameter obj10 = new OsageSkinParameter
				{
					Name = "c_cloth_long_b_osg",
					AirResistance = 0.8f,
					Bocs = 
					{
						new OsageBocParameter
						{
							StNode = 0u,
							EdNode = 0u,
							EdRoot = "c_cloth_long_l_03_osg"
						},
						new OsageBocParameter
						{
							StNode = 0u,
							EdNode = 0u,
							EdRoot = "c_cloth_long_r_03_osg"
						},
						new OsageBocParameter
						{
							StNode = 1u,
							EdNode = 1u,
							EdRoot = "c_cloth_long_l_03_osg"
						},
						new OsageBocParameter
						{
							StNode = 1u,
							EdNode = 1u,
							EdRoot = "c_cloth_long_r_03_osg"
						}
					}
				};
				List<OsageCollisionParameter> collisions65 = obj10.Collisions;
				OsageCollisionParameter osageCollisionParameter65 = new OsageCollisionParameter();
				osageCollisionParameter65.Bone0.Name = "j_momo_l_wj";
				osageCollisionParameter65.Bone0.Position = new Vector3(0.0625f, 0f, -0.00390625f);
				osageCollisionParameter65.Bone1.Name = "j_momo_l_wj";
				osageCollisionParameter65.Bone1.Position = new Vector3(0.367188f, 0f, 0f);
				osageCollisionParameter65.Radius = 3f / 32f;
				osageCollisionParameter65.Type = 2u;
				collisions65.Add(osageCollisionParameter65);
				List<OsageCollisionParameter> collisions66 = obj10.Collisions;
				OsageCollisionParameter osageCollisionParameter66 = new OsageCollisionParameter();
				osageCollisionParameter66.Bone0.Name = "j_momo_r_wj";
				osageCollisionParameter66.Bone0.Position = new Vector3(0.0625f, 0f, 0.00390625f);
				osageCollisionParameter66.Bone1.Name = "j_momo_r_wj";
				osageCollisionParameter66.Bone1.Position = new Vector3(0.367188f, 0f, 0f);
				osageCollisionParameter66.Radius = 3f / 32f;
				osageCollisionParameter66.Type = 2u;
				collisions66.Add(osageCollisionParameter66);
				List<OsageCollisionParameter> collisions67 = obj10.Collisions;
				OsageCollisionParameter osageCollisionParameter67 = new OsageCollisionParameter();
				osageCollisionParameter67.Bone0.Name = "j_sune_l_wj";
				osageCollisionParameter67.Bone0.Position = new Vector3(0f, 0f, 0f);
				osageCollisionParameter67.Bone1.Name = "j_sune_l_wj";
				osageCollisionParameter67.Bone1.Position = new Vector3(0.375f, 0f, 0f);
				osageCollisionParameter67.Radius = 3f / 32f;
				osageCollisionParameter67.Type = 2u;
				collisions67.Add(osageCollisionParameter67);
				List<OsageCollisionParameter> collisions68 = obj10.Collisions;
				OsageCollisionParameter osageCollisionParameter68 = new OsageCollisionParameter();
				osageCollisionParameter68.Bone0.Name = "j_sune_r_wj";
				osageCollisionParameter68.Bone0.Position = new Vector3(0f, 0f, 0f);
				osageCollisionParameter68.Bone1.Name = "j_sune_r_wj";
				osageCollisionParameter68.Bone1.Position = new Vector3(0.375f, 0f, 0f);
				osageCollisionParameter68.Radius = 3f / 32f;
				osageCollisionParameter68.Type = 2u;
				collisions68.Add(osageCollisionParameter68);
				List<OsageCollisionParameter> collisions69 = obj10.Collisions;
				OsageCollisionParameter osageCollisionParameter69 = new OsageCollisionParameter();
				osageCollisionParameter69.Bone0.Name = "kl_asi_r_wj_co";
				osageCollisionParameter69.Bone0.Position = new Vector3(0f, 0f, 0f);
				osageCollisionParameter69.Bone1.Name = "kl_asi_r_wj_co";
				osageCollisionParameter69.Bone1.Position = new Vector3(0f, 0f, 0f);
				osageCollisionParameter69.Radius = 3f / 32f;
				osageCollisionParameter69.Type = 2u;
				collisions69.Add(osageCollisionParameter69);
				List<OsageCollisionParameter> collisions70 = obj10.Collisions;
				OsageCollisionParameter osageCollisionParameter70 = new OsageCollisionParameter();
				osageCollisionParameter70.Bone0.Name = "kl_asi_l_wj_co";
				osageCollisionParameter70.Bone0.Position = new Vector3(0f, 0f, 0f);
				osageCollisionParameter70.Bone1.Name = "kl_asi_l_wj_co";
				osageCollisionParameter70.Bone1.Position = new Vector3(0f, 0f, 0f);
				osageCollisionParameter70.Radius = 3f / 32f;
				osageCollisionParameter70.Type = 2u;
				collisions70.Add(osageCollisionParameter70);
				obj10.CollisionRadius = 0.01f;
				obj10.CollisionType = 0u;
				obj10.Force = 0.015f;
				obj10.ForceGain = 0.9f;
				obj10.Friction = 1f;
				obj10.HingeY = 180f;
				obj10.HingeZ = 180f;
				obj10.InitRotationY = 0f;
				obj10.InitRotationZ = 0f;
				obj10.MoveCancel = 0f;
				obj10.Nodes.Add(new OsageNodeParameter
				{
					Radius = 0.01f,
					HingeYMax = 180f,
					HingeYMin = -180f,
					HingeZMax = 180f,
					HingeZMin = -180f,
					InertialCancel = 0f,
					Weight = 3f
				});
				obj10.Nodes.Add(new OsageNodeParameter
				{
					Radius = 0.01f,
					HingeYMax = 180f,
					HingeYMin = -180f,
					HingeZMax = 180f,
					HingeZMin = -180f,
					InertialCancel = 0f,
					Weight = 3f
				});
				obj10.Nodes.Add(new OsageNodeParameter
				{
					Radius = 0.01f,
					HingeYMax = 180f,
					HingeYMin = -180f,
					HingeZMax = 180f,
					HingeZMin = -180f,
					InertialCancel = 0f,
					Weight = 3f
				});
				obj10.RotationY = 0f;
				obj10.RotationZ = 0f;
				obj10.Stiffness = 0f;
				obj10.WindAffection = 0.5f;
				dictionary7.Add("j_cloth_long_b_000_wj", obj10);
				OsageSkinParameter obj11 = new OsageSkinParameter
				{
					Name = "c_cloth_long_f_osg",
					AirResistance = 0.8f,
					Bocs = 
					{
						new OsageBocParameter
						{
							StNode = 0u,
							EdNode = 0u,
							EdRoot = "c_cloth_long_l_01_osg"
						},
						new OsageBocParameter
						{
							StNode = 0u,
							EdNode = 0u,
							EdRoot = "c_cloth_long_r_01_osg"
						},
						new OsageBocParameter
						{
							StNode = 1u,
							EdNode = 1u,
							EdRoot = "c_cloth_long_l_01_osg"
						},
						new OsageBocParameter
						{
							StNode = 1u,
							EdNode = 1u,
							EdRoot = "c_cloth_long_r_01_osg"
						}
					}
				};
				List<OsageCollisionParameter> collisions71 = obj11.Collisions;
				OsageCollisionParameter osageCollisionParameter71 = new OsageCollisionParameter();
				osageCollisionParameter71.Bone0.Name = "j_momo_l_wj";
				osageCollisionParameter71.Bone0.Position = new Vector3(0.0625f, 0f, -0.00390625f);
				osageCollisionParameter71.Bone1.Name = "j_momo_l_wj";
				osageCollisionParameter71.Bone1.Position = new Vector3(0.367188f, 0f, 0f);
				osageCollisionParameter71.Radius = 3f / 32f;
				osageCollisionParameter71.Type = 2u;
				collisions71.Add(osageCollisionParameter71);
				List<OsageCollisionParameter> collisions72 = obj11.Collisions;
				OsageCollisionParameter osageCollisionParameter72 = new OsageCollisionParameter();
				osageCollisionParameter72.Bone0.Name = "j_momo_r_wj";
				osageCollisionParameter72.Bone0.Position = new Vector3(0.0625f, 0f, 0.00390625f);
				osageCollisionParameter72.Bone1.Name = "j_momo_r_wj";
				osageCollisionParameter72.Bone1.Position = new Vector3(0.367188f, 0f, 0f);
				osageCollisionParameter72.Radius = 3f / 32f;
				osageCollisionParameter72.Type = 2u;
				collisions72.Add(osageCollisionParameter72);
				List<OsageCollisionParameter> collisions73 = obj11.Collisions;
				OsageCollisionParameter osageCollisionParameter73 = new OsageCollisionParameter();
				osageCollisionParameter73.Bone0.Name = "j_sune_l_wj";
				osageCollisionParameter73.Bone0.Position = new Vector3(0f, 0f, 0f);
				osageCollisionParameter73.Bone1.Name = "j_sune_l_wj";
				osageCollisionParameter73.Bone1.Position = new Vector3(0.375f, 0f, 0f);
				osageCollisionParameter73.Radius = 3f / 32f;
				osageCollisionParameter73.Type = 2u;
				collisions73.Add(osageCollisionParameter73);
				List<OsageCollisionParameter> collisions74 = obj11.Collisions;
				OsageCollisionParameter osageCollisionParameter74 = new OsageCollisionParameter();
				osageCollisionParameter74.Bone0.Name = "j_sune_r_wj";
				osageCollisionParameter74.Bone0.Position = new Vector3(0f, 0f, 0f);
				osageCollisionParameter74.Bone1.Name = "j_sune_r_wj";
				osageCollisionParameter74.Bone1.Position = new Vector3(0.375f, 0f, 0f);
				osageCollisionParameter74.Radius = 3f / 32f;
				osageCollisionParameter74.Type = 2u;
				collisions74.Add(osageCollisionParameter74);
				List<OsageCollisionParameter> collisions75 = obj11.Collisions;
				OsageCollisionParameter osageCollisionParameter75 = new OsageCollisionParameter();
				osageCollisionParameter75.Bone0.Name = "kl_asi_r_wj_co";
				osageCollisionParameter75.Bone0.Position = new Vector3(0f, 0f, 0f);
				osageCollisionParameter75.Bone1.Name = "kl_asi_r_wj_co";
				osageCollisionParameter75.Bone1.Position = new Vector3(0f, 0f, 0f);
				osageCollisionParameter75.Radius = 3f / 32f;
				osageCollisionParameter75.Type = 2u;
				collisions75.Add(osageCollisionParameter75);
				List<OsageCollisionParameter> collisions76 = obj11.Collisions;
				OsageCollisionParameter osageCollisionParameter76 = new OsageCollisionParameter();
				osageCollisionParameter76.Bone0.Name = "kl_asi_l_wj_co";
				osageCollisionParameter76.Bone0.Position = new Vector3(0f, 0f, 0f);
				osageCollisionParameter76.Bone1.Name = "kl_asi_l_wj_co";
				osageCollisionParameter76.Bone1.Position = new Vector3(0f, 0f, 0f);
				osageCollisionParameter76.Radius = 3f / 32f;
				osageCollisionParameter76.Type = 2u;
				collisions76.Add(osageCollisionParameter76);
				obj11.CollisionRadius = 0.01f;
				obj11.CollisionType = 0u;
				obj11.Force = 0.015f;
				obj11.ForceGain = 0.7f;
				obj11.Friction = 1f;
				obj11.HingeY = 180f;
				obj11.HingeZ = 180f;
				obj11.InitRotationY = 0f;
				obj11.InitRotationZ = 0f;
				obj11.MoveCancel = 0f;
				obj11.Nodes.Add(new OsageNodeParameter
				{
					Radius = 0.01f,
					HingeYMax = 180f,
					HingeYMin = -180f,
					HingeZMax = 180f,
					HingeZMin = -180f,
					InertialCancel = 0f,
					Weight = 3f
				});
				obj11.Nodes.Add(new OsageNodeParameter
				{
					Radius = 0.01f,
					HingeYMax = 180f,
					HingeYMin = -180f,
					HingeZMax = 180f,
					HingeZMin = -180f,
					InertialCancel = 0f,
					Weight = 3f
				});
				obj11.Nodes.Add(new OsageNodeParameter
				{
					Radius = 0.01f,
					HingeYMax = 180f,
					HingeYMin = -180f,
					HingeZMax = 180f,
					HingeZMin = -180f,
					InertialCancel = 0f,
					Weight = 3f
				});
				obj11.RotationY = 0f;
				obj11.RotationZ = 0f;
				obj11.Stiffness = 0f;
				obj11.WindAffection = 0.5f;
				dictionary7.Add("j_cloth_long_f_000_wj", obj11);
				OsageSkinParameter obj12 = new OsageSkinParameter
				{
					Name = "c_cloth_long_l_01_osg",
					AirResistance = 0.8f,
					Bocs = 
					{
						new OsageBocParameter
						{
							StNode = 0u,
							EdNode = 0u,
							EdRoot = "c_cloth_long_l_02_osg"
						},
						new OsageBocParameter
						{
							StNode = 1u,
							EdNode = 1u,
							EdRoot = "c_cloth_long_l_02_osg"
						}
					}
				};
				List<OsageCollisionParameter> collisions77 = obj12.Collisions;
				OsageCollisionParameter osageCollisionParameter77 = new OsageCollisionParameter();
				osageCollisionParameter77.Bone0.Name = "j_momo_l_wj";
				osageCollisionParameter77.Bone0.Position = new Vector3(0.0625f, 0f, -0.00390625f);
				osageCollisionParameter77.Bone1.Name = "j_momo_l_wj";
				osageCollisionParameter77.Bone1.Position = new Vector3(0.367188f, 0f, 0f);
				osageCollisionParameter77.Radius = 3f / 32f;
				osageCollisionParameter77.Type = 2u;
				collisions77.Add(osageCollisionParameter77);
				List<OsageCollisionParameter> collisions78 = obj12.Collisions;
				OsageCollisionParameter osageCollisionParameter78 = new OsageCollisionParameter();
				osageCollisionParameter78.Bone0.Name = "j_momo_r_wj";
				osageCollisionParameter78.Bone0.Position = new Vector3(0.0625f, 0f, 0.00390625f);
				osageCollisionParameter78.Bone1.Name = "j_momo_r_wj";
				osageCollisionParameter78.Bone1.Position = new Vector3(0.367188f, 0f, 0f);
				osageCollisionParameter78.Radius = 3f / 32f;
				osageCollisionParameter78.Type = 2u;
				collisions78.Add(osageCollisionParameter78);
				List<OsageCollisionParameter> collisions79 = obj12.Collisions;
				OsageCollisionParameter osageCollisionParameter79 = new OsageCollisionParameter();
				osageCollisionParameter79.Bone0.Name = "j_sune_l_wj";
				osageCollisionParameter79.Bone0.Position = new Vector3(0f, 0f, 0f);
				osageCollisionParameter79.Bone1.Name = "j_sune_l_wj";
				osageCollisionParameter79.Bone1.Position = new Vector3(0.375f, 0f, 0f);
				osageCollisionParameter79.Radius = 3f / 32f;
				osageCollisionParameter79.Type = 2u;
				collisions79.Add(osageCollisionParameter79);
				List<OsageCollisionParameter> collisions80 = obj12.Collisions;
				OsageCollisionParameter osageCollisionParameter80 = new OsageCollisionParameter();
				osageCollisionParameter80.Bone0.Name = "j_sune_r_wj";
				osageCollisionParameter80.Bone0.Position = new Vector3(0f, 0f, 0f);
				osageCollisionParameter80.Bone1.Name = "j_sune_r_wj";
				osageCollisionParameter80.Bone1.Position = new Vector3(0.375f, 0f, 0f);
				osageCollisionParameter80.Radius = 3f / 32f;
				osageCollisionParameter80.Type = 2u;
				collisions80.Add(osageCollisionParameter80);
				List<OsageCollisionParameter> collisions81 = obj12.Collisions;
				OsageCollisionParameter osageCollisionParameter81 = new OsageCollisionParameter();
				osageCollisionParameter81.Bone0.Name = "kl_asi_r_wj_co";
				osageCollisionParameter81.Bone0.Position = new Vector3(0f, 0f, 0f);
				osageCollisionParameter81.Bone1.Name = "kl_asi_r_wj_co";
				osageCollisionParameter81.Bone1.Position = new Vector3(0f, 0f, 0f);
				osageCollisionParameter81.Radius = 3f / 32f;
				osageCollisionParameter81.Type = 2u;
				collisions81.Add(osageCollisionParameter81);
				List<OsageCollisionParameter> collisions82 = obj12.Collisions;
				OsageCollisionParameter osageCollisionParameter82 = new OsageCollisionParameter();
				osageCollisionParameter82.Bone0.Name = "kl_asi_l_wj_co";
				osageCollisionParameter82.Bone0.Position = new Vector3(0f, 0f, 0f);
				osageCollisionParameter82.Bone1.Name = "kl_asi_l_wj_co";
				osageCollisionParameter82.Bone1.Position = new Vector3(0f, 0f, 0f);
				osageCollisionParameter82.Radius = 3f / 32f;
				osageCollisionParameter82.Type = 2u;
				collisions82.Add(osageCollisionParameter82);
				obj12.CollisionRadius = 0.01f;
				obj12.CollisionType = 0u;
				obj12.Force = 0.015f;
				obj12.ForceGain = 0.7f;
				obj12.Friction = 1f;
				obj12.HingeY = 180f;
				obj12.HingeZ = 180f;
				obj12.InitRotationY = 0f;
				obj12.InitRotationZ = 0f;
				obj12.MoveCancel = 0f;
				obj12.Nodes.Add(new OsageNodeParameter
				{
					Radius = 0.01f,
					HingeYMax = 180f,
					HingeYMin = -180f,
					HingeZMax = 180f,
					HingeZMin = -180f,
					InertialCancel = 0f,
					Weight = 3f
				});
				obj12.Nodes.Add(new OsageNodeParameter
				{
					Radius = 0.01f,
					HingeYMax = 180f,
					HingeYMin = -180f,
					HingeZMax = 180f,
					HingeZMin = -180f,
					InertialCancel = 0f,
					Weight = 3f
				});
				obj12.Nodes.Add(new OsageNodeParameter
				{
					Radius = 0.01f,
					HingeYMax = 180f,
					HingeYMin = -180f,
					HingeZMax = 180f,
					HingeZMin = -180f,
					InertialCancel = 0f,
					Weight = 3f
				});
				obj12.RotationY = 0f;
				obj12.RotationZ = 0f;
				obj12.Stiffness = 0f;
				obj12.WindAffection = 0.5f;
				dictionary7.Add("j_cloth_long_l_01_000_wj", obj12);
				OsageSkinParameter obj13 = new OsageSkinParameter
				{
					Name = "c_cloth_long_l_02_osg",
					AirResistance = 0.8f,
					Bocs = 
					{
						new OsageBocParameter
						{
							StNode = 0u,
							EdNode = 0u,
							EdRoot = "c_cloth_long_l_03_osg"
						},
						new OsageBocParameter
						{
							StNode = 1u,
							EdNode = 1u,
							EdRoot = "c_cloth_long_l_03_osg"
						}
					}
				};
				List<OsageCollisionParameter> collisions83 = obj13.Collisions;
				OsageCollisionParameter osageCollisionParameter83 = new OsageCollisionParameter();
				osageCollisionParameter83.Bone0.Name = "j_momo_l_wj";
				osageCollisionParameter83.Bone0.Position = new Vector3(0.0625f, 0f, -0.00390625f);
				osageCollisionParameter83.Bone1.Name = "j_momo_l_wj";
				osageCollisionParameter83.Bone1.Position = new Vector3(0.367188f, 0f, 0f);
				osageCollisionParameter83.Radius = 3f / 32f;
				osageCollisionParameter83.Type = 2u;
				collisions83.Add(osageCollisionParameter83);
				List<OsageCollisionParameter> collisions84 = obj13.Collisions;
				OsageCollisionParameter osageCollisionParameter84 = new OsageCollisionParameter();
				osageCollisionParameter84.Bone0.Name = "j_momo_r_wj";
				osageCollisionParameter84.Bone0.Position = new Vector3(0.0625f, 0f, 0.00390625f);
				osageCollisionParameter84.Bone1.Name = "j_momo_r_wj";
				osageCollisionParameter84.Bone1.Position = new Vector3(0.367188f, 0f, 0f);
				osageCollisionParameter84.Radius = 3f / 32f;
				osageCollisionParameter84.Type = 2u;
				collisions84.Add(osageCollisionParameter84);
				List<OsageCollisionParameter> collisions85 = obj13.Collisions;
				OsageCollisionParameter osageCollisionParameter85 = new OsageCollisionParameter();
				osageCollisionParameter85.Bone0.Name = "j_sune_l_wj";
				osageCollisionParameter85.Bone0.Position = new Vector3(0f, 0f, 0f);
				osageCollisionParameter85.Bone1.Name = "j_sune_l_wj";
				osageCollisionParameter85.Bone1.Position = new Vector3(0.375f, 0f, 0f);
				osageCollisionParameter85.Radius = 3f / 32f;
				osageCollisionParameter85.Type = 2u;
				collisions85.Add(osageCollisionParameter85);
				List<OsageCollisionParameter> collisions86 = obj13.Collisions;
				OsageCollisionParameter osageCollisionParameter86 = new OsageCollisionParameter();
				osageCollisionParameter86.Bone0.Name = "j_sune_r_wj";
				osageCollisionParameter86.Bone0.Position = new Vector3(0f, 0f, 0f);
				osageCollisionParameter86.Bone1.Name = "j_sune_r_wj";
				osageCollisionParameter86.Bone1.Position = new Vector3(0.375f, 0f, 0f);
				osageCollisionParameter86.Radius = 3f / 32f;
				osageCollisionParameter86.Type = 2u;
				collisions86.Add(osageCollisionParameter86);
				List<OsageCollisionParameter> collisions87 = obj13.Collisions;
				OsageCollisionParameter osageCollisionParameter87 = new OsageCollisionParameter();
				osageCollisionParameter87.Bone0.Name = "kl_asi_r_wj_co";
				osageCollisionParameter87.Bone0.Position = new Vector3(0f, 0f, 0f);
				osageCollisionParameter87.Bone1.Name = "kl_asi_r_wj_co";
				osageCollisionParameter87.Bone1.Position = new Vector3(0f, 0f, 0f);
				osageCollisionParameter87.Radius = 3f / 32f;
				osageCollisionParameter87.Type = 2u;
				collisions87.Add(osageCollisionParameter87);
				List<OsageCollisionParameter> collisions88 = obj13.Collisions;
				OsageCollisionParameter osageCollisionParameter88 = new OsageCollisionParameter();
				osageCollisionParameter88.Bone0.Name = "kl_asi_l_wj_co";
				osageCollisionParameter88.Bone0.Position = new Vector3(0f, 0f, 0f);
				osageCollisionParameter88.Bone1.Name = "kl_asi_l_wj_co";
				osageCollisionParameter88.Bone1.Position = new Vector3(0f, 0f, 0f);
				osageCollisionParameter88.Radius = 3f / 32f;
				osageCollisionParameter88.Type = 2u;
				collisions88.Add(osageCollisionParameter88);
				obj13.CollisionRadius = 0.01f;
				obj13.CollisionType = 0u;
				obj13.Force = 0.015f;
				obj13.ForceGain = 0.7f;
				obj13.Friction = 1f;
				obj13.HingeY = 180f;
				obj13.HingeZ = 180f;
				obj13.InitRotationY = 0f;
				obj13.InitRotationZ = 0f;
				obj13.MoveCancel = 0f;
				obj13.Nodes.Add(new OsageNodeParameter
				{
					Radius = 0.01f,
					HingeYMax = 180f,
					HingeYMin = -180f,
					HingeZMax = 180f,
					HingeZMin = -180f,
					InertialCancel = 0f,
					Weight = 3f
				});
				obj13.Nodes.Add(new OsageNodeParameter
				{
					Radius = 0.01f,
					HingeYMax = 180f,
					HingeYMin = -180f,
					HingeZMax = 180f,
					HingeZMin = -180f,
					InertialCancel = 0f,
					Weight = 3f
				});
				obj13.Nodes.Add(new OsageNodeParameter
				{
					Radius = 0.01f,
					HingeYMax = 180f,
					HingeYMin = -180f,
					HingeZMax = 180f,
					HingeZMin = -180f,
					InertialCancel = 0f,
					Weight = 3f
				});
				obj13.RotationY = 0f;
				obj13.RotationZ = 0f;
				obj13.Stiffness = 0f;
				obj13.WindAffection = 0.5f;
				dictionary7.Add("j_cloth_long_l_02_000_wj", obj13);
				OsageSkinParameter obj14 = new OsageSkinParameter
				{
					Name = "c_cloth_long_l_03_osg",
					AirResistance = 0.8f,
					Bocs = 
					{
						new OsageBocParameter
						{
							StNode = 0u,
							EdNode = 0u,
							EdRoot = "c_cloth_long_b_osg"
						},
						new OsageBocParameter
						{
							StNode = 1u,
							EdNode = 1u,
							EdRoot = "c_cloth_long_b_osg"
						}
					}
				};
				List<OsageCollisionParameter> collisions89 = obj14.Collisions;
				OsageCollisionParameter osageCollisionParameter89 = new OsageCollisionParameter();
				osageCollisionParameter89.Bone0.Name = "j_momo_l_wj";
				osageCollisionParameter89.Bone0.Position = new Vector3(0.0625f, 0f, -0.00390625f);
				osageCollisionParameter89.Bone1.Name = "j_momo_l_wj";
				osageCollisionParameter89.Bone1.Position = new Vector3(0.367188f, 0f, 0f);
				osageCollisionParameter89.Radius = 3f / 32f;
				osageCollisionParameter89.Type = 2u;
				collisions89.Add(osageCollisionParameter89);
				List<OsageCollisionParameter> collisions90 = obj14.Collisions;
				OsageCollisionParameter osageCollisionParameter90 = new OsageCollisionParameter();
				osageCollisionParameter90.Bone0.Name = "j_momo_r_wj";
				osageCollisionParameter90.Bone0.Position = new Vector3(0.0625f, 0f, 0.00390625f);
				osageCollisionParameter90.Bone1.Name = "j_momo_r_wj";
				osageCollisionParameter90.Bone1.Position = new Vector3(0.367188f, 0f, 0f);
				osageCollisionParameter90.Radius = 3f / 32f;
				osageCollisionParameter90.Type = 2u;
				collisions90.Add(osageCollisionParameter90);
				List<OsageCollisionParameter> collisions91 = obj14.Collisions;
				OsageCollisionParameter osageCollisionParameter91 = new OsageCollisionParameter();
				osageCollisionParameter91.Bone0.Name = "j_sune_l_wj";
				osageCollisionParameter91.Bone0.Position = new Vector3(0f, 0f, 0f);
				osageCollisionParameter91.Bone1.Name = "j_sune_l_wj";
				osageCollisionParameter91.Bone1.Position = new Vector3(0.375f, 0f, 0f);
				osageCollisionParameter91.Radius = 3f / 32f;
				osageCollisionParameter91.Type = 2u;
				collisions91.Add(osageCollisionParameter91);
				List<OsageCollisionParameter> collisions92 = obj14.Collisions;
				OsageCollisionParameter osageCollisionParameter92 = new OsageCollisionParameter();
				osageCollisionParameter92.Bone0.Name = "j_sune_r_wj";
				osageCollisionParameter92.Bone0.Position = new Vector3(0f, 0f, 0f);
				osageCollisionParameter92.Bone1.Name = "j_sune_r_wj";
				osageCollisionParameter92.Bone1.Position = new Vector3(0.375f, 0f, 0f);
				osageCollisionParameter92.Radius = 3f / 32f;
				osageCollisionParameter92.Type = 2u;
				collisions92.Add(osageCollisionParameter92);
				List<OsageCollisionParameter> collisions93 = obj14.Collisions;
				OsageCollisionParameter osageCollisionParameter93 = new OsageCollisionParameter();
				osageCollisionParameter93.Bone0.Name = "kl_asi_r_wj_co";
				osageCollisionParameter93.Bone0.Position = new Vector3(0f, 0f, 0f);
				osageCollisionParameter93.Bone1.Name = "kl_asi_r_wj_co";
				osageCollisionParameter93.Bone1.Position = new Vector3(0f, 0f, 0f);
				osageCollisionParameter93.Radius = 3f / 32f;
				osageCollisionParameter93.Type = 2u;
				collisions93.Add(osageCollisionParameter93);
				List<OsageCollisionParameter> collisions94 = obj14.Collisions;
				OsageCollisionParameter osageCollisionParameter94 = new OsageCollisionParameter();
				osageCollisionParameter94.Bone0.Name = "kl_asi_l_wj_co";
				osageCollisionParameter94.Bone0.Position = new Vector3(0f, 0f, 0f);
				osageCollisionParameter94.Bone1.Name = "kl_asi_l_wj_co";
				osageCollisionParameter94.Bone1.Position = new Vector3(0f, 0f, 0f);
				osageCollisionParameter94.Radius = 3f / 32f;
				osageCollisionParameter94.Type = 2u;
				collisions94.Add(osageCollisionParameter94);
				obj14.CollisionRadius = 0.01f;
				obj14.CollisionType = 0u;
				obj14.Force = 0.015f;
				obj14.ForceGain = 0.7f;
				obj14.Friction = 1f;
				obj14.HingeY = 180f;
				obj14.HingeZ = 180f;
				obj14.InitRotationY = 0f;
				obj14.InitRotationZ = 0f;
				obj14.MoveCancel = 0f;
				obj14.Nodes.Add(new OsageNodeParameter
				{
					Radius = 0.01f,
					HingeYMax = 180f,
					HingeYMin = -180f,
					HingeZMax = 180f,
					HingeZMin = -180f,
					InertialCancel = 0f,
					Weight = 3f
				});
				obj14.Nodes.Add(new OsageNodeParameter
				{
					Radius = 0.01f,
					HingeYMax = 180f,
					HingeYMin = -180f,
					HingeZMax = 180f,
					HingeZMin = -180f,
					InertialCancel = 0f,
					Weight = 3f
				});
				obj14.Nodes.Add(new OsageNodeParameter
				{
					Radius = 0.01f,
					HingeYMax = 180f,
					HingeYMin = -180f,
					HingeZMax = 180f,
					HingeZMin = -180f,
					InertialCancel = 0f,
					Weight = 3f
				});
				obj14.RotationY = 0f;
				obj14.RotationZ = 0f;
				obj14.Stiffness = 0f;
				obj14.WindAffection = 0.5f;
				dictionary7.Add("j_cloth_long_l_03_000_wj", obj14);
				OsageSkinParameter obj15 = new OsageSkinParameter
				{
					Name = "c_cloth_long_r_01_osg",
					AirResistance = 0.8f,
					Bocs = 
					{
						new OsageBocParameter
						{
							StNode = 0u,
							EdNode = 0u,
							EdRoot = "c_cloth_long_r_02_osg"
						},
						new OsageBocParameter
						{
							StNode = 1u,
							EdNode = 1u,
							EdRoot = "c_cloth_long_r_02_osg"
						}
					}
				};
				List<OsageCollisionParameter> collisions95 = obj15.Collisions;
				OsageCollisionParameter osageCollisionParameter95 = new OsageCollisionParameter();
				osageCollisionParameter95.Bone0.Name = "j_momo_l_wj";
				osageCollisionParameter95.Bone0.Position = new Vector3(0.0625f, 0f, -0.00390625f);
				osageCollisionParameter95.Bone1.Name = "j_momo_l_wj";
				osageCollisionParameter95.Bone1.Position = new Vector3(0.367188f, 0f, 0f);
				osageCollisionParameter95.Radius = 3f / 32f;
				osageCollisionParameter95.Type = 2u;
				collisions95.Add(osageCollisionParameter95);
				List<OsageCollisionParameter> collisions96 = obj15.Collisions;
				OsageCollisionParameter osageCollisionParameter96 = new OsageCollisionParameter();
				osageCollisionParameter96.Bone0.Name = "j_momo_r_wj";
				osageCollisionParameter96.Bone0.Position = new Vector3(0.0625f, 0f, 0.00390625f);
				osageCollisionParameter96.Bone1.Name = "j_momo_r_wj";
				osageCollisionParameter96.Bone1.Position = new Vector3(0.367188f, 0f, 0f);
				osageCollisionParameter96.Radius = 3f / 32f;
				osageCollisionParameter96.Type = 2u;
				collisions96.Add(osageCollisionParameter96);
				List<OsageCollisionParameter> collisions97 = obj15.Collisions;
				OsageCollisionParameter osageCollisionParameter97 = new OsageCollisionParameter();
				osageCollisionParameter97.Bone0.Name = "j_sune_l_wj";
				osageCollisionParameter97.Bone0.Position = new Vector3(0f, 0f, 0f);
				osageCollisionParameter97.Bone1.Name = "j_sune_l_wj";
				osageCollisionParameter97.Bone1.Position = new Vector3(0.375f, 0f, 0f);
				osageCollisionParameter97.Radius = 3f / 32f;
				osageCollisionParameter97.Type = 2u;
				collisions97.Add(osageCollisionParameter97);
				List<OsageCollisionParameter> collisions98 = obj15.Collisions;
				OsageCollisionParameter osageCollisionParameter98 = new OsageCollisionParameter();
				osageCollisionParameter98.Bone0.Name = "j_sune_r_wj";
				osageCollisionParameter98.Bone0.Position = new Vector3(0f, 0f, 0f);
				osageCollisionParameter98.Bone1.Name = "j_sune_r_wj";
				osageCollisionParameter98.Bone1.Position = new Vector3(0.375f, 0f, 0f);
				osageCollisionParameter98.Radius = 3f / 32f;
				osageCollisionParameter98.Type = 2u;
				collisions98.Add(osageCollisionParameter98);
				List<OsageCollisionParameter> collisions99 = obj15.Collisions;
				OsageCollisionParameter osageCollisionParameter99 = new OsageCollisionParameter();
				osageCollisionParameter99.Bone0.Name = "kl_asi_r_wj_co";
				osageCollisionParameter99.Bone0.Position = new Vector3(0f, 0f, 0f);
				osageCollisionParameter99.Bone1.Name = "kl_asi_r_wj_co";
				osageCollisionParameter99.Bone1.Position = new Vector3(0f, 0f, 0f);
				osageCollisionParameter99.Radius = 3f / 32f;
				osageCollisionParameter99.Type = 2u;
				collisions99.Add(osageCollisionParameter99);
				List<OsageCollisionParameter> collisions100 = obj15.Collisions;
				OsageCollisionParameter osageCollisionParameter100 = new OsageCollisionParameter();
				osageCollisionParameter100.Bone0.Name = "kl_asi_l_wj_co";
				osageCollisionParameter100.Bone0.Position = new Vector3(0f, 0f, 0f);
				osageCollisionParameter100.Bone1.Name = "kl_asi_l_wj_co";
				osageCollisionParameter100.Bone1.Position = new Vector3(0f, 0f, 0f);
				osageCollisionParameter100.Radius = 3f / 32f;
				osageCollisionParameter100.Type = 2u;
				collisions100.Add(osageCollisionParameter100);
				obj15.CollisionRadius = 0.01f;
				obj15.CollisionType = 0u;
				obj15.Force = 0.015f;
				obj15.ForceGain = 0.7f;
				obj15.Friction = 1f;
				obj15.HingeY = 180f;
				obj15.HingeZ = 180f;
				obj15.InitRotationY = 0f;
				obj15.InitRotationZ = 0f;
				obj15.MoveCancel = 0f;
				obj15.Nodes.Add(new OsageNodeParameter
				{
					Radius = 0.01f,
					HingeYMax = 180f,
					HingeYMin = -180f,
					HingeZMax = 180f,
					HingeZMin = -180f,
					InertialCancel = 0f,
					Weight = 3f
				});
				obj15.Nodes.Add(new OsageNodeParameter
				{
					Radius = 0.01f,
					HingeYMax = 180f,
					HingeYMin = -180f,
					HingeZMax = 180f,
					HingeZMin = -180f,
					InertialCancel = 0f,
					Weight = 3f
				});
				obj15.Nodes.Add(new OsageNodeParameter
				{
					Radius = 0.01f,
					HingeYMax = 180f,
					HingeYMin = -180f,
					HingeZMax = 180f,
					HingeZMin = -180f,
					InertialCancel = 0f,
					Weight = 3f
				});
				obj15.RotationY = 0f;
				obj15.RotationZ = 0f;
				obj15.Stiffness = 0f;
				obj15.WindAffection = 0.5f;
				dictionary7.Add("j_cloth_long_r_01_000_wj", obj15);
				OsageSkinParameter obj16 = new OsageSkinParameter
				{
					Name = "c_cloth_long_r_02_osg",
					AirResistance = 0.8f,
					Bocs = 
					{
						new OsageBocParameter
						{
							StNode = 0u,
							EdNode = 0u,
							EdRoot = "c_cloth_long_r_03_osg"
						},
						new OsageBocParameter
						{
							StNode = 1u,
							EdNode = 1u,
							EdRoot = "c_cloth_long_r_03_osg"
						}
					}
				};
				List<OsageCollisionParameter> collisions101 = obj16.Collisions;
				OsageCollisionParameter osageCollisionParameter101 = new OsageCollisionParameter();
				osageCollisionParameter101.Bone0.Name = "j_momo_l_wj";
				osageCollisionParameter101.Bone0.Position = new Vector3(0.0625f, 0f, -0.00390625f);
				osageCollisionParameter101.Bone1.Name = "j_momo_l_wj";
				osageCollisionParameter101.Bone1.Position = new Vector3(0.367188f, 0f, 0f);
				osageCollisionParameter101.Radius = 3f / 32f;
				osageCollisionParameter101.Type = 2u;
				collisions101.Add(osageCollisionParameter101);
				List<OsageCollisionParameter> collisions102 = obj16.Collisions;
				OsageCollisionParameter osageCollisionParameter102 = new OsageCollisionParameter();
				osageCollisionParameter102.Bone0.Name = "j_momo_r_wj";
				osageCollisionParameter102.Bone0.Position = new Vector3(0.0625f, 0f, 0.00390625f);
				osageCollisionParameter102.Bone1.Name = "j_momo_r_wj";
				osageCollisionParameter102.Bone1.Position = new Vector3(0.367188f, 0f, 0f);
				osageCollisionParameter102.Radius = 3f / 32f;
				osageCollisionParameter102.Type = 2u;
				collisions102.Add(osageCollisionParameter102);
				List<OsageCollisionParameter> collisions103 = obj16.Collisions;
				OsageCollisionParameter osageCollisionParameter103 = new OsageCollisionParameter();
				osageCollisionParameter103.Bone0.Name = "j_sune_l_wj";
				osageCollisionParameter103.Bone0.Position = new Vector3(0f, 0f, 0f);
				osageCollisionParameter103.Bone1.Name = "j_sune_l_wj";
				osageCollisionParameter103.Bone1.Position = new Vector3(0.375f, 0f, 0f);
				osageCollisionParameter103.Radius = 3f / 32f;
				osageCollisionParameter103.Type = 2u;
				collisions103.Add(osageCollisionParameter103);
				List<OsageCollisionParameter> collisions104 = obj16.Collisions;
				OsageCollisionParameter osageCollisionParameter104 = new OsageCollisionParameter();
				osageCollisionParameter104.Bone0.Name = "j_sune_r_wj";
				osageCollisionParameter104.Bone0.Position = new Vector3(0f, 0f, 0f);
				osageCollisionParameter104.Bone1.Name = "j_sune_r_wj";
				osageCollisionParameter104.Bone1.Position = new Vector3(0.375f, 0f, 0f);
				osageCollisionParameter104.Radius = 3f / 32f;
				osageCollisionParameter104.Type = 2u;
				collisions104.Add(osageCollisionParameter104);
				List<OsageCollisionParameter> collisions105 = obj16.Collisions;
				OsageCollisionParameter osageCollisionParameter105 = new OsageCollisionParameter();
				osageCollisionParameter105.Bone0.Name = "kl_asi_r_wj_co";
				osageCollisionParameter105.Bone0.Position = new Vector3(0f, 0f, 0f);
				osageCollisionParameter105.Bone1.Name = "kl_asi_r_wj_co";
				osageCollisionParameter105.Bone1.Position = new Vector3(0f, 0f, 0f);
				osageCollisionParameter105.Radius = 3f / 32f;
				osageCollisionParameter105.Type = 2u;
				collisions105.Add(osageCollisionParameter105);
				List<OsageCollisionParameter> collisions106 = obj16.Collisions;
				OsageCollisionParameter osageCollisionParameter106 = new OsageCollisionParameter();
				osageCollisionParameter106.Bone0.Name = "kl_asi_l_wj_co";
				osageCollisionParameter106.Bone0.Position = new Vector3(0f, 0f, 0f);
				osageCollisionParameter106.Bone1.Name = "kl_asi_l_wj_co";
				osageCollisionParameter106.Bone1.Position = new Vector3(0f, 0f, 0f);
				osageCollisionParameter106.Radius = 3f / 32f;
				osageCollisionParameter106.Type = 2u;
				collisions106.Add(osageCollisionParameter106);
				obj16.CollisionRadius = 0.01f;
				obj16.CollisionType = 0u;
				obj16.Force = 0.015f;
				obj16.ForceGain = 0.7f;
				obj16.Friction = 1f;
				obj16.HingeY = 180f;
				obj16.HingeZ = 180f;
				obj16.InitRotationY = 0f;
				obj16.InitRotationZ = 0f;
				obj16.MoveCancel = 0f;
				obj16.Nodes.Add(new OsageNodeParameter
				{
					Radius = 0.01f,
					HingeYMax = 180f,
					HingeYMin = -180f,
					HingeZMax = 180f,
					HingeZMin = -180f,
					InertialCancel = 0f,
					Weight = 3f
				});
				obj16.Nodes.Add(new OsageNodeParameter
				{
					Radius = 0.01f,
					HingeYMax = 180f,
					HingeYMin = -180f,
					HingeZMax = 180f,
					HingeZMin = -180f,
					InertialCancel = 0f,
					Weight = 3f
				});
				obj16.Nodes.Add(new OsageNodeParameter
				{
					Radius = 0.01f,
					HingeYMax = 180f,
					HingeYMin = -180f,
					HingeZMax = 180f,
					HingeZMin = -180f,
					InertialCancel = 0f,
					Weight = 3f
				});
				obj16.RotationY = 0f;
				obj16.RotationZ = 0f;
				obj16.Stiffness = 0f;
				obj16.WindAffection = 0.5f;
				dictionary7.Add("j_cloth_long_r_02_000_wj", obj16);
				OsageSkinParameter obj17 = new OsageSkinParameter
				{
					Name = "c_cloth_long_r_03_osg",
					AirResistance = 0.8f,
					Bocs = 
					{
						new OsageBocParameter
						{
							StNode = 0u,
							EdNode = 0u,
							EdRoot = "c_cloth_long_b_osg"
						},
						new OsageBocParameter
						{
							StNode = 1u,
							EdNode = 1u,
							EdRoot = "c_cloth_long_b_osg"
						}
					}
				};
				List<OsageCollisionParameter> collisions107 = obj17.Collisions;
				OsageCollisionParameter osageCollisionParameter107 = new OsageCollisionParameter();
				osageCollisionParameter107.Bone0.Name = "j_momo_l_wj";
				osageCollisionParameter107.Bone0.Position = new Vector3(0.0625f, 0f, -0.00390625f);
				osageCollisionParameter107.Bone1.Name = "j_momo_l_wj";
				osageCollisionParameter107.Bone1.Position = new Vector3(0.367188f, 0f, 0f);
				osageCollisionParameter107.Radius = 3f / 32f;
				osageCollisionParameter107.Type = 2u;
				collisions107.Add(osageCollisionParameter107);
				List<OsageCollisionParameter> collisions108 = obj17.Collisions;
				OsageCollisionParameter osageCollisionParameter108 = new OsageCollisionParameter();
				osageCollisionParameter108.Bone0.Name = "j_momo_r_wj";
				osageCollisionParameter108.Bone0.Position = new Vector3(0.0625f, 0f, 0.00390625f);
				osageCollisionParameter108.Bone1.Name = "j_momo_r_wj";
				osageCollisionParameter108.Bone1.Position = new Vector3(0.367188f, 0f, 0f);
				osageCollisionParameter108.Radius = 3f / 32f;
				osageCollisionParameter108.Type = 2u;
				collisions108.Add(osageCollisionParameter108);
				List<OsageCollisionParameter> collisions109 = obj17.Collisions;
				OsageCollisionParameter osageCollisionParameter109 = new OsageCollisionParameter();
				osageCollisionParameter109.Bone0.Name = "j_sune_l_wj";
				osageCollisionParameter109.Bone0.Position = new Vector3(0f, 0f, 0f);
				osageCollisionParameter109.Bone1.Name = "j_sune_l_wj";
				osageCollisionParameter109.Bone1.Position = new Vector3(0.375f, 0f, 0f);
				osageCollisionParameter109.Radius = 3f / 32f;
				osageCollisionParameter109.Type = 2u;
				collisions109.Add(osageCollisionParameter109);
				List<OsageCollisionParameter> collisions110 = obj17.Collisions;
				OsageCollisionParameter osageCollisionParameter110 = new OsageCollisionParameter();
				osageCollisionParameter110.Bone0.Name = "j_sune_r_wj";
				osageCollisionParameter110.Bone0.Position = new Vector3(0f, 0f, 0f);
				osageCollisionParameter110.Bone1.Name = "j_sune_r_wj";
				osageCollisionParameter110.Bone1.Position = new Vector3(0.375f, 0f, 0f);
				osageCollisionParameter110.Radius = 3f / 32f;
				osageCollisionParameter110.Type = 2u;
				collisions110.Add(osageCollisionParameter110);
				List<OsageCollisionParameter> collisions111 = obj17.Collisions;
				OsageCollisionParameter osageCollisionParameter111 = new OsageCollisionParameter();
				osageCollisionParameter111.Bone0.Name = "kl_asi_r_wj_co";
				osageCollisionParameter111.Bone0.Position = new Vector3(0f, 0f, 0f);
				osageCollisionParameter111.Bone1.Name = "kl_asi_r_wj_co";
				osageCollisionParameter111.Bone1.Position = new Vector3(0f, 0f, 0f);
				osageCollisionParameter111.Radius = 3f / 32f;
				osageCollisionParameter111.Type = 2u;
				collisions111.Add(osageCollisionParameter111);
				List<OsageCollisionParameter> collisions112 = obj17.Collisions;
				OsageCollisionParameter osageCollisionParameter112 = new OsageCollisionParameter();
				osageCollisionParameter112.Bone0.Name = "kl_asi_l_wj_co";
				osageCollisionParameter112.Bone0.Position = new Vector3(0f, 0f, 0f);
				osageCollisionParameter112.Bone1.Name = "kl_asi_l_wj_co";
				osageCollisionParameter112.Bone1.Position = new Vector3(0f, 0f, 0f);
				osageCollisionParameter112.Radius = 3f / 32f;
				osageCollisionParameter112.Type = 2u;
				collisions112.Add(osageCollisionParameter112);
				obj17.CollisionRadius = 0.01f;
				obj17.CollisionType = 0u;
				obj17.Force = 0.015f;
				obj17.ForceGain = 0.7f;
				obj17.Friction = 1f;
				obj17.HingeY = 180f;
				obj17.HingeZ = 180f;
				obj17.InitRotationY = 0f;
				obj17.InitRotationZ = 0f;
				obj17.MoveCancel = 0f;
				obj17.Nodes.Add(new OsageNodeParameter
				{
					Radius = 0.01f,
					HingeYMax = 180f,
					HingeYMin = -180f,
					HingeZMax = 180f,
					HingeZMin = -180f,
					InertialCancel = 0f,
					Weight = 3f
				});
				obj17.Nodes.Add(new OsageNodeParameter
				{
					Radius = 0.01f,
					HingeYMax = 180f,
					HingeYMin = -180f,
					HingeZMax = 180f,
					HingeZMin = -180f,
					InertialCancel = 0f,
					Weight = 3f
				});
				obj17.Nodes.Add(new OsageNodeParameter
				{
					Radius = 0.01f,
					HingeYMax = 180f,
					HingeYMin = -180f,
					HingeZMax = 180f,
					HingeZMin = -180f,
					InertialCancel = 0f,
					Weight = 3f
				});
				obj17.RotationY = 0f;
				obj17.RotationZ = 0f;
				obj17.Stiffness = 0f;
				obj17.WindAffection = 0.5f;
				dictionary7.Add("j_cloth_long_r_03_000_wj", obj17);
				OsageSkinParameter obj18 = new OsageSkinParameter
				{
					Name = "c_hair_twin_l_osg",
					AirResistance = 0.1f
				};
				List<OsageCollisionParameter> collisions113 = obj18.Collisions;
				OsageCollisionParameter osageCollisionParameter113 = new OsageCollisionParameter();
				osageCollisionParameter113.Bone0.Name = "kl_mune_b_wj";
				osageCollisionParameter113.Bone0.Position = new Vector3(0f, 0.097656f, 0f);
				osageCollisionParameter113.Bone1.Name = "n_hara_cp";
				osageCollisionParameter113.Bone1.Position = new Vector3(0f, -0.0625f, 0f);
				osageCollisionParameter113.Radius = 0.113281f;
				osageCollisionParameter113.Type = 2u;
				collisions113.Add(osageCollisionParameter113);
				List<OsageCollisionParameter> collisions114 = obj18.Collisions;
				OsageCollisionParameter osageCollisionParameter114 = new OsageCollisionParameter();
				osageCollisionParameter114.Bone0.Name = "j_kata_l_wj_cu";
				osageCollisionParameter114.Bone0.Position = new Vector3(0.1875f, 0f, 0f);
				osageCollisionParameter114.Bone1.Name = "j_kata_l_wj_cu";
				osageCollisionParameter114.Bone1.Position = new Vector3(0f, 0f, 0f);
				osageCollisionParameter114.Radius = 0.039063f;
				osageCollisionParameter114.Type = 2u;
				collisions114.Add(osageCollisionParameter114);
				List<OsageCollisionParameter> collisions115 = obj18.Collisions;
				OsageCollisionParameter osageCollisionParameter115 = new OsageCollisionParameter();
				osageCollisionParameter115.Bone0.Name = "j_kata_r_wj_cu";
				osageCollisionParameter115.Bone0.Position = new Vector3(0.1875f, 0f, 0f);
				osageCollisionParameter115.Bone1.Name = "j_kata_r_wj_cu";
				osageCollisionParameter115.Bone1.Position = new Vector3(0f, 0f, 0f);
				osageCollisionParameter115.Radius = 0.039063f;
				osageCollisionParameter115.Type = 2u;
				collisions115.Add(osageCollisionParameter115);
				List<OsageCollisionParameter> collisions116 = obj18.Collisions;
				OsageCollisionParameter osageCollisionParameter116 = new OsageCollisionParameter();
				osageCollisionParameter116.Bone0.Name = "j_ude_l_wj";
				osageCollisionParameter116.Bone0.Position = new Vector3(0.007813f, 0f, 0f);
				osageCollisionParameter116.Bone1.Name = "j_ude_l_wj";
				osageCollisionParameter116.Bone1.Position = new Vector3(0.113281f, 0f, 0f);
				osageCollisionParameter116.Radius = 0.042969f;
				osageCollisionParameter116.Type = 2u;
				collisions116.Add(osageCollisionParameter116);
				List<OsageCollisionParameter> collisions117 = obj18.Collisions;
				OsageCollisionParameter osageCollisionParameter117 = new OsageCollisionParameter();
				osageCollisionParameter117.Bone0.Name = "j_ude_r_wj";
				osageCollisionParameter117.Bone0.Position = new Vector3(0.007813f, 0f, 0f);
				osageCollisionParameter117.Bone1.Name = "j_ude_r_wj";
				osageCollisionParameter117.Bone1.Position = new Vector3(0.113281f, 0f, 0f);
				osageCollisionParameter117.Radius = 0.042969f;
				osageCollisionParameter117.Type = 2u;
				collisions117.Add(osageCollisionParameter117);
				List<OsageCollisionParameter> collisions118 = obj18.Collisions;
				OsageCollisionParameter osageCollisionParameter118 = new OsageCollisionParameter();
				osageCollisionParameter118.Bone0.Name = "kl_kosi_etc_wj";
				osageCollisionParameter118.Bone0.Position = new Vector3(0.027344f, -13f / 64f, 3f / 64f);
				osageCollisionParameter118.Bone1.Name = "kl_kosi_etc_wj";
				osageCollisionParameter118.Bone1.Position = new Vector3(0.023438f, -13f / 64f, -3f / 64f);
				osageCollisionParameter118.Radius = 5f / 32f;
				osageCollisionParameter118.Type = 2u;
				collisions118.Add(osageCollisionParameter118);
				List<OsageCollisionParameter> collisions119 = obj18.Collisions;
				OsageCollisionParameter osageCollisionParameter119 = new OsageCollisionParameter();
				osageCollisionParameter119.Bone0.Name = "j_kao_wj";
				osageCollisionParameter119.Bone0.Position = new Vector3(0f, 0f, 0.074219f);
				osageCollisionParameter119.Bone1.Name = "j_kao_wj";
				osageCollisionParameter119.Bone1.Position = new Vector3(0.058594f, 0f, 0.074219f);
				osageCollisionParameter119.Radius = 0.113281f;
				osageCollisionParameter119.Type = 2u;
				collisions119.Add(osageCollisionParameter119);
				List<OsageCollisionParameter> collisions120 = obj18.Collisions;
				OsageCollisionParameter osageCollisionParameter120 = new OsageCollisionParameter();
				osageCollisionParameter120.Bone0.Name = "j_momo_l_wj";
				osageCollisionParameter120.Bone0.Position = new Vector3(0f, 0f, 0f);
				osageCollisionParameter120.Bone1.Name = "j_momo_l_wj";
				osageCollisionParameter120.Bone1.Position = new Vector3(0.347656f, 0f, 0f);
				osageCollisionParameter120.Radius = 0.085938f;
				osageCollisionParameter120.Type = 2u;
				collisions120.Add(osageCollisionParameter120);
				List<OsageCollisionParameter> collisions121 = obj18.Collisions;
				OsageCollisionParameter osageCollisionParameter121 = new OsageCollisionParameter();
				osageCollisionParameter121.Bone0.Name = "j_momo_r_wj";
				osageCollisionParameter121.Bone0.Position = new Vector3(0f, 0f, 0f);
				osageCollisionParameter121.Bone1.Name = "j_momo_r_wj";
				osageCollisionParameter121.Bone1.Position = new Vector3(0.347656f, 0f, 0f);
				osageCollisionParameter121.Radius = 0.085938f;
				osageCollisionParameter121.Type = 2u;
				collisions121.Add(osageCollisionParameter121);
				List<OsageCollisionParameter> collisions122 = obj18.Collisions;
				OsageCollisionParameter osageCollisionParameter122 = new OsageCollisionParameter();
				osageCollisionParameter122.Bone0.Name = "j_sune_l_wj";
				osageCollisionParameter122.Bone0.Position = new Vector3(0f, 0f, 0f);
				osageCollisionParameter122.Bone1.Name = "j_sune_l_wj";
				osageCollisionParameter122.Bone1.Position = new Vector3(0.457031f, 0f, 0f);
				osageCollisionParameter122.Radius = 0.085938f;
				osageCollisionParameter122.Type = 2u;
				collisions122.Add(osageCollisionParameter122);
				List<OsageCollisionParameter> collisions123 = obj18.Collisions;
				OsageCollisionParameter osageCollisionParameter123 = new OsageCollisionParameter();
				osageCollisionParameter123.Bone0.Name = "j_sune_r_wj";
				osageCollisionParameter123.Bone0.Position = new Vector3(0f, 0f, 0f);
				osageCollisionParameter123.Bone1.Name = "j_sune_r_wj";
				osageCollisionParameter123.Bone1.Position = new Vector3(0.457031f, 0f, 0f);
				osageCollisionParameter123.Radius = 0.085938f;
				osageCollisionParameter123.Type = 2u;
				collisions123.Add(osageCollisionParameter123);
				obj18.CollisionRadius = 0f;
				obj18.CollisionType = 1u;
				obj18.Force = 0.01f;
				obj18.ForceGain = 0.5f;
				obj18.Friction = 1f;
				obj18.HingeY = 90f;
				obj18.HingeZ = 90f;
				obj18.InitRotationY = 0f;
				obj18.InitRotationZ = 0f;
				obj18.MoveCancel = 0f;
				obj18.Nodes.Add(new OsageNodeParameter
				{
					Radius = 0.04f,
					HingeYMax = 180f,
					HingeYMin = -180f,
					HingeZMax = 180f,
					HingeZMin = -180f,
					InertialCancel = 0.5f,
					Weight = 12f
				});
				obj18.Nodes.Add(new OsageNodeParameter
				{
					Radius = 0.04f,
					HingeYMax = 180f,
					HingeYMin = -180f,
					HingeZMax = 180f,
					HingeZMin = -180f,
					InertialCancel = 0.5f,
					Weight = 15f
				});
				obj18.Nodes.Add(new OsageNodeParameter
				{
					Radius = 0.04f,
					HingeYMax = 180f,
					HingeYMin = -180f,
					HingeZMax = 180f,
					HingeZMin = -180f,
					InertialCancel = 0.5f,
					Weight = 18f
				});
				obj18.Nodes.Add(new OsageNodeParameter
				{
					Radius = 0.04f,
					HingeYMax = 180f,
					HingeYMin = -180f,
					HingeZMax = 180f,
					HingeZMin = -180f,
					InertialCancel = 0.5f,
					Weight = 21f
				});
				obj18.Nodes.Add(new OsageNodeParameter
				{
					Radius = 0.04f,
					HingeYMax = 180f,
					HingeYMin = -180f,
					HingeZMax = 180f,
					HingeZMin = -180f,
					InertialCancel = 0.5f,
					Weight = 24f
				});
				obj18.Nodes.Add(new OsageNodeParameter
				{
					Radius = 0.04f,
					HingeYMax = 180f,
					HingeYMin = -180f,
					HingeZMax = 180f,
					HingeZMin = -180f,
					InertialCancel = 0.5f,
					Weight = 21f
				});
				obj18.RotationY = -30f;
				obj18.RotationZ = -34f;
				obj18.Stiffness = 0f;
				obj18.WindAffection = 2f;
				dictionary7.Add("j_hair_twin_l_000_wj", obj18);
				OsageSkinParameter obj19 = new OsageSkinParameter
				{
					Name = "c_hair_twin_r_osg",
					AirResistance = 0.1f
				};
				List<OsageCollisionParameter> collisions124 = obj19.Collisions;
				OsageCollisionParameter osageCollisionParameter124 = new OsageCollisionParameter();
				osageCollisionParameter124.Bone0.Name = "kl_mune_b_wj";
				osageCollisionParameter124.Bone0.Position = new Vector3(0f, 0.097656f, 0f);
				osageCollisionParameter124.Bone1.Name = "n_hara_cp";
				osageCollisionParameter124.Bone1.Position = new Vector3(0f, -0.0625f, 0f);
				osageCollisionParameter124.Radius = 0.113281f;
				osageCollisionParameter124.Type = 2u;
				collisions124.Add(osageCollisionParameter124);
				List<OsageCollisionParameter> collisions125 = obj19.Collisions;
				OsageCollisionParameter osageCollisionParameter125 = new OsageCollisionParameter();
				osageCollisionParameter125.Bone0.Name = "j_kata_l_wj_cu";
				osageCollisionParameter125.Bone0.Position = new Vector3(0.1875f, 0f, 0f);
				osageCollisionParameter125.Bone1.Name = "j_kata_l_wj_cu";
				osageCollisionParameter125.Bone1.Position = new Vector3(0f, 0f, 0f);
				osageCollisionParameter125.Radius = 0.039063f;
				osageCollisionParameter125.Type = 2u;
				collisions125.Add(osageCollisionParameter125);
				List<OsageCollisionParameter> collisions126 = obj19.Collisions;
				OsageCollisionParameter osageCollisionParameter126 = new OsageCollisionParameter();
				osageCollisionParameter126.Bone0.Name = "j_kata_r_wj_cu";
				osageCollisionParameter126.Bone0.Position = new Vector3(0.1875f, 0f, 0f);
				osageCollisionParameter126.Bone1.Name = "j_kata_r_wj_cu";
				osageCollisionParameter126.Bone1.Position = new Vector3(0f, 0f, 0f);
				osageCollisionParameter126.Radius = 0.039063f;
				osageCollisionParameter126.Type = 2u;
				collisions126.Add(osageCollisionParameter126);
				List<OsageCollisionParameter> collisions127 = obj19.Collisions;
				OsageCollisionParameter osageCollisionParameter127 = new OsageCollisionParameter();
				osageCollisionParameter127.Bone0.Name = "j_ude_l_wj";
				osageCollisionParameter127.Bone0.Position = new Vector3(0.007813f, 0f, 0f);
				osageCollisionParameter127.Bone1.Name = "j_ude_l_wj";
				osageCollisionParameter127.Bone1.Position = new Vector3(0.113281f, 0f, 0f);
				osageCollisionParameter127.Radius = 0.042969f;
				osageCollisionParameter127.Type = 2u;
				collisions127.Add(osageCollisionParameter127);
				List<OsageCollisionParameter> collisions128 = obj19.Collisions;
				OsageCollisionParameter osageCollisionParameter128 = new OsageCollisionParameter();
				osageCollisionParameter128.Bone0.Name = "j_ude_r_wj";
				osageCollisionParameter128.Bone0.Position = new Vector3(0.007813f, 0f, 0f);
				osageCollisionParameter128.Bone1.Name = "j_ude_r_wj";
				osageCollisionParameter128.Bone1.Position = new Vector3(0.113281f, 0f, 0f);
				osageCollisionParameter128.Radius = 0.042969f;
				osageCollisionParameter128.Type = 2u;
				collisions128.Add(osageCollisionParameter128);
				List<OsageCollisionParameter> collisions129 = obj19.Collisions;
				OsageCollisionParameter osageCollisionParameter129 = new OsageCollisionParameter();
				osageCollisionParameter129.Bone0.Name = "kl_kosi_etc_wj";
				osageCollisionParameter129.Bone0.Position = new Vector3(0.027344f, -13f / 64f, 3f / 64f);
				osageCollisionParameter129.Bone1.Name = "kl_kosi_etc_wj";
				osageCollisionParameter129.Bone1.Position = new Vector3(0.023438f, -13f / 64f, -3f / 64f);
				osageCollisionParameter129.Radius = 5f / 32f;
				osageCollisionParameter129.Type = 2u;
				collisions129.Add(osageCollisionParameter129);
				List<OsageCollisionParameter> collisions130 = obj19.Collisions;
				OsageCollisionParameter osageCollisionParameter130 = new OsageCollisionParameter();
				osageCollisionParameter130.Bone0.Name = "j_kao_wj";
				osageCollisionParameter130.Bone0.Position = new Vector3(0f, 0f, 0.074219f);
				osageCollisionParameter130.Bone1.Name = "j_kao_wj";
				osageCollisionParameter130.Bone1.Position = new Vector3(0.058594f, 0f, 0.074219f);
				osageCollisionParameter130.Radius = 0.113281f;
				osageCollisionParameter130.Type = 2u;
				collisions130.Add(osageCollisionParameter130);
				List<OsageCollisionParameter> collisions131 = obj19.Collisions;
				OsageCollisionParameter osageCollisionParameter131 = new OsageCollisionParameter();
				osageCollisionParameter131.Bone0.Name = "j_momo_l_wj";
				osageCollisionParameter131.Bone0.Position = new Vector3(0f, 0f, 0f);
				osageCollisionParameter131.Bone1.Name = "j_momo_l_wj";
				osageCollisionParameter131.Bone1.Position = new Vector3(0.347656f, 0f, 0f);
				osageCollisionParameter131.Radius = 0.085938f;
				osageCollisionParameter131.Type = 2u;
				collisions131.Add(osageCollisionParameter131);
				List<OsageCollisionParameter> collisions132 = obj19.Collisions;
				OsageCollisionParameter osageCollisionParameter132 = new OsageCollisionParameter();
				osageCollisionParameter132.Bone0.Name = "j_momo_r_wj";
				osageCollisionParameter132.Bone0.Position = new Vector3(0f, 0f, 0f);
				osageCollisionParameter132.Bone1.Name = "j_momo_r_wj";
				osageCollisionParameter132.Bone1.Position = new Vector3(0.347656f, 0f, 0f);
				osageCollisionParameter132.Radius = 0.085938f;
				osageCollisionParameter132.Type = 2u;
				collisions132.Add(osageCollisionParameter132);
				List<OsageCollisionParameter> collisions133 = obj19.Collisions;
				OsageCollisionParameter osageCollisionParameter133 = new OsageCollisionParameter();
				osageCollisionParameter133.Bone0.Name = "j_sune_l_wj";
				osageCollisionParameter133.Bone0.Position = new Vector3(0f, 0f, 0f);
				osageCollisionParameter133.Bone1.Name = "j_sune_l_wj";
				osageCollisionParameter133.Bone1.Position = new Vector3(0.457031f, 0f, 0f);
				osageCollisionParameter133.Radius = 0.085938f;
				osageCollisionParameter133.Type = 2u;
				collisions133.Add(osageCollisionParameter133);
				List<OsageCollisionParameter> collisions134 = obj19.Collisions;
				OsageCollisionParameter osageCollisionParameter134 = new OsageCollisionParameter();
				osageCollisionParameter134.Bone0.Name = "j_sune_r_wj";
				osageCollisionParameter134.Bone0.Position = new Vector3(0f, 0f, 0f);
				osageCollisionParameter134.Bone1.Name = "j_sune_r_wj";
				osageCollisionParameter134.Bone1.Position = new Vector3(0.457031f, 0f, 0f);
				osageCollisionParameter134.Radius = 0.085938f;
				osageCollisionParameter134.Type = 2u;
				collisions134.Add(osageCollisionParameter134);
				obj19.CollisionRadius = 0f;
				obj19.CollisionType = 1u;
				obj19.Force = 0.01f;
				obj19.ForceGain = 0.5f;
				obj19.Friction = 1f;
				obj19.HingeY = 90f;
				obj19.HingeZ = 90f;
				obj19.InitRotationY = 0f;
				obj19.InitRotationZ = 0f;
				obj19.MoveCancel = 0f;
				obj19.Nodes.Add(new OsageNodeParameter
				{
					Radius = 0.04f,
					HingeYMax = 180f,
					HingeYMin = -180f,
					HingeZMax = 180f,
					HingeZMin = -180f,
					InertialCancel = 0.5f,
					Weight = 12f
				});
				obj19.Nodes.Add(new OsageNodeParameter
				{
					Radius = 0.04f,
					HingeYMax = 180f,
					HingeYMin = -180f,
					HingeZMax = 180f,
					HingeZMin = -180f,
					InertialCancel = 0.5f,
					Weight = 15f
				});
				obj19.Nodes.Add(new OsageNodeParameter
				{
					Radius = 0.04f,
					HingeYMax = 180f,
					HingeYMin = -180f,
					HingeZMax = 180f,
					HingeZMin = -180f,
					InertialCancel = 0.5f,
					Weight = 18f
				});
				obj19.Nodes.Add(new OsageNodeParameter
				{
					Radius = 0.04f,
					HingeYMax = 180f,
					HingeYMin = -180f,
					HingeZMax = 180f,
					HingeZMin = -180f,
					InertialCancel = 0.5f,
					Weight = 21f
				});
				obj19.Nodes.Add(new OsageNodeParameter
				{
					Radius = 0.04f,
					HingeYMax = 180f,
					HingeYMin = -180f,
					HingeZMax = 180f,
					HingeZMin = -180f,
					InertialCancel = 0.5f,
					Weight = 24f
				});
				obj19.Nodes.Add(new OsageNodeParameter
				{
					Radius = 0.04f,
					HingeYMax = 180f,
					HingeYMin = -180f,
					HingeZMax = 180f,
					HingeZMin = -180f,
					InertialCancel = 0.5f,
					Weight = 21f
				});
				obj19.RotationY = 30f;
				obj19.RotationZ = -34f;
				obj19.Stiffness = 0f;
				obj19.WindAffection = 2f;
				dictionary7.Add("j_hair_twin_r_000_wj", obj19);
				OsageSkinParameter obj20 = new OsageSkinParameter
				{
					Name = "c_hair_long_c_osg",
					AirResistance = 0.8f
				};
				List<OsageCollisionParameter> collisions135 = obj20.Collisions;
				OsageCollisionParameter osageCollisionParameter135 = new OsageCollisionParameter();
				osageCollisionParameter135.Bone0.Name = "kl_mune_b_wj";
				osageCollisionParameter135.Bone0.Position = new Vector3(0f, 0.097656f, 0f);
				osageCollisionParameter135.Bone1.Name = "n_hara_cp";
				osageCollisionParameter135.Bone1.Position = new Vector3(0f, -0.0625f, 0f);
				osageCollisionParameter135.Radius = 0.113281f;
				osageCollisionParameter135.Type = 2u;
				collisions135.Add(osageCollisionParameter135);
				List<OsageCollisionParameter> collisions136 = obj20.Collisions;
				OsageCollisionParameter osageCollisionParameter136 = new OsageCollisionParameter();
				osageCollisionParameter136.Bone0.Name = "j_kata_l_wj_cu";
				osageCollisionParameter136.Bone0.Position = new Vector3(0.1875f, 0f, 0f);
				osageCollisionParameter136.Bone1.Name = "j_kata_l_wj_cu";
				osageCollisionParameter136.Bone1.Position = new Vector3(0f, 0f, 0f);
				osageCollisionParameter136.Radius = 0.039063f;
				osageCollisionParameter136.Type = 2u;
				collisions136.Add(osageCollisionParameter136);
				List<OsageCollisionParameter> collisions137 = obj20.Collisions;
				OsageCollisionParameter osageCollisionParameter137 = new OsageCollisionParameter();
				osageCollisionParameter137.Bone0.Name = "j_kata_r_wj_cu";
				osageCollisionParameter137.Bone0.Position = new Vector3(0.1875f, 0f, 0f);
				osageCollisionParameter137.Bone1.Name = "j_kata_r_wj_cu";
				osageCollisionParameter137.Bone1.Position = new Vector3(0f, 0f, 0f);
				osageCollisionParameter137.Radius = 0.039063f;
				osageCollisionParameter137.Type = 2u;
				collisions137.Add(osageCollisionParameter137);
				List<OsageCollisionParameter> collisions138 = obj20.Collisions;
				OsageCollisionParameter osageCollisionParameter138 = new OsageCollisionParameter();
				osageCollisionParameter138.Bone0.Name = "j_ude_l_wj";
				osageCollisionParameter138.Bone0.Position = new Vector3(0.007813f, 0f, 0f);
				osageCollisionParameter138.Bone1.Name = "j_ude_l_wj";
				osageCollisionParameter138.Bone1.Position = new Vector3(0.113281f, 0f, 0f);
				osageCollisionParameter138.Radius = 0.042969f;
				osageCollisionParameter138.Type = 2u;
				collisions138.Add(osageCollisionParameter138);
				List<OsageCollisionParameter> collisions139 = obj20.Collisions;
				OsageCollisionParameter osageCollisionParameter139 = new OsageCollisionParameter();
				osageCollisionParameter139.Bone0.Name = "j_ude_r_wj";
				osageCollisionParameter139.Bone0.Position = new Vector3(0.007813f, 0f, 0f);
				osageCollisionParameter139.Bone1.Name = "j_ude_r_wj";
				osageCollisionParameter139.Bone1.Position = new Vector3(0.113281f, 0f, 0f);
				osageCollisionParameter139.Radius = 0.042969f;
				osageCollisionParameter139.Type = 2u;
				collisions139.Add(osageCollisionParameter139);
				List<OsageCollisionParameter> collisions140 = obj20.Collisions;
				OsageCollisionParameter osageCollisionParameter140 = new OsageCollisionParameter();
				osageCollisionParameter140.Bone0.Name = "kl_kosi_etc_wj";
				osageCollisionParameter140.Bone0.Position = new Vector3(0.027344f, -13f / 64f, 3f / 64f);
				osageCollisionParameter140.Bone1.Name = "kl_kosi_etc_wj";
				osageCollisionParameter140.Bone1.Position = new Vector3(0.023438f, -13f / 64f, -3f / 64f);
				osageCollisionParameter140.Radius = 5f / 32f;
				osageCollisionParameter140.Type = 2u;
				collisions140.Add(osageCollisionParameter140);
				List<OsageCollisionParameter> collisions141 = obj20.Collisions;
				OsageCollisionParameter osageCollisionParameter141 = new OsageCollisionParameter();
				osageCollisionParameter141.Bone0.Name = "j_kao_wj";
				osageCollisionParameter141.Bone0.Position = new Vector3(0f, 0f, 0.074219f);
				osageCollisionParameter141.Bone1.Name = "j_kao_wj";
				osageCollisionParameter141.Bone1.Position = new Vector3(0.058594f, 0f, 0.074219f);
				osageCollisionParameter141.Radius = 0.113281f;
				osageCollisionParameter141.Type = 2u;
				collisions141.Add(osageCollisionParameter141);
				List<OsageCollisionParameter> collisions142 = obj20.Collisions;
				OsageCollisionParameter osageCollisionParameter142 = new OsageCollisionParameter();
				osageCollisionParameter142.Bone0.Name = "j_momo_l_wj";
				osageCollisionParameter142.Bone0.Position = new Vector3(0f, 0f, 0f);
				osageCollisionParameter142.Bone1.Name = "j_momo_l_wj";
				osageCollisionParameter142.Bone1.Position = new Vector3(0.347656f, 0f, 0f);
				osageCollisionParameter142.Radius = 0.085938f;
				osageCollisionParameter142.Type = 2u;
				collisions142.Add(osageCollisionParameter142);
				List<OsageCollisionParameter> collisions143 = obj20.Collisions;
				OsageCollisionParameter osageCollisionParameter143 = new OsageCollisionParameter();
				osageCollisionParameter143.Bone0.Name = "j_momo_r_wj";
				osageCollisionParameter143.Bone0.Position = new Vector3(0f, 0f, 0f);
				osageCollisionParameter143.Bone1.Name = "j_momo_r_wj";
				osageCollisionParameter143.Bone1.Position = new Vector3(0.347656f, 0f, 0f);
				osageCollisionParameter143.Radius = 0.085938f;
				osageCollisionParameter143.Type = 2u;
				collisions143.Add(osageCollisionParameter143);
				List<OsageCollisionParameter> collisions144 = obj20.Collisions;
				OsageCollisionParameter osageCollisionParameter144 = new OsageCollisionParameter();
				osageCollisionParameter144.Bone0.Name = "j_sune_l_wj";
				osageCollisionParameter144.Bone0.Position = new Vector3(0f, 0f, 0f);
				osageCollisionParameter144.Bone1.Name = "j_sune_l_wj";
				osageCollisionParameter144.Bone1.Position = new Vector3(0.457031f, 0f, 0f);
				osageCollisionParameter144.Radius = 0.085938f;
				osageCollisionParameter144.Type = 2u;
				collisions144.Add(osageCollisionParameter144);
				List<OsageCollisionParameter> collisions145 = obj20.Collisions;
				OsageCollisionParameter osageCollisionParameter145 = new OsageCollisionParameter();
				osageCollisionParameter145.Bone0.Name = "j_sune_r_wj";
				osageCollisionParameter145.Bone0.Position = new Vector3(0f, 0f, 0f);
				osageCollisionParameter145.Bone1.Name = "j_sune_r_wj";
				osageCollisionParameter145.Bone1.Position = new Vector3(0.457031f, 0f, 0f);
				osageCollisionParameter145.Radius = 0.085938f;
				osageCollisionParameter145.Type = 2u;
				collisions145.Add(osageCollisionParameter145);
				obj20.CollisionRadius = 0f;
				obj20.CollisionType = 1u;
				obj20.Force = 0.01f;
				obj20.ForceGain = 0.6f;
				obj20.Friction = 1f;
				obj20.HingeY = 90f;
				obj20.HingeZ = 90f;
				obj20.InitRotationY = 0f;
				obj20.InitRotationZ = 0f;
				obj20.MoveCancel = 0f;
				obj20.Nodes.Add(new OsageNodeParameter
				{
					Radius = 0.04f,
					HingeYMax = 180f,
					HingeYMin = -180f,
					HingeZMax = 180f,
					HingeZMin = -180f,
					InertialCancel = 0f,
					Weight = 3f
				});
				obj20.Nodes.Add(new OsageNodeParameter
				{
					Radius = 0.04f,
					HingeYMax = 180f,
					HingeYMin = -180f,
					HingeZMax = 180f,
					HingeZMin = -180f,
					InertialCancel = 0f,
					Weight = 3f
				});
				obj20.Nodes.Add(new OsageNodeParameter
				{
					Radius = 0.04f,
					HingeYMax = 180f,
					HingeYMin = -180f,
					HingeZMax = 180f,
					HingeZMin = -180f,
					InertialCancel = 0f,
					Weight = 3f
				});
				obj20.Nodes.Add(new OsageNodeParameter
				{
					Radius = 0.04f,
					HingeYMax = 180f,
					HingeYMin = -180f,
					HingeZMax = 180f,
					HingeZMin = -180f,
					InertialCancel = 0f,
					Weight = 3f
				});
				obj20.Nodes.Add(new OsageNodeParameter
				{
					Radius = 0.04f,
					HingeYMax = 180f,
					HingeYMin = -180f,
					HingeZMax = 180f,
					HingeZMin = -180f,
					InertialCancel = 0f,
					Weight = 3f
				});
				obj20.RotationY = 0f;
				obj20.RotationZ = -5f;
				obj20.Stiffness = 0f;
				obj20.WindAffection = 0.5f;
				dictionary7.Add("j_hair_long_c_000_wj", obj20);
				OsageSkinParameter obj21 = new OsageSkinParameter
				{
					Name = "c_hair_long_l_01_osg",
					AirResistance = 0.8f
				};
				List<OsageCollisionParameter> collisions146 = obj21.Collisions;
				OsageCollisionParameter osageCollisionParameter146 = new OsageCollisionParameter();
				osageCollisionParameter146.Bone0.Name = "kl_mune_b_wj";
				osageCollisionParameter146.Bone0.Position = new Vector3(0f, 0.097656f, 0f);
				osageCollisionParameter146.Bone1.Name = "n_hara_cp";
				osageCollisionParameter146.Bone1.Position = new Vector3(0f, -0.0625f, 0f);
				osageCollisionParameter146.Radius = 0.113281f;
				osageCollisionParameter146.Type = 2u;
				collisions146.Add(osageCollisionParameter146);
				List<OsageCollisionParameter> collisions147 = obj21.Collisions;
				OsageCollisionParameter osageCollisionParameter147 = new OsageCollisionParameter();
				osageCollisionParameter147.Bone0.Name = "j_kata_l_wj_cu";
				osageCollisionParameter147.Bone0.Position = new Vector3(0.1875f, 0f, 0f);
				osageCollisionParameter147.Bone1.Name = "j_kata_l_wj_cu";
				osageCollisionParameter147.Bone1.Position = new Vector3(0f, 0f, 0f);
				osageCollisionParameter147.Radius = 0.039063f;
				osageCollisionParameter147.Type = 2u;
				collisions147.Add(osageCollisionParameter147);
				List<OsageCollisionParameter> collisions148 = obj21.Collisions;
				OsageCollisionParameter osageCollisionParameter148 = new OsageCollisionParameter();
				osageCollisionParameter148.Bone0.Name = "j_kata_r_wj_cu";
				osageCollisionParameter148.Bone0.Position = new Vector3(0.1875f, 0f, 0f);
				osageCollisionParameter148.Bone1.Name = "j_kata_r_wj_cu";
				osageCollisionParameter148.Bone1.Position = new Vector3(0f, 0f, 0f);
				osageCollisionParameter148.Radius = 0.039063f;
				osageCollisionParameter148.Type = 2u;
				collisions148.Add(osageCollisionParameter148);
				List<OsageCollisionParameter> collisions149 = obj21.Collisions;
				OsageCollisionParameter osageCollisionParameter149 = new OsageCollisionParameter();
				osageCollisionParameter149.Bone0.Name = "j_ude_l_wj";
				osageCollisionParameter149.Bone0.Position = new Vector3(0.007813f, 0f, 0f);
				osageCollisionParameter149.Bone1.Name = "j_ude_l_wj";
				osageCollisionParameter149.Bone1.Position = new Vector3(0.113281f, 0f, 0f);
				osageCollisionParameter149.Radius = 0.042969f;
				osageCollisionParameter149.Type = 2u;
				collisions149.Add(osageCollisionParameter149);
				List<OsageCollisionParameter> collisions150 = obj21.Collisions;
				OsageCollisionParameter osageCollisionParameter150 = new OsageCollisionParameter();
				osageCollisionParameter150.Bone0.Name = "j_ude_r_wj";
				osageCollisionParameter150.Bone0.Position = new Vector3(0.007813f, 0f, 0f);
				osageCollisionParameter150.Bone1.Name = "j_ude_r_wj";
				osageCollisionParameter150.Bone1.Position = new Vector3(0.113281f, 0f, 0f);
				osageCollisionParameter150.Radius = 0.042969f;
				osageCollisionParameter150.Type = 2u;
				collisions150.Add(osageCollisionParameter150);
				List<OsageCollisionParameter> collisions151 = obj21.Collisions;
				OsageCollisionParameter osageCollisionParameter151 = new OsageCollisionParameter();
				osageCollisionParameter151.Bone0.Name = "kl_kosi_etc_wj";
				osageCollisionParameter151.Bone0.Position = new Vector3(0.027344f, -13f / 64f, 3f / 64f);
				osageCollisionParameter151.Bone1.Name = "kl_kosi_etc_wj";
				osageCollisionParameter151.Bone1.Position = new Vector3(0.023438f, -13f / 64f, -3f / 64f);
				osageCollisionParameter151.Radius = 5f / 32f;
				osageCollisionParameter151.Type = 2u;
				collisions151.Add(osageCollisionParameter151);
				List<OsageCollisionParameter> collisions152 = obj21.Collisions;
				OsageCollisionParameter osageCollisionParameter152 = new OsageCollisionParameter();
				osageCollisionParameter152.Bone0.Name = "j_kao_wj";
				osageCollisionParameter152.Bone0.Position = new Vector3(0f, 0f, 0.074219f);
				osageCollisionParameter152.Bone1.Name = "j_kao_wj";
				osageCollisionParameter152.Bone1.Position = new Vector3(0.058594f, 0f, 0.074219f);
				osageCollisionParameter152.Radius = 0.113281f;
				osageCollisionParameter152.Type = 2u;
				collisions152.Add(osageCollisionParameter152);
				List<OsageCollisionParameter> collisions153 = obj21.Collisions;
				OsageCollisionParameter osageCollisionParameter153 = new OsageCollisionParameter();
				osageCollisionParameter153.Bone0.Name = "j_momo_l_wj";
				osageCollisionParameter153.Bone0.Position = new Vector3(0f, 0f, 0f);
				osageCollisionParameter153.Bone1.Name = "j_momo_l_wj";
				osageCollisionParameter153.Bone1.Position = new Vector3(0.347656f, 0f, 0f);
				osageCollisionParameter153.Radius = 0.085938f;
				osageCollisionParameter153.Type = 2u;
				collisions153.Add(osageCollisionParameter153);
				List<OsageCollisionParameter> collisions154 = obj21.Collisions;
				OsageCollisionParameter osageCollisionParameter154 = new OsageCollisionParameter();
				osageCollisionParameter154.Bone0.Name = "j_momo_r_wj";
				osageCollisionParameter154.Bone0.Position = new Vector3(0f, 0f, 0f);
				osageCollisionParameter154.Bone1.Name = "j_momo_r_wj";
				osageCollisionParameter154.Bone1.Position = new Vector3(0.347656f, 0f, 0f);
				osageCollisionParameter154.Radius = 0.085938f;
				osageCollisionParameter154.Type = 2u;
				collisions154.Add(osageCollisionParameter154);
				List<OsageCollisionParameter> collisions155 = obj21.Collisions;
				OsageCollisionParameter osageCollisionParameter155 = new OsageCollisionParameter();
				osageCollisionParameter155.Bone0.Name = "j_sune_l_wj";
				osageCollisionParameter155.Bone0.Position = new Vector3(0f, 0f, 0f);
				osageCollisionParameter155.Bone1.Name = "j_sune_l_wj";
				osageCollisionParameter155.Bone1.Position = new Vector3(0.457031f, 0f, 0f);
				osageCollisionParameter155.Radius = 0.085938f;
				osageCollisionParameter155.Type = 2u;
				collisions155.Add(osageCollisionParameter155);
				List<OsageCollisionParameter> collisions156 = obj21.Collisions;
				OsageCollisionParameter osageCollisionParameter156 = new OsageCollisionParameter();
				osageCollisionParameter156.Bone0.Name = "j_sune_r_wj";
				osageCollisionParameter156.Bone0.Position = new Vector3(0f, 0f, 0f);
				osageCollisionParameter156.Bone1.Name = "j_sune_r_wj";
				osageCollisionParameter156.Bone1.Position = new Vector3(0.457031f, 0f, 0f);
				osageCollisionParameter156.Radius = 0.085938f;
				osageCollisionParameter156.Type = 2u;
				collisions156.Add(osageCollisionParameter156);
				obj21.CollisionRadius = 0f;
				obj21.CollisionType = 1u;
				obj21.Force = 0.01f;
				obj21.ForceGain = 0.6f;
				obj21.Friction = 1f;
				obj21.HingeY = 90f;
				obj21.HingeZ = 90f;
				obj21.InitRotationY = 0f;
				obj21.InitRotationZ = 0f;
				obj21.MoveCancel = 0f;
				obj21.Nodes.Add(new OsageNodeParameter
				{
					Radius = 0.04f,
					HingeYMax = 180f,
					HingeYMin = -180f,
					HingeZMax = 180f,
					HingeZMin = -180f,
					InertialCancel = 0f,
					Weight = 3f
				});
				obj21.Nodes.Add(new OsageNodeParameter
				{
					Radius = 0.04f,
					HingeYMax = 180f,
					HingeYMin = -180f,
					HingeZMax = 180f,
					HingeZMin = -180f,
					InertialCancel = 0f,
					Weight = 3f
				});
				obj21.Nodes.Add(new OsageNodeParameter
				{
					Radius = 0.04f,
					HingeYMax = 180f,
					HingeYMin = -180f,
					HingeZMax = 180f,
					HingeZMin = -180f,
					InertialCancel = 0f,
					Weight = 3f
				});
				obj21.Nodes.Add(new OsageNodeParameter
				{
					Radius = 0.04f,
					HingeYMax = 180f,
					HingeYMin = -180f,
					HingeZMax = 180f,
					HingeZMin = -180f,
					InertialCancel = 0f,
					Weight = 3f
				});
				obj21.Nodes.Add(new OsageNodeParameter
				{
					Radius = 0.04f,
					HingeYMax = 180f,
					HingeYMin = -180f,
					HingeZMax = 180f,
					HingeZMin = -180f,
					InertialCancel = 0f,
					Weight = 3f
				});
				obj21.RotationY = 0f;
				obj21.RotationZ = -5f;
				obj21.Stiffness = 0f;
				obj21.WindAffection = 0.5f;
				dictionary7.Add("j_hair_long_l_01_000_wj", obj21);
				OsageSkinParameter obj22 = new OsageSkinParameter
				{
					Name = "c_hair_long_r_01_osg",
					AirResistance = 0.8f
				};
				List<OsageCollisionParameter> collisions157 = obj22.Collisions;
				OsageCollisionParameter osageCollisionParameter157 = new OsageCollisionParameter();
				osageCollisionParameter157.Bone0.Name = "kl_mune_b_wj";
				osageCollisionParameter157.Bone0.Position = new Vector3(0f, 0.097656f, 0f);
				osageCollisionParameter157.Bone1.Name = "n_hara_cp";
				osageCollisionParameter157.Bone1.Position = new Vector3(0f, -0.0625f, 0f);
				osageCollisionParameter157.Radius = 0.113281f;
				osageCollisionParameter157.Type = 2u;
				collisions157.Add(osageCollisionParameter157);
				List<OsageCollisionParameter> collisions158 = obj22.Collisions;
				OsageCollisionParameter osageCollisionParameter158 = new OsageCollisionParameter();
				osageCollisionParameter158.Bone0.Name = "j_kata_l_wj_cu";
				osageCollisionParameter158.Bone0.Position = new Vector3(0.1875f, 0f, 0f);
				osageCollisionParameter158.Bone1.Name = "j_kata_l_wj_cu";
				osageCollisionParameter158.Bone1.Position = new Vector3(0f, 0f, 0f);
				osageCollisionParameter158.Radius = 0.039063f;
				osageCollisionParameter158.Type = 2u;
				collisions158.Add(osageCollisionParameter158);
				List<OsageCollisionParameter> collisions159 = obj22.Collisions;
				OsageCollisionParameter osageCollisionParameter159 = new OsageCollisionParameter();
				osageCollisionParameter159.Bone0.Name = "j_kata_r_wj_cu";
				osageCollisionParameter159.Bone0.Position = new Vector3(0.1875f, 0f, 0f);
				osageCollisionParameter159.Bone1.Name = "j_kata_r_wj_cu";
				osageCollisionParameter159.Bone1.Position = new Vector3(0f, 0f, 0f);
				osageCollisionParameter159.Radius = 0.039063f;
				osageCollisionParameter159.Type = 2u;
				collisions159.Add(osageCollisionParameter159);
				List<OsageCollisionParameter> collisions160 = obj22.Collisions;
				OsageCollisionParameter osageCollisionParameter160 = new OsageCollisionParameter();
				osageCollisionParameter160.Bone0.Name = "j_ude_l_wj";
				osageCollisionParameter160.Bone0.Position = new Vector3(0.007813f, 0f, 0f);
				osageCollisionParameter160.Bone1.Name = "j_ude_l_wj";
				osageCollisionParameter160.Bone1.Position = new Vector3(0.113281f, 0f, 0f);
				osageCollisionParameter160.Radius = 0.042969f;
				osageCollisionParameter160.Type = 2u;
				collisions160.Add(osageCollisionParameter160);
				List<OsageCollisionParameter> collisions161 = obj22.Collisions;
				OsageCollisionParameter osageCollisionParameter161 = new OsageCollisionParameter();
				osageCollisionParameter161.Bone0.Name = "j_ude_r_wj";
				osageCollisionParameter161.Bone0.Position = new Vector3(0.007813f, 0f, 0f);
				osageCollisionParameter161.Bone1.Name = "j_ude_r_wj";
				osageCollisionParameter161.Bone1.Position = new Vector3(0.113281f, 0f, 0f);
				osageCollisionParameter161.Radius = 0.042969f;
				osageCollisionParameter161.Type = 2u;
				collisions161.Add(osageCollisionParameter161);
				List<OsageCollisionParameter> collisions162 = obj22.Collisions;
				OsageCollisionParameter osageCollisionParameter162 = new OsageCollisionParameter();
				osageCollisionParameter162.Bone0.Name = "kl_kosi_etc_wj";
				osageCollisionParameter162.Bone0.Position = new Vector3(0.027344f, -13f / 64f, 3f / 64f);
				osageCollisionParameter162.Bone1.Name = "kl_kosi_etc_wj";
				osageCollisionParameter162.Bone1.Position = new Vector3(0.023438f, -13f / 64f, -3f / 64f);
				osageCollisionParameter162.Radius = 5f / 32f;
				osageCollisionParameter162.Type = 2u;
				collisions162.Add(osageCollisionParameter162);
				List<OsageCollisionParameter> collisions163 = obj22.Collisions;
				OsageCollisionParameter osageCollisionParameter163 = new OsageCollisionParameter();
				osageCollisionParameter163.Bone0.Name = "j_kao_wj";
				osageCollisionParameter163.Bone0.Position = new Vector3(0f, 0f, 0.074219f);
				osageCollisionParameter163.Bone1.Name = "j_kao_wj";
				osageCollisionParameter163.Bone1.Position = new Vector3(0.058594f, 0f, 0.074219f);
				osageCollisionParameter163.Radius = 0.113281f;
				osageCollisionParameter163.Type = 2u;
				collisions163.Add(osageCollisionParameter163);
				List<OsageCollisionParameter> collisions164 = obj22.Collisions;
				OsageCollisionParameter osageCollisionParameter164 = new OsageCollisionParameter();
				osageCollisionParameter164.Bone0.Name = "j_momo_l_wj";
				osageCollisionParameter164.Bone0.Position = new Vector3(0f, 0f, 0f);
				osageCollisionParameter164.Bone1.Name = "j_momo_l_wj";
				osageCollisionParameter164.Bone1.Position = new Vector3(0.347656f, 0f, 0f);
				osageCollisionParameter164.Radius = 0.085938f;
				osageCollisionParameter164.Type = 2u;
				collisions164.Add(osageCollisionParameter164);
				List<OsageCollisionParameter> collisions165 = obj22.Collisions;
				OsageCollisionParameter osageCollisionParameter165 = new OsageCollisionParameter();
				osageCollisionParameter165.Bone0.Name = "j_momo_r_wj";
				osageCollisionParameter165.Bone0.Position = new Vector3(0f, 0f, 0f);
				osageCollisionParameter165.Bone1.Name = "j_momo_r_wj";
				osageCollisionParameter165.Bone1.Position = new Vector3(0.347656f, 0f, 0f);
				osageCollisionParameter165.Radius = 0.085938f;
				osageCollisionParameter165.Type = 2u;
				collisions165.Add(osageCollisionParameter165);
				List<OsageCollisionParameter> collisions166 = obj22.Collisions;
				OsageCollisionParameter osageCollisionParameter166 = new OsageCollisionParameter();
				osageCollisionParameter166.Bone0.Name = "j_sune_l_wj";
				osageCollisionParameter166.Bone0.Position = new Vector3(0f, 0f, 0f);
				osageCollisionParameter166.Bone1.Name = "j_sune_l_wj";
				osageCollisionParameter166.Bone1.Position = new Vector3(0.457031f, 0f, 0f);
				osageCollisionParameter166.Radius = 0.085938f;
				osageCollisionParameter166.Type = 2u;
				collisions166.Add(osageCollisionParameter166);
				List<OsageCollisionParameter> collisions167 = obj22.Collisions;
				OsageCollisionParameter osageCollisionParameter167 = new OsageCollisionParameter();
				osageCollisionParameter167.Bone0.Name = "j_sune_r_wj";
				osageCollisionParameter167.Bone0.Position = new Vector3(0f, 0f, 0f);
				osageCollisionParameter167.Bone1.Name = "j_sune_r_wj";
				osageCollisionParameter167.Bone1.Position = new Vector3(0.457031f, 0f, 0f);
				osageCollisionParameter167.Radius = 0.085938f;
				osageCollisionParameter167.Type = 2u;
				collisions167.Add(osageCollisionParameter167);
				obj22.CollisionRadius = 0f;
				obj22.CollisionType = 1u;
				obj22.Force = 0.01f;
				obj22.ForceGain = 0.6f;
				obj22.Friction = 1f;
				obj22.HingeY = 90f;
				obj22.HingeZ = 90f;
				obj22.InitRotationY = 0f;
				obj22.InitRotationZ = 0f;
				obj22.MoveCancel = 0f;
				obj22.Nodes.Add(new OsageNodeParameter
				{
					Radius = 0.04f,
					HingeYMax = 180f,
					HingeYMin = -180f,
					HingeZMax = 180f,
					HingeZMin = -180f,
					InertialCancel = 0f,
					Weight = 3f
				});
				obj22.Nodes.Add(new OsageNodeParameter
				{
					Radius = 0.04f,
					HingeYMax = 180f,
					HingeYMin = -180f,
					HingeZMax = 180f,
					HingeZMin = -180f,
					InertialCancel = 0f,
					Weight = 3f
				});
				obj22.Nodes.Add(new OsageNodeParameter
				{
					Radius = 0.04f,
					HingeYMax = 180f,
					HingeYMin = -180f,
					HingeZMax = 180f,
					HingeZMin = -180f,
					InertialCancel = 0f,
					Weight = 3f
				});
				obj22.Nodes.Add(new OsageNodeParameter
				{
					Radius = 0.04f,
					HingeYMax = 180f,
					HingeYMin = -180f,
					HingeZMax = 180f,
					HingeZMin = -180f,
					InertialCancel = 0f,
					Weight = 3f
				});
				obj22.Nodes.Add(new OsageNodeParameter
				{
					Radius = 0.04f,
					HingeYMax = 180f,
					HingeYMin = -180f,
					HingeZMax = 180f,
					HingeZMin = -180f,
					InertialCancel = 0f,
					Weight = 3f
				});
				obj22.RotationY = 0f;
				obj22.RotationZ = -5f;
				obj22.Stiffness = 0f;
				obj22.WindAffection = 0.5f;
				dictionary7.Add("j_hair_long_r_01_000_wj", obj22);
				Dictionary<string, OsageSkinParameter> dictionary8 = dictionary7;
				Dictionary<string, List<string>> dictionary9 = new Dictionary<string, List<string>>
				{
					{
						"j_cloth_skirt_b_000_wj",
						new List<string> { "" }
					},
					{
						"j_cloth_skirt_f_000_wj",
						new List<string> { "= 3 n 0 v 3.n_momo_a_l_wj_cd_ex v 3.n_momo_a_r_wj_cd_ex g + n 0.5 g * n 0.75 g * g min", "= 5 n 0 v 5.n_momo_a_l_wj_cd_ex v 5.n_momo_a_r_wj_cd_ex g + n 0.5 g * g min" }
					},
					{
						"j_cloth_skirt_l_02_000_wj",
						new List<string> { "= 3 n 0 v 3.n_momo_a_l_wj_cd_ex n 0.5 g * g min", "= 5 n 0 v 5.n_momo_a_l_wj_cd_ex g min" }
					},
					{
						"j_cloth_skirt_l_04_000_wj",
						new List<string> { "= 3 n 0 v 3.n_momo_a_l_wj_cd_ex g min", "= 5 n 0 v 5.n_momo_a_l_wj_cd_ex n 0.5 g * g min" }
					},
					{
						"j_cloth_skirt_l_06_000_wj",
						new List<string> { "= 3 n 0 v 3.n_momo_a_l_wj_cd_ex n 0.5 g * g min", "= 5 n 0 v 5.n_momo_a_l_wj_cd_ex n 0.25 g * g min" }
					},
					{
						"j_cloth_skirt_r_02_000_wj",
						new List<string> { "= 3 n 0 v 3.n_momo_a_r_wj_cd_ex n 0.5 g * g max", "= 5 n 0 v 5.n_momo_a_r_wj_cd_ex g min" }
					},
					{
						"j_cloth_skirt_r_04_000_wj",
						new List<string> { "= 3 n 0 v 3.n_momo_a_r_wj_cd_ex g max", "= 5 n 0 v 5.n_momo_a_r_wj_cd_ex n 0.5 g * g min" }
					},
					{
						"j_cloth_skirt_r_06_000_wj",
						new List<string> { "= 3 n 0 v 3.n_momo_a_r_wj_cd_ex n 0.5 g * g max", "= 5 n 0 v 5.n_momo_a_r_wj_cd_ex n 0.25 g * g min" }
					},
					{
						"j_cloth_long_b_000_wj",
						new List<string> { "" }
					},
					{
						"j_cloth_long_f_000_wj",
						new List<string> { "= 3 n 0 v 3.n_momo_a_l_wj_cd_ex v 3.n_momo_a_r_wj_cd_ex g + n 0.5 g * n 0.75 g * g min", "= 5 n 0 v 5.n_momo_a_l_wj_cd_ex v 5.n_momo_a_r_wj_cd_ex g + n 0.5 g * g min" }
					},
					{
						"j_cloth_long_l_01_000_wj",
						new List<string> { "= 3 n 0 v 3.n_momo_a_l_wj_cd_ex n 0.5 g * g min", "= 5 n 0 v 5.n_momo_a_l_wj_cd_ex g min" }
					},
					{
						"j_cloth_long_l_02_000_wj",
						new List<string> { "= 3 n 0 v 3.n_momo_a_l_wj_cd_ex g min", "= 5 n 0 v 5.n_momo_a_l_wj_cd_ex n 0.5 g * g min" }
					},
					{
						"j_cloth_long_l_03_000_wj",
						new List<string> { "= 3 n 0 v 3.n_momo_a_l_wj_cd_ex n 0.5 g * g min", "= 5 n 0 v 5.n_momo_a_l_wj_cd_ex n 0.25 g * g min" }
					},
					{
						"j_cloth_long_r_01_000_wj",
						new List<string> { "= 3 n 0 v 3.n_momo_a_r_wj_cd_ex n 0.5 g * g max", "= 5 n 0 v 5.n_momo_a_r_wj_cd_ex g min" }
					},
					{
						"j_cloth_long_r_02_000_wj",
						new List<string> { "= 3 n 0 v 3.n_momo_a_r_wj_cd_ex g max", "= 5 n 0 v 5.n_momo_a_r_wj_cd_ex n 0.5 g * g min" }
					},
					{
						"j_cloth_long_r_03_000_wj",
						new List<string> { "= 3 n 0 v 3.n_momo_a_r_wj_cd_ex n 0.5 g * g max", "= 5 n 0 v 5.n_momo_a_r_wj_cd_ex n 0.25 g * g min" }
					},
					{
						"j_hair_twin_l_000_wj",
						new List<string> { "= 3 v 5.kl_kubi f neg", "= 4 v 3.cl_kao", "= 5 n 90 v 5.j_kao_wj g -" }
					},
					{
						"j_hair_twin_r_000_wj",
						new List<string> { "= 3 v 5.kl_kubi f neg", "= 4 v 3.cl_kao", "= 5 n 90 v 5.j_kao_wj g -" }
					},
					{
						"j_hair_long_c_000_wj",
						new List<string> { "= 3 v 5.kl_kubi f neg", "= 4 v 3.cl_kao", "= 5 n 90 v 5.j_kao_wj g -" }
					},
					{
						"j_hair_long_l_01_000_wj",
						new List<string> { "= 3 v 5.kl_kubi f neg", "= 4 v 3.cl_kao", "= 5 n 90 v 5.j_kao_wj g -" }
					},
					{
						"j_hair_long_r_01_000_wj",
						new List<string> { "= 3 v 5.kl_kubi f neg", "= 4 v 3.cl_kao", "= 5 n 90 v 5.j_kao_wj g -" }
					}
				};
				if (modules.Count == 0 || list4.All((List<Item> x) => x.Count == 0))
				{
					Console.WriteLine("The selected content has no module/item databases.");
					return;
				}
				StreamWriter streamWriter2 = File.CreateText(Path.Combine(args[2], "rom", "lang2", "mod_str_array.toml"));
				SpriteDatabase spriteDatabase2 = new SpriteDatabase();
				ParameterTreeWriter parameterTreeWriter = new ParameterTreeWriter();
				int num2 = 0;
				parameterTreeWriter.PushScope("module");
				uint num3 = 10010000u;
				uint num4 = 10011000u;
				uint num5 = 10012000u;
				foreach (ModuleInfo module in modules)
				{
					if (!args.Contains("-i"))
					{
						short sourcePresentationId = sourceModuleIds.GetValueOrDefault(module.ModuleID, module.ModuleID);
						// Snow Miku 2013 Alt. has no large portrait: reuse the normal portrait,
                        // while retaining the alternate module's output identity.
                        string[] portraitCandidates = sourcePresentationId == 99
                            ? new[] { "spr_mdl_thmb098l.farc", "spr_mdl_thmb099l.farc", "spr_mdl_thmb099s.farc" }
                            : new[] { $"spr_mdl_thmb{sourcePresentationId:D3}l.farc", $"spr_mdl_thmb{sourcePresentationId:D3}s.farc" };
                        string text2 = portraitCandidates.FirstOrDefault((string item5) => activeContainers.Any((ACCT x) => x.Auth2DFlist.Contains(item5)));
						if (text2 != null)
						{
							using Stream source9 = contentResolver.OpenRequired(text2, out source6);
							using FarcArchive archive3 = BinaryFile.Load<FarcArchive>(source9);
							string fileNameWithoutExtension2 = Path.GetFileNameWithoutExtension(text2);
							archive3.Open<SpriteDatabase>(fileNameWithoutExtension2 + ".spi");
							SpriteSet spriteSet = archive3.Open<SpriteSet>(fileNameWithoutExtension2 + ".spr");
							if (fileNameWithoutExtension2.EndsWith("s", StringComparison.OrdinalIgnoreCase))
							{
								Console.WriteLine($"[SPRITE_CONVERSION] --> Module {module.ModuleID}: " + "large sprite unavailable; using small sprite");
							}
							if (spriteDatabase.SpriteSets.FirstOrDefault((SpriteSetInfo x) => x.Name == $"SPR_SEL_MD{module.ModuleID:D3}CMN") == null)
							{
								SpriteSetInfo item2 = new SpriteSetInfo
								{
									Id = num3,
									Name = $"SPR_SEL_MD{module.ModuleID:D3}CMN",
									FileName = $"spr_sel_md{module.ModuleID:D3}cmn.bin",
									Sprites = 
									{
										new SpriteInfo
										{
											Name = $"SPR_SEL_MD{module.ModuleID:D3}CMN_MD_IMG",
											Id = num4,
											Index = 0
										}
									},
									Textures = 
									{
										new SpriteTextureInfo
										{
											Name = $"SPR_SEL_MD{module.ModuleID:D3}CMN_MERGE_BC5COMP",
											Id = num5,
											Index = 0
										}
									}
								};
								num3++;
								num4++;
								num5++;
								spriteDatabase2.SpriteSets.Add(item2);
							}
							using Bitmap image = SpriteCropper.Crop(spriteSet.Sprites[0], spriteSet);
							using Bitmap bitmap = new Bitmap(512, 512);
							using (Graphics graphics = Graphics.FromImage(bitmap))
							{
								graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
								graphics.CompositingQuality = CompositingQuality.HighQuality;
								graphics.SmoothingMode = SmoothingMode.HighQuality;
								graphics.DrawImage(image, 2, 2, 408, 408);
							}
							bitmap.RotateFlip(RotateFlipType.Rotate180FlipX);
							spriteSet.TextureSet.Textures[0] = TextureEncoder.EncodeYCbCrFromBitmap(bitmap);
							spriteSet.Sprites[0].Name = "MD_IMG";
							spriteSet.Sprites[0].ResolutionMode = ResolutionMode.HDTV1080;
							spriteSet.Sprites[0].X = 2f;
							spriteSet.Sprites[0].Y = 2f;
							spriteSet.Sprites[0].Width = 408f;
							spriteSet.Sprites[0].Height = 494f;
							spriteSet.Sprites[0].RectangleBegin = new Vector2(spriteSet.Sprites[0].X / 512f, spriteSet.Sprites[0].Y / 512f);
							spriteSet.Sprites[0].RectangleEnd = new Vector2(spriteSet.Sprites[0].X / 512f + spriteSet.Sprites[0].Width / 512f, spriteSet.Sprites[0].Y / 512f + spriteSet.Sprites[0].Height / 512f);
							using FarcArchive farcArchive2 = new FarcArchive();
							MemoryStream memoryStream2 = new MemoryStream();
							spriteSet.Format = BinaryFormat.DT;
							spriteSet.Save(memoryStream2, leaveOpen: true);
							farcArchive2.Add($"spr_sel_md{module.ModuleID:D3}cmn.bin", memoryStream2, leaveOpen: false);
							farcArchive2.Save(Path.Combine(args[2], "rom", "2d", $"spr_sel_md{module.ModuleID:D3}cmn.farc"));
						}
					}
					string text3 = "";
					foreach (STRA item11 in stringArrays)
					{
						text3 = item11.GetStringById(10010000 + sourceModuleIds.GetValueOrDefault(module.ModuleID, module.ModuleID));
						if (text3 != "")
						{
							break;
						}
					}
					if (text3 == "" && sKnownModuleNames.TryGetValue(module.ModuleID, out string value))
					{
						text3 = value;
					}
					streamWriter2.WriteLine($"module.{module.ModuleID} = \"{text3}\"");
					Console.WriteLine("[GAME_MODULE_TABLE_CONVERSION] --> " + text3);
					parameterTreeWriter.PushScope(num2);
					parameterTreeWriter.Write("attr", 0);
					parameterTreeWriter.Write("chara", dictionary[(int)module.Character]);
					parameterTreeWriter.Write("cos", $"COS_{module.CosID + 1}");
					parameterTreeWriter.Write("id", (int)module.ModuleID);
					parameterTreeWriter.Write("name", text3);
					parameterTreeWriter.Write("ng", 0);
					parameterTreeWriter.Write("shop_ed_day", 1);
					parameterTreeWriter.Write("shop_ed_month", 1);
					parameterTreeWriter.Write("shop_ed_year", 2029);
					parameterTreeWriter.Write("shop_price", 0);
					parameterTreeWriter.Write("shop_st_day", 9);
					parameterTreeWriter.Write("shop_st_month", 3);
					parameterTreeWriter.Write("shop_st_year", 2025);
					parameterTreeWriter.Write("sort_index", (int)module.SortIndex);
					parameterTreeWriter.PopScope();
					num2++;
				}
				if (!args.Contains("-i"))
					spriteDatabase2.Save(Path.Combine(args[2], "rom", "2d", "mod_spr_db.bin"));
				parameterTreeWriter.Write("data_list.length", modules.Count);
				using (FarcArchive farcArchive3 = new FarcArchive())
				{
					MemoryStream memoryStream3 = new MemoryStream();
					parameterTreeWriter.Flush(memoryStream3);
					farcArchive3.Add("gm_module_id.bin", memoryStream3, leaveOpen: false);
					farcArchive3.Save(Path.Combine(args[2], "rom", "gm_module_tbl.farc"));
				}
				ParameterTreeWriter parameterTreeWriter2 = new ParameterTreeWriter();
				int num6 = 0;
				parameterTreeWriter2.PushScope("cstm_item");
				foreach (CustomizeItemInfo item12 in list2)
				{
					parameterTreeWriter2.PushScope(num6);
					parameterTreeWriter2.Write("bind_module", -1);
					parameterTreeWriter2.Write("chara", "ALL");
					parameterTreeWriter2.Write("id", item12.ItemID);
					string text4 = "";
					foreach (STRA item13 in stringArrays)
					{
						text4 = item13.GetStringById(10030000 + item12.ItemID);
						if (text4 != "")
						{
							break;
						}
					}
					streamWriter2.WriteLine($"customize.{item12.ItemID} = \"{text4}\"");
					parameterTreeWriter2.Write("name", text4);
					parameterTreeWriter2.Write("ng", 0);
					parameterTreeWriter2.Write("obj_id", item12.ItemNumber);
					parameterTreeWriter2.Write("parts", item12.Part.ToString());
					parameterTreeWriter2.Write("sell_type", 1);
					parameterTreeWriter2.Write("shop_ed_day", 1);
					parameterTreeWriter2.Write("shop_ed_month", 1);
					parameterTreeWriter2.Write("shop_ed_year", 2029);
					parameterTreeWriter2.Write("shop_price", 200);
					parameterTreeWriter2.Write("shop_st_day", 9);
					parameterTreeWriter2.Write("shop_st_month", 3);
					parameterTreeWriter2.Write("shop_st_year", 2025);
					parameterTreeWriter2.Write("sort_index", item12.U04);
					parameterTreeWriter2.PopScope();
					num6++;
				}
				parameterTreeWriter2.PopScope();
				parameterTreeWriter2.Write("cstm_item.data_list.length", list2.Count);
				parameterTreeWriter2.Write("patch", 0);
				parameterTreeWriter2.Write("version", 0);
				if (list2.Count > 0)
				using (FarcArchive farcArchive4 = new FarcArchive())
				{
					MemoryStream memoryStream4 = new MemoryStream();
					parameterTreeWriter2.Flush(memoryStream4);
					farcArchive4.Add("gm_customize_item_id.bin", memoryStream4, leaveOpen: false);
					farcArchive4.Save(Path.Combine(args[2], "rom", "mod_gm_customize_item_tbl.farc"));
				}
				List<string> list11 = new List<string>();
				using (FarcArchive farcArchive5 = new FarcArchive())
				{
					for (int i2 = 0; i2 < 10; i2++)
					{
						ParameterTreeWriter chritmTbl = new ParameterTreeWriter();
						CharacterType characterType = (CharacterType)i2;
						// Focused builds normally reference MegaMix's base ITM000. A head
						// compatibility microtest may explicitly include the Diva X ITM000;
						// this is intentionally opt-in because it replaces global face assets.
						if ((moduleFilterIndex < 0 || includeBaseHead) && (list4[i2].Count > 0 || characterCostumes[i2].Count > 0))
							list11.Add(characterType.ToString().ToUpper() + "ITM000");
						chritmTbl.Write("cos", characterCostumes[i2], delegate(Costume x)
						{
							DefaultInterpolatedStringHandler defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(31, 2);
							defaultInterpolatedStringHandler.AppendLiteral("[");
							CharacterType characterType2 = (CharacterType)i2;
							defaultInterpolatedStringHandler.AppendFormatted(characterType2.ToString().ToUpper());
							defaultInterpolatedStringHandler.AppendLiteral("ITM_TABLE_CONVERSION] --> COS_");
							defaultInterpolatedStringHandler.AppendFormatted(x.CostumeID + 1, "D3");
							Console.WriteLine(defaultInterpolatedStringHandler.ToStringAndClear());
							chritmTbl.Write("id", x.CostumeID);
							chritmTbl.PushScope("item");
							int num21 = 0;
							foreach (CostumePart part in x.Parts)
							{
								if (part.ItemNumber != 0)
								{
									chritmTbl.Write($"{num21}", part.ItemNumber);
									num21++;
								}
							}
							chritmTbl.Write("length", num21);
							chritmTbl.PopScope();
						});
						int num7 = 0;
						chritmTbl.PushScope("item");
						foreach (Item item3 in list4[i2])
						{
							chritmTbl.PushScope(num7);
							chritmTbl.Write("attr", item3.Attribute);
							if (item3.Objects.Count > 0)
							{
								chritmTbl.Write("data.obj", item3.Objects, delegate(ItemObject x)
								{
									chritmTbl.Write("rpk", (sbyte)x.RPK);
									DefaultInterpolatedStringHandler defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(32, 2);
									defaultInterpolatedStringHandler.AppendLiteral("[");
									CharacterType characterType2 = (CharacterType)i2;
									defaultInterpolatedStringHandler.AppendFormatted(characterType2.ToString().ToUpper());
									defaultInterpolatedStringHandler.AppendLiteral("ITM_TABLE_CONVERSION] --> Item ");
									defaultInterpolatedStringHandler.AppendFormatted(item3.ItemNumber, "D3");
									Console.WriteLine(defaultInterpolatedStringHandler.ToStringAndClear());
									if (objectHashNameMap.TryGetValue(x.ObjectID, out string value6))
									{
										if (!value6.Contains("ITM000") && value6 != "NULL")
										{
											chritmTbl.Write("uid", value6 + "_X");
										}
										else
										{
											chritmTbl.Write("uid", value6 ?? "");
										}
									}
									else
									{
										chritmTbl.Write("uid", $"MISSING_OBJ_{x.ObjectID}");
									}
								});
							}
							if (item3.TextureChanges.Count > 0)
							{
								chritmTbl.Write("data.tex", item3.TextureChanges, delegate(TextureChange x)
								{
									if (textureHashNameMap.TryGetValue(x.ReplacementTextureID, out string value6))
									{
										chritmTbl.Write("chg", value6);
									}
									else
									{
										chritmTbl.Write("chg", $"MISSING_TEX_{x.ReplacementTextureID}");
									}
									if (textureHashNameMap.TryGetValue(x.OriginalTextureID, out string value7))
									{
										chritmTbl.Write("org", value7);
									}
									else
									{
										chritmTbl.Write("org", $"MISSING_TEX_{x.OriginalTextureID}");
									}
								});
							}
							chritmTbl.Write("des_id", item3.DestID);
							chritmTbl.Write("exclusion", 0);
							chritmTbl.Write("face_depth", 0);
							chritmTbl.Write("flag", 0);
							string text5 = "";
							foreach (STRA item14 in stringArrays)
							{
								text5 = item14.GetStringById(int.Parse($"190{i2:D2}{item3.ItemNumber:D4}"));
								if (text5 != "")
								{
									break;
								}
							}
							if (text5 == "")
							{
								text5 = $"{i2}ITM{item3.ItemNumber:D3}";
							}
							chritmTbl.Write("name", text5);
							chritmTbl.Write("no", item3.ItemNumber);
							chritmTbl.PushScope("objset");
							int num8 = 0;
							foreach (uint objectSet2 in item3.ObjectSets)
							{
								if (objsetHashNameMap.TryGetValue(objectSet2, out string value2))
								{
									if (!list11.Contains(value2))
									{
										list11.Add(value2);
									}
									if (!value2.Contains("ITM000"))
									{
										chritmTbl.Write($"{num8}", value2 + "_X");
									}
									else
									{
										chritmTbl.Write($"{num8}", value2 ?? "");
									}
								}
								else
								{
									chritmTbl.Write($"{num8}", $"MISSING_OBJSET_{objectSet2}");
								}
								num8++;
							}
							chritmTbl.Write("length", item3.ObjectSets.Count);
							chritmTbl.PopScope();
							chritmTbl.Write("org_itm", 0);
							chritmTbl.Write("point", 0);
							chritmTbl.Write("sub_id", (int)item3.SubID);
							chritmTbl.Write("type", item3.Type);
							chritmTbl.PopScope();
							num7++;
						}
						foreach (Item item4 in list3)
						{
							chritmTbl.PushScope(num7);
							chritmTbl.Write("attr", item4.Attribute);
							if (item4.Objects.Count > 0)
							{
								chritmTbl.Write("data.obj", item4.Objects, delegate(ItemObject x)
								{
									chritmTbl.Write("rpk", (sbyte)x.RPK);
									DefaultInterpolatedStringHandler defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(42, 2);
									defaultInterpolatedStringHandler.AppendLiteral("[");
									CharacterType characterType2 = (CharacterType)i2;
									defaultInterpolatedStringHandler.AppendFormatted(characterType2.ToString().ToUpper());
									defaultInterpolatedStringHandler.AppendLiteral("ITM_TABLE_CONVERSION] --> Customize Item ");
									defaultInterpolatedStringHandler.AppendFormatted(item4.ItemNumber, "D4");
									Console.WriteLine(defaultInterpolatedStringHandler.ToStringAndClear());
									if (objectHashNameMap.TryGetValue(x.ObjectID, out string value6))
									{
										if (!value6.Contains("ITM000") && value6 != "NULL")
										{
											chritmTbl.Write("uid", value6 + "_X");
										}
										else
										{
											chritmTbl.Write("uid", value6 ?? "");
										}
									}
									else
									{
										chritmTbl.Write("uid", $"MISSING_OBJ_{x.ObjectID}");
									}
								});
							}
							if (item4.TextureChanges.Count > 0)
							{
								chritmTbl.Write("data.tex", item4.TextureChanges, delegate(TextureChange x)
								{
									if (textureHashNameMap.TryGetValue(x.ReplacementTextureID, out string value6))
									{
										chritmTbl.Write("chg", value6);
									}
									else
									{
										chritmTbl.Write("chg", $"MISSING_TEX_{x.ReplacementTextureID}");
									}
									if (textureHashNameMap.TryGetValue(x.OriginalTextureID, out string value7))
									{
										chritmTbl.Write("org", value7);
									}
									else
									{
										chritmTbl.Write("org", $"MISSING_TEX_{x.OriginalTextureID}");
									}
								});
							}
							chritmTbl.Write("des_id", item4.DestID);
							chritmTbl.Write("exclusion", 0);
							chritmTbl.Write("face_depth", 0);
							chritmTbl.Write("flag", 0);
							string text6 = "";
							foreach (STRA item15 in stringArrays)
							{
								text6 = item15.GetStringById(int.Parse($"19099{item4.ItemNumber:D4}"));
								if (text6 != "")
								{
									break;
								}
							}
							if (text6 == "")
							{
								text6 = $"CMNITM{item4.ItemNumber:D4}";
							}
							chritmTbl.Write("name", text6);
							chritmTbl.Write("no", item4.ItemNumber);
							chritmTbl.PushScope("objset");
							int value3 = 0;
							foreach (uint objectSet3 in item4.ObjectSets)
							{
								if (objsetHashNameMap.TryGetValue(objectSet3, out string value4))
								{
									if (!list11.Contains(value4))
									{
										list11.Add(value4);
									}
									if (!value4.Contains("ITM000"))
									{
										chritmTbl.Write($"{value3}", value4 + "_X");
									}
									else
									{
										chritmTbl.Write($"{value3}", value4 ?? "");
									}
								}
								else
								{
									chritmTbl.Write($"{value3}", $"MISSING_OBJSET_{objectSet3}");
								}
							}
							chritmTbl.Write("length", item4.ObjectSets.Count);
							chritmTbl.PopScope();
							chritmTbl.Write("org_itm", 0);
							chritmTbl.Write("point", 0);
							chritmTbl.Write("sub_id", (int)item4.SubID);
							chritmTbl.Write("type", item4.Type);
							chritmTbl.PopScope();
							num7++;
						}
						chritmTbl.Write("length", list4[i2].Count + list3.Count);
						chritmTbl.PopScope();
						MemoryStream memoryStream5 = new MemoryStream();
						chritmTbl.Flush(memoryStream5);
						characterType = (CharacterType)i2;
						if (moduleFilterIndex < 0 || list4[i2].Count > 0 || characterCostumes[i2].Count > 0)
							farcArchive5.Add(characterType.ToString().ToLower() + "itm_tbl.txt", memoryStream5, leaveOpen: false);
						else
							memoryStream5.Dispose();
					}
					farcArchive5.Save(Path.Combine(args[2], "rom", "mod_chritm_prop.farc"));
				}
				if (!args.Contains("--no-motion"))
				{
				foreach (Skeleton skeleton5 in boneData2.Skeletons)
				{
					Console.WriteLine("[BONEDATA_CONVERSION] !!! " + skeleton5.Name);
				}
				foreach (Skeleton mmSkel in boneData.Skeletons)
				{
					Skeleton skeleton = boneData2.Skeletons.First((Skeleton x) => x.Name == mmSkel.Name);
					Console.WriteLine("[BONEDATA_CONVERSION] --> Converting skeleton " + mmSkel.Name);
					foreach (Bone bone2 in mmSkel.Bones)
					{
						int num9 = 0;
						int num10 = 0;
						foreach (Bone bone3 in mmSkel.Bones)
						{
							if (bone3 == bone2)
							{
								break;
							}
							num9++;
							if (bone3.Type == BoneType.HeadIKRotation)
							{
								num9++;
							}
							else if (bone3.Type >= BoneType.ArmIKRotation)
							{
								num9 += 2;
							}
						}
						Bone bone = skeleton.GetBone(bone2.Name);
						if (bone == null)
						{
							continue;
						}
						foreach (Bone bone4 in skeleton.Bones)
						{
							if (bone4 == bone)
							{
								break;
							}
							num10++;
							if (bone4.Type == BoneType.HeadIKRotation)
							{
								num10++;
							}
							else if (bone4.Type >= BoneType.ArmIKRotation)
							{
								num10 += 2;
							}
						}
						mmSkel.Positions[num9] = skeleton.Positions[num10];
						if (bone2.Type == BoneType.HeadIKRotation)
						{
							mmSkel.Positions[num9 + 1] = skeleton.Positions[num10 + 1];
						}
						if (bone2.Type >= BoneType.ArmIKRotation)
						{
							mmSkel.Positions[num9 + 2] = skeleton.Positions[num10 + 2];
						}
					}
					mmSkel.HeelHeight = skeleton.HeelHeight;
				}
				boneData.Save(Path.Combine(args[2], "rom", "bone_data.bin"));
				}
				uint num11 = 40000u;
				uint num12 = 90000u;
				ObjectDatabase objectDatabase3 = new ObjectDatabase();
				TextureDatabase textureDatabase2 = new TextureDatabase();
				var convertedTextureIds = new Dictionary<string, uint>(StringComparer.Ordinal);
				if (!args.Contains("-n"))
				{
					OsageSetting osageSetting = new OsageSetting
					{
						Categories = 
						{
							new OsageSettingCategory
							{
								Name = "long_tail_f",
								Osg = 
								{
									new OsageSettingParameter
									{
										EXF = 6,
										Parts = OsageSettingPartType.LEFT,
										Root = "c_hair_twin_l_osg"
									},
									new OsageSettingParameter
									{
										EXF = 6,
										Parts = OsageSettingPartType.RIGHT,
										Root = "c_hair_twin_r_osg"
									}
								}
							},
							new OsageSettingCategory
							{
								Name = "long_hair_f",
								Osg = 
								{
									new OsageSettingParameter
									{
										EXF = 3,
										Parts = OsageSettingPartType.LONG_C,
										Root = "c_hair_long_c_osg"
									},
									new OsageSettingParameter
									{
										EXF = 3,
										Parts = OsageSettingPartType.LONG_C,
										Root = "c_hair_long_l_01_osg"
									},
									new OsageSettingParameter
									{
										EXF = 3,
										Parts = OsageSettingPartType.LONG_C,
										Root = "c_hair_long_r_01_osg"
									}
								}
							}
						}
					};
					foreach (string objsetName in list11)
					{
						Console.WriteLine("[OBJECT_CONVERSION] --> " + objsetName);
						string fileName2 = objsetName.ToLower() + ".farc";
						ContentSource source11;
						using Stream source10 = contentResolver.OpenRequired(fileName2, out source11);
						using FarcArchive archive4 = BinaryFile.Load<FarcArchive>(source10);
						Console.WriteLine("[OBJECT_CONVERSION]     source: " + source11.Id);
						ObjectSet objectSet = archive4.Open<ObjectSet>(objsetName.ToLower() + ".osd");
						TextureSet textureSet = archive4.Open<TextureSet>(objsetName.ToLower() + ".txd");
						TextureDatabase textureDatabase3 = archive4.Open<TextureDatabase>(objsetName.ToLower() + ".txi");
						for (int num13 = 0; num13 < objectSet.TextureIds.Count; num13++)
						{
							TextureInfo texInfo = textureDatabase3.GetTextureInfo(objectSet.TextureIds[num13]);
							if (!convertedTextureIds.TryGetValue(texInfo.Name, out uint existingTextureId))
							{
								textureDatabase2.Textures.Add(new TextureInfo
								{
									Name = texInfo.Name,
									Id = num12
								});
								objectSet.TextureIds[num13] = num12;
								convertedTextureIds.Add(texInfo.Name, num12);
								textureSet.Textures[num13].Id = num12;
							}
							else
							{
								uint id = existingTextureId;
								objectSet.TextureIds[num13] = id;
								textureSet.Textures[num13].Id = id;
							}
							num12++;
						}
						if (objsetName.EndsWith("ITM000"))
						{
							MikuMikuLibrary.Objects.Object obj23 = objectSet.Objects.First((MikuMikuLibrary.Objects.Object x) => x.Name == objsetName.Substring(0, 3) + "ITM000_ATAM_HEAD_01__DIVSKN");
							MikuMikuLibrary.Objects.Object obj24 = new MikuMikuLibrary.Objects.Object();
							foreach (Mesh mesh in obj23.Meshes)
							{
								obj24.Meshes.Add(mesh);
							}
							foreach (Material material3 in obj23.Materials)
							{
								obj24.Materials.Add(material3);
							}
							obj24.Skin = obj23.Skin;
							obj24.BoundingSphere = obj23.BoundingSphere;
							obj24.Name = objsetName.Substring(0, 3) + "ITM000_ATAM_HEAD_07__DIVSKN";
							objectSet.Objects.First((MikuMikuLibrary.Objects.Object x) => x.Name == objsetName.Substring(0, 3) + "ITM000_ATAM_HEAD_07__DIVSKN").Name = objsetName.Substring(0, 3) + "ITM000_ATAM_HEAD_08__DIVSKN";
							objectSet.Objects.Add(obj24);
						}
						ObjectSetInfo objectSetInfo = new ObjectSetInfo
						{
							Name = objsetName
						};
						if (objsetName.EndsWith("ITM000"))
						{
							ObjectSetInfo objectSetInfo2 = objectDatabase2.GetObjectSetInfo(objsetName);
							objectSetInfo.Id = ((objectSetInfo2.Id == 0) ? num11 : objectSetInfo2.Id);
						}
						else
						{
							objectSetInfo.Name = objsetName + "_X";
							objectSetInfo.Id = num11;
						}
						if (objsetName.EndsWith("ITM000"))
						{
							objectSetInfo.ArchiveFileName = objsetName.ToLower() + ".farc";
							objectSetInfo.FileName = objsetName.ToLower() + "_obj.bin";
							objectSetInfo.TextureFileName = objsetName.ToLower() + "_tex.bin";
						}
						else
						{
							objectSetInfo.ArchiveFileName = objsetName.ToLower() + "_x.farc";
							objectSetInfo.FileName = objsetName.ToLower() + "_x_obj.bin";
							objectSetInfo.TextureFileName = objsetName.ToLower() + "_x_tex.bin";
						}
						uint num14 = 0u;
						foreach (MikuMikuLibrary.Objects.Object object2 in objectSet.Objects)
						{
							object2.Name = object2.Name.ToLower();
							OsageSkinParameterSet osageSkinParameterSet = new OsageSkinParameterSet();
							string text7 = "ext_skp_" + object2.Name + ".osp";
							// The OSP belongs to the object source. Do not resolve it globally by name,
							// because another DLC/base source may contain a same-named unrelated file.
							if (source11.TryOpen(text7, out Stream? stream) && stream != null)
							{
								using (stream)
								{
									Console.WriteLine("[SKIN PARAMETER] - " + text7 + " <- " + source11.Id);
									osageSkinParameterSet.Load(stream);
									osageSkinParameterSet.Format = BinaryFormat.DT;
								}
							}
							if (objsetName.EndsWith("ITM000"))
							{
								ObjectInfo objectInfo = objectDatabase2.GetObjectInfo(object2.Name);
								object2.Id = ((objectInfo.Id == 0) ? num14 : objectInfo.Id);
							}
							else
							{
								object2.Name += "_x";
								object2.Id = num14;
							}
							foreach (Mesh mesh2 in object2.Meshes)
							{
								foreach (SubMesh subMesh in mesh2.SubMeshes)
								{
									if (subMesh.PrimitiveType != PrimitiveType.TriangleStrip)
									{
										subMesh.PrimitiveType = PrimitiveType.TriangleStrip;
										subMesh.Indices = Stripifier.Stripify(subMesh.Indices);
									}
								}
							}
							objectSetInfo.Objects.Add(new ObjectInfo
							{
								Name = object2.Name.ToUpper(),
								Id = object2.Id
							});
							num14++;
							for (int num15 = 0; num15 < object2.Materials.Count; num15++)
							{
								Material material = object2.Materials[num15];
								Material material2 = new Material();
								material2.Name = material.Name;
								material2.ShaderFlags = material.ShaderFlags;
								material2.BlendFlags = material.BlendFlags;
								material2.Flags = material.Flags;
								material2.Diffuse = material.Diffuse;
								material2.Specular = material.Specular;
								material2.Ambient = material.Ambient;
								material2.Emission = material.Emission;
								material2.Shininess = material.Shininess;
								if (material.ShaderName == "SKIN" || material.ShaderName == "HAIR")
								{
									if (material.MaterialTextures[0].TextureId != 212676728 && material.MaterialTextures[0].TextureId != 1509989155)
									{
										material2.MaterialTextures[0] = material.MaterialTextures[0];
										try
										{
											material2.MaterialTextures[0].TextureId = textureDatabase2.GetTextureInfo(textureDatabase3.GetTextureInfo(material2.MaterialTextures[0].TextureId).Name).Id;
										}
										catch
										{
										}
									}
									if (material.MaterialTextures[1].TextureId != 212676728 && material.MaterialTextures[1].TextureId != 1509989155)
									{
										material2.MaterialTextures[1] = material.MaterialTextures[4];
										try
										{
											material2.MaterialTextures[1].TextureId = textureDatabase2.GetTextureInfo(textureDatabase3.GetTextureInfo(material2.MaterialTextures[1].TextureId).Name).Id;
										}
										catch
										{
										}
									}
									if (material.MaterialTextures[3].TextureId != 212676728 && material.MaterialTextures[3].TextureId != 1509989155)
									{
										material2.MaterialTextures[3] = material.MaterialTextures[3];
										try
										{
											material2.MaterialTextures[3].TextureId = textureDatabase2.GetTextureInfo(textureDatabase3.GetTextureInfo(material2.MaterialTextures[3].TextureId).Name).Id;
										}
										catch
										{
										}
									}
									material2.ShaderName = "ITEM";
									ushort num16 = 32768;
									if (!args.Contains("-s"))
									{
										num16 += 8192;
									}
									if (material.ShaderName == "HAIR")
									{
										num16 += 16384;
									}
									material2.Ambient = new Vector4(material2.Ambient.X, material2.Ambient.Y, material2.Ambient.Z, (int)num16);
								}
								else
								{
									for (int num17 = 0; num17 < 7; num17++)
									{
										if (material.MaterialTextures[num17].TextureId != 212676728 && material.MaterialTextures[num17].TextureId != 1509989155)
										{
											material2.MaterialTextures[num17] = material.MaterialTextures[num17];
											try
											{
												material2.MaterialTextures[num17].TextureId = textureDatabase2.GetTextureInfo(textureDatabase3.GetTextureInfo(material2.MaterialTextures[num17].TextureId).Name).Id;
											}
											catch
											{
											}
										}
									}
									material2.ShaderName = "ITEM";
								}
								object2.Materials[num15] = material2;
							}
							if (object2.Skin == null)
							{
								continue;
							}
							Skeleton? skeleton2 = boneData2.Skeletons.Find((Skeleton x) => x.Name == objsetName.Substring(0, 3));
							Skeleton skeleton3 = boneData.Skeletons.Find((Skeleton x) => x.Name == objsetName.Substring(0, 3));
							if (skeleton2 == null || skeleton3 == null)
							{
								if (boneData2.Skeletons.Count <= 0 && boneData.Skeletons.Count <= 0)
								{
									throw new InvalidDataException("A required bone data file contains no Skeletons. How is this possible? Aborting.");
								}
								Skeleton skeleton4 = boneData2.Skeletons[0];
								skeleton3 = boneData.Skeletons[0];
								if (skeleton4 == null || skeleton3 == null)
								{
									throw new InvalidDataException("Skeleton for " + objsetName.Substring(0, 3) + " not in both bone datas, and CMN could not be located. Aborting.");
								}
							}
							foreach (MikuMikuLibrary.Objects.BoneInfo bone5 in object2.Skin.Bones)
							{
								if (!bone5.IsEx)
								{
									bone5.Id = (uint)skeleton3.ObjectBoneNames.IndexOf(bone5.Name);
								}
							}
							List<IBlock> list12 = new List<IBlock>();
							if (object2.Skin == null)
							{
								continue;
							}
							foreach (IBlock block in object2.Skin.Blocks)
							{
								if (!(block is MotionBlock motionBlock))
								{
									continue;
								}
								Console.WriteLine(motionBlock.Name.IndexOf("_mot"));
								string text8 = motionBlock.Name.Substring(2, motionBlock.Name.IndexOf("_mot") - 2);
								Dictionary<string, List<string>> dictionary10;
								switch (text8)
								{
								case "cloth_skirt":
									dictionary10 = dictionary2;
									break;
								case "cloth_long":
									dictionary10 = dictionary3;
									break;
								case "hair_twin":
									dictionary10 = dictionary4;
									osageSetting.Objects.Add(new OsageSettingObject
									{
										Name = object2.Name,
										Category = "long_tail_f"
									});
									break;
								case "hair_long":
									dictionary10 = dictionary5;
									osageSetting.Objects.Add(new OsageSettingObject
									{
										Name = object2.Name,
										Category = "long_hair_f"
									});
									break;
								default:
									throw new InvalidDataException("Unknown mot block type " + text8);
								}
								foreach (string key in dictionary10.Keys)
								{
									MikuMikuLibrary.Objects.BoneInfo boneInfoByName = object2.Skin.GetBoneInfoByName(key);
									for (int num18 = 0; num18 < dictionary10[key].Count; num18++)
									{
										string text9 = $"{key.Substring(0, key.IndexOf("000_wj"))}{num18:D3}_wj";
										boneInfoByName = object2.Skin.GetBoneInfoByName(text9);
										if (boneInfoByName == null)
										{
											Console.WriteLine($"Failed to get bone {key.Substring(0, key.IndexOf("000_wj"))}{num18:D3}_wj. Attempting to locate bone {key.Substring(0, key.IndexOf("000_wj"))}{num18 + 1:D3}_wj");
											continue;
										}
										if (boneInfoByName.Parent == null)
										{
											boneInfoByName.Parent = object2.Skin.GetBoneInfoByName(motionBlock.ParentName);
										}
										OsageBlock osageBlock = new OsageBlock();
										osageBlock.ParentName = boneInfoByName.Parent.Name;
										osageBlock.Name = "e_" + boneInfoByName.Name.Substring(2, boneInfoByName.Name.IndexOf($"_{num18:D3}_wj") - 2);
										osageBlock.ExternalName = "c_" + boneInfoByName.Name.Substring(2, boneInfoByName.Name.IndexOf($"_{num18:D3}_wj") - 2) + "_osg";
										if (dictionary8.TryGetValue(key, out var value5))
										{
											osageSkinParameterSet.Parameters.Add(CloneSkinParameterTemplate(value5));
											List<OsageSkinParameter> parameters = osageSkinParameterSet.Parameters;
											parameters[parameters.Count - 1].Nodes.RemoveRange(0, num18);
										}
										Matrix4x4.Invert(boneInfoByName.InverseBindPoseMatrix, out var result);
										Matrix4x4 matrix = Matrix4x4.Multiply(result, boneInfoByName.Parent?.InverseBindPoseMatrix ?? Matrix4x4.Identity);
										Matrix4x4.Decompose(matrix, out var scale, out var rotation, out var _);
										rotation = Quaternion.Normalize(rotation);
										osageBlock.Position = matrix.Translation;
										osageBlock.Rotation = rotation.ToEulerAngles();
										osageBlock.Scale = scale;
										if (dictionary9.ContainsKey(text9))
										{
											ExpressionBlock expressionBlock = new ExpressionBlock();
											expressionBlock.Position = matrix.Translation;
											expressionBlock.Scale = Vector3.One;
											expressionBlock.Name = "n_" + boneInfoByName.Name.Substring(2, boneInfoByName.Name.IndexOf($"_{num18:D3}_wj") - 2) + "_ex";
											expressionBlock.ParentName = osageBlock.ParentName;
											expressionBlock.Expressions.AddRange(dictionary9[key]);
											osageBlock.Position = Vector3.Zero;
											osageBlock.ParentName = expressionBlock.Name;
											list12.Add(expressionBlock);
										}
										foreach (string item16 in dictionary10[key].Skip(num18))
										{
											OsageNode osageNode = new OsageNode();
											osageNode.Name = item16;
											osageNode.Length = dictionary6[item16];
											osageBlock.Nodes.Add(osageNode);
										}
										string name = osageBlock.Name;
										if (!(name == "e_hair_twin_l"))
										{
											if (name == "e_hair_twin_r")
											{
												osageBlock.Rotation = new Vector3(0.3490656f, 1.570796f, 0f);
											}
										}
										else
										{
											osageBlock.Rotation = new Vector3((float)Math.PI * 8f / 9f, 1.570796f, 0f);
										}
										list12.Add(osageBlock);
										break;
									}
								}
							}
							object2.Skin?.Blocks.RemoveAll((IBlock x) => x.Signature == "MOT");
							object2.Skin?.Blocks.AddRange(list12);
							string skinParameterFileName = "ext_skp_" + object2.Name + ".txt";
							string skinParameterOutputPath = Path.Combine(args[2], "rom", "skin_param", skinParameterFileName);
                            // Full conversions use the corrected catalog; focused physical probes retain source parameters.
                            bool correctedParameter = moduleFilterIndex < 0 && CorrectedSkinParamCatalog.TryCopy(skinParameterFileName, skinParameterOutputPath);
                            if (!correctedParameter && osageSkinParameterSet.Parameters.Count != 0)
							{
								osageSkinParameterSet.Save(skinParameterOutputPath);
							}
						}
						objectDatabase3.ObjectSets.Add(objectSetInfo);
						objectSet.Format = BinaryFormat.DT;
						textureSet.Format = BinaryFormat.DT;
						using (FarcArchive farcArchive6 = new FarcArchive())
						{
							if (objsetName.EndsWith("ITM000"))
							{
								farcArchive6.Add(objsetName.ToLower() + "_obj.bin", objectSet);
								farcArchive6.Add(objsetName.ToLower() + "_tex.bin", textureSet);
								farcArchive6.Save(Path.Combine(args[2], "rom", "objset", objsetName.ToLower() + ".farc"));
							}
							else
							{
								farcArchive6.Add(objsetName.ToLower() + "_x_obj.bin", objectSet);
								farcArchive6.Add(objsetName.ToLower() + "_x_tex.bin", textureSet);
								farcArchive6.Save(Path.Combine(args[2], "rom", "objset", objsetName.ToLower() + "_x.farc"));
							}
						}
						num11++;
					}
					objectDatabase3.Save(Path.Combine(args[2], "rom", "objset", "mod_obj_db.bin"));
					textureDatabase2.Save(Path.Combine(args[2], "rom", "objset", "mod_tex_db.bin"));
                    if (!args.Contains("--target-items", StringComparer.OrdinalIgnoreCase) &&
                        !args.Contains("--target-id", StringComparer.OrdinalIgnoreCase) &&
                        !args.Contains("--target-cos-id", StringComparer.OrdinalIgnoreCase))
                    {
                        HeadAccessoryMigrator.Write(args[2]);
                        if (sourceModuleIds.Values.Contains((short)0) && File.Exists(Path.Combine(args[2], "rom", "objset", "mikitm001_x.farc")))
                            DefaultMikuHeadphones.Write(args[2]);
                        HeadAdornmentTransfer.Write(args[2]);
                        HeadSlotAccessoryOwnership.Write(args[2]);
                        NativeHairReplaceWriter.Write(args[2], archive2, objectDatabase2, sourceModuleIds.Values);
                    }
					if (!args.Contains("--no-motion"))
					{
					for (int num19 = 0; num19 < 10; num19++)
					{
						CharacterType characterType = (CharacterType)num19;
						if (moduleFilterIndex >= 0 && !modules.Any(x => x.Character == characterType))
							continue;
						string charName = characterType.ToString();
						Skeleton mmCharSkel = boneData.Skeletons.First((Skeleton x) => x.Name == charName);
						Skeleton xCharSkel = boneData2.Skeletons.First((Skeleton x) => x.Name == charName);
						MotionSetInfo motionSetInfo = mmMotionData.GetMotionSetInfo(charName);
						MotionSet motionSet = archive2.Open<FarcArchive>("rom/rob/mot_" + charName + ".farc").Open("mot_" + charName + ".bin", delegate(Stream source14)
						{
							MotionSet motionSet2 = new MotionSet();
							motionSet2.Load(source14, mmCharSkel, mmMotionData);
							return motionSet2;
						});
						string fileName3 = "mot_" + charName.ToLower() + ".farc";
						using Stream source13 = contentSource2.OpenRequired(fileName3);
						using FarcArchive farcArchive7 = BinaryFile.Load<FarcArchive>(source13);
						foreach (string fileName4 in farcArchive7.FileNames)
						{
							if (!fileName4.EndsWith(".mot"))
							{
								continue;
							}
							Motion motion = farcArchive7.Open(fileName4, delegate(Stream source14)
							{
								Motion motion2 = new Motion();
								motion2.Load(source14, xCharSkel);
								return motion2;
							});
							for (int num20 = 0; num20 < motionSet.Motions.Count; num20++)
							{
								if (ClosedEyeRestorer.Matches(motionSetInfo.Motions[num20].Name, motion.Name))
								{
									motionSet.Motions[num20] = motion;
								}
							}
						}
						using FarcArchive farcArchive8 = new FarcArchive();
						MemoryStream memoryStream6 = new MemoryStream();
						motionSet.Save(memoryStream6, mmCharSkel, mmMotionData, leaveOpen: true);
						farcArchive8.Add("mot_" + charName + ".bin", memoryStream6, leaveOpen: false);
						farcArchive8.Save(Path.Combine(args[2], "rom", "rob", "mot_" + charName + ".farc"));
					}
					}
					if (osageSetting.Objects.Count > 0)
					{
					ParameterTreeWriter osageSettingWriter = new ParameterTreeWriter();
					osageSettingWriter.Write("cat", osageSetting.Categories, delegate(OsageSettingCategory x)
					{
						osageSettingWriter.Write("name", x.Name);
						osageSettingWriter.Write("osg", x.Osg, delegate(OsageSettingParameter o)
						{
							osageSettingWriter.Write("exf", o.EXF);
							osageSettingWriter.Write("parts", o.Parts.ToString());
							osageSettingWriter.Write("root", o.Root);
						});
					});
					osageSettingWriter.Write("obj", osageSetting.Objects, delegate(OsageSettingObject x)
					{
						osageSettingWriter.Write("cat", x.Category);
						osageSettingWriter.Write("name", x.Name.ToUpper());
					});
					osageSettingWriter.Save(Path.Combine(args[2], "rom", "skin_param", "mod_osage_setting.txt"));
					}
					// CorrectedSkinParamCatalog is deliberately excluded from this prototype.
				}
				streamWriter2.Close();
                XOriginalHairstyles.Write(args[2],args[1]);
                SecondaryAccessoryCompatibility.Write(args[2],args[1]);
                NativeAccessoryReplaceWriter.Write(args[2],args[1]);
                XExclusiveAccessories.Write(args[2],args[1]);
				ClosedEyeRestorer.Write(args[2], args[1]);
                PspSurpriseSmileReplacer.Write(args[2], args[1]);
                int recommendations = RecommendedModulesWriter.Write(args[2], modules.Select(module =>
					new RecommendedBinding(sourceModuleIds.GetValueOrDefault(module.ModuleID, module.ModuleID), (int)module.Character, module.CosID)));
				Console.WriteLine($"[RECOMMENDED] Integrated {recommendations} allocated costume recommendations.");
                if (modules.Any(module => module.Character == CharacterType.MIK))
                {
                    CurrentPvFacialPatches.Write(args[2]);
                    StardusterMouthPatch.Write(args[2], archive2);
                }
                if (!args.Contains("--no-motion"))
                    FreelyTomorrowClKaoPatch.Write(args[2], archive2);
                HairRootCompensation.Write(args[2]);
                OsageCategoryPropagation.Write(args[2]);
                OsageModuleCoverage.Write(args[2]);
                Console.WriteLine($"[COMPLETE] Conversion completed successfully. Output: {Path.GetFullPath(args[2])}");
			}
		}
	}
}


