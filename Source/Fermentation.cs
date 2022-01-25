using System;
using System.Collections.Generic;
using System.Text;
using System.Diagnostics;
using UnityEngine;
using Verse;
using Verse.AI;
using RimWorld;

namespace AdvancedCultivation
{
    [DefOf]
    public static class ThingDefOf
    {
        public static ThingDef AC_RawCompost;
        public static ThingDef AC_FermentedCompost;
        public static ThingDef AC_CompostBin;
    }

    [DefOf]
    public static class JobDefOf
    {
        public static JobDef AC_EmptyCompostBin;
        public static JobDef AC_FillCompostBin;
        public static JobDef AC_Till;
        public static JobDef CutPlant;
    }

    // Fermenter CompProperties //

    public class CompProperties_AC_Fermenter : CompProperties
    {
        public float daysToFerment = 1f;
        public ThingDef fermentedThing;
        public float maxSafeTemp;
        public float minSafeTemp;
        public float maxFermentTemp;
        public float minFermentTemp;
        public float ruinProgressPerDegreePerTick;
        public float heatPerSecondPerFermenter = 0.5f;
        public float heatPushMinTemperature = -99999f;
        public float heatPushMaxTemperature = 99999f;

        public CompProperties_AC_Fermenter()
        {
            this.compClass = typeof(AC_CompFermenter);
        }
    }

    // Fermenter Comp //
    
    public class AC_CompFermenter : ThingComp
    {
        public float fermentProgress = 0f;
        public float ruinedPercent;

        public CompProperties_AC_Fermenter Props
        {
            get
            {
                return (CompProperties_AC_Fermenter)this.props;
            }
        }

        public int FermenterCount
        {
            get
            {
                if (this.parent.GetType() == typeof(Building_AC_CompostBin))
                {
                    Building_AC_CompostBin compostBin = (Building_AC_CompostBin)this.parent;
                    return compostBin.compostCount;
                }
                else
                {
                    return this.parent.stackCount;
                }
            }
        }

        public bool Ruined
        {
            get
            {
                return this.ruinedPercent >= 1f;
            }
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look<float>(ref this.fermentProgress, "AC.FermentProgressSave".Translate(), 0f, false);
        }

        private float CurrentTempProgressSpeedFactor
        {
            get
            {
                float minFermentTemp = this.Props.minFermentTemp;
                float maxFermentTemp = this.Props.maxFermentTemp;
                float minSafeTemp = this.Props.minSafeTemp;
                float maxSafeTemp = this.Props.maxSafeTemp;
                float ambientTemperature = this.parent.AmbientTemperature;
                if (ambientTemperature <= minSafeTemp)
                {
                    return 0.0f;
                }
                if (ambientTemperature <= minFermentTemp)
                {
                    return GenMath.LerpDouble(minSafeTemp,
                        minFermentTemp,
                        0.0f, 1f, ambientTemperature);
                }
                if (ambientTemperature <= maxFermentTemp)
                {
                    return 1.0f;
                }
                if (ambientTemperature <= maxSafeTemp)
                {
                    return GenMath.LerpDouble(maxFermentTemp,
                        maxSafeTemp,
                        1f, 0.0f, ambientTemperature);
                }
                return 0.0f;
            }
        }

        protected virtual bool ShouldPushHeatNow
        {
            get
            {
                if (this.parent.Map != null)
                {
                    return true;
                }
                else
                {
                    return false;
                }
            }
        }

        private float FermentProgressPerTickAtCurrentTemp
        {
            get
            {
                return 1f / (this.Props.daysToFerment * 60000f) * this.CurrentTempProgressSpeedFactor;
            }
        }

        public override void CompTick()
        {
            this.DoTicks(1);
        }
        
        public override void CompTickRare()
        {
            this.DoTicks(250);
        }
        
