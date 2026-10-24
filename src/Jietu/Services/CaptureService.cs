using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;

namespace Jietu;

public readonly record struct CapturedFrame(SoftwareBitmap Bitmap, byte[] Pixels);

internal static class CaptureService
{
    private const int DxgiWaitTimeout = unchecked((int)0x887A0027);
    private const int DxgiAccessLost = unchecked((int)0x887A0026);
    private const int DxgiDeviceRemoved = unchecked((int)0x887A0005);
    private const uint FormatBgra = 87;
    private const uint FormatBgraSrgb = 91;
    private const uint FormatFloat16 = 10;

    // 桌面复制空闲多久后自动释放（毫秒）。Windows 同一输出只允许一个 duplication 客户端，
    // 常驻持有会抢走 OBS/录屏/其他截图工具，所以最后一次抓屏后延时释放，下次抓屏再按需重建。
    private const int DuplicationIdleMs = 3000;

    private static readonly Guid ItemId = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid DxgiDeviceId = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
    private static readonly Guid Output1Id = new("00cddea8-939b-4b83-a340-a685226666cc");
    private static readonly Guid TextureId = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
    private static readonly object Gate = new();
    private static IDirect3DDevice? _device;
    private static IntPtr _d3d;
    private static IntPtr _context;
    private static IntPtr _duplication;
    private static int _dupX = int.MinValue;
    private static int _dupY = int.MinValue;
    private static byte[]? _frameCache;
    private static int _cacheW;
    private static int _cacheH;
    // staging 回读纹理缓存：只与 D3D 设备绑定、与 duplication 无关，但随 DropDuplication 一起释放，
    // 这样空闲释放/失联/换屏时资源占用行为与旧实现完全一致，仅在连续抓屏期间免掉每次 CreateTexture。
    private static IntPtr _staging;
    private static int _stagingW;
    private static int _stagingH;
    private static uint _stagingFormat;
    private static long _skipGdiUntil;
    private static int _gdiBlankStreak;
    private static long _skipDxgiUntil;
    private static int _dxgiMissStreak;
    private static bool _dupWarm;
    private static Timer? _idleTimer;
    private static long _lastGrabTicks;
    private static IGraphicsCaptureItemInterop? _interop;

    public static void Warmup()
    {
        try
        {
            GetDevice();
            _ = GetInterop();

            // 这里只预热 D3D 设备与 WGC 激活工厂，**不再**预先打开 IDXGIOutputDuplication：
            // 常驻持有 duplication 会让 OBS/录屏/其他截图工具拿不到桌面复制。
            // duplication 由第一次抓屏按需创建，并在最后一次抓屏 3 秒后自动释放（见 OnDuplicationIdle）。
        }
        catch (Exception ex)
        {
            Log.Info("截屏预热失败：" + ex.Message);
        }
    }

    public static async Task<CapturedFrame?> CaptureMonitorAsync(DisplayMonitor monitor)
    {
        var started = Environment.TickCount64;
        CapturedFrame? frame = null;
        var path = "无";
        if (HdrDisplay.IsActive(monitor.Handle))
        {
            try
            {
                frame = await CaptureWithGraphicsAsync(monitor);
                path = "高动态范围";
            }
            catch (Exception ex)
            {
                Log.Info("高动态范围截屏失败，改用普通方式：" + ex.Message);
            }
        }

        if (frame == null)
        {
            try
            {
                var grab = await Task.Run(() => CaptureFast(monitor));
                frame = grab.Frame;
                path = grab.Path;
            }
            catch (Exception ex)
            {
                Log.Info("快速截屏失败，改用系统接口：" + ex.Message);
            }
        }

        if (frame == null)
        {
            try
            {
                frame = await CaptureWithGraphicsAsync(monitor);
                path = "系统接口";
            }
            catch (Exception ex)
            {
                Log.Error("系统截屏接口失败：" + ex);
            }
        }

        if (frame == null)
        {
            try
            {
                var gdi = CaptureWithGdi(monitor);
                // 最后一层兜底也不能把全黑图当成功返回：锁屏/UAC/受保护内容下 BitBlt 会成功但画面全黑，
                // 交给上层提示失败，而不是静默产出黑图。
                if (ImageBytes.IsMostlyBlank(gdi.Bitmap))
                {
                    gdi.Bitmap.Dispose();
                    Log.Info("GDI 画面为空（可能锁屏/UAC/受保护内容），本次截图失败");
                }
                else
                {
                    frame = gdi;
                    path = "GDI";
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex);
            }
        }

        ArmDuplicationIdleRelease();

        if (frame is { } captured)
        {
            Log.Info($"截屏 {captured.Bitmap.PixelWidth}×{captured.Bitmap.PixelHeight} {path} {Environment.TickCount64 - started}ms");
        }

        return frame;
    }

