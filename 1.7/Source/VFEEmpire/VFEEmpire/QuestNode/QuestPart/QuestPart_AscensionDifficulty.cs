using RimWorld;
using Verse;

namespace VFEEmpire;

public enum AscensionDifficulty
{
    //The ascension as built, and what an ascension begun before the choice existed keeps
    LosingIsFun,
    Normal
}

//How dangerous an ascension is, asked as its countdown begins. Losing is fun is the ascension as built. Normal hides half as
//many deserters among the delegations, has the parade's deserters plant three bombs instead of five, leaves the Brutus one
//ally at most, sends the Brutus for her guards first with the others rather than straight for her, and lets everyone's
//lower opinion of a Brutus after a truce lapse after a year rather than last for as long as the Brutus stays. The window
//waits for an answer, and comes back after a load until it has one
public class QuestPart_AscensionDifficulty : QuestPartActivable
{
    //What Normal changes: QuestPart_Delegations.TrySend, LordJob_Parade.SpawnBomb, GameComponent_Conspiracy.TryBeginPlot,
    //GameComponent_Conspiracy.StrikeTargetFor and GameComponent_Conspiracy.Truce
    public const float NormalPointsPerDeserterFactor = 2f;
    public const int NormalRoomBombs = 2;
    public const int NormalAllies = 1;
    public const int NormalBrutusOpinionDays = 60;

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

    //The difficulty of the ascension under way for her; one begun before the choice existed is Losing is fun, as built
    public static AscensionDifficulty For(Pawn stellarch)
    {
        foreach (var quest in Find.QuestManager.QuestsListForReading)
        {
            if (quest.State != QuestState.Ongoing) continue;
            foreach (var part in quest.PartsListForReading)
                if (part is QuestPart_AscensionDifficulty choice && choice.stellarch == stellarch)
                    return choice.difficulty;
        }

        return AscensionDifficulty.LosingIsFun;
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