        private void DoTicks(int ticks)
        {
            if (this.parent.IsHashIntervalTick(60) && this.ShouldPushHeatNow)
            {
                CompProperties_AC_Fermenter props = this.Props;
                float ambientTemperature = this.parent.AmbientTemperature;
                if (ambientTemperature < props.heatPushMaxTemperature && ambientTemperature > props.heatPushMinTemperature)
                {
                    GenTemperature.PushHeat(this.parent.Position, this.parent.Map, props.heatPerSecondPerFermenter * this.FermenterCount);
                }
            }
            if (!this.Ruined)
            {
                if (this.FermenterCount > 0)
                {
                    if (this.fermentProgress < 1f)
                    {
                        float fermentPerTick = this.FermentProgressPerTickAtCurrentTemp;
                        this.fermentProgress += fermentPerTick * ticks;
                    }
                }
                if (this.fermentProgress >= 1f)
                {
                    if (this.parent.GetType() != typeof(Building_AC_CompostBin))
                    {
                        Thing thing = ThingMaker.MakeThing(this.Props.fermentedThing, null);
                        thing.stackCount = this.parent.stackCount;
                        GenSpawn.Spawn(thing, this.parent.Position, this.parent.Map);
                        this.parent.Destroy(DestroyMode.Vanish);
                    }
                }
            }
            else
            {
                if (this.parent.IsInAnyStorage() && this.parent.SpawnedOrAnyParentSpawned)
                {
                    Messages.Message(TranslatorFormattedStringExtensions.Translate("AC.CompostRuined", parent.Label)
                        .CapitalizeFirst(), new TargetInfo(this.parent.PositionHeld, this.parent.MapHeld, false), MessageTypeDefOf.NegativeEvent, true);
                }
                if (this.parent.GetType() != typeof(Building_AC_CompostBin))
                {
                    this.parent.Destroy(DestroyMode.Vanish);
                }
            }
            this.UpdateRuinedPercent(ticks);
        }

        private int EstimatedTicksLeft
        {
            get
            {
                return Mathf.Max(Mathf.RoundToInt((1f - this.fermentProgress) /
                    this.FermentProgressPerTickAtCurrentTemp), 0);
            }
        }

        public void Reset()
        {
            this.ruinedPercent = 0f;
            this.fermentProgress = 0f;
        }
        
        private void UpdateRuinedPercent(int ticks)
        {
            if (!this.Ruined && this.FermenterCount > 0)
            {
                float ambientTemperature = this.parent.AmbientTemperature;
                if (ambientTemperature > this.Props.maxSafeTemp)
                {
                    this.ruinedPercent += (ambientTemperature - this.Props.maxSafeTemp) * this.Props.ruinProgressPerDegreePerTick * (float)ticks;
                }
                else if (this.Props.minSafeTemp > ambientTemperature)
                {
                    this.ruinedPercent += (this.Props.minSafeTemp - ambientTemperature) * this.Props.ruinProgressPerDegreePerTick * (float)ticks;
                }
                else
                {
                    float averageSafeTemp = (this.Props.minSafeTemp + this.Props.maxSafeTemp) / 2;
                    this.ruinedPercent -= (averageSafeTemp / Math.Max(Math.Abs(averageSafeTemp - ambientTemperature), 5)) * this.Props.ruinProgressPerDegreePerTick * (float)ticks;
                }
                if (this.ruinedPercent >= 1f)
                {
                    this.ruinedPercent = 1f;
                    this.parent.BroadcastCompSignal("RuinedByTemperature");
                }
                else if (this.ruinedPercent < 0f)
                {
                    this.ruinedPercent = 0f;
                }
            }
        }

        public override void PreAbsorbStack(Thing otherStack, int count)
        {
            float t = (float)count / (float)(this.parent.stackCount + count);
            AC_CompFermenter comp = ((ThingWithComps)otherStack).GetComp<AC_CompFermenter>();
            float b = comp.fermentProgress;
            this.ruinedPercent = Mathf.Lerp(this.ruinedPercent, comp.ruinedPercent, t);
            this.fermentProgress = Mathf.Lerp(this.fermentProgress, b, t);
        }
        
