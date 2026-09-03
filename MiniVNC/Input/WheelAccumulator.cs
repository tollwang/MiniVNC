namespace MiniVNC.Input;

/// <summary>
/// 把滚轮增量累积成整格，再换算成要发送给服务器的 VNC 滚轮点击数。
///
/// <para>为什么需要累积：标准鼠标一个物理格上报 <see cref="DeltaPerNotch"/>(120)，
/// 但精密触摸板与高分辨率滚轮会把一格拆成多次小增量（如三次 40）。
/// 若对每次事件都直接向上取整成一格，一个物理格就会被当成三格发出去——远端滚动快出数倍。
/// 累积余数后，只有攒满 120 才发一格，剩下的留到下次。</para>
/// </summary>
public sealed class WheelAccumulator
{
    /// <summary>一个物理格对应的增量值（Windows 约定）。</summary>
    public const int DeltaPerNotch = 120;

    /// <summary>单次事件最多发送的点击数，防止快速甩动时刷出大量报文。</summary>
    public const int MaxClicksPerEvent = 15;

    /// <summary>每格对应的行数上限（对应 Windows"一次一屏"等极端设置）。</summary>
    public const int MaxLinesPerNotch = 10;

    private int _accumulated;

    /// <summary>清空累积（切换会话时调用，避免上一会话的余数影响新会话）。</summary>
    public void Reset() => _accumulated = 0;

    /// <summary>
    /// 累积一次滚轮增量，返回本次应发送的点击数与方向。
    /// </summary>
    /// <param name="delta">本次事件的增量（正=上滚，负=下滚）。</param>
    /// <param name="linesPerNotch">每格发送几次点击，会被夹到 1..<see cref="MaxLinesPerNotch"/>。</param>
    /// <returns>点击数为 0 表示尚未攒满一格，本次不发送。</returns>
    public (int Clicks, bool Up) Accumulate(int delta, int linesPerNotch)
    {
        _accumulated += delta;

        // int 除法向零取整，正负方向都成立
        int notches = _accumulated / DeltaPerNotch;
        if (notches == 0) return (0, true);

        _accumulated -= notches * DeltaPerNotch;   // 保留余数

        int lines = Math.Clamp(linesPerNotch, 1, MaxLinesPerNotch);
        int clicks = Math.Min(Math.Abs(notches) * lines, MaxClicksPerEvent);
        return (clicks, notches > 0);
    }
}
