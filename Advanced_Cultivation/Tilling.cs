﻿using System;
using System.Collections.Generic;
using System.Text;
using System.Diagnostics;
using UnityEngine;
using Verse;
using System.Linq;
using Verse.AI;
using RimWorld;
using Harmony;

namespace Advanced_Cultivation
{
    [DefOf]
    public static class TerrainDefOf
    {
        public static TerrainDef AC_SoilTilled;
        public static TerrainDef Soil;
    }

    [DefOf]
    public static class ResearchProjectDefOf
    {
        public static ResearchProjectDef AC_Tilling;
    }

    [DefOf]
    public static class ReservationLayerDefOf
    {
        public static ReservationLayerDef Floor;
    }

    [StaticConstructorOnStartup]
    internal static class TillToggle
    {
        private static readonly HashSet<Zone_Growing> DisallowedZones = new HashSet<Zone_Growing>();

        static TillToggle() => HarmonyInstance.Create("ToggleTilling").PatchAll();

        public static void Reset() => DisallowedZones.Clear();
        public static bool IsAllowed(Zone_Growing zone) => !DisallowedZones.Contains(zone);
        public static void SetAllowed(Zone_Growing zone, bool allowed)
        {
            var isDisallowed = DisallowedZones.Contains(zone);
            if (allowed && isDisallowed) { DisallowedZones.Remove(zone); }
            else if (!allowed && !isDisallowed) { DisallowedZones.Add(zone); }
        }
    }

    [StaticConstructorOnStartup]
    public static class TexCommand
    {
        public static readonly Texture2D Till = ContentFinder<Texture2D>.Get("UI/Commands/Till");
    }

    // Harmony Patches

    [HarmonyPatch(typeof(Game), nameof(Game.InitNewGame))]
    internal static class Verse_Game_InitNewGame
    {
        private static void Prefix() => TillToggle.Reset();
    }

    [HarmonyPatch(typeof(Game), nameof(Game.LoadGame))]
    internal static class Verse_GameLoadGame
    {
        private static void Prefix() => TillToggle.Reset();
    }

    [HarmonyPatch(typeof(Zone_Growing), nameof(Zone_Growing.GetGizmos))]
    internal static class RimWorld_Zone_Growing_GetGizmos
    {
        private static void Postfix(Zone_Growing __instance, ref IEnumerable<Gizmo> __result)
        {
            if (ResearchProjectDefOf.AC_Tilling.IsFinished)
            {
                var toggleTillCommand = new Command_Toggle
                {
                    defaultLabel = "AC.CommandToggleTill".Translate(),
                    defaultDesc = "AC.CommandToggleTillDesc".Translate(),
                    icon = TexCommand.Till,
                    isActive = () => TillToggle.IsAllowed(__instance),
                    toggleAction = () => TillToggle.SetAllowed(__instance, !TillToggle.IsAllowed(__instance))
                };

                __result = new List<Gizmo>(__result) { toggleTillCommand };
            } else
            {
                __result = new List<Gizmo>(__result);
            }
        }
    }

    [HarmonyPatch(typeof(Zone_Growing), nameof(Zone_Growing.ExposeData))]
    internal static class RimWorld_Zone_Growing_ExposeData
    {
        private static void Postfix(ref Zone_Growing __instance)
        {
            var allowTill = TillToggle.IsAllowed(__instance);
            Scribe_Values.Look(ref allowTill, "allowTill", true);
            TillToggle.SetAllowed(__instance, allowTill);
        }
    }

    [HarmonyPatch(typeof(JobDriver_PlantHarvest), "PlantWorkDoneToil")]
    internal static class RimWorld_JobDriver_PlantHarvest_PlantWorkDoneToil
    {
        private static void Postfix(ref JobDriver_PlantHarvest __instance, ref Toil __result)
        {
            Toil toil = __result;
            toil.initAction += () =>
            {
                IntVec3 c = toil.actor.jobs.curJob.GetTarget(TargetIndex.A).Cell;
                TerrainDef terrainTo = toil.actor.Map.terrainGrid.TerrainAt(c).GetModExtension<TerrainExtension>().tilledFrom;
                toil.actor.Map.terrainGrid.SetTerrain(c, terrainTo);
            };
        }
    }

    // DefModExtension

    public class TerrainExtension : DefModExtension
    {
        public static readonly TerrainExtension defaultValues = new TerrainExtension();
        public bool tillable = false;
        public TerrainDef tillsTo = TerrainDefOf.AC_SoilTilled;
        public TerrainDef tilledFrom = TerrainDefOf.Soil;
        public float tillWorkAmount = 400;
    }

    public class JobDriver_TillCell : JobDriver
    {
        protected virtual StatDef SpeedStat
        {
            get
            {
                return StatDefOf.PlantWorkSpeed;
            }
        }

