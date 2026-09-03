using MiniVNC.Input;
using static MiniVNC.Tests.TestRunner;

namespace MiniVNC.Tests;

/// <summary>
/// 滚轮增量累积。修复前的实现是 <c>Math.Max(1, |delta| / 120)</c>，
/// 精密触摸板把一个物理格拆成多次小增量时，每次都被向上取整成一整格 —— 远端滚动快出数倍。
/// </summary>
public static class WheelTests
{
    public static void Run()
    {
        Section("滚轮增量累积");

        // ---- 标准鼠标：一次事件正好一格 ----
        var w = new WheelAccumulator();
        var r = w.Accumulate(120, 1);
        Check("标准鼠标上滚一格 → 1 次点击、方向向上", r == (1, true));

        r = w.Accumulate(-120, 1);
        Check("标准鼠标下滚一格 → 1 次点击、方向向下", r == (1, false));

        // ---- 精密触摸板：一个物理格拆成三次 40 ----
        w = new WheelAccumulator();
        var a = w.Accumulate(40, 1);
        var b = w.Accumulate(40, 1);
        var c = w.Accumulate(40, 1);
        Check("触摸板三次 40 增量合并为恰好 1 格（修复前会发 3 格）",
            a.Clicks == 0 && b.Clicks == 0 && c == (1, true),
            $"{a.Clicks}/{b.Clicks}/{c.Clicks}");

        // 余数必须留到下次，不能丢
        w = new WheelAccumulator();
        w.Accumulate(80, 1);                       // 攒下 80
        var next = w.Accumulate(40, 1);            // 80+40=120 → 正好一格
        Check("不足一格的余数保留到下次事件", next == (1, true));

        // ---- 负方向同样要累积，且不能与正方向串味 ----
        w = new WheelAccumulator();
        w.Accumulate(-40, 1);
        w.Accumulate(-40, 1);
        var down = w.Accumulate(-40, 1);
        Check("下滚方向同样按整格累积", down == (1, false));

        w = new WheelAccumulator();
        w.Accumulate(60, 1);
        var cancel = w.Accumulate(-60, 1);
        Check("正负增量抵消后不产生滚动", cancel.Clicks == 0);

        // ---- 快速甩动：一次事件多格 ----
        w = new WheelAccumulator();
        Check("一次事件 360 增量 → 3 格", w.Accumulate(360, 1) == (3, true));

        // ---- 每格行数倍率 ----
        w = new WheelAccumulator();
        Check("每格 3 行 → 一格发 3 次点击", w.Accumulate(120, 3) == (3, true));
        w = new WheelAccumulator();
        Check("每格 5 行 → 两格发 10 次点击", w.Accumulate(240, 5) == (10, true));

        // 行数参数越界要被夹住，而不是发出荒唐的点击数
        w = new WheelAccumulator();
        Check("行数为 0 时按 1 处理", w.Accumulate(120, 0) == (1, true));
        w = new WheelAccumulator();
        Check("行数为负时按 1 处理", w.Accumulate(120, -5) == (1, true));
        w = new WheelAccumulator();
        Check($"行数超过上限被夹到 {WheelAccumulator.MaxLinesPerNotch}",
            w.Accumulate(120, 999) == (WheelAccumulator.MaxLinesPerNotch, true));

        // ---- 单次事件的点击数上限：防止快速甩动刷出海量报文 ----
        w = new WheelAccumulator();
        var flood = w.Accumulate(120 * 50, 10);
        Check($"单次事件点击数不超过 {WheelAccumulator.MaxClicksPerEvent}",
            flood.Clicks == WheelAccumulator.MaxClicksPerEvent, $"得到 {flood.Clicks}");

        // ---- Reset：切换会话时清掉余数 ----
        w = new WheelAccumulator();
        w.Accumulate(80, 1);
        w.Reset();
        Check("Reset 后余数被清空", w.Accumulate(40, 1).Clicks == 0);
    }
}
