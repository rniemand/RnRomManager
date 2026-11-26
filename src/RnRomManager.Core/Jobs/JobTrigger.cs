namespace RnRomManager.Core.Jobs;

public enum JobTrigger
{
    Manual,
    Schedule,

    /// <summary>S4: started by the app itself after startup.</summary>
    Startup,
}
