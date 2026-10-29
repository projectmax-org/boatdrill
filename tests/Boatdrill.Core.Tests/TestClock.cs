namespace Boatdrill.Core.Tests;

/// <summary>A clock a test moves by hand, so drills days apart run in milliseconds.</summary>
internal sealed class TestClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}
