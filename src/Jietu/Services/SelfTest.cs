namespace Jietu;

internal static class SelfTest
{
    public static int Run()
    {
        var failures = new List<string>();
        CheckHotkeys(failures);
        CheckInteropLayouts(failures);
        CheckSingleInstanceStop(failures);
        CheckFormat(failures);
        CheckClipboardExclusion(failures);
        CheckDib(failures);
        CheckPixels(failures);
        CheckPicture(failures);
        CheckStore(failures);
        CheckStoreRecovery(failures);
        CheckHardwareMonitor(failures);
        CheckAnnotations(failures);
        CheckMosaic(failures);
        CheckRenderingMetrics(failures);
        CheckPhase2(failures);
        CheckPhase3(failures);
        CheckPhase4(failures);
        CheckVault(failures);
        var message = failures.Count == 0 ? "self-test ok" : string.Join(Environment.NewLine, failures);
        try
        {
            if (failures.Count == 0)
            {
                Console.WriteLine(message);
            }
            else
            {
                Console.Error.WriteLine(message);
            }
        }
        catch
        {
            // WinExe 没有控制台时仍然把结果写到文件。
        }

        try
        {
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "snapflow-selftest-last.txt"), message);
        }
        catch
        {
            // 结果文件写不了时，退出码仍然有效。
        }

        return failures.Count == 0 ? 0 : 1;
    }

    /// <summary>
    /// 互操作结构体布局守卫：这些结构一旦与原生 ABI 错位，不会抛异常，
    /// 只会在运行期静默产出错误数据（FPS 恒为 HZ、HDR 识别错屏）。尺寸与关键字段偏移必须钉死。
    /// </summary>
    private static void CheckInteropLayouts(List<string> failures)
    {
        // DISPLAYCONFIG_PATH_INFO：sourceInfo(20) + targetInfo(48) + flags(4) = 72 字节/路径。
        Expect(System.Runtime.InteropServices.Marshal.SizeOf<HdrDisplay.DisplayConfigPath>() == 72,
            "DISPLAYCONFIG_PATH_INFO 尺寸 72 字节", failures);
        Expect((int)System.Runtime.InteropServices.Marshal.OffsetOf<HdrDisplay.DisplayConfigPath>(nameof(HdrDisplay.DisplayConfigPath.TargetAdapterLow)) == 20,
            "DISPLAYCONFIG_PATH_INFO TargetAdapterLow 偏移 20", failures);
        Expect((int)System.Runtime.InteropServices.Marshal.OffsetOf<HdrDisplay.DisplayConfigPath>(nameof(HdrDisplay.DisplayConfigPath.TargetId)) == 28,
            "DISPLAYCONFIG_PATH_INFO TargetId 偏移 28", failures);
        Expect((int)System.Runtime.InteropServices.Marshal.OffsetOf<HdrDisplay.DisplayConfigPath>(nameof(HdrDisplay.DisplayConfigPath.RefreshNumerator)) == 48,
            "DISPLAYCONFIG_PATH_INFO RefreshNumerator 偏移 48", failures);

        // DISPLAYCONFIG_MODE_INFO：header(16) + union(48) = 64 字节；source 分支 size 在前 position 在后。
        Expect(System.Runtime.InteropServices.Marshal.SizeOf<HdrDisplay.DisplayConfigMode>() == 64,
            "DISPLAYCONFIG_MODE_INFO 尺寸 64 字节", failures);
        Expect((int)System.Runtime.InteropServices.Marshal.OffsetOf<HdrDisplay.DisplayConfigMode>(nameof(HdrDisplay.DisplayConfigMode.Width)) == 16,
            "DISPLAYCONFIG_MODE_INFO Width 偏移 16", failures);
        Expect((int)System.Runtime.InteropServices.Marshal.OffsetOf<HdrDisplay.DisplayConfigMode>(nameof(HdrDisplay.DisplayConfigMode.PositionX)) == 28,
            "DISPLAYCONFIG_MODE_INFO PositionX 偏移 28", failures);

        // DWM_TIMING_INFO：dwmapi.h 全 40 字段共 320 字节，cbSize 必须按 320 填，否则接口拒绝。
        // rateRefresh 在 cbSize(@0) 之后：UNSIGNED_RATIO 对齐为 4，原生布局即在 offset 4，
        // 第一个 ULONGLONG（qpcRefreshPeriod）才补齐到 8 字节对齐（@16）。
        Expect(System.Runtime.InteropServices.Marshal.SizeOf<FrameRateMeter.TimingInfo>() == 320,
            "DWM_TIMING_INFO 尺寸 320 字节", failures);
        Expect((int)System.Runtime.InteropServices.Marshal.OffsetOf<FrameRateMeter.TimingInfo>(nameof(FrameRateMeter.TimingInfo.RateRefresh)) == 4,
            "DWM_TIMING_INFO RateRefresh 偏移 4", failures);
        Expect((int)System.Runtime.InteropServices.Marshal.OffsetOf<FrameRateMeter.TimingInfo>(nameof(FrameRateMeter.TimingInfo.CFramesDisplayed)) == 208,
            "DWM_TIMING_INFO CFramesDisplayed 偏移 208", failures);
        var timing = new FrameRateMeter.TimingInfo { CbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<FrameRateMeter.TimingInfo>() };
        Expect(timing.CbSize == 320, "DWM_TIMING_INFO cbSize=320", failures);
    }

    private static void CheckHotkeys(List<string> failures)
    {
        Expect(HotkeyGesture.TryParse("Alt+A", out var region, out _) && region.Display == "Alt+A", "Alt+A", failures);
        Expect(HotkeyGesture.TryParse("Alt+F", out var full, out _) && full.Display == "Alt+F", "Alt+F", failures);
        Expect(HotkeyGesture.TryParse("Alt+W", out _, out _), "Alt+W", failures);
        Expect(HotkeyGesture.TryParse("Alt+V", out _, out _), "Alt+V", failures);
        Expect(!HotkeyGesture.TryParse("A", out _, out _), "拒绝单独字母", failures);
        Expect(!HotkeyGesture.TryParse("Shift+A", out _, out _), "拒绝只有 Shift", failures);
        Expect(HotkeyGesture.TryParse("Ctrl+Shift+1", out var digit, out _) && digit.Display == "Ctrl+Shift+1", "Ctrl+Shift+1", failures);
        Expect(HotkeyGesture.TryParse("alt + a", out var spaced, out _) && spaced.Display == "Alt+A", "空格和大小写", failures);
        // 回归：录制侧曾能生成 "Ctrl+0x20" 这种解析器读不回的字符串，导致设置页整页无法保存。
        Expect(HotkeyGesture.TryParse("Ctrl+Space", out var space, out _) && space.Display == "Ctrl+Space", "Ctrl+Space 可解析", failures);
        Expect(HotkeyGesture.TryParse(HotkeyGesture.Format(HotkeyGesture.ModControl, 0x20), out var roundTrip, out _) && roundTrip.Display == "Ctrl+Space", "非字母数字键往返", failures);
        Expect(HotkeyGesture.TryParse("Ctrl+0x20", out var legacy, out _) && legacy.VirtualKey == 0x20, "旧 0x 热键配置仍可用", failures);
        Expect(HotkeyGesture.TryParse("Alt+Tab", out var tab, out _) && tab.Display == "Alt+Tab", "Alt+Tab 可解析", failures);
        var duplicate = HotkeyGesture.DuplicateMessage([region, full, region]);
        Expect(duplicate != null, "重复热键", failures);
    }

    /// <summary>
    /// 回归：Restart/Shutdown 会连续调用两次 SingleInstance.Stop；旧实现第二次会抛
    /// ObjectDisposedException，导致 Shutdown 后半段全部跳过（留下僵尸进程）。
    /// </summary>
    private static void CheckSingleInstanceStop(List<string> failures)
    {
        try
        {
            SingleInstance.Stop();
            SingleInstance.Stop();
            Expect(true, "重复 Stop 幂等", failures);
        }
        catch (Exception ex)
        {
            failures.Add("SingleInstance.Stop 幂等: " + ex.Message);
        }
    }

    private static void CheckFormat(List<string> failures)
    {
        var plain = System.Text.Encoding.UTF8.GetBytes("<html><body><!--StartFragment-->E:\\jietu\\src<!--EndFragment--></body></html>");
        var bold = System.Text.Encoding.UTF8.GetBytes("<html><body><!--StartFragment--><b>你好</b><!--EndFragment--></body></html>");
        var link = System.Text.Encoding.UTF8.GetBytes("<a href=\"https://example.com\">hi</a>");
        var plainRtf = System.Text.Encoding.ASCII.GetBytes("{\\rtf1 \\b0 hi}");
        var boldRtf = System.Text.Encoding.ASCII.GetBytes("{\\rtf1 \\b hi}");
        Expect(!ClipFormat.ShouldKeep("E:\\jietu\\src", plain, plainRtf), "纯文字外层不保留格式", failures);
        Expect(ClipFormat.ShouldKeep("你好", bold, null), "加粗保留格式", failures);
        Expect(ClipFormat.ShouldKeep("hi", link, null), "链接保留格式", failures);
        Expect(ClipFormat.ShouldKeep("hi", null, boldRtf), "只有 RTF 加粗时保留", failures);
        Expect(!ClipFormat.ShouldKeep("hi", null, plainRtf), "RTF 未加粗不保留", failures);
        Expect(SensitiveForeground.IsSensitiveProcess("KeePass.exe"), "密码管理器", failures);
        Expect(SensitiveForeground.IsTerminal("WindowsTerminal.exe"), "终端", failures);
        Expect(!SensitiveForeground.IsSensitiveProcess("WindowsTerminal.exe"), "终端不在密码名单", failures);
        Expect(SensitiveForeground.BlocksProcess("cmd.exe"), "终端默认不记录", failures);
        SensitiveForeground.SetRecordTerminals(true);
        Expect(!SensitiveForeground.BlocksProcess("cmd.exe"), "打开后终端可以记录", failures);
        SensitiveForeground.SetRecordTerminals(false);
        Expect(!SensitiveForeground.IsSensitiveProcess("notepad.exe"), "记事本不在内置名单", failures);
    }

    /// <summary>
    /// 剪贴板「请勿收录」契约：密码管理器写入内容时会附带
    /// ExcludeClipboardContentFromMonitorProcessing 等标记，历史库必须整条跳过，
    /// 否则一次性密码会明文入库并被全文索引。
    /// </summary>
    private static void CheckClipboardExclusion(List<string> failures)
    {
        try
        {
            var excludeFormat = NativeMethods.RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing");
            var historyFormat = NativeMethods.RegisterClipboardFormat("CanIncludeInClipboardHistory");
            Expect(excludeFormat != 0 && historyFormat != 0, "排除格式可注册", failures);

            // 摆一条带排除标记的文本：应被识别为「不要收录」。
            PutClipboardTextWithFlags("selftest-secret", excludeFormat, BitConverter.GetBytes(1u));
            Expect(ClipboardService.ClipboardMarkedExcluded(), "排除标记生效", failures);

            // 摆一条普通文本：不应误伤正常复制。
            PutClipboardTextWithFlags("selftest-plain", 0, null);
            Expect(!ClipboardService.ClipboardMarkedExcluded(), "无标记不误伤", failures);

            // CanIncludeInClipboardHistory=0 同样表示排除。
            PutClipboardTextWithFlags("selftest-secret2", historyFormat, BitConverter.GetBytes(0u));
            Expect(ClipboardService.ClipboardMarkedExcluded(), "历史标记 0 排除", failures);
        }
        catch (InvalidOperationException ex) when (ex.Message == "无法打开剪贴板")
        {
            // 没有桌面剪贴板的会话（CI 的服务会话等）跳过本项不算失败；本机打包时始终会执行。
            try { Console.WriteLine("skip: 当前会话没有剪贴板，跳过排除标记检查"); } catch { }
        }
        catch (Exception ex)
        {
            failures.Add("剪贴板排除: " + ex.Message);
        }
        finally
        {
            // 清掉测试内容，不在打包机上留下痕迹。
            try
            {
                if (NativeMethods.OpenClipboard(IntPtr.Zero))
                {
                    try
                    {
                        NativeMethods.EmptyClipboard();
                    }
                    finally
                    {
                        NativeMethods.CloseClipboard();
                    }
                }
            }
            catch
            {
                // 清理失败不影响测试结论。
            }
        }
    }

    private static void PutClipboardTextWithFlags(string text, uint flagFormat, byte[]? flagValue)
    {
        if (!NativeMethods.OpenClipboard(IntPtr.Zero))
        {
            throw new InvalidOperationException("无法打开剪贴板");
        }

        try
        {
            NativeMethods.EmptyClipboard();
            var handle = NativeMethods.AllocBytes(System.Text.Encoding.Unicode.GetBytes(text + "\0"));
            if (NativeMethods.SetClipboardData(NativeMethods.CfUnicodeText, handle) == IntPtr.Zero)
            {
                NativeMethods.GlobalFree(handle);
                throw new InvalidOperationException("写入测试文本失败");
            }

            if (flagFormat != 0 && flagValue != null)
            {
                var flag = NativeMethods.AllocBytes(flagValue);
                if (NativeMethods.SetClipboardData(flagFormat, flag) == IntPtr.Zero)
                {
                    NativeMethods.GlobalFree(flag);
                }
            }
        }
        finally
        {
            NativeMethods.CloseClipboard();
        }
    }

    private static void CheckDib(List<string> failures)
    {
        var pixels = new byte[2 * 2 * 4];
        pixels[2] = 255;
        for (var i = 3; i < pixels.Length; i += 4)
        {
            pixels[i] = 255;
        }

        var dib = DibImage.Build(pixels, 2, 2);
        var roundTrip = DibImage.TryRead(dib, out var back, out var width, out var height);
        Expect(roundTrip && width == 2 && height == 2 && back[2] == 255 && back[0] == 0, "位图往返", failures);

        var masked = new byte[40 + 12 + 4];
        BitConverter.TryWriteBytes(masked.AsSpan(0), 40);
        BitConverter.TryWriteBytes(masked.AsSpan(4), 1);
        BitConverter.TryWriteBytes(masked.AsSpan(8), 1);
        BitConverter.TryWriteBytes(masked.AsSpan(12), (ushort)1);
        BitConverter.TryWriteBytes(masked.AsSpan(14), (ushort)32);
        BitConverter.TryWriteBytes(masked.AsSpan(16), 3);
        masked[52] = 9;
        masked[53] = 8;
        masked[54] = 7;
        masked[55] = 255;
        Expect(DibImage.TryRead(masked, out var one, out _, out _) && one[0] == 9 && one[1] == 8 && one[2] == 7, "位图掩码后才是像素", failures);

        var v5 = new byte[124 + 8 + 4];
        BitConverter.TryWriteBytes(v5.AsSpan(0), 124);
        BitConverter.TryWriteBytes(v5.AsSpan(4), 1);
        BitConverter.TryWriteBytes(v5.AsSpan(8), 1);
        BitConverter.TryWriteBytes(v5.AsSpan(12), (ushort)1);
        BitConverter.TryWriteBytes(v5.AsSpan(14), (ushort)32);
        BitConverter.TryWriteBytes(v5.AsSpan(16), 3);
        BitConverter.TryWriteBytes(v5.AsSpan(112), 124);
        BitConverter.TryWriteBytes(v5.AsSpan(116), 8);
        v5[132] = 4;
        v5[133] = 5;
        v5[134] = 6;
        v5[135] = 255;
        Expect(DibImage.TryRead(v5, out var v5Pixels, out _, out _) && v5Pixels[0] == 4 && v5Pixels[1] == 5 && v5Pixels[2] == 6, "V5 配置文件之后才是像素", failures);
    }

    private static void CheckPixels(List<string> failures)
    {
        var clear = new byte[64];
        clear[2] = 4;
        ImageBytes.ForceOpaque(clear);
        Expect(clear[2] == 4 && clear[3] == 255 && clear[63] == 255, "透明通道补成不透明", failures);

        var mixed = new byte[64];
        mixed[3] = 200;
        mixed[4] = 5;
        ImageBytes.ForceOpaque(mixed);
        Expect(mixed[3] == 200 && mixed[7] == 0 && mixed[4] == 5, "已有透明通道时保持原样", failures);

        var small = new byte[8];
        small[0] = 1;
        ImageBytes.ForceOpaque(small);
        Expect(small[0] == 1 && small[3] == 255 && small[7] == 255, "小图透明通道", failures);
    }

    private static void CheckPicture(List<string> failures)
    {
        Expect(HdrTone.ToByte(0) == 0 && HdrTone.ToByte(1) == 255 && HdrTone.ToByte(2) == 255, "高动态范围白色保持白色", failures);
        Expect(HdrTone.ToByte(0.5f) > 128 && HdrTone.ToByte(0.5f) < 220, "高动态范围中间调经过伽马", failures);
        var dest = new byte[4 * 2 * 4];
        var src = new byte[8];
        src[2] = 9;
        FrameCompose.Blit(dest, 4, 2, src, 2, 1, 1, 1);
        Expect(dest[(1 * 4 + 1) * 4 + 2] == 9, "跨屏拼接写到目标位置", failures);
        CheckScaleDown(failures);
    }

    /// <summary>
    /// 校验硬件缩放路径：尺寸正确、颜色没有跑偏，并且不放大原图。
    /// </summary>
    private static void CheckScaleDown(List<string> failures)
    {
        try
        {
            const int width = 400;
            const int height = 300;
            var pixels = new byte[width * height * 4];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var i = (y * width + x) * 4;
                    pixels[i] = 200;
                    pixels[i + 1] = 100;
                    pixels[i + 2] = 50;
                    pixels[i + 3] = 255;
                }
            }

            using var source = ImageBytes.FromBgra(pixels, width, height);
            using var scaled = ImageBytes.ScaleDownAsync(source, 100).GetAwaiter().GetResult();
            Expect(scaled.PixelWidth == 100 && scaled.PixelHeight == 75, "缩放到指定上限尺寸", failures);

            var outPixels = ImageBytes.ReadBgra(scaled);
            var mid = ((scaled.PixelHeight / 2) * scaled.PixelWidth + (scaled.PixelWidth / 2)) * 4;
            Expect(Math.Abs(outPixels[mid] - 200) <= 4, "缩放后红色通道保持", failures);
            Expect(Math.Abs(outPixels[mid + 1] - 100) <= 4, "缩放后绿色通道保持", failures);
            Expect(Math.Abs(outPixels[mid + 2] - 50) <= 4, "缩放后蓝色通道保持", failures);

            using var unchanged = ImageBytes.ScaleDownAsync(source, 4096).GetAwaiter().GetResult();
            Expect(ReferenceEquals(unchanged, source), "未超限时原图直接返回", failures);
        }
        catch (Exception ex)
        {
            failures.Add("缩放路径: " + ex.Message);
        }
    }

    private static void CheckStore(List<string> failures)
    {
        var root = Path.Combine(Path.GetTempPath(), "snapflow-selftest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var images = Path.Combine(root, "images");
        try
        {
            using var store = new HistoryStore(Path.Combine(root, "history.db"), images);
            var now = DateTimeOffset.UtcNow;
            var first = store.TryAdd(ClipKind.Text, "第一段", null, "h1", ClipSource.Clipboard, now);
            Expect(first != null, "写入第一段", failures);
            Expect(store.TryAdd(ClipKind.Text, "第一段", null, "h1", ClipSource.Clipboard, now.AddSeconds(1)) == null, "连续相同内容去重", failures);
            Expect(store.TryAdd(ClipKind.Text, "第二段 hello", null, "h2", ClipSource.Clipboard, now.AddSeconds(2)) != null, "写入第二段", failures);
            var image = Path.Combine(images, "a.png");
            File.WriteAllBytes(image, [1, 2, 3]);
            Expect(store.TryAdd(ClipKind.Image, null, image, "img", ClipSource.Screenshot, now.AddSeconds(3)) != null, "写入图片", failures);
            var all = store.Query(null);
            Expect(all.Count == 3 && all[0].Kind == ClipKind.Image, "最新的在前", failures);
            store.SetRecognizedText(all[0].Id, "蓝屏错误");
            Expect(store.QueryPage("蓝屏", "shot", 10, null).Count == 1, "截图可按识别文字搜索", failures);
            var firstPage = store.QueryPage(null, "all", 2, null);
            Expect(firstPage.Count == 2, "分页只取两行", failures);
            if (firstPage.Count == 2)
            {
                var secondPage = store.QueryPage(null, "all", 2, new HistoryCursor(firstPage[^1].Pinned ? 1 : 0, firstPage[^1].CreatedUtc, firstPage[^1].Id));
                Expect(secondPage.Count == 1 && secondPage[0].Id != firstPage[0].Id && secondPage[0].Id != firstPage[1].Id, "第二页不重复", failures);
            }
            var shotPage = store.QueryPage(null, "shot", 10, null);
            Expect(shotPage.Count == 1 && shotPage[0].Source == ClipSource.Screenshot, "筛选截图", failures);
            var texts = store.QueryPage(null, "text", 10, null);
            Expect(texts.Count == 2 && texts.All(item => item.Kind == ClipKind.Text), "筛选文字", failures);
            Expect(store.QueryPage(null, "image", 10, null).Count == 0, "截图不进图片筛选", failures);
            Expect(store.QueryPage("hello", "image", 10, null).Count == 0, "无关文字不会搜到图片", failures);
            Expect(store.QueryPage("hello", "shot", 10, null).Count == 0, "无关文字不会搜到截图", failures);
            Expect(store.QueryPage("hello", "text", 10, null).Count == 1, "文字筛选里可以搜索", failures);
            var revived = store.TryAdd(ClipKind.Text, "第一段", null, "h1", ClipSource.Clipboard, now.AddMinutes(1));
            Expect(revived != null && first != null && revived.Id == first.Id, "再次复制旧内容时回到最前", failures);
            var afterRevive = store.Query(null);
            Expect(afterRevive.Count == 3 && revived != null && afterRevive[0].Id == revived.Id, "回到最前且不新增一行", failures);
            Expect(store.TryAdd(ClipKind.Text, "第一段", null, "h1", ClipSource.Clipboard, now.AddMinutes(2)) == null, "已经在最前就不再动", failures);
            var found = store.Query("hello");
            Expect(found.Count == 1 && found[0].Text == "第二段 hello", "搜索文字", failures);
            Expect(store.Query("第二段").Count == 1, "搜索中文", failures);
            // 全文索引由触发器维护：写入后立刻可搜到，且不产生重复条目。
            Expect(store.Query("第一段").Count == 1, "索引同步：写入即可搜到", failures);
            Expect(store.Query("蓝屏错误").Count == 1, "索引同步：识别文字可搜到", failures);
            store.SetPinned(found[0].Id, true);
            Expect(store.Query(null)[0].Id == found[0].Id && store.Query(null)[0].Pinned, "固定的排在前面", failures);
            Expect(store.QueryPage(null, "pinned", 10, null).Count == 1, "筛选固定项", failures);
            Expect(store.Query("不存在").Count == 0, "没有匹配", failures);
            Expect(store.TryAdd(ClipKind.Text, "很旧", null, "old", ClipSource.Clipboard, now.AddDays(-40)) != null, "写入过期记录", failures);
            var removed = store.Purge(30);
            Expect(removed == 1, "清理 30 天前的记录", failures);
            Expect(store.Query(null).Any(item => item.Id == found[0].Id && item.Pinned), "固定项没有被清理", failures);
            store.Delete(all[0].Id);
            Expect(!File.Exists(image), "删除图片文件", failures);
            store.Clear();
            var left = store.Query(null);
            Expect(left.Count == 1 && left[0].Pinned, "清空保留固定项", failures);
            store.Delete(left[0].Id);
            Expect(store.Query(null).Count == 0, "删除固定项后为空", failures);

            var settingsPath = Path.Combine(root, "settings.json");
            SettingsService.Save(settingsPath, new AppSettings
            {
                Paused = true,
                RetentionDays = 30,
                RegionHotkey = "Alt+A",
                IgnoredProcesses = ["chrome.exe", "chrome"],
                AiApiUrl = "https://api.deepseek.com/v1",
                AiApiKey = "sk-test-123456",
                AiModel = "deepseek-chat",
                AiPrompt = "测试提示词",
            });
            var loaded = SettingsService.Load(settingsPath);
            Expect(loaded.Paused && loaded.RetentionDays == 30 && loaded.RegionHotkey == "Alt+A", "设置往返", failures);
            Expect(loaded.AiApiUrl == "https://api.deepseek.com/v1" && loaded.AiApiKey == "sk-test-123456" && loaded.AiModel == "deepseek-chat" && loaded.AiPrompt == "测试提示词", "AI设置往返", failures);
            Expect(loaded.IgnoredProcesses.Count == 1 && loaded.IgnoredProcesses[0] == "chrome.exe", "排除程序会合并同名", failures);

            // 设置持久化回归：AI Key 必须以 DPAPI 密文落盘，且读回仍是明文（供 AI 请求使用）。
            var storedJson = File.ReadAllText(settingsPath);
            Expect(!storedJson.Contains("sk-test-123456", StringComparison.Ordinal) && storedJson.Contains("dpapi:", StringComparison.Ordinal), "AI Key 以 DPAPI 密文落盘", failures);

            // 回归：坏设置文件要备份后回退默认值并给出可见错误，而不是静默清空配置。
            var brokenPath = Path.Combine(root, "settings-broken.json");
            File.WriteAllText(brokenPath, "{ broken");
            var recovered = SettingsService.Load(brokenPath);
            Expect(recovered.RegionHotkey == "Alt+A" && SettingsService.LastLoadError != null, "坏设置文件回退默认并有提示", failures);
            Expect(Directory.GetFiles(root, "settings-broken.json.bad-*").Length == 1, "坏设置文件已备份", failures);

            // 回归：逐字段兜底（手改 null / 非法枚举 / 越界天数不应把应用带崩）。
            var nullPath = Path.Combine(root, "settings-null.json");
            File.WriteAllText(nullPath, "{\"saveFolder\":null,\"regionHotkey\":null,\"osdPosition\":\"garbage\",\"retentionDays\":0}");
            var normalized = SettingsService.Load(nullPath);
            Expect(normalized.SaveFolder == "" && normalized.RegionHotkey == "Alt+A" && normalized.OsdPosition == "TopRight" && normalized.RetentionDays == 1, "非法字段逐项兜底", failures);

            // 回归：写盘失败要能被调用方感知（返回 false + LastSaveError），而不是静默丢失。
            var saveFailed = SettingsService.Save(root, new AppSettings());
            Expect(!saveFailed && SettingsService.LastSaveError != null, "保存失败可感知", failures);
            Expect(ProcessNames.Normalize(["  Notepad  ", @"C:\Windows\notepad.exe"]).Count == 1, "程序名只保留文件名", failures);
            SensitiveForeground.SetIgnored(ProcessNames.Parse("notepad"));
            Expect(SensitiveForeground.IsUserIgnored("notepad.exe"), "用户排除的程序", failures);
            Expect(!SensitiveForeground.IsUserIgnored("code.exe"), "未排除的程序仍记录", failures);
            SensitiveForeground.SetIgnored([]);
            var shots = Path.Combine(root, "shots");
            var firstShot = ScreenshotFiles.Save([1, 2, 3], shots);
            var secondShot = ScreenshotFiles.Save([4, 5, 6], shots);
            Expect(File.Exists(firstShot) && File.Exists(secondShot) && firstShot != secondShot, "截图可另存且不覆盖", failures);

            var blank = new byte[8 * 8 * 4];
            for (var i = 3; i < blank.Length; i += 4)
            {
                blank[i] = 255;
            }

            var drawn = AnnotationRenderer.Render(blank, 8, 8,
            [
                new AnnotateStroke
                {
                    Kind = AnnotateKind.Pen,
                    B = 0,
                    G = 0,
                    R = 255,
                    Points = [(1, 1), (4, 1)],
                },
            ]);
            Expect(drawn[(1 * 8 + 2) * 4 + 2] == 255, "画笔写上红色", failures);
            var empty = new byte[8 * 8 * 4];
            Expect(ReferenceEquals(AnnotationRenderer.Render(empty, 8, 8, []), empty), "没有标注时不复制整图", failures);
            var thin = new byte[8 * 8 * 4];
            var thinDrawn = AnnotationRenderer.Render(thin, 8, 8,
            [
                new AnnotateStroke
                {
                    Kind = AnnotateKind.Pen,
                    B = 0,
                    G = 0,
                    R = 255,
                    WidthPx = 1,
                    Points = [(1, 1), (4, 1)],
                },
            ]);
            Expect(thinDrawn[(1 * 8 + 2) * 4 + 2] == 255, "细线画在路径上", failures);
            Expect(thinDrawn[(0 * 8 + 2) * 4 + 2] == 0, "细线不撑到旁边", failures);

            Expect(store.TryAdd(ClipKind.File, "C:\\a\\报告.docx\nC:\\a\\图.png", null, "filehash", ClipSource.Clipboard, now.AddSeconds(4)) != null, "写入文件", failures);
            Expect(store.Query("报告.docx").Count == 1, "搜索文件名", failures);
            var html = System.Text.Encoding.UTF8.GetBytes("<html><b>你好</b></html>");
            var rtf = System.Text.Encoding.ASCII.GetBytes("{\\rtf1 \\b hi}");
            Expect(store.TryAdd(ClipKind.Rich, "你好", null, "richhash", ClipSource.Clipboard, now.AddSeconds(5), html, rtf) != null, "写入带格式文字", failures);
            var rich = store.Query("你好");
            Expect(rich.Count == 1 && rich[0].Kind == ClipKind.Rich, "搜索带格式文字", failures);
            var payload = store.GetRich(rich[0].Id);
            Expect(payload.Html != null && payload.Html.AsSpan().SequenceEqual(html) && payload.Rtf != null && payload.Rtf.AsSpan().SequenceEqual(rtf), "格式内容还在", failures);
            var drop = FileDrop.Parse(FileDrop.Build([@"C:\a\b.txt", @"D:\图片.png"]));
            Expect(drop.Count == 2 && drop[0] == @"C:\a\b.txt" && drop[1] == @"D:\图片.png", "文件列表可还原", failures);
            var body = new string('字', 3000) + "结尾";
            var addedLong = store.TryAdd(ClipKind.Text, body, null, "longhash", ClipSource.Clipboard, now.AddSeconds(6));
            Expect(addedLong != null && addedLong.Text != null && addedLong.Text.Length == HistoryStore.PreviewChars && !addedLong.Text.EndsWith("结尾"), "写入长文字只返回预览", failures);
            var listed = store.Query("结尾");
            var preview = listed.Count == 1 ? listed[0].Text : null;
            Expect(preview != null && preview.Length < body.Length && !preview.EndsWith("结尾"), "列表不带全文", failures);
            Expect(listed.Count == 1 && store.GetText(listed[0].Id) == body, "全文还在", failures);
            CheckLegacyStoreMigration(failures);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // 临时目录删不掉不影响结果。
            }
        }
    }

    /// <summary>
    /// 第二批回归：数据库文件损坏时不能把异常抛到 AppHost（否则启动即沉默），
    /// 必须备份坏文件、重建 schema 并继续可用。
    /// </summary>
    private static void CheckStoreRecovery(List<string> failures)
    {
        var root = Path.Combine(Path.GetTempPath(), "snapflow-selftest-recovery-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var database = Path.Combine(root, "history.db");
            // 随机/填充字节：header 非法，quick_check 会判定损坏。
            var garbage = new byte[4096];
            for (var i = 0; i < garbage.Length; i++)
            {
                garbage[i] = (byte)(0x5A + (i % 7));
            }

            File.WriteAllBytes(database, garbage);
            using (var store = new HistoryStore(database, Path.Combine(root, "images")))
            {
                Expect(HistoryStore.LastRecoveryMessage != null, "损坏数据库自动备份重建", failures);
                Expect(store.TryAdd(ClipKind.Text, "恢复后写入", null, "recovery-hash", ClipSource.Clipboard) != null, "恢复后仍可写入", failures);
                Expect(store.Query("恢复后写入").Count == 1, "恢复后搜索可用", failures);
            }

            Expect(Directory.GetFiles(root, "history.db.corrupt-*").Length >= 1, "损坏数据库已备份", failures);
        }
        catch (Exception ex)
        {
            failures.Add("CheckStoreRecovery: " + ex.Message);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // 临时目录删不掉不影响结果。
            }
        }
    }

    private static void CheckHardwareMonitor(List<string> failures)
    {
        try
        {
            using var monitor = new HardwareMonitorService();
            var metrics = monitor.Sample();
            Expect(metrics.RamTotalBytes > 0, "物理内存大于0", failures);
            Expect(metrics.CpuUsagePercent >= 0 && metrics.CpuUsagePercent <= 100, "CPU 使用率有效", failures);
            Expect(metrics.RamUsagePercent >= 0 && metrics.RamUsagePercent <= 100, "内存使用率有效", failures);
        }
        catch (Exception ex)
        {
            failures.Add("HardwareMonitor: " + ex.Message);
        }
    }

    /// <summary>
    /// 构造旧版本数据库（独立复制表 FTS），确认升级后索引能自动重建并继续检索。
    /// </summary>
    private static void CheckLegacyStoreMigration(List<string> failures)
    {
        var root = Path.Combine(Path.GetTempPath(), "snapflow-selftest-legacy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var images = Path.Combine(root, "images");
        Directory.CreateDirectory(images);
        var dbPath = Path.Combine(root, "history.db");
        try
        {
            using (var legacy = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}"))
            {
                legacy.Open();
                using var cmd = legacy.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE items (
                      id TEXT PRIMARY KEY,
                      kind TEXT NOT NULL,
                      text TEXT,
                      image_path TEXT,
                      content_hash TEXT NOT NULL,
                      created_utc INTEGER NOT NULL,
                      source TEXT NOT NULL
                    );
                    CREATE VIRTUAL TABLE items_fts USING fts5(id UNINDEXED, body, tokenize = 'trigram');
                    """;
                cmd.ExecuteNonQuery();
                using var seed = legacy.CreateCommand();
                seed.CommandText = "INSERT INTO items (id, kind, text, image_path, content_hash, created_utc, source) VALUES ('old1','text','旧版历史内容',NULL,'h1',1700000000000,'clipboard');";
                seed.ExecuteNonQuery();
                using var seedFts = legacy.CreateCommand();
                seedFts.CommandText = "INSERT INTO items_fts(id, body) VALUES ('old1','旧版历史内容');";
                seedFts.ExecuteNonQuery();
            }

            using var store = new HistoryStore(dbPath, images);
            Expect(store.Query("旧版历史内容").Count == 1, "旧库升级后历史可搜到", failures);
            var added = store.TryAdd(ClipKind.Text, "升级后新写入", null, "h2", ClipSource.Clipboard);
            Expect(added != null, "旧库升级后可继续写入", failures);
            Expect(store.Query("升级后新写入").Count == 1, "旧库升级后新内容可搜到", failures);
            Expect(store.Query("旧版历史内容").Count == 1, "旧库升级后旧内容仍可搜到", failures);
        }
        catch (Exception ex)
        {
            failures.Add("旧库升级: " + ex.Message);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // 临时目录删不掉不影响结果。
            }
        }
    }

    private static void CheckAnnotations(List<string> failures)
    {
        var whiteCanvas = new byte[10 * 10 * 4];
        whiteCanvas.AsSpan().Fill(255);
        var hlStroke = new AnnotateStroke
        {
            Kind = AnnotateKind.Highlighter,
            R = 255, G = 214, B = 10,
            Points = [(2, 5), (8, 5)],
            WidthPx = 4,
        };
        var renderedHl = AnnotationRenderer.Render(whiteCanvas, 10, 10, [hlStroke]);
        var centerIdx = (5 * 10 + 5) * 4;
        Expect(renderedHl[centerIdx] < 200 && renderedHl[centerIdx + 1] > 200 && renderedHl[centerIdx + 2] == 255, "荧光笔高亮着色成功", failures);

        var baseCanvas = new byte[20 * 20 * 4];
        baseCanvas.AsSpan().Fill(200);
        var spotStroke = new AnnotateStroke
        {
            Kind = AnnotateKind.Spotlight,
            R = 0, G = 0, B = 0,
            Points = [(5, 5), (15, 15)],
        };
        var renderedSpot = AnnotationRenderer.Render(baseCanvas, 20, 20, [spotStroke]);
        var outsideIdx = (2 * 20 + 2) * 4;
        Expect(renderedSpot[outsideIdx] == 80, "聚光灯外部暗化正确", failures);
        var insideIdx = (10 * 20 + 10) * 4;
        Expect(renderedSpot[insideIdx] == 200, "聚光灯内部焦点保真", failures);

        var oobCanvas = new byte[20 * 20 * 4];
        oobCanvas.AsSpan().Fill(200);
        var oobSpotStroke = new AnnotateStroke
        {
            Kind = AnnotateKind.Spotlight,
            R = 0, G = 0, B = 0,
            Points = [(-10, -10), (30, 30)],
        };
        var renderedOobSpot = AnnotationRenderer.Render(oobCanvas, 20, 20, [oobSpotStroke]);
        Expect(renderedOobSpot[insideIdx] == 200, "聚光灯越界坐标几何边界钳制有效", failures);
    }

    private static void CheckPhase2(List<string> failures)
    {
        // 1. 条码测试
        var emptyRes = BarcodeService.Scan([], 0, 0);
        Expect(emptyRes == null, "条码空数据安全跳过", failures);

        var blankRes = BarcodeService.Scan(new byte[100 * 100 * 4], 100, 100);
        Expect(blankRes == null, "纯色图像无条码", failures);

        // 2. 隐私规则脱敏测试
        var phoneRegex = new System.Text.RegularExpressions.Regex(@"(?<!\d)1[3-9]\d{9}(?!\d)");
        Expect(phoneRegex.IsMatch("我的手机号是13812345678请联系"), "手机号正则匹配", failures);

        var idRegex = new System.Text.RegularExpressions.Regex(@"(?<!\d)[1-9]\d{5}(18|19|20)\d{2}(0[1-9]|1[0-2])(0[1-9]|[12]\d|3[01])\d{3}[\dXx](?!\d)");
        Expect(idRegex.IsMatch("证件号码: 110101199003072345 谢谢"), "身份证正则匹配", failures);

        var emailRegex = new System.Text.RegularExpressions.Regex(@"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b");
        Expect(emailRegex.IsMatch("邮箱发送至 test.user@gmail.com 查收"), "邮箱正则匹配", failures);

        var ipRegex = new System.Text.RegularExpressions.Regex(@"\b(?:(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\.){3}(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\b");
        Expect(ipRegex.IsMatch("服务器IP为 192.168.1.100 请连接"), "IP地址正则匹配", failures);

        var tokenRegex = new System.Text.RegularExpressions.Regex(@"(?i)\b(?:sk-[a-zA-Z0-9]{20,}|ghp_[a-zA-Z0-9]{20,}|bearer\s+[a-zA-Z0-9_\-\.]{20,})\b");
        Expect(tokenRegex.IsMatch("api_key = sk-1234567890abcdefghijklmn"), "Token正则匹配", failures);

        // 3. AI 视觉服务测试
        var noKeyMsg = AiVisionService.AskVisionAsync([], "test", new AppSettings { AiApiKey = "" }).GetAwaiter().GetResult();
        Expect(noKeyMsg.Contains("尚未配置 AI API Key"), "AI未配置Key提示", failures);
    }

        private static void CheckPhase3(List<string> failures)
    {
        // 1. DXGI + PDH 全品牌显卡通用监视适配器测试
        try
        {
            using var dxgi = new DxgiPdhGpuAdapter();
            dxgi.Initialize();
            if (dxgi.IsAvailable)
            {
                Expect(!string.IsNullOrWhiteSpace(dxgi.GpuName), "DXGI 显卡名称识别", failures);

                var snap = dxgi.Sample();
                Expect(snap.UsagePercent >= 0 && snap.UsagePercent <= 100, "DXGI+PDH GPU利用率合法", failures);
                Expect(snap.MemoryTotalBytes > 0, "DXGI 专有显存总量大于0", failures);
            }
            else
            {
                // 无独显 / 远程会话 / 受限驱动环境下 GPU 监控本来就会降级，不能因此让发布自测失败。
                Console.WriteLine("DXGI+PDH 不可用，跳过 GPU 指标检查");
            }
        }
        catch (Exception ex)
        {
            failures.Add("CheckPhase3 DXGI: " + ex.Message);
        }

        // 2. 多显示器截屏范围模式策略测试
        try
        {
            var cursorMon = ScreenUtil.ShotMonitor("ActiveMonitor");
            Expect(cursorMon.Width > 0 && cursorMon.Height > 0, "光标屏幕范围合法", failures);

            var allMon = ScreenUtil.ShotMonitor("AllMonitors");
            Expect(allMon.Width >= cursorMon.Width && allMon.Height >= 0, "全景跨屏范围合法", failures);

            var autoMon = ScreenUtil.ShotMonitor("Auto");
            Expect(autoMon.Width > 0 && autoMon.Height > 0, "自适应屏幕范围合法", failures);
        }
        catch (Exception ex)
        {
            failures.Add("CheckPhase3 Screen: " + ex.Message);
        }
    }

        private static void CheckPhase4(List<string> failures)
    {
        // 1. 代码提取与语言智能识别测试
        try
        {
            var csharpSnippet = "1 | using System;\n2 | namespace App {\n3 |     public class Calculator {\n4 |         public int Add(int a, int b) => a + b;\n5 |     }\n6 | }";
            var csResult = CodeExtractService.Process(csharpSnippet);
            Expect(csResult.Language == "csharp", "C# 语言精准识别", failures);
            Expect(!csResult.Code.Contains("1 |"), "去除代码行号前缀", failures);
            Expect(csResult.Markdown.StartsWith("```csharp"), "Markdown 代码块包装 C#", failures);

            var pySnippet = "def compute_factorial(n):\n    if n <= 1:\n        return 1\n    return n * compute_factorial(n - 1)\n\nif __name__ == '__main__':\n    print(compute_factorial(5))";
            var pyResult = CodeExtractService.Process(pySnippet);
            Expect(pyResult.Language == "python", "Python 语言精准识别", failures);
            Expect(pyResult.Markdown.StartsWith("```python"), "Markdown 代码块包装 Python", failures);

            var sqlSnippet = "SELECT u.id, u.username, COUNT(o.id) FROM users u LEFT JOIN orders o ON u.id = o.user_id WHERE u.status = 'active' GROUP BY u.id;";
            var sqlResult = CodeExtractService.Process(sqlSnippet);
            Expect(sqlResult.Language == "sql", "SQL 语言精准识别", failures);

            var cleanSample = "Console.WriteLine(“Hello, World”；）—> test";
            var cleaned = CodeExtractService.CleanOcrArtifacts(cleanSample);
            Expect(cleaned.Contains("\"Hello, World\";)") && cleaned.Contains("-> test"), "OCR全角及畸变字符智能纠正", failures);
        }
        catch (Exception ex)
        {
            failures.Add("CheckPhase4 CodeExtract: " + ex.Message);
        }

        // 2. 长截图双层精细化对齐与拼接测试
        try
        {
            var w = 80;
            var h = 100;
            var frame1 = new byte[w * h * 4];
            var frame2 = new byte[w * h * 4];

            // 构造带连续 Y 轴特征的渐变测试图案
            for (var y = 0; y < h; y++)
            {
                var val1 = (byte)(y % 256);
                for (var x = 0; x < w; x++)
                {
                    var idx = (y * w + x) * 4;
                    frame1[idx] = val1;
                    frame1[idx + 1] = (byte)(x % 256);
                    frame1[idx + 2] = 128;
                    frame1[idx + 3] = 255;
                }
            }

            // frame2 向下移动 30 像素
            const int shift = 30;
            for (var y = 0; y < h; y++)
            {
                var srcY = y + shift;
                var val2 = (byte)(srcY % 256);
                for (var x = 0; x < w; x++)
                {
                    var idx = (y * w + x) * 4;
                    frame2[idx] = val2;
                    frame2[idx + 1] = (byte)(x % 256);
                    frame2[idx + 2] = 128;
                    frame2[idx + 3] = 255;
                }
            }

            var detectedFresh = LongShot.FindFresh(frame1, frame2, w, h);
            Expect(Math.Abs(detectedFresh - shift) <= 1, $"长截图双层细搜精准检测位移 (期望 {shift}, 实际 {detectedFresh})", failures);

            var sameRes = LongShot.NearlySame(frame1, frame1);
            Expect(sameRes, "长截图完全相同帧快速识别", failures);

            var joined = LongShot.Join([frame1, frame2], [0, h - shift], w, h, h + shift);
            Expect(joined.Length == w * (h + shift) * 4, "长截图缝合尺寸完全正确", failures);
        }
        catch (Exception ex)
        {
            failures.Add("CheckPhase4 LongShot: " + ex.Message);
        }
    }

    /// <summary>
    /// 马赛克从「每个瓦片一个 XAML 元素」改成「一张位图」后，瓦片光栅化的像素级校验。
    /// </summary>
    private static void CheckMosaic(List<string> failures)
    {
        try
        {
            const int w = 64;
            const int h = 48;
            var source = new byte[w * h * 4];
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    var i = ((y * w) + x) * 4;
                    source[i] = (byte)(x * 3);
                    source[i + 1] = (byte)(y * 4);
                    source[i + 2] = 90;
                    source[i + 3] = 255;
                }
            }

            // 1. 矩形马赛克：包围盒、颜色写入、整块覆盖
            var rect = AnnotationRenderer.MosaicBlocks(source, w, h, (8, 6), (48, 36), 10);
            Expect(rect.Count > 0, "马赛克矩形出块", failures);
            var bounds = MosaicRaster.Bounds(rect);
            Expect(bounds.Width == 40 && bounds.Height == 30, "马赛克包围盒", failures);

            var canvas = new byte[bounds.Width * bounds.Height * 4];
            var painted = MosaicRaster.Paint(canvas, bounds.Width, bounds.Height, bounds.X, bounds.Y, rect);
            Expect(painted == rect.Count, "马赛克全部上色", failures);

            var tile = rect[0];
            var at = (((tile.Y - bounds.Y) * bounds.Width) + (tile.X - bounds.X)) * 4;
            Expect(canvas[at] == tile.B && canvas[at + 1] == tile.G && canvas[at + 2] == tile.R && canvas[at + 3] == 255,
                "马赛克颜色写入正确", failures);

            // 2. 增量：同一批瓦片第二次不应重复上色
            var drawn = new HashSet<long>();
            var first = MosaicRaster.Paint(canvas, bounds.Width, bounds.Height, bounds.X, bounds.Y, rect, drawn);
            var second = MosaicRaster.Paint(canvas, bounds.Width, bounds.Height, bounds.X, bounds.Y, rect, drawn);
            Expect(first == rect.Count && second == 0, "马赛克增量不重复上色", failures);

            // 3. 越界裁剪：画布小于瓦片范围时不能越界写入
            var small = new byte[12 * 12 * 4];
            MosaicRaster.Paint(small, 12, 12, bounds.X + 30, bounds.Y + 24, rect);
            Expect(small.Length == 12 * 12 * 4, "马赛克越界裁剪", failures);

            // 4. 笔刷：能出块、能增量、重复调用不叠加
            var brush = AnnotationRenderer.MosaicBrush(source, w, h, [(20, 20), (40, 26)], 9);
            Expect(brush.Count > 0, "马赛克笔刷出块", failures);
            var brushBounds = MosaicRaster.Bounds(brush);
            var brushCanvas = new byte[brushBounds.Width * brushBounds.Height * 4];
            var brushDrawn = new HashSet<long>();
            var b1 = MosaicRaster.Paint(brushCanvas, brushBounds.Width, brushBounds.Height, brushBounds.X, brushBounds.Y, brush, brushDrawn);
            var b2 = MosaicRaster.Paint(brushCanvas, brushBounds.Width, brushBounds.Height, brushBounds.X, brushBounds.Y, brush, brushDrawn);
            Expect(b1 == brush.Count && b2 == 0, "马赛克笔刷增量", failures);

            // 5. 超限不静默：大量点 + 大半径时必须返回 truncated，而不是悄悄截断覆盖范围。
            const int bigW = 1200;
            const int bigH = 1200;
            var bigSource = new byte[bigW * bigH * 4];
            var many = new List<(int X, int Y)>();
            for (var i = 0; i < 160; i++)
            {
                many.Add((40 + (i % 40) * 28, 40 + (i / 40) * 260));
            }

            var capped = AnnotationRenderer.MosaicBrush(bigSource, bigW, bigH, many, 48, 12, out var truncated);
            Expect(capped.Count > 0 && truncated, "马赛克超限会标记 truncated", failures);
        }
        catch (Exception ex)
        {
            failures.Add("CheckMosaic: " + ex.Message);
        }
    }

    /// <summary>
    /// 第二批回归：序号徽标预览/导出共用公式，以及 HDR 按 SDR 白点归一化不再整体过曝。
    /// </summary>
    private static void CheckRenderingMetrics(List<string> failures)
    {
        try
        {
            var badge = AnnotationRenderer.NumberBadgeMetrics(20);
            Expect(badge.Radius >= 12 && badge.FontSize == 20, "序号徽标尺寸公式", failures);
            var bigBadge = AnnotationRenderer.NumberBadgeMetrics(28);
            Expect(bigBadge.Radius > badge.Radius && bigBadge.FontSize == 28, "序号徽标随字号增长", failures);

            // 同一线性亮度 0.5：SDR 白点 200nit 时应比 80nit 基准更暗（而不是两边都过曝成白）。
            var buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(8);
            try
            {
                // 三个通道取不同线性值：输出约定是 BGRA（byte0=B、byte2=R），
                // 若实现把 R/B 写反，这里立刻暴露，同值测试测不出通道互换。
                var r = BitConverter.HalfToUInt16Bits((Half)0.5f);
                var g = BitConverter.HalfToUInt16Bits((Half)0.25f);
                var b = BitConverter.HalfToUInt16Bits((Half)0.75f);
                var one = BitConverter.HalfToUInt16Bits((Half)1f);
                System.Runtime.InteropServices.Marshal.WriteInt16(buffer, 0, unchecked((short)r));
                System.Runtime.InteropServices.Marshal.WriteInt16(buffer, 2, unchecked((short)g));
                System.Runtime.InteropServices.Marshal.WriteInt16(buffer, 4, unchecked((short)b));
                System.Runtime.InteropServices.Marshal.WriteInt16(buffer, 6, unchecked((short)one));
                var baseWhite = HdrTone.FromFloat16(buffer, 1, 1, 8);
                var hdrWhite = HdrTone.FromFloat16(buffer, 1, 1, 8, 200f);
                Expect(baseWhite[0] == HdrTone.ToByte(0.75f) && baseWhite[1] == HdrTone.ToByte(0.25f)
                    && baseWhite[2] == HdrTone.ToByte(0.5f), "HDR 输出按 BGRA 通道序", failures);
                Expect(baseWhite[2] > hdrWhite[2], "HDR 按 SDR 白点归一化", failures);
                Expect(baseWhite[3] == 255 && hdrWhite[3] == 255, "HDR 透明通道不透明", failures);
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer);
            }

            Expect(Math.Abs(HdrDisplay.SdrWhiteLevel(IntPtr.Zero) - 80f) < 0.1f, "SDR 白点查询安全回退", failures);
        }
        catch (Exception ex)
        {
            failures.Add("CheckRenderingMetrics: " + ex.Message);
        }
    }

    private static void Expect(bool condition, string name, List<string> failures)
    {
        if (!condition)
        {
            failures.Add("失败: " + name);
        }
    }

    /// <summary>
    /// 密码库容器冒烟：头序列化/解析、整库加密往返、篡改必败、损坏文件备份。
    /// 用小 KDF 参数（8MiB/1 迭代）保证自检秒级完成。
    /// </summary>
    private static void CheckVault(List<string> failures)
    {
        try
        {
            var header = new VaultHeader
            {
                Version = VaultHeader.CurrentVersion,
                Salt = System.Security.Cryptography.RandomNumberGenerator.GetBytes(VaultHeader.SaltLength),
                Iterations = 1,
                MemoryKiB = VaultHeader.MinMemoryKiB,
                Parallelism = 1,
                Nonce = System.Security.Cryptography.RandomNumberGenerator.GetBytes(VaultHeader.NonceLength),
            };
            Expect(VaultHeader.TryParse(header.ToBytes(), out var parsed), "vault 头序列化往返", failures);
            Expect(parsed.Iterations == 1 && parsed.MemoryKiB == VaultHeader.MinMemoryKiB, "vault 头参数保真", failures);

            var bad = header.ToBytes();
            bad[0] = (byte)'X';
            Expect(!VaultHeader.TryParse(bad, out _), "vault 头拒绝坏 magic", failures);

            var key = VaultCrypto.DeriveKey("self-test"u8, header);
            var body = VaultCrypto.Seal(header, key, VaultDefaults.NewData());
            Expect(VaultCrypto.Open(header, key, body) != null, "vault 加解密往返", failures);
            var tampered = (byte[])body.Clone();
            tampered[0] ^= 0xFF;
            Expect(VaultCrypto.Open(header, key, tampered) == null, "vault 篡改必败", failures);

            var path = Path.Combine(Path.GetTempPath(), "snapflow-selftest-vault-" + Guid.NewGuid().ToString("N") + ".spv");
            try
            {
                VaultStore.Save(path, header, body);
                var raw = VaultStore.Load(path);
                Expect(VaultStore.Parse(path, raw) != null, "vault 文件落盘解析", failures);
            }
            finally
            {
                try { File.Delete(path); } catch { }
            }
        }
        catch (Exception ex)
        {
            failures.Add("CheckVault: " + ex.Message);
        }
    }
}
