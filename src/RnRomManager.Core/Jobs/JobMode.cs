namespace RnRomManager.Core.Jobs;

public enum JobMode
{
    Quick,
    Deep,

    /// <summary>B15: an integrity check of one game's ZIP, as deep as <see cref="Deep"/>.</summary>
    Game,
}
