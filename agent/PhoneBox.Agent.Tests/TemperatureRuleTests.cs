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

    [Fact]
    public void BothTriggersAreKnownRuleConditions()
    {
        Assert.Contains("cpu-temp-above", RuleConfig.Triggers);
        Assert.Contains("phone-battery-temp-above", RuleConfig.Triggers);
    }
}
