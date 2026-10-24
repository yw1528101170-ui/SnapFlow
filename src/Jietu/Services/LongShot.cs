namespace Jietu;

internal static class LongShot
{
    public readonly record struct Result(byte[] Pixels, int Width, int Height);

    public static Result? Capture(
        int x,
        int y,
        int width,
        int height,
        Action<int, int>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        if (width < 16 || height < 40 || width > 7680 || height > 4320)
        {
            return null;
        }

        Thread.Sleep(60);
        NativeMethods.DwmFlush();
        var first = CaptureService.CopyRect(x, y, width, height);
        if (first == null)
        {
            return null;
        }

        // 激活被滚动的目标窗口并将鼠标置于选区中部偏下
        var target = NativeMethods.WindowFromPoint(new NativeMethods.Point { X = x + (width / 2), Y = y + (height / 2) });
        if (target != IntPtr.Zero)
        {
            NativeMethods.GetWindowThreadProcessId(target, out var pid);
            if (pid != (uint)Environment.ProcessId)
            {
                NativeMethods.SetForegroundWindow(target);
            }
        }

        NativeMethods.SetCursorPos(x + (width / 2), y + Math.Max(16, height * 3 / 4));
        var frames = new List<byte[]> { first };
        var skips = new List<int> { 0 };
        var previous = first;
        var total = height;

        onProgress?.Invoke(1, total);

        // 最多自动滚动拼接 25 屏，限制最大高度与像素总数防止超大图 OOM（约 160MB BGRA 缓冲）
        const int maxSteps = 25;
        const int maxHeight = 20000;
        const long maxTotalPixels = 40_000_000;

        for (var step = 1; step <= maxSteps && total < maxHeight && ((long)width * total < maxTotalPixels); step++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            ScrollDown();
            // 等待页面平滑滚动与渲染稳定
            Thread.Sleep(200);

            var next = CaptureService.CopyRect(x, y, width, height);
            if (next == null || NearlySame(previous, next))
            {
                // 到达底部或页面未滚动
                break;
            }

            // 精准粗细双层像素位移匹配 (包含粘性顶部与滚动条剔除)
            var fresh = FindFresh(previous, next, width, height);
            if (fresh < 6 || fresh > height - 6)
            {
                break;
            }

            frames.Add(next);
            skips.Add(height - fresh);
            total += fresh;
            previous = next;

            onProgress?.Invoke(frames.Count, total);
        }

        if (frames.Count <= 1)
        {
            return null;
        }

        return new Result(Join(frames, skips, width, height, total), width, total);
    }

    private static void ScrollDown()
    {
        var input = new NativeMethods.Input
        {
            Type = 0,
            U = new NativeMethods.InputUnion
            {
                Mi = new NativeMethods.MouseInput
                {
                    MouseData = unchecked((uint)-120),
                    DwFlags = 0x0800, // MOUSEEVENTF_WHEEL
                },
            },
        };
        var inputs = new[] { input, input, input };
        NativeMethods.SendInput((uint)inputs.Length, inputs, System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.Input>());
    }

    public static bool NearlySame(byte[] left, byte[] right)
    {
        if (left.Length != right.Length || left.Length < 16)
        {
            return false;
        }

        var step = Math.Max(16, left.Length / 1000);
        step -= step % 4;
        var same = 0;
        var seen = 0;
        for (var i = 0; i + 3 < left.Length; i += step)
        {
            seen++;
            if (left[i] == right[i] && left[i + 1] == right[i + 1] && left[i + 2] == right[i + 2])
            {
                same++;
            }
        }

        return seen > 0 && (same * 100.0 / seen) >= 98.5;
    }

    /// <summary>
    /// 双层精细化搜索算法：粗搜 (Step 4) 确定大致重叠区间，细搜 (Step 1) 实现 1 像素级严丝合缝对齐
    /// </summary>
    public static int FindFresh(byte[] previous, byte[] next, int width, int height)
    {
        var minFresh = Math.Max(8, height / 12);
        var maxFresh = (int)(height * 0.85);

        // 阶段 1：粗搜 (步长 4)
        var bestCoarse = height / 3;
        var bestScore = int.MaxValue;

        for (var fresh = minFresh; fresh <= maxFresh; fresh += 4)
        {
            var score = Score(previous, next, width, height, fresh);
            if (score < bestScore)
            {
                bestScore = score;
                bestCoarse = fresh;
            }
        }

        // 阶段 2：以 bestCoarse 为中心进行 ±4 像素的 1 像素级细搜
        var bestFine = bestCoarse;
        var fineMin = Math.Max(minFresh, bestCoarse - 5);
        var fineMax = Math.Min(maxFresh, bestCoarse + 5);

        for (var fresh = fineMin; fresh <= fineMax; fresh++)
        {
            var score = Score(previous, next, width, height, fresh);
            if (score < bestScore)
            {
                bestScore = score;
                bestFine = fresh;
            }
        }

        // 阈值保护：如果最小差异依然过大，视为无法有效对齐
        const int maxAcceptableDiff = 20 * 24 * 3 * 64;
        return bestScore > maxAcceptableDiff ? height : bestFine;
    }

    /// <summary>
    /// SAD (Sum of Absolute Differences) 绝对差值累加评估，排除两侧边缘与可能存在的固定导航栏
    /// </summary>
    private static int Score(byte[] previous, byte[] next, int width, int height, int fresh)
    {
        var score = 0;
        var span = Math.Max(1, height - fresh);

        // 选取 20 组采样行，避开最顶部可能固定的导航栏（从 span * 15% 处开始）
        var startRow = Math.Max(2, span * 15 / 100);
        var endRow = Math.Min(span - 2, span * 95 / 100);
        var validSpan = Math.Max(1, endRow - startRow);

        for (var r = 0; r < 20; r++)
        {
            var y = startRow + (r * validSpan / 20);
            var prevY = y + fresh;
            if (prevY >= height)
            {
                break;
            }

            // 水平采样：避开左右各 10%（排除右侧滚动条和左侧阴影干扰）
            for (var col = 2; col <= 22; col++)
            {
                var x = width * col / 25;
                var a = ((prevY * width) + x) * 4;
                var b = ((y * width) + x) * 4;

                score += Math.Abs(previous[a] - next[b]);
                score += Math.Abs(previous[a + 1] - next[b + 1]);
                score += Math.Abs(previous[a + 2] - next[b + 2]);
            }
        }

        return score;
    }

    public static byte[] Join(List<byte[]> frames, List<int> skips, int width, int height, int total)
    {
        var bufferSize = (long)width * total * 4;
        if (bufferSize <= 0 || bufferSize > 256 * 1024 * 1024)
        {
            total = (int)(256 * 1024 * 1024 / (width * 4));
        }

        var pixels = new byte[width * total * 4];
        var currentY = 0;

        for (var i = 0; i < frames.Count; i++)
        {
            var start = i == 0 ? 0 : skips[i];
            var rows = height - start;
            if (currentY + rows > total)
            {
                rows = total - currentY;
            }
            if (rows <= 0) break;

            Buffer.BlockCopy(frames[i], start * width * 4, pixels, currentY * width * 4, rows * width * 4);
            currentY += rows;
        }

        return pixels;
    }
}