        private float workleft = -1000f;

        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            Pawn pawn = this.pawn;
            LocalTargetInfo targetA = this.job.targetA;
            Job job = this.job;
            ReservationLayerDef floor = ReservationLayerDefOf.Floor;
            return (pawn.Reserve(targetA, job, 1, -1, floor, errorOnFailed)
                    && pawn.Reserve(targetA, job, 1, -1, null, errorOnFailed));
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            IntVec3 c = this.TargetLocA;
            TerrainDef curTerrain = this.Map.terrainGrid.TerrainAt(c);
            bool tillAllowed = TillToggle.IsAllowed((Zone_Growing)this.Map.zoneManager.ZoneAt(c));
            bool terrainTillable = curTerrain.GetModExtension<TerrainExtension>().tillable;
            this.FailOn(() => !tillAllowed && !terrainTillable);
            TerrainDef tillTerrain = curTerrain.GetModExtension<TerrainExtension>().tillsTo;
            float tillworkAmount = curTerrain.GetModExtension<TerrainExtension>().tillWorkAmount;
            yield return Toils_Goto.GotoCell(TargetIndex.A, PathEndMode.Touch);
            Toil tillCell = new Toil();
            tillCell.initAction = delegate ()
            {
                this.workleft = tillworkAmount;
            };
            tillCell.tickAction = delegate ()
            {
                Pawn actor = tillCell.actor;
                float statValue = actor.GetStatValue(StatDefOf.PlantWorkSpeed, true);
                float num = statValue;
                this.workleft -= num;
                if (actor.skills != null)
                {
                    actor.skills.Learn(SkillDefOf.Plants, 0.05f, false);
                }
                if (this.workleft <= 0f)
                {
                    this.Map.terrainGrid.SetTerrain(c, tillTerrain);
                    this.ReadyForNextToil();
                    return;
                }
            };
            tillCell.FailOnCannotTouch(TargetIndex.A, PathEndMode.Touch);
            tillCell.WithProgressBar(TargetIndex.A, () => 1f - this.workleft / tillworkAmount, false, -0.5f);
            tillCell.defaultCompleteMode = ToilCompleteMode.Never;
            tillCell.activeSkill = (() => SkillDefOf.Plants);
            yield return tillCell;
            yield break;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look<float>(ref this.workleft, "workLeft", 0f, false);
        }
    }

    public class WorkGiver_Till : WorkGiver_Scanner
    {
        public override PathEndMode PathEndMode
        {
            get
            {
                return PathEndMode.ClosestTouch;
            }
        }

        public override bool AllowUnreachable
        {
            get
            {
                return true;
            }
        }

        public override IEnumerable<IntVec3> PotentialWorkCellsGlobal(Pawn pawn)
        {
            Danger maxDanger = pawn.NormalMaxDanger();
            List<Zone> zonesList = pawn.Map.zoneManager.AllZones;
            ReservationLayerDef layer = ReservationLayerDefOf.Floor;
            for (int j = 0; j <zonesList.Count; j++)
            {
                Zone_Growing growZone = zonesList[j] as Zone_Growing;
                if (growZone != null && TillToggle.IsAllowed(growZone))
                {
                    if (growZone.cells.Count == 0)
                    {
                        Log.ErrorOnce("Grow zone has 0 cells: " + growZone, -563487, false);
                    } else if (!growZone.ContainsStaticFire && pawn.CanReach(growZone.Cells[0], PathEndMode.OnCell, maxDanger, false, TraverseMode.ByPawn))
                    {
                        for (int k = 0; k < growZone.cells.Count; k++)
                        {
                            IntVec3 c = growZone.cells[k];
                            if (pawn.Map.terrainGrid.TerrainAt(c).GetModExtension<TerrainExtension>().tillable
                                && pawn.CanReach(growZone.Cells[0], PathEndMode.OnCell, maxDanger, false, TraverseMode.ByPawn)
                                && pawn.CanReserve(c, 1, -1, layer, false))
                            {
                                yield return growZone.cells[k];
                            }
                        }
                    }
                }
            }
            yield break;
        }

        public override Job JobOnCell(Pawn pawn, IntVec3 c, bool forced = false)
        {
            Map map = pawn.Map;
            bool flag = false;
            if (c.IsForbidden(pawn))
            {
                return null;
            }
            if (!PlantUtility.GrowthSeasonNow(c, map, true))
            {
                return null;
            }
            List<Thing> thingList = c.GetThingList(map);
            for (int i = 0; i < thingList.Count; i++)
            {
                Thing thing = thingList[i];
                if ((thing is Blueprint || thing is Frame) && thing.Faction == pawn.Faction)
                {
                    flag = true;
                }
            }
            if (flag)
            {
                Thing edifice = c.GetEdifice(map);
                if (edifice == null || edifice.def.fertility < 0f)
                {
                    return null;
                }
            }
            Plant plant = c.GetPlant(map);
            if (plant != null && plant.def.plant.blockAdjacentSow)
            {
                LocalTargetInfo target = plant;
                if (!pawn.CanReserve(target, 1, -1, null, forced) || plant.IsForbidden(pawn))
                {
                    return null;
                }
                return new Job(JobDefOf.CutPlant, plant);
            }
            int j = 0;
            while (j < thingList.Count)
            {
                Thing thing3 = thingList[j];
                if (thing3.def.BlockPlanting)
                {
                    LocalTargetInfo target = thing3;
                    if (!pawn.CanReserve(target, 1, -1, null, forced))
                    {
                        return null;
                    }
                    if (thing3.def.category == ThingCategory.Plant)
                    {
                        if (!thing3.IsForbidden(pawn))
                        {
                            return new Job(JobDefOf.CutPlant, thing3);
                        }
                        return null;
                    }
                    else
                    {
                        if (thing3.def.EverHaulable)
                        {
                            return HaulAIUtility.HaulAsideJobFor(pawn, thing3);
                        }
                        return null;
                    }
                }
                else
                {
                    j++;
                }
            }
            if (PlantUtility.GrowthSeasonNow(c, map, true))
            {
                LocalTargetInfo target = c;
                if (pawn.CanReserve(target, 1, -1, null, forced))
                {
                    return new Job(JobDefOf.AC_Till, c);
                }
            }
            return null;
        }
    }
} // namespace Advanced Cultivation
