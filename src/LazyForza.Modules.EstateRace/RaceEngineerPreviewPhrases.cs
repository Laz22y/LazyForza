namespace LazyForza.Modules.EstateRace;

/// <summary>Illustrative race messages; selection never changes live observer/queue evidence.</summary>
public sealed class RaceEngineerPreviewPhrases
{
    private static readonly (string Chinese, string English)[] Phrases =
    [
        ("黄旗，注意减速。", "Yellow flag. Caution."),
        ("绿旗，恢复比赛。", "Green flag."),
        ("红旗，比赛暂停。", "Red flag. Session suspended."),
        ("罚时 5 秒。", "Time penalty, 5 seconds."),
        ("个人最快圈，65.32 秒。", "Personal best, 65.32 seconds."),
        ("预计进入进站机会，可考虑进站，结果仍有不确定性。", "Estimated pit opportunity. Consider pitting; this is a prediction.")
    ];
    private int previous = -1;

    public string Next(bool english, Random? random = null)
    {
        random ??= Random.Shared;
        var index = random.Next(Phrases.Length - (previous < 0 ? 0 : 1));
        if (previous >= 0 && index >= previous) index++;
        previous = index;
        return english ? Phrases[index].English : Phrases[index].Chinese;
    }
}
