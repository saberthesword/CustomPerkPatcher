using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Synthesis;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.FormKeys.SkyrimSE;
using Mutagen.Bethesda.Plugins.Records;

namespace CustomPerkCompiler
{
    // ======================================================================
    // JSON DATA STRUCTURES
    // ======================================================================
    public class CustomTree
    {
        public string? ProxyVanillaSkill { get; set; }
        public List<string>? Factions { get; set; }
        public List<string>? ActorTypeKeywords { get; set; }
        public List<string>? RequiredSpellKeywords { get; set; }
        public List<string>? RequiredSpellIDs { get; set; }
        public List<CustomPerkEntry>? Perks { get; set; }

        [System.Text.Json.Serialization.JsonIgnore]
        public List<Regex>? PrecompiledSpellIDRegex { get; set; }
    }

    public class CustomPerkEntry
    {
        public string? EditorID { get; set; }
        public string? BasePerk { get; set; }
    }

    public class CustomPerksMapping
    {
        public List<CustomTree> CustomTrees { get; set; } = new();
    }

    // ======================================================================
    // MAIN PATCHER
    // ======================================================================
    public class Program
    {
        public static async Task<int> Main(string[] args)
        {
            return await SynthesisPipeline.Instance
                .AddPatch<ISkyrimMod, ISkyrimModGetter>(RunPatch)
                .SetTypicalOpen(GameRelease.SkyrimSE, "TUS_CustomPerks.esp")
                .Run(args);
        }

        public static void RunPatch(IPatcherState<ISkyrimMod, ISkyrimModGetter> state)
        {
            // 1. Resolve Path Safely
            string configPath = Path.Combine(state.DataFolderPath, "CustomPerksMapping.json");

            if (!File.Exists(configPath))
            {
                Console.WriteLine($"[Error] Configuration file not found at: {configPath}. Skipping execution.");
                return;
            }

            CustomPerksMapping customPerks;
            try
            {
                string jsonText = File.ReadAllText(configPath);
                customPerks = JsonSerializer.Deserialize<CustomPerksMapping>(jsonText) ?? new CustomPerksMapping();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Error] Error reading configuration JSON: {ex.Message}");
                return;
            }

            foreach (var tree in customPerks.CustomTrees)
            {
                if (tree.RequiredSpellIDs != null && tree.RequiredSpellIDs.Count > 0)
                {
                    tree.PrecompiledSpellIDRegex = new List<Regex>();
                    foreach (var snippet in tree.RequiredSpellIDs)
                    {
                        string pattern = Regex.Escape(snippet).Replace("\\.\\.\\.", ".*");
                        tree.PrecompiledSpellIDRegex.Add(new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled));
                    }
                }
            }

