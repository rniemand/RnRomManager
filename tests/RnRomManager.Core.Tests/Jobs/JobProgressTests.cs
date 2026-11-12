using RnRomManager.Core.Jobs;

namespace RnRomManager.Core.Tests.Jobs;

/// <summary>Large ROM import progress B1, B3, B4: the per-file step.</summary>
public class JobProgressTests
{
    private static readonly DateTime Start = new(2026, 11, 11, 12, 0, 0, DateTimeKind.Utc);

    [Test]
    public void NoStep_IsNull()
    {
        Assert.That(new JobProgress().StepAt(Start), Is.Null);
    }

    [Test]
    public void Step_ReportsPhaseBytesAndTotal()
    {
        var progress = new JobProgress();
        progress.BeginStep(JobStepPhases.Hashing, 1000, Start);
        progress.StepAdvanced(400);

        Assert.That(progress.StepAt(Start.AddSeconds(2)), Is.EqualTo(new JobStepSnapshot(JobStepPhases.Hashing, 400, 1000, 200)));
    }

    [Test]
    public void EndStep_ClearsIt()
    {
        var progress = new JobProgress();
        progress.BeginStep(JobStepPhases.Copying, 1000, Start);
        progress.EndStep();

        Assert.That(progress.StepAt(Start), Is.Null);
    }

    [Test]
    public void SetCurrentItem_ClearsTheStep()
    {
        var progress = new JobProgress();
        progress.BeginStep(JobStepPhases.Hashing, 1000, Start);
        progress.SetCurrentItem("next.nsp");

        Assert.That(progress.StepAt(Start), Is.Null);
        Assert.That(progress.CurrentItem, Is.EqualTo("next.nsp"));
    }

    [Test]
    public void BeginStep_ResetsBytes()
    {
        var progress = new JobProgress();
        progress.BeginStep(JobStepPhases.Hashing, 1000, Start);
        progress.StepAdvanced(1000);
        progress.BeginStep(JobStepPhases.CopyingToLibrary, 1000, Start);

        Assert.That(progress.StepAt(Start)!.BytesDone, Is.Zero);
        Assert.That(progress.StepAt(Start)!.Phase, Is.EqualTo(JobStepPhases.CopyingToLibrary));
    }

    [TestCase(0.5, 100, null)]
    [TestCase(5.0, 0, null)]
    [TestCase(1.0, 100, 100L)]
    [TestCase(4.0, 100, 25L)]
    public void BytesPerSecond_NeedsASecondAndSomeBytes(double seconds, long done, long? expected)
    {
        var progress = new JobProgress();
        progress.BeginStep(JobStepPhases.Hashing, 1000, Start);
        progress.StepAdvanced(done);

        Assert.That(progress.StepAt(Start.AddSeconds(seconds))!.BytesPerSecond, Is.EqualTo(expected));
    }

    [Test]
    public void BytesDone_IsClampedToTheTotal()
    {
        var progress = new JobProgress();
        progress.BeginStep(JobStepPhases.Hashing, 1000, Start);
        progress.StepAdvanced(1500);

        Assert.That(progress.StepAt(Start)!.BytesDone, Is.EqualTo(1000));
    }
}