        public override bool AllowStackWith(Thing other)
        {
            AC_CompFermenter comp = ((ThingWithComps)other).GetComp<AC_CompFermenter>();
            return this.Ruined == comp.Ruined;
        }

        public override void PostSplitOff(Thing piece)
        {
            AC_CompFermenter comp = ((ThingWithComps)piece).GetComp<AC_CompFermenter>();
            comp.fermentProgress = this.fermentProgress;
            comp.ruinedPercent = this.ruinedPercent;
        }

        public override string CompInspectStringExtra()
        {
            if (this.Ruined)
            {
                return "RuinedByTemperature".Translate();
            }
            StringBuilder stringBuilder = new StringBuilder();
            if (this.ruinedPercent > 0f)
            {
                float ambientTemperature = this.parent.AmbientTemperature;
                if (ambientTemperature > this.Props.maxSafeTemp)
                {
                    stringBuilder.AppendLine(string.Concat(new string[]
                        { "Overheating".Translate(),
                        ": ",
                        this.ruinedPercent.ToStringPercent() }));
            }
                else if (ambientTemperature <= this.Props.minSafeTemp)
                {
                    stringBuilder.AppendLine(string.Concat(new string[]
                        { "Freezing".Translate(),
                        ": ",
                        this.ruinedPercent.ToStringPercent() }));
                }
                else
                {
                    stringBuilder.AppendLine(string.Concat(new string[]
                        {
                        "AC.Recovering".Translate(),
                        ": ",
                        this.ruinedPercent.ToStringPercent()
                        }));
                }
            }
            if (!this.Ruined && this.FermenterCount > 0)
            {
                if (this.fermentProgress < 1f)
                {
                    string completes_in;
                    if (this.CurrentTempProgressSpeedFactor > 0)
                    {
                        completes_in = "AC.CompletesIn".Translate() + this.EstimatedTicksLeft.ToStringTicksToPeriod();
                    }
                    else
                    {
                        completes_in = "";
                    }
                    stringBuilder.AppendLine(string.Concat(new string[]
                        {
                            "AC.FermentProgress".Translate() + ": " + this.fermentProgress.ToStringPercent(),
                            completes_in
                        }));
                }
                else
                {
                    stringBuilder.AppendLine(string.Concat(new string[]
                        {
                            "AC.FermentationComplete".Translate()
                        }));
                }
            }
            return stringBuilder.ToString().TrimEndNewlines();
        }
    }

    // AC_Buildings //

    [StaticConstructorOnStartup]
    public class Building_AC_CompostBin : Building
    {
        // Graphics stuff //
        [Unsaved]
        public static Graphic compostBinEmpty = GraphicDatabase.Get<Graphic_Single>("CompostBin", ShaderDatabase.Cutout);
        [Unsaved]
        public static Graphic compostBinRaw = GraphicDatabase.Get<Graphic_Single>("CompostBinRaw", ShaderDatabase.Cutout);
        [Unsaved]
        public static Graphic compostBinFermented = GraphicDatabase.Get<Graphic_Single>("CompostBinFermented", ShaderDatabase.Cutout);
        
        public int compostCount;
        private Material barFilledCachedMat;
        private const int BaseFermentationDuration = 360000;
        private bool fermentFlag = false;
        public int Capacity = 25;
        private static readonly Vector2 BarSize = new Vector2(0.55f, 0.1f);
        private static readonly Color BarZeroProgressColor = new Color(0.4f, 0.27f, 0.22f);
        private static readonly Color BarFermentedColor = new Color(0.46f, 0.70f, 0.13f);
        private static readonly Material BarUnfilledMat = SolidColorMaterials.SimpleSolidColorMaterial(
            new Color(0.3f, 0.3f, 0.3f), false);

