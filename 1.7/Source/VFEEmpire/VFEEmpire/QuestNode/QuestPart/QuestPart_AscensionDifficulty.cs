using RimWorld;
using Verse;

namespace VFEEmpire;

public enum AscensionDifficulty
{
    //The ascension as built, and what an ascension begun before the choice existed keeps
    LosingIsFun,
    Normal
}

//How dangerous an ascension is, asked as its countdown begins. Losing is fun is the ascension as built. Normal hides half
//as many deserters among the delegations, has the parade's deserters plant three bombs instead of five, leaves the Brutus
//one ally at most, sends the Brutus for her guards first with the others rather than straight for her, lets everyone's
//lower opinion of a Brutus after a truce lapse after a year rather than last for as long as the Brutus stays, and gives a
//cell and the plot one smuggled EMP grenade each for what guards her, where a cell would get one for each blast it takes
//and the plot a second against more than four war mechs. The window waits for an answer, and comes back after a load until
//it has one
public class QuestPart_AscensionDifficulty : QuestPartActivable
{
    //What Normal changes: QuestPart_Delegations.TrySend, LordJob_Parade.SpawnBomb, GameComponent_Conspiracy.AlliesWanted,
    //GameComponent_Conspiracy.StrikeTargetFor, GameComponent_Conspiracy.Truce, AscensionCounters.CellGrenades and
    //AscensionCounters.PlotGrenades. The parade's deserter raid is the same on both (QuestPart_DeserterRaid)
    public const float NormalPointsPerDeserterFactor = 2f;
    public const int NormalRoomBombs = 2;
    public const int NormalAllies = 1;
    public const int NormalBrutusOpinionDays = 60;
    public const int NormalCellGrenades = 1;
    public const int NormalPlotGrenades = 1;

    public Pawn stellarch;
    public AscensionDifficulty difficulty;

    //Not saved
    private Dialog_NodeTree window;

    protected override void Enable(SignalArgs receivedArgs)
    {
        base.Enable(receivedArgs);
        Ask();
    }

    //A window is not saved, so a game loaded before the answer asks again
    public override void QuestPartTick()
    {
        base.QuestPartTick();
        if (window == null || !Find.WindowStack.IsOpen(window)) Ask();
    }

    private void Ask()
    {
        var node = new DiaNode("VFEE.Ascension.Difficulty".Translate(stellarch.Named("STELLARCH")));
        node.options.Add(Option("VFEE.Ascension.DifficultyNormal", AscensionDifficulty.Normal));
        node.options.Add(Option("VFEE.Ascension.DifficultyLosingIsFun", AscensionDifficulty.LosingIsFun));
        window = new Dialog_NodeTree(node, true, title: "VFEE.Ascension.DifficultyTitle".Translate())
        {
            closeOnAccept = false
        };
        Find.WindowStack.Add(window);

        DiaOption Option(string key, AscensionDifficulty choice) => new(key.Translate())
        {
            action = () => Choose(choice),
            resolveTree = true
        };
    }

    private void Choose(AscensionDifficulty choice)
    {
        difficulty = choice;
        Complete();
    }

    //The difficulty of the ascension under way for her; one never asked is unasked, Losing is fun unless the caller says
    //otherwise, as built for an ascension begun before the choice existed
    public static AscensionDifficulty For(Pawn stellarch, AscensionDifficulty unasked = AscensionDifficulty.LosingIsFun)
    {
        foreach (var quest in Find.QuestManager.QuestsListForReading)
        {
            if (quest.State != QuestState.Ongoing) continue;
            foreach (var part in quest.PartsListForReading)
                if (part is QuestPart_AscensionDifficulty choice && choice.stellarch == stellarch)
                    return choice.difficulty;
        }

        return unasked;
    }

    public static AscensionDifficulty Of(Quest quest)
    {
        if (quest != null)
            foreach (var part in quest.PartsListForReading)
                if (part is QuestPart_AscensionDifficulty choice)
                    return choice.difficulty;
        return AscensionDifficulty.LosingIsFun;
    }

    public override void ExposeData()
    {
        base.ExposeData();
        Scribe_References.Look(ref stellarch, "stellarch");
        Scribe_Values.Look(ref difficulty, "difficulty");
    }
}
