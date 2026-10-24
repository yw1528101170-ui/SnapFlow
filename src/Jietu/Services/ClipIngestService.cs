namespace Jietu;

/// <summary>
/// 剪贴板/截图内容的入库与后台 OCR 编排。
/// 此前「查重→写盘→登记→清理多余副本」在 AppHost.StoreClipAsync 与
/// BitmapCaptureHandler.StoreShot 各有一份且行为已分叉（异步/同步、失败路径处理不同），
/// OCR 调度又与 AppHost 生命周期缠在一起；收敛为本服务作为唯一领域入口。
/// 本服务不触碰任何 UI 类型，识别完成经 <see cref="Recognized"/> 事件通知调用方。
/// </summary>
internal sealed class ClipIngestService
{
    private readonly HistoryStore _store;
    // OCR 全局单飞：识别是重活，串行化避免一屏图片同时识别把 CPU 打满
    private readonly SemaphoreSlim _ocrGate = new(1, 1);

    public ClipIngestService(HistoryStore store)
    {
        _store = store;
    }

    /// <summary>识别完成（已写入历史库）时触发，参数为 (记录 Id, 识别文字)。在后台线程触发。</summary>
    public event Action<string, string>? Recognized;

    /// <summary>
    /// 图片入库（截图路径）：查重→写盘→登记→删除未被采用的副本。
    /// SQLite 异常刻意上抛，由调用方决定用户可见行为（截图流程提示「截图失败」）。
    /// 返回 (落库记录——null 表示重复且已是最新, 实际保留的文件路径)。
    /// </summary>
    public (ClipRecord? Added, string? KeptPath) StoreImage(byte[] png, string source)
    {
        var hash = Hashing.OfBytes(png);
        string? written = null;
        if (!_store.ContainsHash(hash))
        {
            written = Path.Combine(_store.ImageDirectory, Guid.NewGuid().ToString("N") + ".png");
            try
            {
                File.WriteAllBytes(written, png);
            }
            catch (Exception ex)
            {
                // 写盘失败：不要往库里登记一个指向不存在文件的路径（缩略图仍走内存缓存）。
                Log.Error(ex);
                written = null;
            }
        }

        var added = _store.TryAdd(ClipKind.Image, null, written, hash, source);
        if (written != null && added != null && added.ImagePath != written)
        {
            // 命中已有记录（旧重复被重新置顶）：刚写的副本成了孤儿，删掉。
            try
            {
                File.Delete(written);
            }
            catch
            {
            }
        }

        var kept = added?.ImagePath ?? written ?? _store.ImagePathForHash(hash);
        return (added, kept);
    }

    /// <summary>剪贴板监听入库：文本/富文本/文件清单直接登记，图片走同一套查重写盘。</summary>
    public async Task<ClipRecord?> AddCapturedAsync(CapturedClip clip)
    {
        if (clip.Kind == ClipKind.Image && clip.Png == null)
        {
            return null;
        }

        string? path = null;
        if (clip.Kind == ClipKind.Image && clip.Png != null && !_store.ContainsHash(clip.Hash))
        {
            path = Path.Combine(_store.ImageDirectory, Guid.NewGuid().ToString("N") + ".png");
            await File.WriteAllBytesAsync(path, clip.Png);
        }

        var added = _store.TryAdd(clip.Kind, clip.Text, path, clip.Hash, ClipSource.Clipboard, html: clip.Html, rtf: clip.Rtf);
        DeleteUnused(path, added?.ImagePath);
        return added;
    }

    private static void DeleteUnused(string? written, string? kept)
    {
        if (string.IsNullOrEmpty(written) || string.Equals(written, kept, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            File.Delete(written);
        }
        catch (Exception ex)
        {
            Log.Info("删除重复图片失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 后台识别：已有文字且非强制时跳过；全局单飞；返回识别出的文字（跳过/取消/为空时为 null）。
    /// </summary>
    public async Task<string?> RecognizeAsync(string id, byte[] png, bool force, CancellationToken lifetime)
    {
        // 退出排空契约：Shutdown 会在 UI 线程上同步等待本任务（Task.WaitAll），
        // 如果 await 的续体还要排队回 UI 线程，就变成「等的人被等」，等待必然超时、
        // SQLite 连接先被释放、迟到的续体撞上已释放的连接。本方法只碰线程安全的
        // HistoryStore，续体留在线程池即可。
        try
        {
            if (!force && HasRecognized(id))
            {
                return null;
            }

            await _ocrGate.WaitAsync(lifetime).ConfigureAwait(false);
            try
            {
                if (!force && HasRecognized(id))
                {
                    return null;
                }

                var text = await OcrService.ReadAsync(png).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(text) && !lifetime.IsCancellationRequested)
                {
                    _store.SetRecognizedText(id, text, force);
                    Recognized?.Invoke(id, text);
                    return text;
                }
            }
            finally
            {
                _ocrGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Info("文字识别失败：" + ex.Message);
        }

        return null;
    }

    private bool HasRecognized(string id) => !string.IsNullOrWhiteSpace(_store.GetText(id));

    /// <summary>
    /// 启动后空闲时把老管线的识别结果重跑一小批：老版本会在中文之间留下空格，
    /// 预览看起来像一长串乱码。识别结果不再符合该特征，所以不会反复被选中。
    /// </summary>
    public async Task RefreshLegacyAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(6), token).ConfigureAwait(false);
            var pending = _store.GetLegacyOcrImages(24);
            Log.Info($"重识别候选图片：{pending.Count}");
            foreach (var (id, path) in pending)
            {
                if (token.IsCancellationRequested)
                {
                    return;
                }

                try
                {
                    if (!File.Exists(path))
                    {
                        continue;
                    }

                    var png = await File.ReadAllBytesAsync(path, token).ConfigureAwait(false);
                    await RecognizeAsync(id, png, force: true, token).ConfigureAwait(false);
                    await Task.Delay(200, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Log.Info("重识别失败：" + ex.Message);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Info("批量重识别失败：" + ex.Message);
        }
    }
}