    public static async Task<CapturedFrame?> CaptureDesktopAsync(DisplayMonitor desktop)
    {
        var monitors = ScreenUtil.Monitors();
        var left = monitors.Min(item => item.X);
        var top = monitors.Min(item => item.Y);
        var width = monitors.Max(item => item.X + item.Width) - left;
        var height = monitors.Max(item => item.Y + item.Height) - top;
        if (monitors.Count <= 1 || desktop.X != left || desktop.Y != top || desktop.Width != width || desktop.Height != height)
        {
            return await CaptureMonitorAsync(desktop);
        }

        var pixels = new byte[desktop.Width * desktop.Height * 4];
        var any = false;
        foreach (var monitor in monitors)
        {
            CapturedFrame? frame = null;
            try
            {
                frame = await CaptureMonitorAsync(monitor);
                if (frame == null)
                {
                    continue;
                }

                FrameCompose.Blit(
                    pixels,
                    desktop.Width,
                    desktop.Height,
                    frame.Value.Pixels,
                    frame.Value.Bitmap.PixelWidth,
                    frame.Value.Bitmap.PixelHeight,
                    monitor.X - desktop.X,
                    monitor.Y - desktop.Y);
                any = true;
            }
            catch (Exception ex)
            {
                Log.Info("这块屏幕没有拼上：" + ex.Message);
            }
            finally
            {
                frame?.Bitmap.Dispose();
            }
        }

        if (!any)
        {
            return null;
        }

        var bitmap = ImageBytes.FromBgra(pixels, desktop.Width, desktop.Height);
        return new CapturedFrame(bitmap, pixels);
    }

    internal static int CheckPrimary()
    {
        // 与常驻运行时一致：先预热 D3D 设备与 WGC 工厂（duplication 改为抓屏时按需创建、空闲 3 秒释放）。
        Warmup();
        NativeMethods.GetCursorPos(out var point);
        var monitor = NativeMethods.MonitorFromPoint(point, NativeMethods.MonitorDefaultToNearest);
        var info = new NativeMethods.MonitorInfo { CbSize = Marshal.SizeOf<NativeMethods.MonitorInfo>() };
        if (monitor == IntPtr.Zero || !NativeMethods.GetMonitorInfo(monitor, ref info))
        {
            Console.Error.WriteLine("找不到显示器");
            return 1;
        }

        var bounds = info.RcMonitor;
        var target = new DisplayMonitor(monitor, bounds.Left, bounds.Top, bounds.Width, bounds.Height);
        CapturedFrame? frame = null;
        var failed = false;
        try
        {
            for (var pass = 1; pass <= 2; pass++)
            {
                var started = Environment.TickCount64;
                var grab = CaptureFast(target);
                frame?.Bitmap.Dispose();
                frame = grab.Frame;
                Console.WriteLine($"pass {pass} {grab.Path} {frame.Value.Bitmap.PixelWidth}x{frame.Value.Bitmap.PixelHeight} {Environment.TickCount64 - started}ms");
            }
        }
        catch (Exception ex)
        {
            failed = true;
            Console.Error.WriteLine(ex);
            return 1;
        }
        finally
        {
            // 第二轮 CaptureFast 抛异常时第一轮已抓到的帧不能泄漏；成功路径交由下方 using 释放。
            if (failed)
            {
                frame?.Bitmap.Dispose();
            }
        }

        if (frame == null)
        {
            Console.Error.WriteLine("截屏结果为空");
            return 1;
        }

        using (frame.Value.Bitmap)
        {
            var pixels = frame.Value.Pixels;
            var colored = 0;
            for (var i = 0; i < pixels.Length && colored < 8; i += 4096)
            {
                if (pixels[i] != 0 || pixels[i + 1] != 0 || pixels[i + 2] != 0)
                {
                    colored++;
                }
            }

            Console.WriteLine($"samples={colored}");
            return frame.Value.Bitmap.PixelWidth > 0 && frame.Value.Bitmap.PixelHeight > 0 ? 0 : 1;
        }
    }

    private readonly record struct Grab(CapturedFrame Frame, string Path);