            var perkLookup = state.LoadOrder.PriorityOrder.Perk().WinningOverrides()
                .Where(p => !string.IsNullOrEmpty(p.EditorID))
                .GroupBy(p => p.EditorID!.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            var originalToDuplicateMap = new Dictionary<FormKey, IPerkGetter>();
            var perkAdditionMap = new Dictionary<FormKey, HashSet<FormKey>>();
            var precompiledPerks = new Dictionary<string, IPerkGetter>(StringComparer.OrdinalIgnoreCase);

            void MapPerkAddition(FormKey anchorKey, FormKey newPerkKey)
            {
                if (!perkAdditionMap.ContainsKey(anchorKey))
                    perkAdditionMap[anchorKey] = new HashSet<FormKey>();
                perkAdditionMap[anchorKey].Add(newPerkKey);
            }

            // ======================================================================
            // PASS 1: GENERATE PERKS DIRECTLY INTO MAIN PATCHMOD
            // ======================================================================
            Console.WriteLine("Pass 1: Validating and extracting master records into main patch...");

            foreach (var tree in customPerks.CustomTrees)
            {
                if (tree.Perks == null || tree.Perks.Count == 0) continue;

                foreach (var customEntry in tree.Perks)
                {
                    if (string.IsNullOrEmpty(customEntry.EditorID)) continue;
                    string targetID = customEntry.EditorID.Trim();

                    if (!perkLookup.TryGetValue(targetID, out var sourcePerk) || sourcePerk == null)
                    {
                        Console.WriteLine($"[Warning] Skipping '{targetID}' - Not found in active load order.");
                        continue;
                    }

                    if (!originalToDuplicateMap.TryGetValue(sourcePerk.FormKey, out var existingDuplicated))
                    {
                        if (!state.PatchMod.ModHeader.MasterReferences.Any(m => m.Master == sourcePerk.FormKey.ModKey))
                        {
                            state.PatchMod.ModHeader.MasterReferences.Add(new MasterReference { Master = sourcePerk.FormKey.ModKey });
                        }

                        var mutableDuplicatedPerk = state.PatchMod.Perks.DuplicateInAsNewRecord(sourcePerk);
                        mutableDuplicatedPerk.EditorID = "TUS_Custom_" + targetID;

                        originalToDuplicateMap[sourcePerk.FormKey] = mutableDuplicatedPerk;
                        precompiledPerks[targetID] = mutableDuplicatedPerk;
                    }

                    if (originalToDuplicateMap.TryGetValue(sourcePerk.FormKey, out var finalDuplicatedRef))
                    {
                        MapPerkAddition(sourcePerk.FormKey, finalDuplicatedRef.FormKey);

                        if (!string.IsNullOrEmpty(customEntry.BasePerk))
                        {
                            string baseID = customEntry.BasePerk.Trim();
                            if (perkLookup.TryGetValue(baseID, out var basePerkRecord) && basePerkRecord != null)
                            {
                                MapPerkAddition(basePerkRecord.FormKey, finalDuplicatedRef.FormKey);
                            }
                        }
                    }
                }
            }

            Console.WriteLine($"Precompiled {originalToDuplicateMap.Count} unique custom standalone perks.");

            // ======================================================================
            // PASS 2: REMAP INTERNAL LINKS INSIDE MAIN PATCHMOD
            // ======================================================================
            Console.WriteLine("Pass 2: Remapping internal condition links...");
            foreach (var customEntry in precompiledPerks.Values)
            {
                if (!state.PatchMod.Perks.TryGetValue(customEntry.FormKey, out var duplicatedPerk) || duplicatedPerk == null)
                    continue;

                void RemapCondition(Condition? cond)
                {
                    if (cond is ConditionFloat floatCond && floatCond.Data is HasPerkConditionData perkData)
                    {
                        var currentFormKey = perkData.Perk.Link.FormKey;
                        if (currentFormKey != FormKey.Null && originalToDuplicateMap.TryGetValue(currentFormKey, out var linkedCustomPerk))
                        {
                            perkData.Perk.Link.FormKey = linkedCustomPerk.FormKey;
                        }
                    }
                }

                if (duplicatedPerk.Conditions != null)
                {
                    foreach (var cond in duplicatedPerk.Conditions) RemapCondition(cond);
                }

                if (duplicatedPerk.Effects != null)
                {
                    foreach (var effect in duplicatedPerk.Effects)
                    {
                        if (effect?.Conditions != null)
                        {
                            foreach (var perkCond in effect.Conditions)
                            {
                                if (perkCond?.Conditions != null)
                                {
                                    foreach (var subCond in perkCond.Conditions) RemapCondition(subCond);
                                }
                            }
                        }
                    }
                }
            }

            // ======================================================================
            // PASS 3: DISTRIBUTE TO NPCS
            // ======================================================================
            Console.WriteLine("Pass 3: Distributing custom duplicated perks via granular identity & spell scans...");

            Console.WriteLine("Building active NPC whitelist...");
            var activeNPCFormKeys = new HashSet<FormKey>();
            var directWorldKeys = new HashSet<FormKey>();

            foreach (var placedNpc in state.LoadOrder.PriorityOrder.PlacedNpc().WinningOverrides())
            {
                if (placedNpc.Base != null && !placedNpc.Base.IsNull)
                {
                    directWorldKeys.Add(placedNpc.Base.FormKey);
                }
            }

            foreach (var leveledNpc in state.LoadOrder.PriorityOrder.LeveledNpc().WinningOverrides())
            {
                if (leveledNpc.Entries == null) continue;
                foreach (var entry in leveledNpc.Entries)
                {
                    if (entry.Data != null && entry.Data.Reference != null && !entry.Data.Reference.IsNull)
                    {
                        directWorldKeys.Add(entry.Data.Reference.FormKey);
                    }
                }
            }

            foreach (var baseNpc in state.LoadOrder.PriorityOrder.Npc().WinningOverrides())
            {
                if (baseNpc.Configuration != null && baseNpc.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Unique))
                {
                    directWorldKeys.Add(baseNpc.FormKey);
                }
            }

