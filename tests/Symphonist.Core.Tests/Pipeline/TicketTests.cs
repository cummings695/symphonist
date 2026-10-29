using System.Reflection;
using Symphonist.Core.Pipeline;
using Symphonist.Core.Risk;

namespace Symphonist.Core.Tests.Pipeline;

public class TicketTests
{
    [Fact] public void Clone_copies_every_public_property()
    {
        var original = new Ticket();
        var properties = typeof(Ticket).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var i = 0;
        foreach (var prop in properties) prop.SetValue(original, SampleValue(prop.PropertyType, ++i));

        var clone = original.Clone();

        Assert.NotSame(original, clone);
        foreach (var prop in properties)
        {
            Assert.NotEqual(SampleValue(prop.PropertyType, 0), prop.GetValue(clone));
            Assert.Equal(prop.GetValue(original), prop.GetValue(clone));
        }
        clone.Status = StageStatus.Done;
        Assert.NotEqual(StageStatus.Done, original.Status);
    }

    private static object SampleValue(Type type, int n)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;
        if (t == typeof(string)) return $"value-{n}";
        if (t == typeof(int)) return n;
        if (t == typeof(long)) return (long)n;
        if (t == typeof(Guid)) return new Guid(n, 0, 0, new byte[8]);
        if (t == typeof(DateTimeOffset)) return DateTimeOffset.UnixEpoch.AddDays(n);
        if (t == typeof(StageStatus)) return (StageStatus)(n % 5 + 1);
        if (t == typeof(bool)) return n != 0;
        if (t == typeof(RiskLevel)) return n == 0 ? RiskLevel.Low : RiskLevel.High;
        if (t == typeof(TicketKind)) return n == 0 ? TicketKind.Work : TicketKind.Release;
        throw new NotSupportedException($"add a sample value for {type} (property type new to Ticket)");
    }
}
