using System.Reflection;
using FirstLib.FirstRead;
using FirstLib.FirstRead.Common;

namespace XRomConverter;

/// <summary>
/// Applies exact MegaMix+ hair identities to the physical Project DIVA X
/// items without changing module IDs, costume IDs, or object-set assets.
/// </summary>
internal static class HairIdentityRemapper
{
	private const string ResourceName = "XRomConverter.HairIdentityV5.csv";
	private const int ExpectedEntryCount = 247;
	private const int HairSubId = 1;
	private const short FirstPrivateItemNumber = 9000;

	private sealed record HairIdentity(short SourceModuleId, CharacterType Character, short TargetHairNumber);

	private sealed record HairPlan(
		HairIdentity Identity,
		CostumePart HairPart,
		Item SourceItem,
		string SourceSignature);

	private static readonly IReadOnlyDictionary<short, HairIdentity> sIdentities = Load();

	public static void Apply(
		List<ModuleInfo> modules,
		List<List<Item>> characterItems,
		List<List<Costume>> characterCostumes)
	{
		Dictionary<short, ModuleInfo> modulesById = modules.ToDictionary(x => x.ModuleID);
		int aliasesCreated = 0;
		int occupantsRelocated = 0;
		int modulesRepointed = 0;
		int ambiguousModulesPreserved = 0;

		for (int characterIndex = 0; characterIndex < 10; characterIndex++)
		{
			CharacterType character = (CharacterType)characterIndex;
			List<Item> items = characterItems[characterIndex];
			List<Costume> costumes = characterCostumes[characterIndex];
			Dictionary<short, Item> itemsByNumber = items.ToDictionary(x => x.ItemNumber);
			Dictionary<short, Costume> costumesById = costumes.ToDictionary(x => x.CostumeID);
			List<HairPlan> plans = new();

			foreach (HairIdentity identity in sIdentities.Values
				.Where(x => x.Character == character)
				.OrderBy(x => x.SourceModuleId))
			{
				if (!modulesById.TryGetValue(identity.SourceModuleId, out ModuleInfo? module))
				{
					continue;
				}
				if (module.Character != identity.Character)
				{
					throw new InvalidDataException(
						$"Hair identity module {identity.SourceModuleId} is {module.Character}, expected {identity.Character}.");
				}
				if (!costumesById.TryGetValue(module.CosID, out Costume? costume))
				{
					throw new InvalidDataException(
						$"Hair identity module {identity.SourceModuleId} references missing COS_{module.CosID + 1}.");
				}

				List<(CostumePart Part, Item Item)> hairParts = costume.Parts
					.Where(x => x.ItemNumber != 0 && itemsByNumber.ContainsKey(x.ItemNumber))
					.Select(x => (Part: x, Item: itemsByNumber[x.ItemNumber]))
					.Where(x => (int)x.Item.SubID == HairSubId)
					.ToList();
				if (hairParts.Count != 1)
				{
					throw new InvalidDataException(
						$"Hair identity module {identity.SourceModuleId} COS_{module.CosID + 1} has {hairParts.Count} hair items; expected exactly one.");
				}

				plans.Add(new HairPlan(
					identity,
					hairParts[0].Part,
					hairParts[0].Item,
					GetItemSignature(hairParts[0].Item)));
			}

			HashSet<short> reservedTargets = plans.Select(x => x.Identity.TargetHairNumber).ToHashSet();
			short nextPrivateItemNumber = FirstPrivateItemNumber;

			short AllocatePrivateItemNumber()
			{
				while (itemsByNumber.ContainsKey(nextPrivateItemNumber) || reservedTargets.Contains(nextPrivateItemNumber))
				{
					nextPrivateItemNumber++;
					if (nextPrivateItemNumber > 9999)
					{
						throw new InvalidDataException($"No private item numbers remain for {character} hair remapping.");
					}
				}
				return nextPrivateItemNumber++;
			}

			foreach (IGrouping<short, HairPlan> targetGroup in plans
				.GroupBy(x => x.Identity.TargetHairNumber)
				.OrderBy(x => x.Key))
			{
				List<IGrouping<string, HairPlan>> physicalGroups = targetGroup
					.GroupBy(x => x.SourceSignature)
					.OrderBy(x => x.Min(y => y.Identity.SourceModuleId))
					.ToList();
				IGrouping<string, HairPlan> primaryPhysicalGroup = physicalGroups[0];
				HairPlan primaryPlan = primaryPhysicalGroup.OrderBy(x => x.Identity.SourceModuleId).First();

				foreach (IGrouping<string, HairPlan> ambiguousGroup in physicalGroups.Skip(1))
				{
					foreach (HairPlan ambiguousPlan in ambiguousGroup)
					{
						ambiguousModulesPreserved++;
						Console.WriteLine(
							$"[HAIR_IDENTITY] {character} module {ambiguousPlan.Identity.SourceModuleId}: " +
							$"native hair {targetGroup.Key} is physically ambiguous; preserving X item {ambiguousPlan.SourceItem.ItemNumber} for the full module.");
					}
				}

				if (itemsByNumber.TryGetValue(targetGroup.Key, out Item? occupiedItem) &&
					GetItemSignature(occupiedItem) != primaryPlan.SourceSignature)
				{
					short relocatedNumber = AllocatePrivateItemNumber();
					short occupiedNumber = occupiedItem.ItemNumber;
					foreach (Costume costume in costumes)
					{
						foreach (CostumePart part in costume.Parts.Where(x => x.ItemNumber == occupiedNumber))
						{
							part.ItemNumber = relocatedNumber;
						}
					}
					itemsByNumber.Remove(occupiedNumber);
					occupiedItem.ItemNumber = relocatedNumber;
					itemsByNumber.Add(relocatedNumber, occupiedItem);
					occupantsRelocated++;
					Console.WriteLine(
						$"[HAIR_IDENTITY] {character} item {occupiedNumber} ({occupiedItem.SubID}) -> private item {relocatedNumber}.");
				}

				if (!itemsByNumber.TryGetValue(targetGroup.Key, out Item? aliasItem))
				{
					aliasItem = CloneItem(primaryPlan.SourceItem, targetGroup.Key);
					items.Add(aliasItem);
					itemsByNumber.Add(aliasItem.ItemNumber, aliasItem);
					aliasesCreated++;
				}
				else if (GetItemSignature(aliasItem) != primaryPlan.SourceSignature)
				{
					throw new InvalidDataException(
						$"Failed to free {character} target hair item {targetGroup.Key}.");
				}

				foreach (HairPlan plan in primaryPhysicalGroup)
				{
					if (plan.HairPart.ItemNumber != targetGroup.Key)
					{
						plan.HairPart.ItemNumber = targetGroup.Key;
						modulesRepointed++;
					}
				}
			}
		}

		Console.WriteLine(
			$"[HAIR_IDENTITY] Applied exact aliases: {aliasesCreated} created, " +
			$"{occupantsRelocated} occupied IDs relocated, {modulesRepointed} costumes repointed, " +
			$"{ambiguousModulesPreserved} ambiguous full-module hairs preserved.");
	}

