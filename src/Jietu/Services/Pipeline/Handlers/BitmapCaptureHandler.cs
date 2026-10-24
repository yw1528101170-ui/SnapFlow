namespace Jietu;

internal sealed class BitmapCaptureHandler : ICaptureActionHandler
{
    public int Priority => 100; // Default fallback

    public bool CanHandle(PostCaptureContext context) => true;

    public async Task ExecuteAsync(PostCaptureContext context)
    {
        await ExecuteBitmapAsync(context);
    }

    public static async Task ExecuteBitmapAsync(PostCaptureContext context)
    {
        var shot = context.Shot;
        var saveShots = context.Settings.SaveScreenshots;
        var folder = context.Settings.SaveFolder;
        var png = await ImageBytes.ToPngAsync(shot.Pixels, shot.Width, shot.Height);
        var result = await Task.Run(() => StoreShot(png, shot.Pixels, shot.Width, shot.Height, saveShots, folder, context));
        
        if (result.Added != null)
        {
            context.NotifyHistoryInserted(result.Added);
            context.RememberImageCache(result.Added.Id, png);
        }

        var pasted = false;
        // 剪贴板没写成功就不要再发 Ctrl+V：那样只会把用户剪贴板里的旧内容粘到前台窗口里。
        if (!result.ClipboardFailed)
        {
            try
            {
                await Task.Delay(60);
                pasted = await PasteService.TryPasteAsync(context.PasteBack);
            }
            catch (Exception ex)
            {
                Log.Error(ex);
            }
        }

        if (shot.Pin && !string.IsNullOrEmpty(result.ImagePath))
        {
            context.PinFileAction(result.ImagePath);
        }

        var pinned = shot.Pin ? "，已钉在桌面" : "";
        if (result.ClipboardFailed)
        {
            // 截图本身已经生成，失败的只是「复制到剪贴板」这一步；按文件/历史是否落地给不同提示。
            // TryAdd 返回 null 表示这张图已是最新历史记录（重复截图），历史里其实有它。
            var inHistory = result.Added != null || context.Store.ContainsHash(Hashing.OfBytes(png));
            if (result.Saved)
            {
                context.Toast.Show("已保存，但复制到剪贴板失败（剪贴板被其他程序占用），可从历史记录重新复制" + pinned);
            }
            else if (inHistory)
            {
                context.Toast.Show("复制到剪贴板失败，但已记录到历史，可从历史记录重新复制" + pinned);
            }
            else
            {
                // 历史入库也失败（TryAdd 没有返回记录且库里确实没有）：截图只存在于内存里。
                context.Toast.Show("截图已生成，但保存历史失败" + pinned);
            }

            return;
        }

        if (pasted)
        {
            context.Toast.Show(result.Saved
                ? "已粘贴，图片已保存" + pinned
                : saveShots && result.SaveFailed ? "已粘贴，图片保存失败" + pinned : "已粘贴" + pinned);
            return;
        }

        context.Toast.Show(result.Saved
            ? "已复制，图片已保存" + pinned
            : saveShots && result.SaveFailed ? "已复制，图片保存失败" + pinned : "已复制，可直接粘贴" + pinned);
    }

    private readonly record struct ShotCommit(ClipRecord? Added, bool Saved, bool SaveFailed, string? ImagePath, bool ClipboardFailed);

    private static ShotCommit StoreShot(byte[] png, byte[] pixels, int width, int height, bool saveShots, string? folder, PostCaptureContext context)
    {
        // 剪贴板可能被其他程序占用（OpenClipboard 失败）：不能让它中断后续入库/写盘。
        // 截图已经生成，复制失败只降级为「没复制成功」，用户仍可从历史里重新复制。
        var clipboardFailed = false;
        try
        {
            context.Clipboard.SetImage(png, pixels, width, height);
        }
        catch (Exception ex)
        {
            clipboardFailed = true;
            Log.Error(ex);
        }

        ClipRecord? added = null;
        var hash = Hashing.OfBytes(png);
        string? path = null;
        if (!context.Store.ContainsHash(hash))
        {
            path = Path.Combine(AppPaths.Images, Guid.NewGuid().ToString("N") + ".png");
            try
            {
                File.WriteAllBytes(path, png);
            }
            catch (Exception ex)
            {
                // 写盘失败：不要往库里登记一个指向不存在文件的路径（缩略图仍走内存缓存）。
                Log.Error(ex);
                path = null;
            }
        }

        // SQLite 异常继续上抛，由 CaptureAsync 的兜底 catch 统一提示「截图失败」。
        added = context.Store.TryAdd(ClipKind.Image, null, path, hash, ClipSource.Screenshot);
        if (path != null && added != null && added.ImagePath != path)
        {
            try { File.Delete(path); } catch { }
        }

        var kept = added?.ImagePath ?? path ?? context.Store.ImagePathForHash(hash);

        var saved = false;
        var failed = false;
        if (saveShots)
        {
            try
            {
                var target = string.IsNullOrWhiteSpace(folder) ? ScreenshotFiles.DefaultFolder : folder;
                ScreenshotFiles.Save(png, target);
                saved = true;
            }
            catch (Exception ex)
            {
                Log.Error(ex);
                failed = true;
            }
        }

        return new ShotCommit(added, saved, failed, kept, clipboardFailed);
    }
}