    private static Grab CaptureFast(DisplayMonitor monitor)
    {
        lock (Gate)
        {
            // 首选桌面复制：它是一次 GPU 回读，实测 1440p 热态 16ms；
            // 而 GDI 的屏幕 BitBlt 要 28ms（耗时严格随像素线性增长，且与 DC 来源、CAPTUREBLT 无关）。
            // 连续三次拿不到帧就退避十分钟，避免在受保护内容 / 驱动异常时每次截图都白试一遍。
            if (Environment.TickCount64 >= _skipDxgiUntil)
            {
                try
                {
                    var fast = CaptureWithDxgi(monitor);
                    if (fast != null)
                    {
                        _dxgiMissStreak = 0;
                        ArmDuplicationIdleRelease();
                        return new Grab(fast.Value, "桌面复制");
                    }

                    if (++_dxgiMissStreak >= 3)
                    {
                        _skipDxgiUntil = Environment.TickCount64 + 600_000;
                        _dxgiMissStreak = 0;
                        Log.Info("桌面复制连续拿不到画面，接下来十分钟改用 GDI");
                    }
                }
                catch (Exception ex)
                {
                    Log.Info("桌面复制失败，改用 GDI：" + ex.Message);
                }
            }

            // 与桌面复制一致：GDI 也有黑屏退避期（连续两次空画面后退避十分钟），期间不再白试。
            if (Environment.TickCount64 >= _skipGdiUntil)
            {
                try
                {
                    var gdi = CaptureWithGdi(monitor);
                    if (!ImageBytes.IsMostlyBlank(gdi.Bitmap))
                    {
                        _gdiBlankStreak = 0;
                        ArmDuplicationIdleRelease();
                        return new Grab(gdi, "GDI");
                    }

                    gdi.Bitmap.Dispose();
                    if (++_gdiBlankStreak >= 2)
                    {
                        _skipGdiUntil = Environment.TickCount64 + 600_000;
                    }

                    Log.Info("GDI 画面是空的");
                }
                catch (Exception ex)
                {
                    Log.Info("GDI 截屏失败：" + ex.Message);
                }
            }

            // 这一次抓屏彻底失败：如果 duplication 已经建好也不能一直挂着，同样安排空闲释放。
            ArmDuplicationIdleRelease();
            throw new InvalidOperationException("快速截屏失败。");
        }
    }

    private static async Task<CapturedFrame?> CaptureWithGraphicsAsync(IntPtr monitor)
    {
        var interop = GetInterop();
        var itemId = ItemId;
        var hr = interop.CreateForMonitor(monitor, ref itemId, out var itemPtr);
        Marshal.ThrowExceptionForHR(hr);
        GraphicsCaptureItem item;
        try
        {
            item = WinRT.MarshalInspectable<GraphicsCaptureItem>.FromAbi(itemPtr);
        }
        finally
        {
            Marshal.Release(itemPtr);
        }
        Direct3D11CaptureFramePool? pool = null;
        GraphicsCaptureSession? session = null;
        try
        {
            var device = GetDevice();
            pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                device,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                1,
                item.Size);
            session = pool.CreateCaptureSession(item);
            session.IsCursorCaptureEnabled = false;
            try
            {
                session.IsBorderRequired = false;
            }
            catch (Exception ex)
            {
                Log.Info("无法关闭截屏边框：" + ex.Message);
            }

            try
            {
                session.MinUpdateInterval = TimeSpan.Zero;
            }
            catch (Exception ex)
            {
                Log.Info("无法缩短截屏间隔：" + ex.Message);
            }

            var completion = new TaskCompletionSource<SoftwareBitmap>(TaskCreationOptions.RunContinuationsAsynchronously);
            var skips = new int[1];
            var processing = 0;
            void OnFrame(Direct3D11CaptureFramePool sender, object args)
            {
                if (completion.Task.IsCompleted)
                {
                    return;
                }

                if (Interlocked.CompareExchange(ref processing, 1, 0) != 0)
                {
                    return;
                }

                Direct3D11CaptureFrame? frame = null;
                try
                {
                    frame = sender.TryGetNextFrame();
                }
                catch
                {
                    Interlocked.Exchange(ref processing, 0);
                    return;
                }

                if (frame == null)
                {
                    Interlocked.Exchange(ref processing, 0);
                    return;
                }

                _ = Task.Run(async () =>
                {
                    using (frame)
                    {
                        try
                        {
                            var bitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface);
                            if (ImageBytes.IsMostlyBlank(bitmap) && Interlocked.Increment(ref skips[0]) <= 2)
                            {
                                bitmap.Dispose();
                                Interlocked.Exchange(ref processing, 0);
                                return;
                            }

                            if (!completion.TrySetResult(bitmap))
                            {
                                bitmap.Dispose();
                            }
                        }
                        catch (Exception ex)
                        {
                            completion.TrySetException(ex);
                        }
                    }
                });
            }

            pool.FrameArrived += OnFrame;
            session.StartCapture();
            var finished = await Task.WhenAny(completion.Task, Task.Delay(TimeSpan.FromSeconds(2)));
            pool.FrameArrived -= OnFrame;
            if (finished != completion.Task)
            {
                // 超时后没人再 await completion.Task：先把 TCS 置为结束（取消），
                // 后台任务稍后 TrySetException 会返回 false，避免异常无人观察变成 UnobservedTaskException。
                completion.TrySetCanceled();
                throw new TimeoutException("截屏超时。");
            }

