using XiaoK.Core;

internal static class NotificationBaselinePolicyCheck
{
    public static int Run()
    {
        var startedAt = new DateTimeOffset(2026, 10, 12, 4, 0, 0, TimeSpan.Zero);
        var sameInstantWithDifferentOffset = startedAt.ToOffset(TimeSpan.FromHours(8));
        var cases = new (string Name, DateTimeOffset CreatedAt, bool Expected)[]
        {
            ("startup-old-toast", startedAt.AddTicks(-1), true),
            ("same-instant-toast", startedAt, false),
            ("same-instant-different-offset", sameInstantWithDifferentOffset, false),
            ("toast-during-baseline-enumeration", startedAt.AddTicks(1), false)
        };

        var failures = 0;
        foreach (var testCase in cases)
        {
            var actual = NotificationStartupBaselinePolicy.ShouldAddToBaseline(testCase.CreatedAt, startedAt);
            if (actual == testCase.Expected) continue;
            failures++;
            Console.Error.WriteLine($"{testCase.Name}: expected {testCase.Expected}, got {actual}");
        }

        Console.WriteLine($"通知启动基线策略：{cases.Length - failures}/{cases.Length} 项通过；未读取系统通知。 ");
        return failures == 0 ? 0 : 1;
    }
}