        public float Progress
        {
            get
            {
                AC_CompFermenter comp = this.GetComp<AC_CompFermenter>();
                return comp.fermentProgress;
            }
            set
            {
                AC_CompFermenter comp = this.GetComp<AC_CompFermenter>();
                if (value == this.Progress)
                {
                    return;
                }
                comp.fermentProgress = value;
                this.barFilledCachedMat = null;
            }
        }

        private Material BarFilledMat
        {
            get
            {
                if (this.barFilledCachedMat == null)
                {
                    this.barFilledCachedMat = SolidColorMaterials.SimpleSolidColorMaterial(Color.Lerp
                        (Building_AC_CompostBin.BarZeroProgressColor,
                        Building_AC_CompostBin.BarFermentedColor, this.Progress), false);
                }
                return this.barFilledCachedMat;
            }
        }

        public int SpaceLeftForCompost
        {
            get
            {
                if (this.Fermented)
                {
                    return 0;
                }
                return this.Capacity - this.compostCount;
            }
        }

        private bool Empty
        {
            get
            {
                return this.compostCount <= 0;
            }
        }

        public bool Fermented
        {
            get
            {
                return !this.Empty && this.Progress >= 1f;
            }
        }

        public override Graphic Graphic
        {
            get
            {
                if (this.Empty)
                {
                    if (Building_AC_CompostBin.compostBinEmpty != null)
                    {
                        return Building_AC_CompostBin.compostBinEmpty;
                    }
                    Log.Warning(this.def + " has no graphic data for its empty state!");
                }
                if (!this.Fermented)
                {
                    if (Building_AC_CompostBin.compostBinRaw != null)
                    {
                        return Building_AC_CompostBin.compostBinRaw;
                    }
                    Log.Warning(this.def + " has no graphic data for its filled with raw compost state!");
                }
                if (Building_AC_CompostBin.compostBinFermented != null)
                {
                    return Building_AC_CompostBin.compostBinFermented;
                }
                if (Building_AC_CompostBin.compostBinFermented == null)
                {
                    Log.Warning(this.def + " has no graphic data for its filled with fermented compost state!");
                }
                Log.ErrorOnce("Couldn't retrieve proper graphic data for " + this.def + ".", 764532);
                return BaseContent.BadGraphic;
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look<int>(ref this.compostCount, "compostCount", 0, false);
        }

        public void AddCompost(int count, Thing addedCompost)
        {
            if (this.Fermented)
            {
                Log.Warning("Tried to add compost to a bin with fermented compost in it. " +
                    "Colonists should harvest the bin first.");
                return;
            }
            int numToAdd = Mathf.Min(count, this.SpaceLeftForCompost);
            if (numToAdd <= 0)
            {
                return;
            }
            float addedFermenterCompProgress = ((ThingWithComps)addedCompost).GetComp<AC_CompFermenter>().fermentProgress;
            this.Progress = GenMath.WeightedAverage(this.Progress, (float)this.compostCount, addedFermenterCompProgress, (float)count);
            this.compostCount += numToAdd;
            base.Map.mapDrawer.MapMeshDirty(base.Position, MapMeshFlag.Things);
        }

        protected override void ReceiveCompSignal(string signal)
        {
            if (signal == "RuinedByTemperature")
            {
                Messages.Message(TranslatorFormattedStringExtensions.Translate("AC.CompostRuined", this.Label)
                    .CapitalizeFirst(), new TargetInfo(this.PositionHeld, this.MapHeld, false), MessageTypeDefOf.NegativeEvent, true);
                this.Reset();
            }
        }

        private void Reset()
        {
            Log.Message($"{ThingID} reset.");
            this.compostCount = 0;
            fermentFlag = false;
            this.GetComp<AC_CompFermenter>().Reset();
            base.Map.mapDrawer.MapMeshDirty(base.Position, MapMeshFlag.Things);
        }

        public void AddCompost(Thing compost)
        {
            int numToAdd = Mathf.Min(compost.stackCount, this.SpaceLeftForCompost);
            if (numToAdd > 0)
            {
                this.AddCompost(numToAdd, compost);
                compost.SplitOff(numToAdd).Destroy(DestroyMode.Vanish);
            }
        }

        public override string GetInspectString()
        {
            CompProperties_AC_Fermenter compProperties =
                this.def.GetCompProperties<CompProperties_AC_Fermenter>();
            StringBuilder stringBuilder = new StringBuilder();
            stringBuilder.Append(base.GetInspectString());
            if (stringBuilder.Length != 0)
            {
                stringBuilder.AppendLine();
            }
            AC_CompFermenter comp = base.GetComp<AC_CompFermenter>();
            if (!this.Empty && !comp.Ruined)
            {
                if (this.Fermented)
                {
                    stringBuilder.AppendLine(string.Concat(new string[]
                        {
                        "AC.ContainsFermentedCompost".Translate(),
                        ": ",
                        this.compostCount.ToString(),
                        " / ",
                        this.Capacity.ToString()
                        }));
                }
                else
                {
                    stringBuilder.AppendLine(string.Concat(new string[]
                        {
                        "AC.ContainsRawCompost".Translate(),
                        ": ",
                        this.compostCount.ToString(),
                        " / ",
                        this.Capacity.ToString()
                        }));
                }
            }
            stringBuilder.AppendLine("AC.Temp".Translate() + ": " +
                base.AmbientTemperature.ToStringTemperature("F0"));
            stringBuilder.AppendLine(string.Concat(new string[]
            {
                "AC.IdealTemp".Translate(),
                ": ",
                compProperties.minFermentTemp.ToStringTemperature("F0"),
                "-",
                compProperties.maxFermentTemp.ToStringTemperature("F0")
            }));
            return stringBuilder.ToString().TrimEndNewlines();
        }

        public Thing TakeOutCompost()
        {
            if (!this.Fermented)
            {
                Log.Warning("AC.NotReady".Translate());
                return null;
            }
            Thing thing = ThingMaker.MakeThing(ThingDefOf.AC_FermentedCompost, null);
            thing.stackCount = this.compostCount;
            this.Reset();
            return thing;
        }

        [DebuggerHidden]
        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (Gizmo g in base.GetGizmos())
            {
                yield return g;
            }
            if (Prefs.DevMode && !this.Empty)
            {
                yield return new Command_Action
                {
                    defaultLabel = "Debug: Set progress to 1",
                    action = delegate ()
                    {
                        this.Progress = 1f;
                    }
                };
            }
            yield break;
        }