	private static Item CloneItem(Item source, short itemNumber)
	{
		return new Item
		{
			ItemNumber = itemNumber,
			Attribute = source.Attribute,
			U04 = source.U04,
			U06 = source.U06,
			U07 = source.U07,
			Type = source.Type,
			U09 = source.U09,
			Objects = source.Objects.Select(x => new ItemObject
			{
				ObjectID = x.ObjectID,
				RPK = x.RPK
			}).ToList(),
			TextureChanges = source.TextureChanges.Select(x => new TextureChange
			{
				OriginalTextureID = x.OriginalTextureID,
				ReplacementTextureID = x.ReplacementTextureID
			}).ToList(),
			ObjectSets = source.ObjectSets.ToList(),
			DestID = source.DestID,
			SubID = source.SubID,
			U0F = source.U0F,
			U10 = source.U10,
			U14 = source.U14
		};
	}

	private static string GetItemSignature(Item item)
	{
		return string.Join("|",
			item.Attribute,
			item.U04,
			item.U06,
			item.U07,
			item.Type,
			item.U09,
			item.DestID,
			item.SubID,
			item.U0F,
			item.U10,
			item.U14,
			string.Join(",", item.Objects.Select(x => $"{x.ObjectID}:{x.RPK}")),
			string.Join(",", item.TextureChanges.Select(x => $"{x.OriginalTextureID}:{x.ReplacementTextureID}")),
			string.Join(",", item.ObjectSets));
	}

	private static IReadOnlyDictionary<short, HairIdentity> Load()
	{
		Assembly assembly = typeof(HairIdentityRemapper).Assembly;
		using Stream? stream = assembly.GetManifestResourceStream(ResourceName);
		if (stream == null)
		{
			throw new InvalidDataException($"Missing embedded hair identity map '{ResourceName}'.");
		}

		using StreamReader reader = new(stream);
		Dictionary<short, HairIdentity> identities = new();
		string? header = reader.ReadLine();
		if (header != "source_module_id,character,target_hair_no")
		{
			throw new InvalidDataException("The embedded hair identity map has an invalid header.");
		}

		for (string? line = reader.ReadLine(); line != null; line = reader.ReadLine())
		{
			if (string.IsNullOrWhiteSpace(line))
			{
				continue;
			}
			string[] fields = line.Split(',');
			if (fields.Length != 3 ||
				!short.TryParse(fields[0], out short sourceModuleId) ||
				!TryParseCharacter(fields[1], out CharacterType character) ||
				!short.TryParse(fields[2], out short targetHairNumber))
			{
				throw new InvalidDataException($"Invalid hair identity row: {line}");
			}
			HairIdentity identity = new(sourceModuleId, character, targetHairNumber);
			if (!identities.TryAdd(sourceModuleId, identity))
			{
				throw new InvalidDataException($"Duplicate source module ID {sourceModuleId} in the hair identity map.");
			}
		}

		if (identities.Count != ExpectedEntryCount)
		{
			throw new InvalidDataException(
				$"The hair identity map contains {identities.Count} entries; expected {ExpectedEntryCount}.");
		}
		return identities;
	}

	private static bool TryParseCharacter(string value, out CharacterType character)
	{
		character = value switch
		{
			"MIKU" => CharacterType.MIK,
			"RIN" => CharacterType.RIN,
			"LEN" => CharacterType.LEN,
			"LUKA" => CharacterType.LUK,
			"NERU" => CharacterType.NER,
			"HAKU" => CharacterType.HAK,
			"KAITO" => CharacterType.KAI,
			"MEIKO" => CharacterType.MEI,
			"SAKINE" => CharacterType.SAK,
			"TETO" => CharacterType.TET,
			_ => CharacterType.EXT
		};
		return value is "MIKU" or "RIN" or "LEN" or "LUKA" or "NERU" or "HAKU" or "KAITO" or "MEIKO" or "SAKINE" or "TETO";
	}
}
