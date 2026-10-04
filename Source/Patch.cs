using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using FluffyManager;
using HarmonyLib;
using Verse;

namespace ColonyManagerFix
{
    [StaticConstructorOnStartup]
    public static class Startup
    {
        static Startup()
        {
            new Harmony("ifchen0.colonymanagerfix").PatchAll();
        }
    }

    /// <summary>
    /// Building_AIManager.Tick asks for map.mapPawns.FreeColonists on every tick, which goes through
    /// MapPawns.AllPawnsUnspawned and walks every thing holder on the map (storage, beds, pawn inventories, ...).
    /// The list is only really used on work ticks (ticksGame % speed == 0) to pick the managing pawn; on all other
    /// ticks the building just checks that it is non-empty to keep its lights blinking. Recomputes the list on work
    /// ticks and at most once per RefreshInterval otherwise, returning a cached copy in between.
    /// The call is replaced inside Tick rather than patching GetFreeColonists itself, because that private method is
    /// small enough for the JIT to inline into Tick, which would bypass a patch on it.
    /// </summary>
    [HarmonyPatch(typeof(Building_AIManager), "Tick")]
    public static class Patch_Building_AIManager_Tick
    {
        private const int RefreshInterval = 60;

        private static readonly MethodInfo GetFreeColonistsMethod =
            AccessTools.Method(typeof(Building_AIManager), "GetFreeColonists");

        public static bool Prepare() => GetFreeColonistsMethod != null;

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            int replaced = 0;
            foreach (CodeInstruction instruction in instructions)
            {
                if (instruction.Calls(GetFreeColonistsMethod))
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = AccessTools.Method(typeof(Patch_Building_AIManager_Tick), nameof(GetFreeColonistsCached));
                    replaced++;
                }
                yield return instruction;
            }
            if (replaced != 1)
                Log.Warning($"[Colony Manager Fix] Expected one GetFreeColonists call in Building_AIManager.Tick, found {replaced}.");
        }

        private class Cache
        {
            public readonly List<Pawn> Pawns = new List<Pawn>();
            public int LastRefreshTick = -1;
        }

        private static readonly Dictionary<Building_AIManager, Cache> Caches = new Dictionary<Building_AIManager, Cache>();

        public static List<Pawn> GetFreeColonistsCached(Building_AIManager instance, Map map)
        {
            if (map?.mapPawns == null)
                return new List<Pawn>();

            if (!Caches.TryGetValue(instance, out Cache cache))
            {
                PruneDestroyed();
                cache = new Cache();
                Caches[instance] = cache;
            }

            int ticksGame = Find.TickManager.TicksGame;
            int speed = instance.ManagerStation?.Props?.speed ?? 0;
            bool workTick = speed <= 0 || ticksGame % speed == 0;
            if (workTick || cache.LastRefreshTick < 0 || ticksGame - cache.LastRefreshTick >= RefreshInterval || ticksGame < cache.LastRefreshTick)
            {
                // Copy: vanilla reuses the returned list for every FreeHumanlikesOfFaction caller.
                cache.Pawns.Clear();
                cache.Pawns.AddRange(map.mapPawns.FreeColonists);
                cache.LastRefreshTick = ticksGame;
            }

            return cache.Pawns;
        }

        private static void PruneDestroyed()
        {
            List<Building_AIManager> stale = null;
            foreach (Building_AIManager building in Caches.Keys)
            {
                if (building.Destroyed || !building.Spawned)
                    (stale ??= new List<Building_AIManager>()).Add(building);
            }
            if (stale != null)
            {
                foreach (Building_AIManager building in stale)
                    Caches.Remove(building);
            }
        }
    }
}