            foreach (var key in directWorldKeys)
            {
                activeNPCFormKeys.Add(key);
                var currentKey = key;
                var visitedTemplates = new HashSet<FormKey> { currentKey };

                while (state.LinkCache.TryResolve<INpcGetter>(currentKey, out var currentNpc) && currentNpc != null)
                {
                    if (currentNpc.Template.FormKey != FormKey.Null && visitedTemplates.Add(currentNpc.Template.FormKey))
                    {
                        activeNPCFormKeys.Add(currentNpc.Template.FormKey);
                        currentKey = currentNpc.Template.FormKey;
                        continue;
                    }
                    break;
                }
            }
            Console.WriteLine($"Found {activeNPCFormKeys.Count} total active NPCs and parent templates in the load order.");

            int patchedNPCCount = 0;

            var allNpcs = state.LoadOrder.PriorityOrder.Npc().WinningOverrides();

            foreach (var npcGetter in allNpcs)
            {
                // Fix: If an earlier module or plugin has already touched this record 
                // inside our own working patch, look at the mutated state instead!
                INpcGetter activeNpc = npcGetter;
                if (state.PatchMod.Npcs.TryGetValue(npcGetter.FormKey, out var workingOverride))
                {
                    activeNpc = workingOverride;
                }

                if (!activeNPCFormKeys.Contains(activeNpc.FormKey))
                    continue;

                // ... Swap out all downstream 'npcGetter' references to 'activeNpc' instead ...
                if (activeNpc.Configuration != null && activeNpc.Configuration.Flags.HasFlag(NpcConfiguration.Flag.IsCharGenFacePreset))
                    continue;

                var gatheredKeywords = GatherAllKeywordsInChain(activeNpc, state.LinkCache);

                if (gatheredKeywords.Contains("ActorTypePlayer"))
                    continue;

                // Target checks
                bool isDragonPriest = gatheredKeywords.Contains("ActorTypeDragonPriest");
                bool isAnimal = gatheredKeywords.Contains("ActorTypeAnimal");
                bool isDwarvenAutomaton = gatheredKeywords.Contains("ActorTypeDwarven");
                bool isNonHumanoidDaedra = gatheredKeywords.Contains("ActorTypeDaedra") && !gatheredKeywords.Contains("ActorTypeNPC");

                // --- NEW EXCLUSIONS: MONSTERS & CREATURES ---
                bool isDragon = gatheredKeywords.Contains("ActorTypeDragon");
                bool isGiant = gatheredKeywords.Contains("ActorTypeGiant");
                bool isTroll = gatheredKeywords.Contains("ActorTypeTroll");
                // Catch-alls for other beasts (Chaurus, Spriggans, etc.) that aren't tagged as standard NPCs
                bool isMonster = gatheredKeywords.Contains("ActorTypeMonster") && !gatheredKeywords.Contains("ActorTypeNPC");
                bool isCreature = gatheredKeywords.Contains("ActorTypeCreature") && !gatheredKeywords.Contains("ActorTypeNPC");

                bool isSummon = false;
                if (activeNpc.EditorID != null)
                {
                    isSummon = activeNpc.EditorID.Contains("Summon", StringComparison.OrdinalIgnoreCase) ||
                               activeNpc.EditorID.Contains("Atronach", StringComparison.OrdinalIgnoreCase) ||
                               activeNpc.EditorID.Contains("Familiar", StringComparison.OrdinalIgnoreCase);
                }

                if (isAnimal || isDwarvenAutomaton || isNonHumanoidDaedra || isSummon || isDragon || isGiant || isTroll || isMonster || isCreature)
                    continue;

                if (activeNpc.Race.TryResolve(state.LinkCache, out var raceRecord))
                {
                    bool hasVanillaPerks = activeNpc.Perks != null && activeNpc.Perks.Count > 0;
                    bool isPlayable = raceRecord.Flags.HasFlag(Mutagen.Bethesda.Skyrim.Race.Flag.Playable);

                    if (!isPlayable && !hasVanillaPerks && !isDragonPriest)
                        continue;
                }

                var perksToAdd = new Dictionary<FormKey, byte>();

                if (activeNpc.Perks != null)
                {
                    foreach (var perkPlacement in activeNpc.Perks)
                    {
                        if (perkPlacement.Perk.FormKey != FormKey.Null && perkAdditionMap.TryGetValue(perkPlacement.Perk.FormKey, out var customPerksTriggered))
                        {
                            foreach (var customPerk in customPerksTriggered)
                            {
                                if (!perksToAdd.ContainsKey(customPerk)) perksToAdd[customPerk] = perkPlacement.Rank;
                            }
                        }
                    }
                }

                var gatheredClasses = GatherAllClassesInChain(activeNpc, state.LinkCache);
                var gatheredFactions = GatherAllFactionsInChain(activeNpc, state.LinkCache);
                var gatheredSpellKeywords = GatherAllSpellKeywordsInChain(activeNpc, state.LinkCache);
                var gatheredSpellIDs = GatherAllSpellEditorIDsInChain(activeNpc, state.LinkCache);
                int npcLevel = ResolveMaxLevelInChain(activeNpc, state.LinkCache);

                foreach (var tree in customPerks.CustomTrees)
                {
                    if (tree.Perks == null || tree.Perks.Count == 0) continue;

                    bool qualifiesForTree = true;

                    if (tree.ActorTypeKeywords != null && tree.ActorTypeKeywords.Count > 0)
                    {
                        if (!tree.ActorTypeKeywords.Any(kw => gatheredKeywords.Contains(kw)))
                            qualifiesForTree = false;
                    }

                    if (qualifiesForTree && tree.RequiredSpellKeywords != null && tree.RequiredSpellKeywords.Count > 0)
                    {
                        if (!tree.RequiredSpellKeywords.Any(kw => gatheredSpellKeywords.Contains(kw)))
                            qualifiesForTree = false;
                    }

                    if (qualifiesForTree && tree.PrecompiledSpellIDRegex != null && tree.PrecompiledSpellIDRegex.Count > 0)
                    {
                        bool hasMatchingSpellID = tree.PrecompiledSpellIDRegex.Any(regex =>
                            gatheredSpellIDs.Any(id => regex.IsMatch(id)));

                        if (!hasMatchingSpellID)
                            qualifiesForTree = false;
                    }

                    if (qualifiesForTree && tree.Factions != null && tree.Factions.Count > 0)
                    {
                        if (!gatheredFactions.Any(f => tree.Factions.Contains(f.EditorID ?? "", StringComparer.OrdinalIgnoreCase)))
                            qualifiesForTree = false;
                    }

                    int estimatedSkillLevel = 0;

                    if (qualifiesForTree && !string.IsNullOrEmpty(tree.ProxyVanillaSkill))
                    {
                        int explicitSkill = GetExplicitNpcSkill(activeNpc, tree.ProxyVanillaSkill);
                        bool isAutoCalc = activeNpc.Configuration == null || activeNpc.Configuration.Flags.HasFlag(NpcConfiguration.Flag.AutoCalcStats);

                        if (!isAutoCalc && explicitSkill >= 0)
                        {
                            estimatedSkillLevel = explicitSkill;
                        }
                        else
                        {
                            byte maxWeight = 0;
                            if (gatheredClasses.Count > 0)
                            {
                                maxWeight = gatheredClasses.Max(cls => GetSkillWeight(cls, tree.ProxyVanillaSkill));
                            }

                            bool matchesHeuristic = EvaluateTextHeuristics(activeNpc, gatheredClasses, gatheredFactions, gatheredKeywords, tree.ProxyVanillaSkill);

                            if (maxWeight == 0 && matchesHeuristic)
                            {
                                maxWeight = 1;
                            }

                            if (maxWeight == 0 && !matchesHeuristic)
                            {
                                qualifiesForTree = false;
                            }

                            if (qualifiesForTree)
                            {
                                estimatedSkillLevel = 15 + (int)((npcLevel - 1) * (maxWeight * 0.85));
                            }
                        }
                    }
                    else
                    {
                        estimatedSkillLevel = npcLevel * 2;
                    }

                    if (!qualifiesForTree) continue;

                    foreach (var customEntry in tree.Perks)
                    {
                        if (string.IsNullOrEmpty(customEntry.EditorID)) continue;
                        string targetID = customEntry.EditorID.Trim();

                        if (precompiledPerks.TryGetValue(targetID, out var standalonePerkRecord) && standalonePerkRecord != null)
                        {
                            int skillReq = GetPerkLevelRequirement(customEntry.BasePerk ?? customEntry.EditorID);
                            bool levelQualifies = (estimatedSkillLevel > 15) && (estimatedSkillLevel >= skillReq);

                            if (skillReq < 25) levelQualifies = npcLevel >= 6;
                            else if (skillReq >= 80) levelQualifies = npcLevel >= 32;
                            else if (skillReq >= 60) levelQualifies = npcLevel >= 22;
                            else if (skillReq >= 40) levelQualifies = npcLevel >= 12;
                            else if (skillReq >= 25) levelQualifies = npcLevel >= 6;

                            if (levelQualifies)
                            {
                                if (!perksToAdd.ContainsKey(standalonePerkRecord.FormKey))
                                {
                                    perksToAdd[standalonePerkRecord.FormKey] = 1;
                                }
                            }
                        }
                    }
                }

                if (perksToAdd.Count > 0)
                {
                    // Reverted back to storing inside the primary PatchMod
                    var npcCopy = state.PatchMod.Npcs.GetOrAddAsOverride(activeNpc);

                    var existingPerkKeys = npcCopy.Perks?.Select(p => p.Perk.FormKey).ToHashSet() ?? new HashSet<FormKey>();
                    bool npcModified = false;
                    int perksAddedToThisNPC = 0; // Local counter for this specific NPC

                    foreach (var kvp in perksToAdd)
                    {
                        if (!existingPerkKeys.Contains(kvp.Key))
                        {
                            var newPerkPlacement = new PerkPlacement { Rank = kvp.Value };
                            newPerkPlacement.SetPerk(kvp.Key);

                            if (npcCopy.Perks == null)
                            {
                                npcCopy.Perks = new Noggog.ExtendedList<PerkPlacement>();
                            }

                            npcCopy.Perks.Add(newPerkPlacement);
                            existingPerkKeys.Add(kvp.Key);
                            npcModified = true;
                            perksAddedToThisNPC++; // Count the perk addition
                        }
                    }

                    if (npcModified)
                    {
                        patchedNPCCount++;

                        // Safely extract the Name and EditorID strings
                        string npcEditorID = activeNpc.EditorID ?? "UnknownID";
                        string npcName = activeNpc.Name?.ToString() ?? "Unknown Name";

                        // Print the real-time granular distribution log
                        Console.WriteLine($"Distributed {perksAddedToThisNPC} perks to {npcEditorID} ({npcName})");
                    }
                }
            }
        }

        // ======================================================================
        // FAIL-SAFE DATA ACCUMULATION RESOLVERS
        // ======================================================================
        private static List<IClassGetter> GatherAllClassesInChain(INpcGetter npc, ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache)
        {
            var classes = new List<IClassGetter>();
            var current = npc;
            var visited = new HashSet<FormKey>();

            while (current != null)
            {
                if (current.Class.FormKey != FormKey.Null && linkCache.TryResolve<IClassGetter>(current.Class.FormKey, out var npcClass) && npcClass != null)
                {
                    if (!string.Equals(npcClass.EditorID, "DefaultNPC", StringComparison.OrdinalIgnoreCase))
                    {
                        classes.Add(npcClass);
                    }
                }
                if (current.Template.FormKey != FormKey.Null && visited.Add(current.Template.FormKey))
                {
                    if (linkCache.TryResolve<INpcGetter>(current.Template.FormKey, out var template))
                    {
                        current = template;
                        continue;
                    }
                }
                break;
            }
            return classes;
        }

        private static List<IFactionGetter> GatherAllFactionsInChain(INpcGetter npc, ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache)
        {
            var factions = new List<IFactionGetter>();
            var current = npc;
            var visited = new HashSet<FormKey>();
            var trackedFactionKeys = new HashSet<FormKey>();

            while (current != null)
            {
                if (current.Factions != null)
                {
                    foreach (var placement in current.Factions)
                    {
                        if (placement.Faction.FormKey != FormKey.Null && trackedFactionKeys.Add(placement.Faction.FormKey))
                        {
                            if (linkCache.TryResolve<IFactionGetter>(placement.Faction.FormKey, out var faction) && faction != null)
                            {
                                factions.Add(faction);
                            }
                        }
                    }
                }
                if (current.Template.FormKey != FormKey.Null && visited.Add(current.Template.FormKey))
                {
                    if (linkCache.TryResolve<INpcGetter>(current.Template.FormKey, out var template))
                    {
                        current = template;
                        continue;
                    }
                }
                break;
            }
            return factions;
        }

        private static HashSet<string> GatherAllKeywordsInChain(INpcGetter npc, ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache)
        {
            var keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var current = npc;
            var visited = new HashSet<FormKey>();

            while (current != null)
            {
                if (current.Keywords != null)
                {
                    foreach (var kwRef in current.Keywords)
                    {
                        if (kwRef.FormKey != FormKey.Null && linkCache.TryResolve<IKeywordGetter>(kwRef.FormKey, out var kw) && kw?.EditorID != null)
                        {
                            keywords.Add(kw.EditorID.Trim());
                        }
                    }
                }

                if (current.Race.FormKey != FormKey.Null && linkCache.TryResolve<IRaceGetter>(current.Race.FormKey, out var raceRecord) && raceRecord?.Keywords != null)
                {
                    foreach (var kwRef in raceRecord.Keywords)
                    {
                        if (kwRef.FormKey != FormKey.Null && linkCache.TryResolve<IKeywordGetter>(kwRef.FormKey, out var kw) && kw?.EditorID != null)
                        {
                            keywords.Add(kw.EditorID.Trim());
                        }
                    }
                }

                if (current.Template.FormKey != FormKey.Null && visited.Add(current.Template.FormKey))
                {
                    if (linkCache.TryResolve<INpcGetter>(current.Template.FormKey, out var template))
                    {
                        current = template;
                        continue;
                    }
                }
                break;
            }
            return keywords;
        }

        private static HashSet<string> GatherAllSpellKeywordsInChain(INpcGetter npc, ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache)
        {
            var keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var current = npc;
            var visited = new HashSet<FormKey>();

            while (current != null)
            {
                if (current.ActorEffect != null)
                {
                    foreach (var effectLink in current.ActorEffect)
                    {
                        if (effectLink.FormKey == FormKey.Null) continue;

                        if (linkCache.TryResolve<ISpellGetter>(effectLink.FormKey, out var spell) && spell?.Effects != null)
                        {
                            foreach (var effect in spell.Effects)
                            {
                                if (effect.BaseEffect.FormKey == FormKey.Null) continue;

                                if (linkCache.TryResolve<IMagicEffectGetter>(effect.BaseEffect.FormKey, out var mgef) && mgef?.Keywords != null)
                                {
                                    foreach (var kwLink in mgef.Keywords)
                                    {
                                        if (kwLink.FormKey != FormKey.Null && linkCache.TryResolve<IKeywordGetter>(kwLink.FormKey, out var kw) && kw?.EditorID != null)
                                        {
                                            keywords.Add(kw.EditorID.Trim());
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                if (current.Template.FormKey != FormKey.Null && visited.Add(current.Template.FormKey))
                {
                    if (linkCache.TryResolve<INpcGetter>(current.Template.FormKey, out var template))
                    {
                        current = template;
                        continue;
                    }
                }
                break;
            }
            return keywords;
        }

        private static HashSet<string> GatherAllSpellEditorIDsInChain(INpcGetter npc, ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache)
        {
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var current = npc;
            var visited = new HashSet<FormKey>();

            while (current != null)
            {
                if (current.ActorEffect != null)
                {
                    foreach (var effectLink in current.ActorEffect)
                    {
                        if (effectLink.FormKey == FormKey.Null) continue;

                        if (linkCache.TryResolve<ISpellGetter>(effectLink.FormKey, out var spell) && spell?.EditorID != null)
                        {
                            ids.Add(spell.EditorID.Trim());
                        }
                    }
                }

                if (current.Template.FormKey != FormKey.Null && visited.Add(current.Template.FormKey))
                {
                    if (linkCache.TryResolve<INpcGetter>(current.Template.FormKey, out var template))
                    {
                        current = template;
                        continue;
                    }
                }
                break;
            }
            return ids;
        }

        private static int ResolveMaxLevelInChain(INpcGetter npc, ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache)
        {
            int maxLevel = 1;
            var current = npc;
            var visited = new HashSet<FormKey>();

            while (current != null)
            {
                var levelData = current.Configuration?.Level;
                if (levelData != null)
                {
                    var type = levelData.GetType();
                    var properties = type.GetInterfaces()
                                         .SelectMany(i => i.GetProperties())
                                         .Concat(type.GetProperties());

                    var levelProp = properties.FirstOrDefault(p => p.Name == "Level");
                    var minLevelProp = properties.FirstOrDefault(p => p.Name == "CalcMinLevel" || p.Name == "MinLevel");
                    var maxLevelProp = properties.FirstOrDefault(p => p.Name == "CalcMaxLevel" || p.Name == "MaxLevel");

                    if (levelProp != null)
                    {
                        int val = Convert.ToInt32(levelProp.GetValue(levelData));
                        if (val > maxLevel) maxLevel = val;
                    }
                    else if (minLevelProp != null)
                    {
                        int minVal = Convert.ToInt32(minLevelProp.GetValue(levelData));
                        int maxVal = maxLevelProp != null ? Convert.ToInt32(maxLevelProp.GetValue(levelData)) : 0;

                        int evaluatedLevel = (maxVal == 0 || maxVal >= 45) ? 45 : Math.Max(minVal, maxVal);

                        if (evaluatedLevel > maxLevel) maxLevel = evaluatedLevel;
                    }
                }

                if (current.Template.FormKey != FormKey.Null && visited.Add(current.Template.FormKey))
                {
                    if (linkCache.TryResolve<INpcGetter>(current.Template.FormKey, out var template))
                    {
                        current = template;
                        continue;
                    }
                }
                break;
            }
            return maxLevel;
        }

        private static bool EvaluateTextHeuristics(INpcGetter npc, List<IClassGetter> classes, List<IFactionGetter> factions, HashSet<string> keywords, string skillName)
        {
            string skillLower = skillName.ToLower();

            var textTokens = new List<string> { npc.EditorID ?? "", npc.Name?.ToString() ?? "" };
            foreach (var cls in classes)
            {
                textTokens.Add(cls.EditorID ?? "");
                textTokens.Add(cls.Name?.ToString() ?? "");
            }
            foreach (var fac in factions)
            {
                textTokens.Add(fac.EditorID ?? "");
                textTokens.Add(fac.Name?.ToString() ?? "");
            }
            foreach (var kw in keywords)
            {
                textTokens.Add(kw);
            }

            string combinedText = string.Join(" ", textTokens).ToLower();

            if (skillLower.Contains("destruction"))
                return combinedText.Contains("destr") || combinedText.Contains("mage") || combinedText.Contains("warlock") || combinedText.Contains("wizard") || combinedText.Contains("witch") || combinedText.Contains("winterhold");
            if (skillLower.Contains("conjuration"))
                return combinedText.Contains("conju") || combinedText.Contains("necro") || combinedText.Contains("summoner") || combinedText.Contains("warlock");
            if (skillLower.Contains("restoration"))
                return combinedText.Contains("restor") || combinedText.Contains("healer") || combinedText.Contains("priest") || combinedText.Contains("paladin") || combinedText.Contains("vigilant");
            if (skillLower.Contains("alteration"))
                return combinedText.Contains("alter") || combinedText.Contains("mage") || combinedText.Contains("spellsword");
            if (skillLower.Contains("illusion"))
                return combinedText.Contains("illu") || combinedText.Contains("nightblade") || combinedText.Contains("assassin") || combinedText.Contains("shrouded");

            if (skillLower.Contains("onehanded") || skillLower.Contains("one handed"))
                return combinedText.Contains("1h") || combinedText.Contains("warrior") || combinedText.Contains("soldier") || combinedText.Contains("guard") || combinedText.Contains("knight") || combinedText.Contains("bandit") || combinedText.Contains("spellsword") || combinedText.Contains("imperial") || combinedText.Contains("stormcloak");
            if (skillLower.Contains("twohanded") || skillLower.Contains("two handed"))
                return combinedText.Contains("2h") || combinedText.Contains("barbarian") || combinedText.Contains("berserker") || combinedText.Contains("warchief") || combinedText.Contains("companions");
            if (skillLower.Contains("archery") || skillLower.Contains("marksman"))
                return combinedText.Contains("arch") || combinedText.Contains("marksm") || combinedText.Contains("ranger") || combinedText.Contains("hunter") || combinedText.Contains("scout");
            if (skillLower.Contains("block"))
                return combinedText.Contains("shield") || combinedText.Contains("defender") || combinedText.Contains("knight") || combinedText.Contains("guard");

            if (skillLower.Contains("heavyarmor") || skillLower.Contains("heavy armor"))
                return combinedText.Contains("heavy") || combinedText.Contains("knight") || combinedText.Contains("paladin") || combinedText.Contains("iron") || combinedText.Contains("steel");
            if (skillLower.Contains("lightarmor") || skillLower.Contains("light armor"))
                return combinedText.Contains("light") || combinedText.Contains("thief") || combinedText.Contains("assassin") || combinedText.Contains("rogue") || combinedText.Contains("scout") || combinedText.Contains("ranger");
            if (skillLower.Contains("sneak"))
                return combinedText.Contains("sneak") || combinedText.Contains("thief") || combinedText.Contains("assassin") || combinedText.Contains("rogue") || combinedText.Contains("shadow") || combinedText.Contains("nightingale");

            return false;
        }

        private static byte GetSkillWeight(IClassGetter npcClass, string skillName)
        {
            if (npcClass == null || npcClass.SkillWeights == null || string.IsNullOrEmpty(skillName)) return 0;

            string cleanInput = Regex.Replace(skillName, @"[^a-zA-Z0-9]", "").ToLower();
            var weightsType = npcClass.SkillWeights.GetType();
            var prop = weightsType.GetProperties()
                .FirstOrDefault(p => Regex.Replace(p.Name, @"[^a-zA-Z0-9]", "").ToLower() == cleanInput);

            if (prop != null)
            {
                var val = prop.GetValue(npcClass.SkillWeights);
                if (val != null) return Convert.ToByte(val);
            }
            return 0;
        }

        private static int GetPerkLevelRequirement(string? basePerkName)
        {
            if (string.IsNullOrEmpty(basePerkName)) return 0;

            var match = Regex.Match(basePerkName, @"\d+$");
            if (match.Success && int.TryParse(match.Value, out int lvl)) return lvl;

            string lower = basePerkName.ToLower();
            if (lower.Contains("novice")) return 0;
            if (lower.Contains("apprentice")) return 25;
            if (lower.Contains("adept")) return 50;
            if (lower.Contains("expert")) return 75;
            if (lower.Contains("master")) return 100;

            return 0;
        }

        private static int GetExplicitNpcSkill(INpcGetter npc, string skillName)
        {
            if (npc.PlayerSkills == null) return -1;

            string cleanSkillName = Regex.Replace(skillName, @"[^a-zA-Z0-9]", "").ToLower();
            var type = npc.PlayerSkills.GetType();

            var prop = type.GetProperties()
                .FirstOrDefault(p => Regex.Replace(p.Name, @"[^a-zA-Z0-9]", "").ToLower() == cleanSkillName);

            if (prop != null)
            {
                var val = prop.GetValue(npc.PlayerSkills);
                if (val != null)
                {
                    return Convert.ToInt32(val);
                }
            }

            var dictProp = type.GetProperties().FirstOrDefault(p => p.Name.Contains("Skills") || p.Name.Contains("Values"));
            if (dictProp != null)
            {
                var dictObj = dictProp.GetValue(npc.PlayerSkills);
                if (dictObj is System.Collections.IEnumerable enumerable)
                {
                    foreach (var item in enumerable)
                    {
                        if (item == null) continue;
                        var itemType = item.GetType();
                        var keyProp = itemType.GetProperty("Key");
                        var valueProp = itemType.GetProperty("Value");

                        if (keyProp != null && valueProp != null)
                        {
                            string keyStr = keyProp.GetValue(item)?.ToString() ?? "";
                            if (Regex.Replace(keyStr, @"[^a-zA-Z0-9]", "").ToLower() == cleanSkillName)
                            {
                                var val = valueProp.GetValue(item);
                                if (val != null)
                                {
                                    return Convert.ToInt32(val);
                                }
                            }
                        }
                    }
                }
            }

            return -1;
        }
    }
}

public static class PerkPlacementExtensions
{
    public static void SetPerk(this PerkPlacement placement, FormKey key)
    {
        dynamic dPlacement = placement;
        try { dPlacement.Perk.SetTo(key); }
        catch { dPlacement.Perk = key; }
    }
}