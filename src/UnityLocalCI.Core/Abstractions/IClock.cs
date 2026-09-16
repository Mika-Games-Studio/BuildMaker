namespace UnityLocalCI.Core.Abstractions;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
    DateTimeOffset Now { get; }
    Task Delay(TimeSpan delay, CancellationToken ct);
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    public DateTimeOffset Now => DateTimeOffset.Now;
    public Task Delay(TimeSpan delay, CancellationToken ct) => Task.Delay(delay, ct);
}
