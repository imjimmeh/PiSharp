namespace PiSharp.Continuity.Tests;

public class CronScheduleTests
{
    private static DateTimeOffset Utc(int year, int month, int day, int hour = 0, int minute = 0)
        => new DateTimeOffset(year, month, day, hour, minute, 0, TimeSpan.Zero);

    [Fact]
    public void Star_minute_hourly_returns_next_minute()
    {
        var cron = new CronSchedule("* * * * *");
        Assert.Equal(Utc(2026, 8, 14, 9, 31), cron.Next(Utc(2026, 8, 14, 9, 30)));
    }

    [Fact]
    public void Hourly_alias_runs_at_top_of_hour()
    {
        var cron = new CronSchedule("@hourly");
        Assert.Equal(Utc(2026, 8, 14, 10, 0), cron.Next(Utc(2026, 8, 14, 9, 30)));
    }

    [Fact]
    public void Daily_alias_runs_at_midnight()
    {
        var cron = new CronSchedule("@daily");
        Assert.Equal(Utc(2026, 8, 15, 0, 0), cron.Next(Utc(2026, 8, 14, 9, 30)));
    }

    [Fact]
    public void Weekly_alias_runs_next_sunday()
    {
        // 2026-08-14 is a Friday; @weekly (0 0 * * 0) next is Sunday 2026-08-16.
        var cron = new CronSchedule("@weekly");
        Assert.Equal(Utc(2026, 8, 16, 0, 0), cron.Next(Utc(2026, 8, 14, 9, 30)));
    }

    [Fact]
    public void Every_30_minutes_respects_step()
    {
        var cron = new CronSchedule("*/30 * * * *");
        Assert.Equal(Utc(2026, 8, 14, 9, 30), cron.Next(Utc(2026, 8, 14, 9, 15)));
        Assert.Equal(Utc(2026, 8, 14, 10, 0), cron.Next(Utc(2026, 8, 14, 9, 30)));
    }

    [Fact]
    public void Range_and_list_fields()
    {
        // Minutes 5 and 20, hour 9.
        var cron = new CronSchedule("5,20 9 * * *");
        Assert.Equal(Utc(2026, 8, 14, 9, 20), cron.Next(Utc(2026, 8, 14, 9, 5)));
        Assert.Equal(Utc(2026, 8, 15, 9, 5), cron.Next(Utc(2026, 8, 14, 9, 20)));
    }

    [Fact]
    public void Range_a_to_b()
    {
        // Minutes 10-12, hour 8.
        var cron = new CronSchedule("10-12 8 * * *");
        Assert.Equal(Utc(2026, 8, 14, 8, 11), cron.Next(Utc(2026, 8, 14, 8, 10)));
    }

    [Fact]
    public void Next_across_month_boundary()
    {
        // 2:30 every day.
        var cron = new CronSchedule("30 2 * * *");
        Assert.Equal(Utc(2026, 9, 1, 2, 30), cron.Next(Utc(2026, 8, 31, 2, 30)));
    }

    [Fact]
    public void Dom_and_dow_union_when_both_restricted()
    {
        // 1st of month OR Sunday, at 00:05.
        var cron = new CronSchedule("5 0 1 * 0");
        // 2026-08-14 is Friday; 1st was Aug 1 (past), next is Sunday Aug 16.
        Assert.Equal(Utc(2026, 8, 16, 0, 5), cron.Next(Utc(2026, 8, 14, 0, 5)));
    }

    [Fact]
    public void Invalid_field_count_throws()
        => Assert.Throws<FormatException>(() => new CronSchedule("* * * *"));

    [Fact]
    public void Invalid_value_out_of_range_throws_naming_field()
    {
        var ex = Assert.Throws<FormatException>(() => new CronSchedule("60 * * * *"));
        Assert.Contains("minute", ex.Message);
    }

    [Fact]
    public void Invalid_step_throws()
        => Assert.Throws<FormatException>(() => new CronSchedule("*/0 * * * *"));

    [Fact]
    public void Non_numeric_value_throws_naming_field()
    {
        var ex = Assert.Throws<FormatException>(() => new CronSchedule("* * * banana *"));
        Assert.Contains("month", ex.Message);
    }
}