        public override void Draw()
        {
            if (this.Fermented && !this.fermentFlag)
            {
                this.fermentFlag = true;
                base.Map.mapDrawer.MapMeshDirty(base.Position, MapMeshFlag.Things);
            }
            base.Draw();
            GenDraw.FillableBarRequest r = default(GenDraw.FillableBarRequest);
            r.center = this.DrawPos + new Vector3(0, 0.1f, -0.4f);
            r.size = Building_AC_CompostBin.BarSize;
            r.fillPercent = this.compostCount / this.Capacity;
            r.filledMat = this.BarFilledMat;
            r.unfilledMat = Building_AC_CompostBin.BarUnfilledMat;
            r.margin = 0.1f;
            r.rotation = this.Rotation;
            Log.Message($"{this.Empty}");
            if (!this.Empty)
            {
                GenDraw.DrawFillableBar(r);
            }
        }
    }

    public class WorkGiver_FillCompostBin : WorkGiver_Scanner
    {
        private static string TemperatureTrans = "AC.BadTemp".Translate();
        private static string NoCompostTrans = "AC.NoRawCompost".Translate();

        public override ThingRequest PotentialWorkThingRequest
        {
            get
            {
                return ThingRequest.ForDef(ThingDefOf.AC_CompostBin);
            }
        }

        public override PathEndMode PathEndMode
        {
            get
            {
                return PathEndMode.Touch;
            }
        }

