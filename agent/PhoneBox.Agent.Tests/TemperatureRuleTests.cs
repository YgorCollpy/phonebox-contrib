using PhoneBox.Agent;
using Xunit;

/// <summary>The temperature triggers: the CPU's sensor and the phone's battery report.</summary>
public class TemperatureRuleTests
{
    [Theory]
    [InlineData(84.9, null, false)]
    [InlineData(85.0, null, true)]   // 85 °C by default, the limit itself counts
    [InlineData(71.0, 70.0, true)]
    [InlineData(null, 70.0, false)]  // no sensor reading
    public void CpuTemperature(double? temp, double? limit, bool holds)
    {
        var (h, detail) = RuleEngine.CpuTempAbove(temp, limit);
        Assert.Equal(holds, h);
        if (holds) Assert.Equal($"CPU {temp:0} °C", detail);
    }

    private const long Now = 1_800_000_000_000;
    private static PhoneBatteryReport Report(double? temp, long ageMs) => new(60, true, temp, Now - ageMs);

    [Fact]
    public void PhoneBatteryTemperatureHoldsForARecentReportAtTheLimit()
    {
        Assert.Equal((true, "Phone battery 41 °C"), RuleEngine.PhoneBatteryTempAbove(Report(41, 30_000), null, Now));
        Assert.True(RuleEngine.PhoneBatteryTempAbove(Report(40, 0), null, Now).Holds);    // 40 °C by default
        Assert.False(RuleEngine.PhoneBatteryTempAbove(Report(39.9, 0), null, Now).Holds);
        Assert.True(RuleEngine.PhoneBatteryTempAbove(Report(36, 0), 35, Now).Holds);
    }

    [Fact]
    public void AnOldOrMissingReportDoesNotKeepThePhoneHot()
    {
        Assert.False(RuleEngine.PhoneBatteryTempAbove(Report(45, RuleEngine.PhoneReportFreshMs), null, Now).Holds);
        Assert.False(RuleEngine.PhoneBatteryTempAbove(Report(null, 0), null, Now).Holds);
        Assert.False(RuleEngine.PhoneBatteryTempAbove(null, null, Now).Holds);
    }

    [Theory]
    [InlineData("cpu-temp-above", 0, 15)]  // like cpu-above: a spike or a CPU hovering at the limit doesn't flap
    [InlineData("cpu-above", 0, 15)]
    [InlineData("cpu-temp-above", 60, 60)] // the rule's own seconds win
    [InlineData("phone-battery-temp-above", 0, 0)] // the phone reports every 30 s: no spikes to wait out
    public void TheCpuTemperatureWaitsLikeTheCpuLoad(string when, int forSec, int expected) =>
        Assert.Equal(expected, RuleEngine.SustainSec(new RuleConfig { When = when, ForSec = forSec }));

    [Fact]
    public void BothTriggersAreKnownRuleConditions()
    {
        Assert.Contains("cpu-temp-above", RuleConfig.Triggers);
        Assert.Contains("phone-battery-temp-above", RuleConfig.Triggers);
    }
}
