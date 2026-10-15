using ImmichDesktop.Core.Settings;
using ImmichDesktop.Core.Sync;

namespace ImmichDesktop.Core.Tests;

public class BackupScheduleTests
{
    static AppSettings Window(int fromHour, int toHour) =>
        new() { BackupWindowEnabled = true, BackupWindowStartMinutes = fromHour * 60, BackupWindowEndMinutes = toHour * 60 };

    static DateTime At(int hour, int minute = 0) => new(2026, 10, 15, hour, minute, 0);

    [Fact]
    public void Daytime_window()
    {
        var s = Window(9, 17);
        Assert.False(BackupSchedule.IsOpen(s, At(8, 59)));
        Assert.True(BackupSchedule.IsOpen(s, At(9)));
        Assert.True(BackupSchedule.IsOpen(s, At(16, 59)));
        Assert.False(BackupSchedule.IsOpen(s, At(17)));
        Assert.Equal(At(9), BackupSchedule.NextOpen(s, At(6)));
        Assert.Equal(At(9).AddDays(1), BackupSchedule.NextOpen(s, At(18)));
    }

    [Fact]
    public void Overnight_window_crosses_midnight()
    {
        var s = Window(22, 6);
        Assert.True(BackupSchedule.IsOpen(s, At(23)));
        Assert.True(BackupSchedule.IsOpen(s, At(2)));
        Assert.False(BackupSchedule.IsOpen(s, At(12)));
        Assert.Equal(At(22), BackupSchedule.NextOpen(s, At(12)));
    }

    [Fact]
    public void Off_or_whole_day_is_always_open()
    {
        Assert.True(BackupSchedule.IsOpen(new AppSettings(), At(3)));
        Assert.True(BackupSchedule.IsOpen(Window(5, 5), At(3)));
    }
}