        public override bool HasJobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            Building_AC_CompostBin CompostBin = t as Building_AC_CompostBin;
            if (CompostBin == null ||
                CompostBin.Fermented ||
                CompostBin.SpaceLeftForCompost <= 0)
            {
                return false;
            }
            float temperature = CompostBin.Position.GetTemperature(CompostBin.Map);
            CompProperties_AC_Fermenter compProperties = CompostBin.def.GetCompProperties<CompProperties_AC_Fermenter>();
            if (temperature < compProperties.minSafeTemp || temperature > compProperties.maxSafeTemp)
            {
                JobFailReason.Is(WorkGiver_FillCompostBin.TemperatureTrans);
                return false;
            }
            if (t.IsForbidden(pawn) ||
                !pawn.CanReserveAndReach(t, PathEndMode.Touch, pawn.NormalMaxDanger(), 1))
            {
                return false;
            }
            if (pawn.Map.designationManager.DesignationOn(t, DesignationDefOf.Deconstruct) != null)
            {
                return false;
            }
            if (this.FindCompost(pawn, CompostBin) == null)
            {
                JobFailReason.Is(WorkGiver_FillCompostBin.NoCompostTrans);
                return false;
            }
            return !t.IsBurning();
        }

        public override Job JobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            Building_AC_CompostBin CompostBin = t as Building_AC_CompostBin;
            Thing t2 = this.FindCompost(pawn, CompostBin);
            return new Job(JobDefOf.AC_FillCompostBin, t, t2)
            {
                count = CompostBin.SpaceLeftForCompost
            };
        }

        private Thing FindCompost(Pawn pawn, Building_AC_CompostBin bin)
        {
            Predicate<Thing> predicate = (Thing x) => !x.IsForbidden(pawn) && pawn.CanReserve(x, 1);
            Predicate<Thing> validator = predicate;
            return GenClosest.ClosestThingReachable(
                pawn.Position,
                pawn.Map,
                ThingRequest.ForDef(ThingDefOf.AC_RawCompost),
                PathEndMode.ClosestTouch,
                TraverseParms.For(pawn, Danger.Deadly, TraverseMode.ByPawn, false),
                9999f,
                validator,
                null,
                0,
                -1,
                false);
        }
    }

    public class WorkGiver_EmptyCompostBin : WorkGiver_Scanner
    {

        public override ThingRequest PotentialWorkThingRequest
        {
            get
            {
                return ThingRequest.ForDef(ThingDefOf.AC_CompostBin);
            }
        }

        public override PathEndMode PathEndMode
        {
            get
            {
                return PathEndMode.Touch;
            }
        }

        public override bool HasJobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            Building_AC_CompostBin CompostBin = t as Building_AC_CompostBin;
            return CompostBin != null &&
                CompostBin.Fermented &&
                !t.IsBurning() &&
                !t.IsForbidden(pawn) &&
                pawn.CanReserveAndReach(t, PathEndMode.Touch, pawn.NormalMaxDanger(), 1);
        }

        public override Job JobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            return new Job(JobDefOf.AC_EmptyCompostBin, t);
        }
    }

    public class JobDriver_FillCompostBin : JobDriver
    {
        protected Building_AC_CompostBin CompostBin
        {
            get
            {
                return (Building_AC_CompostBin)this.job.GetTarget(TargetIndex.A).Thing;
            }
        }

        protected Thing RawCompost
        {
            get
            {
                return this.job.GetTarget(TargetIndex.B).Thing;
            }
        }

        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            return this.pawn.Reserve(this.CompostBin, this.job, 1, -1, null, errorOnFailed) &&
                this.pawn.Reserve(this.RawCompost, this.job, 1, -1, null, errorOnFailed);
        }

        [DebuggerHidden]
        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedNullOrForbidden(TargetIndex.A);
            this.FailOnBurningImmobile(TargetIndex.A);
            base.AddEndCondition(() => (this.CompostBin.SpaceLeftForCompost > 0) ? JobCondition.Ongoing : JobCondition.Succeeded);
            yield return Toils_General.DoAtomic(delegate {
                this.job.count = this.CompostBin.SpaceLeftForCompost;
            });
            Toil reserveRawCompost = Toils_Reserve.Reserve(TargetIndex.B, 1, -1, null);
            yield return reserveRawCompost;
            yield return Toils_Goto.GotoThing(TargetIndex.B, PathEndMode.ClosestTouch).FailOnDespawnedNullOrForbidden(TargetIndex.B).FailOnSomeonePhysicallyInteracting(TargetIndex.B);
            yield return Toils_Haul.StartCarryThing(TargetIndex.B, false, true, false).FailOnDestroyedNullOrForbidden(TargetIndex.B);
            yield return Toils_Haul.CheckForGetOpportunityDuplicate(reserveRawCompost, TargetIndex.B, TargetIndex.None, true, null);
            yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.Touch);
            yield return Toils_General.Wait(200).FailOnDestroyedNullOrForbidden(TargetIndex.B).FailOnDestroyedNullOrForbidden(TargetIndex.A).FailOnCannotTouch(TargetIndex.A, PathEndMode.Touch).WithProgressBarToilDelay(TargetIndex.A, false, -0.5f);
            yield return new Toil
            {
                initAction = delegate {
                    this.CompostBin.AddCompost(this.RawCompost);
                },
                defaultCompleteMode = ToilCompleteMode.Instant
            };
        }
    }

    public class JobDriver_EmptyCompostBin : JobDriver
    {
        protected Building_AC_CompostBin CompostBin
        {
            get
            {
                return (Building_AC_CompostBin)this.job.GetTarget(TargetIndex.A).Thing;
            }
        }

        protected Thing FermentedCompost
        {
            get
            {
                return this.job.GetTarget(TargetIndex.B).Thing;
            }
        }

        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            return this.pawn.Reserve(this.CompostBin, this.job, 1, -1, null, errorOnFailed);
        }

        [DebuggerHidden]
        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedNullOrForbidden(TargetIndex.A);
            this.FailOnBurningImmobile(TargetIndex.A);
            yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.Touch);
            yield return Toils_General.Wait(200, TargetIndex.None).FailOnDestroyedNullOrForbidden(TargetIndex.A).FailOnCannotTouch(TargetIndex.A, PathEndMode.Touch).FailOn(() => !this.CompostBin.Fermented).WithProgressBarToilDelay(TargetIndex.A, false, -0.5f);
            yield return new Toil
            {
                initAction = delegate {
                    Thing thing = this.CompostBin.TakeOutCompost();
                    GenPlace.TryPlaceThing(thing, this.pawn.Position, base.Map, ThingPlaceMode.Near, null, null);
                    StoragePriority currentPriority = StoreUtility.CurrentStoragePriorityOf(thing);
                    if (StoreUtility.TryFindBestBetterStoreCellFor(thing, this.pawn, base.Map, currentPriority, this.pawn.Faction, out IntVec3 c, true))
                    {
                        this.job.SetTarget(TargetIndex.C, c);
                        this.job.SetTarget(TargetIndex.B, thing);
                        this.job.count = thing.stackCount;
                    }
                    else
                    {
                        this.EndJobWith(JobCondition.Incompletable);
                    }
                },
                defaultCompleteMode = ToilCompleteMode.Instant
            };
            yield return Toils_Reserve.Reserve(TargetIndex.B, 1, -1, null);
            yield return Toils_Reserve.Reserve(TargetIndex.C, 1, -1, null);
            yield return Toils_Goto.GotoThing(TargetIndex.B, PathEndMode.ClosestTouch);
            yield return Toils_Haul.StartCarryThing(TargetIndex.B, false, false, false);
            Toil carryToCell = Toils_Haul.CarryHauledThingToCell(TargetIndex.C);
            yield return carryToCell;
            yield return Toils_Haul.PlaceHauledThingInCell(TargetIndex.C, carryToCell, true);
        }
    }
}