            var bitmapResult = await completion.Task;
            return new CapturedFrame(bitmapResult, ImageBytes.ReadBgra(bitmapResult));
        }
        finally
        {
            session?.Dispose();
            pool?.Dispose();
        }
    }

    private static async Task<CapturedFrame?> CaptureWithGraphicsAsync(DisplayMonitor monitor)
        => await CaptureWithGraphicsAsync(monitor.Handle);

    private static CapturedFrame CaptureWithGdi(DisplayMonitor monitor)
    {
        if (monitor.Width <= 0 || monitor.Height <= 0)
        {
            throw new InvalidOperationException("显示器尺寸无效。");
        }

        var info = new NativeMethods.BitmapInfo
        {
            Header = new NativeMethods.BitmapInfoHeader
            {
                BiSize = 40,
                BiWidth = monitor.Width,
                BiHeight = -monitor.Height,
                BiPlanes = 1,
                BiBitCount = 32,
                BiCompression = NativeMethods.BiRgb,
            },
        };
        var screen = NativeMethods.GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero)
        {
            throw new InvalidOperationException("GetDC 失败。");
        }

        var memory = NativeMethods.CreateCompatibleDC(screen);
        var dib = NativeMethods.CreateDIBSection(screen, ref info, 0, out var bits, IntPtr.Zero, 0);
        if (memory == IntPtr.Zero || dib == IntPtr.Zero || bits == IntPtr.Zero)
        {
            if (dib != IntPtr.Zero)
            {
                NativeMethods.DeleteObject(dib);
            }

            if (memory != IntPtr.Zero)
            {
                NativeMethods.DeleteDC(memory);
            }

            NativeMethods.ReleaseDC(IntPtr.Zero, screen);
            throw new InvalidOperationException("CreateDIBSection 失败。");
        }

        var previous = NativeMethods.SelectObject(memory, dib);
        try
        {
            if (!NativeMethods.BitBlt(
                    memory, 0, 0, monitor.Width, monitor.Height,
                    screen, monitor.X, monitor.Y,
                    NativeMethods.SrcCopy | NativeMethods.CaptureBlt))
            {
                throw new InvalidOperationException("BitBlt 失败。");
            }

            var pixels = new byte[monitor.Width * monitor.Height * 4];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            var bitmap = ImageBytes.FromBgra(pixels, monitor.Width, monitor.Height);
            return new CapturedFrame(bitmap, pixels);
        }
        finally
        {
            NativeMethods.SelectObject(memory, previous);
            NativeMethods.DeleteObject(dib);
            NativeMethods.DeleteDC(memory);
            NativeMethods.ReleaseDC(IntPtr.Zero, screen);
        }
    }

    internal static byte[]? CopyRect(int x, int y, int width, int height)
    {
        if (width < 2 || height < 2 || width > 8000 || height > 8000)
        {
            return null;
        }

        var info = new NativeMethods.BitmapInfo
        {
            Header = new NativeMethods.BitmapInfoHeader
            {
                BiSize = 40,
                BiWidth = width,
                BiHeight = -height,
                BiPlanes = 1,
                BiBitCount = 32,
                BiCompression = NativeMethods.BiRgb,
            },
        };
        var screen = NativeMethods.GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero)
        {
            return null;
        }

        var memory = NativeMethods.CreateCompatibleDC(screen);
        var dib = NativeMethods.CreateDIBSection(screen, ref info, 0, out var bits, IntPtr.Zero, 0);
        if (memory == IntPtr.Zero || dib == IntPtr.Zero || bits == IntPtr.Zero)
        {
            if (dib != IntPtr.Zero)
            {
                NativeMethods.DeleteObject(dib);
            }

            if (memory != IntPtr.Zero)
            {
                NativeMethods.DeleteDC(memory);
            }

            NativeMethods.ReleaseDC(IntPtr.Zero, screen);
            return null;
        }

        var previous = NativeMethods.SelectObject(memory, dib);
        try
        {
            if (!NativeMethods.BitBlt(memory, 0, 0, width, height, screen, x, y, NativeMethods.SrcCopy | NativeMethods.CaptureBlt))
            {
                return null;
            }

            var pixels = new byte[width * height * 4];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            for (var i = 3; i < pixels.Length; i += 4)
            {
                pixels[i] = 255;
            }

            return pixels;
        }
        finally
        {
            NativeMethods.SelectObject(memory, previous);
            NativeMethods.DeleteObject(dib);
            NativeMethods.DeleteDC(memory);
            NativeMethods.ReleaseDC(IntPtr.Zero, screen);
        }
    }

    private static CapturedFrame? CaptureWithDxgi(DisplayMonitor monitor)
    {
        var frame = CaptureWithDxgiOnce(monitor, allowReset: true);
        return frame;
    }

    private static CapturedFrame? CaptureWithDxgiOnce(DisplayMonitor monitor, bool allowReset)
    {
        GetDeviceUnlocked();
        var d3d = _d3d;
        if (d3d == IntPtr.Zero)
        {
            return null;
        }

        if (_duplication != IntPtr.Zero && (_dupX != monitor.X || _dupY != monitor.Y))
        {
            DropDuplication();
        }

        string? miss = null;
        if (_duplication == IntPtr.Zero && !TryOpenDuplication(d3d, monitor, out miss))
        {
            if (miss != null)
            {
                Log.Info("DXGI 未使用：" + miss);
            }

            return null;
        }

        var frame = TryReadDuplication(monitor, out var lost, out var removed, out miss);

        // 刚建立 DuplicateOutput 时拿到的第一帧经常是纯黑（WGC 那条路径也有同样的空帧问题）。
        // 这里再取一次，避免「第一次截图」平白退回 GDI；仍然失败才让调用方兜底。
        if (frame == null && !lost && !_dupWarm)
        {
            frame = TryReadDuplication(monitor, out lost, out removed, out miss);
        }

        if (lost)
        {
            if (removed && allowReset)
            {
                DropDevice();
                return CaptureWithDxgiOnce(monitor, allowReset: false);
            }

            DropDuplication();
            if (!TryOpenDuplication(d3d, monitor, out miss))
            {
                if (removed)
                {
                    DropDevice();
                }

                if (miss != null)
                {
                    Log.Info("DXGI 未使用：" + miss);
                }

                return null;
            }

            frame = TryReadDuplication(monitor, out _, out _, out miss);
        }

        if (frame == null && miss != null)
        {
            Log.Info("DXGI 未使用：" + miss);
        }

        return frame;
    }

    private static bool TryOpenDuplication(IntPtr d3d, DisplayMonitor monitor, out string? miss)
    {
        miss = null;
        var adapter = IntPtr.Zero;
        var output = IntPtr.Zero;
        var output1 = IntPtr.Zero;
        var duplication = IntPtr.Zero;
        try
        {
            var dxgiId = DxgiDeviceId;
            var hr = Fn<QueryFn>(d3d, 0)(d3d, ref dxgiId, out var dxgiDevice);
            if (hr < 0)
            {
                miss = "Query DXGI " + hr.ToString("X8");
                return false;
            }

            try
            {
                hr = Fn<GetAdapterFn>(dxgiDevice, 7)(dxgiDevice, out adapter);
            }
            finally
            {
                Release(dxgiDevice);
            }

            if (hr < 0 || adapter == IntPtr.Zero)
            {
                miss = "GetAdapter " + hr.ToString("X8");
                return false;
            }

            var output1Id = Output1Id;
            for (uint index = 0; index < 16 && output == IntPtr.Zero; index++)
            {
                hr = Fn<EnumOutputsFn>(adapter, 7)(adapter, index, out var candidate);
                if (hr < 0 || candidate == IntPtr.Zero)
                {
                    break;
                }

                if (!OutputMatches(candidate, monitor))
                {
                    Release(candidate);
                    continue;
                }

                output = candidate;
            }

            if (output == IntPtr.Zero)
            {
                miss = "没有匹配的输出 " + hr.ToString("X8");
                return false;
            }

            hr = Fn<QueryFn>(output, 0)(output, ref output1Id, out output1);
            if (hr < 0 || output1 == IntPtr.Zero)
            {
                miss = "Query Output1 " + hr.ToString("X8");
                return false;
            }

            hr = Fn<DuplicateFn>(output1, 22)(output1, d3d, out duplication);
            if (hr < 0 || duplication == IntPtr.Zero)
            {
                miss = "DuplicateOutput " + hr.ToString("X8");
                return false;
            }

            _duplication = duplication;
            _dupX = monitor.X;
            _dupY = monitor.Y;
            duplication = IntPtr.Zero;
            return true;
        }
        finally
        {
            Release(duplication);
            Release(output1);
            Release(output);
            Release(adapter);
        }
    }

    private static CapturedFrame? TryReadDuplication(DisplayMonitor monitor, out bool lost, out bool removed, out string? miss)
    {
        lost = false;
        removed = false;
        miss = null;
        var duplication = _duplication;
        var resource = IntPtr.Zero;
        var texture = IntPtr.Zero;
        var staging = IntPtr.Zero;
        var stagingIsCached = false;
        var acquired = false;
        try
        {
            // FrameInfo 不再丢弃：AccumulatedFrames/LastPresentTime 用于诊断「没有新帧」还是「帧是空白的」。
            var hr = Fn<AcquireFn>(duplication, 8)(duplication, 0, out var frameInfo, out resource);
            if (hr == DxgiWaitTimeout)
            {
                hr = Fn<AcquireFn>(duplication, 8)(duplication, 16, out frameInfo, out resource);
            }

            if (hr == DxgiWaitTimeout)
            {
                var cached = CachedFrame(monitor);
                if (cached != null)
                {
                    return cached;
                }

                miss = $"等待画面超时（AccumulatedFrames={frameInfo.AccumulatedFrames}, LastPresentTime={frameInfo.LastPresentTime}）";
                return null;
            }

            if (hr == DxgiAccessLost || hr == DxgiDeviceRemoved)
            {
                lost = true;
                removed = hr == DxgiDeviceRemoved;
                return null;
            }

            Marshal.ThrowExceptionForHR(hr);
            acquired = true;
            var textureId = TextureId;
            hr = Fn<QueryFn>(resource, 0)(resource, ref textureId, out texture);
            if (hr < 0 || texture == IntPtr.Zero)
            {
                miss = "Query Texture " + hr.ToString("X8");
                return null;
            }

            Fn<GetTexDescFn>(texture, 10)(texture, out var sourceDesc);
            if (sourceDesc.Format is not FormatBgra and not FormatBgraSrgb and not FormatFloat16)
            {
                miss = "格式 " + sourceDesc.Format;
                return null;
            }

            if (sourceDesc.Width != (uint)monitor.Width || sourceDesc.Height != (uint)monitor.Height)
            {
                Log.Info($"DXGI 尺寸 {sourceDesc.Width}×{sourceDesc.Height} 与显示器 {monitor.Width}×{monitor.Height} 不一致");
                lost = true;
                return null;
            }

            var d3d = _d3d;
            // 优先复用按 (宽,高,格式) 缓存的 staging 纹理；尺寸/格式不匹配时释放旧的重建。
            staging = _staging;
            if (staging != IntPtr.Zero
                && (_stagingW != (int)sourceDesc.Width
                    || _stagingH != (int)sourceDesc.Height
                    || _stagingFormat != sourceDesc.Format))
            {
                Release(staging);
                staging = IntPtr.Zero;
                _staging = IntPtr.Zero;
            }

            stagingIsCached = staging != IntPtr.Zero;
            if (staging == IntPtr.Zero)
            {
                var stagingDesc = sourceDesc;
                stagingDesc.MipLevels = 1;
                stagingDesc.ArraySize = 1;
                stagingDesc.SampleCount = 1;
                stagingDesc.SampleQuality = 0;
                stagingDesc.Usage = 3;
                stagingDesc.BindFlags = 0;
                stagingDesc.CpuAccessFlags = 0x20000;
                stagingDesc.MiscFlags = 0;
                hr = Fn<CreateTexFn>(d3d, 5)(d3d, ref stagingDesc, IntPtr.Zero, out staging);
                if (hr < 0 || staging == IntPtr.Zero)
                {
                    miss = "CreateTexture " + hr.ToString("X8");
                    return null;
                }

                _staging = staging;
                _stagingW = (int)sourceDesc.Width;
                _stagingH = (int)sourceDesc.Height;
                _stagingFormat = sourceDesc.Format;
                stagingIsCached = true;
            }

            var context = _context;
            if (context == IntPtr.Zero)
            {
                miss = "没有上下文";
                return null;
            }

            Fn<CopyFn>(context, 47)(context, staging, texture);
            hr = Fn<MapFn>(context, 14)(context, staging, 0, 1, 0, out var mapped);
            if (hr < 0 || mapped.Data == IntPtr.Zero)
            {
                miss = "Map " + hr.ToString("X8");
                return null;
            }

            try
            {
                var width = (int)sourceDesc.Width;
                var height = (int)sourceDesc.Height;
                byte[] pixels;
                if (sourceDesc.Format == FormatFloat16)
                {
                    // HDR（scRGB）帧：按当前显示器的 SDR 白点归一化后再映射，避免 SDR 窗口白底一片死白。
                    pixels = HdrTone.FromFloat16(
                        mapped.Data,
                        width,
                        height,
                        (int)mapped.RowPitch,
                        HdrDisplay.SdrWhiteLevel(monitor.Handle));
                }
                else
                {
                    // 与 _frameCache 同尺寸时直接复用，省掉 4K 一帧约 33MB 的重复分配。
                    // 该数组随后在 _frameCache = pixels 处成为新缓存（沿用既有共享语义）；
                    // 上一帧对它的引用（遮罩 _source）已在 OverlayWindow.Complete 中清空。
                    var scratch = _frameCache;
                    if (scratch == null || scratch.Length != width * height * 4)
                    {
                        scratch = new byte[width * height * 4];
                    }

                    pixels = scratch;
                    if (mapped.RowPitch == (uint)(width * 4))
                    {
                        Marshal.Copy(mapped.Data, pixels, 0, pixels.Length);
                    }
                    else
                    {
                        for (var y = 0; y < height; y++)
                        {
                            Marshal.Copy(mapped.Data + (y * (int)mapped.RowPitch), pixels, y * width * 4, width * 4);
                        }
                    }
                }

                if (IsBlank(pixels))
                {
                    // 复用缓存数组时空白帧已经覆盖了旧缓存，作废缓存避免超时兜底返回空白帧。
                    _frameCache = null;
                    miss = $"画面是空的（AccumulatedFrames={frameInfo.AccumulatedFrames}, LastPresentTime={frameInfo.LastPresentTime}）";
                    return null;
                }

                _dupWarm = true;
                _frameCache = pixels;
                _cacheW = width;
                _cacheH = height;
                var bitmap = ImageBytes.FromBgra(pixels, width, height);
                return new CapturedFrame(bitmap, pixels);
            }
            finally
            {
                Fn<UnmapFn>(context, 15)(context, staging, 0);
            }
        }
        finally
        {
            if (acquired && duplication != IntPtr.Zero)
            {
                Fn<ReleaseFrameFn>(duplication, 14)(duplication);
            }

            Release(resource);
            Release(texture);
            // 缓存的 staging 纹理跨帧复用，不能在这里释放；只有本次新建且未入缓存时才释放。
            if (!stagingIsCached)
            {
                Release(staging);
            }
        }
    }

    private static CapturedFrame? CachedFrame(DisplayMonitor monitor)
    {
        if (_frameCache == null || _cacheW != monitor.Width || _cacheH != monitor.Height || _dupX != monitor.X || _dupY != monitor.Y)
        {
            return null;
        }

        var copy = new byte[_frameCache.Length];
        Buffer.BlockCopy(_frameCache, 0, copy, 0, copy.Length);
        return new CapturedFrame(ImageBytes.FromBgra(copy, _cacheW, _cacheH), copy);
    }

    private static void DropDuplication()
    {
        if (_duplication != IntPtr.Zero)
        {
            Release(_duplication);
            _duplication = IntPtr.Zero;
        }

        _dupX = int.MinValue;
        _dupY = int.MinValue;
        _frameCache = null;
        _dupWarm = false;
        // staging 纹理只依赖设备，但随 duplication 一起释放，保持空闲时的资源占用与旧行为一致。
        if (_staging != IntPtr.Zero)
        {
            Release(_staging);
            _staging = IntPtr.Zero;
        }

        _stagingW = 0;
        _stagingH = 0;
        _stagingFormat = 0;
    }

    private static void DropDevice()
    {
        DropDuplication();
        _device = null;
        if (_context != IntPtr.Zero)
        {
            Release(_context);
            _context = IntPtr.Zero;
        }

        if (_d3d != IntPtr.Zero)
        {
            Release(_d3d);
            _d3d = IntPtr.Zero;
        }
    }

    /// <summary>
    /// 记一次「刚抓到帧」并安排空闲释放。锁 Gate 后调用；在已持有 Gate 的抓屏路径里可重入
    /// （Monitor 可重入），所以 CaptureFast / CaptureMonitorAsync 都能直接调。
    /// 定时器单实例、随进程存活；到点后若期间没有新抓屏就释放 duplication，下次抓屏会按需重建。
    /// </summary>
    private static void ArmDuplicationIdleRelease()
    {
        lock (Gate)
        {
            _lastGrabTicks = Environment.TickCount64;
            if (_duplication == IntPtr.Zero)
            {
                // 这次走的是 GDI/WGC，没有 duplication 需要释放。
                return;
            }

            if (_idleTimer == null)
            {
                _idleTimer = new Timer(OnDuplicationIdle, null, DuplicationIdleMs, Timeout.Infinite);
            }
            else
            {
                _idleTimer.Change(DuplicationIdleMs, Timeout.Infinite);
            }
        }
    }

    private static void OnDuplicationIdle(object? state)
    {
        try
        {
            lock (Gate)
            {
                if (_duplication == IntPtr.Zero)
                {
                    return;
                }

                // 定时器排队等 Gate 期间可能又有新的抓屏（抓屏会续期并重排定时器），这里再确认一次。
                var idle = Environment.TickCount64 - _lastGrabTicks;
                if (idle < DuplicationIdleMs)
                {
                    return;
                }

                Log.Info($"桌面复制空闲 {idle}ms，释放（避免占用 OBS/录屏等桌面复制）");
                DropDuplication();
            }
        }
        catch (Exception ex)
        {
            Log.Info("释放空闲桌面复制失败：" + ex.Message);
        }
    }

    private static bool IsBlank(byte[] pixels)
    {
        if (pixels.Length < 16)
        {
            return true;
        }

        var step = Math.Max(16, pixels.Length / 48);
        step -= step % 4;
        for (var i = 0; i + 3 < pixels.Length; i += step)
        {
            if ((pixels[i] | pixels[i + 1] | pixels[i + 2]) != 0)
            {
                return false;
            }
        }

        return true;
    }

    private static bool OutputMatches(IntPtr output, DisplayMonitor monitor)
    {
        var memory = Marshal.AllocHGlobal(104);
        try
        {
            var hr = Fn<GetDescPtrFn>(output, 7)(output, memory);
            if (hr < 0)
            {
                return false;
            }

            var left = Marshal.ReadInt32(memory, 64);
            var top = Marshal.ReadInt32(memory, 68);
            var handle = Marshal.ReadIntPtr(memory, 88);
            return handle == monitor.Handle || (left == monitor.X && top == monitor.Y);
        }
        finally
        {
            Marshal.FreeHGlobal(memory);
        }
    }

    private static IDirect3DDevice GetDevice()
    {
        lock (Gate)
        {
            return GetDeviceUnlocked();
        }
    }

    private static IDirect3DDevice GetDeviceUnlocked()
    {
        if (_device != null)
        {
            return _device;
        }

        var hr = NativeMethods.D3D11CreateDevice(
            IntPtr.Zero, 1, IntPtr.Zero, 0x20, IntPtr.Zero, 0, 7,
            out var device, out _, out var context);
        if (hr < 0)
        {
            hr = NativeMethods.D3D11CreateDevice(
                IntPtr.Zero, 5, IntPtr.Zero, 0x20, IntPtr.Zero, 0, 7,
                out device, out _, out context);
        }

        Marshal.ThrowExceptionForHR(hr);
        var iid = DxgiDeviceId;
        hr = Marshal.QueryInterface(device, in iid, out var dxgi);
        Marshal.ThrowExceptionForHR(hr);
        try
        {
            hr = NativeMethods.CreateDirect3D11DeviceFromDXGIDevice(dxgi, out var winrtPointer);
            Marshal.ThrowExceptionForHR(hr);
            try
            {
                _device = WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(winrtPointer);
            }
            finally
            {
                Marshal.Release(winrtPointer);
            }
            _d3d = device;
            _context = context;
            return _device;
        }
        finally
        {
            Marshal.Release(dxgi);
            if (_d3d != device)
            {
                Marshal.Release(device);
            }

            if (_context != context && context != IntPtr.Zero)
            {
                Marshal.Release(context);
            }
        }
    }

    private static IGraphicsCaptureItemInterop GetInterop()
    {
        if (_interop != null)
        {
            return _interop;
        }

        lock (Gate)
        {
            if (_interop != null)
            {
                return _interop;
            }

            var name = "Windows.Graphics.Capture.GraphicsCaptureItem";
            var hr = NativeMethods.WindowsCreateString(name, name.Length, out var className);
            Marshal.ThrowExceptionForHR(hr);
            try
            {
                var iid = typeof(IGraphicsCaptureItemInterop).GUID;
                hr = NativeMethods.RoGetActivationFactory(className, ref iid, out var factory);
                Marshal.ThrowExceptionForHR(hr);
                try
                {
                    _interop = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factory);
                    return _interop;
                }
                finally
                {
                    Marshal.Release(factory);
                }
            }
            finally
            {
                NativeMethods.WindowsDeleteString(className);
            }
        }
    }

    private static void Release(IntPtr pointer)
    {
        if (pointer != IntPtr.Zero)
        {
            Fn<ReleaseFn>(pointer, 2)(pointer);
        }
    }

    private static T Fn<T>(IntPtr self, int slot) where T : Delegate
    {
        var table = Marshal.ReadIntPtr(self);
        var method = Marshal.ReadIntPtr(table, slot * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<T>(method);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int QueryFn(IntPtr self, ref Guid iid, out IntPtr result);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint ReleaseFn(IntPtr self);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetAdapterFn(IntPtr self, out IntPtr adapter);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumOutputsFn(IntPtr self, uint index, out IntPtr output);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDescPtrFn(IntPtr self, IntPtr desc);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int DuplicateFn(IntPtr self, IntPtr device, out IntPtr duplication);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int AcquireFn(IntPtr self, uint timeout, out FrameInfo info, out IntPtr resource);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ReleaseFrameFn(IntPtr self);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void GetTexDescFn(IntPtr self, out TextureDesc desc);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateTexFn(IntPtr self, ref TextureDesc desc, IntPtr initialData, out IntPtr texture);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void CopyFn(IntPtr self, IntPtr destination, IntPtr source);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int MapFn(IntPtr self, IntPtr resource, uint subresource, uint mapType, uint mapFlags, out Mapped mapped);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void UnmapFn(IntPtr self, IntPtr resource, uint subresource);

    [StructLayout(LayoutKind.Sequential)]
    private struct FrameInfo
    {
        public long LastPresentTime;
        public long LastMouseUpdateTime;
        public uint AccumulatedFrames;
        public int RectsCoalesced;
        public int ProtectedContentMaskedOut;
        public int PointerX;
        public int PointerY;
        public int PointerVisible;
        public uint TotalMetadataBufferSize;
        public uint PointerShapeBufferSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TextureDesc
    {
        public uint Width;
        public uint Height;
        public uint MipLevels;
        public uint ArraySize;
        public uint Format;
        public uint SampleCount;
        public uint SampleQuality;
        public uint Usage;
        public uint BindFlags;
        public uint CpuAccessFlags;
        public uint MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Mapped
    {
        public IntPtr Data;
        public uint RowPitch;
        public uint DepthPitch;
    }

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        [PreserveSig]
        int CreateForWindow(IntPtr window, [In] ref Guid iid, out IntPtr result);

        [PreserveSig]
        int CreateForMonitor(IntPtr monitor, [In] ref Guid iid, out IntPtr result);
    }
}
