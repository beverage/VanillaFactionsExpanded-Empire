using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI.Group;

namespace VFEEmpire;

public class Command_Parade : Command
{
    private readonly Action<RitualRoleAssignments> action;
    private readonly Pawn bestNoble;
    private readonly LordJob_Parade job;

    public Command_Parade(LordJob_Parade job, Pawn bestNoble, Action<RitualRoleAssignments> action)
    {
        this.bestNoble = bestNoble;
        this.action = action;
        this.job = job;
        defaultLabel = "VFEE.Parade.Begin".Translate();
        icon = ContentFinder<Texture2D>.Get("UI/Rituals/BestowingParade");
    }

    public override GizmoResult GizmoOnGUI(Vector2 topLeft, float maxWidth, GizmoRenderParms parms) => base.GizmoOnGUI(topLeft, maxWidth, parms);

    //The begin dialog's lines: its header, and the colony's fighters who are away, whom the deserter raid counts all the
    //same (QuestPart_DeserterRaid)
    public static List<string> ExtraInfo(LordJob_Parade job)
    {
        var info = new List<string> { "VFEE.Parade.ChooseParticipants".Translate() };
        var away = QuestPart_DeserterRaid.DefendersAway(job.Map, job.stellarch);
        if (away.Count > 0)
            info.Add("VFEE.Parade.DefendersAway".Translate(away.Select(pawn => pawn.LabelShort).ToCommaList(true).Named("NAMES")));
        return info;
    }

    public override void ProcessInput(Event ev)
    {
        base.ProcessInput(ev);
        var label = job.RitualLabel.CapitalizeFirst();
        Dialog_BeginRitual.ActionCallback callBack = participants =>
        {
            action(participants);
            return true;
        };
        Dialog_BeginRitual.PawnFilter filter = (pawn, voluntary, allowOtherIdeos) =>
        {
            var lord = pawn.GetLord();
            var result = (lord == null || !(lord.LordJob is LordJob_Ritual)) && !pawn.IsPrisonerOfColony && !pawn.RaceProps.Animal;
            if (!result) return false;
            return pawn.Faction == Faction.OfEmpire || pawn.Faction == Faction.OfPlayer;
        };
        var forced = new Dictionary<string, Pawn>();
        forced.Add("stellarch", job.stellarch);
        string okButtonText = "Begin".Translate();
        var outcomeDef = InternalDefOf.VFEE_Parade_Outcome;
        Find.WindowStack.Add(new Dialog_BeginRitual(label, job.Ritual, job.target.ToTargetInfo(job.Map), job.Map, callBack, bestNoble, null, filter,
            okButtonText, outcome: outcomeDef, forcedForRole: forced, extraInfoText: ExtraInfo(job)));
    }
}
