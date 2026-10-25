using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.UI.Dispatching;

namespace Jietu;

/// <summary>返回给扩展的登录条目：含密码（填充需要），仅限已解锁 + 已关联令牌。</summary>
public sealed record VaultBridgeLogin(string Title, string Username, string Password, string Url);

/// <summary>
/// 本机命名管道桥：浏览器扩展的原生消息宿主进程经此管道与主程序通信。
/// 管道 ACL 只允许当前用户；协议层的准入靠「关联令牌」——扩展首次连接时
/// 出示随机令牌，桌面端弹窗确认后只保存其 SHA-256，此后每条请求比对哈希。
/// </summary>
public static class VaultBridgeProtocol
{
    /// <summary>单次返回条目上限（每条都含明文密码，返回越少泄漏面越小）。</summary>
    public const int MaxReturnedLogins = 5;

    /// <summary>取 host 供审计记录用：不记录完整 URL，避免把路径里的敏感信息带进审计。</summary>
    public static string HostOfUrl(string url) => HostOf(url);

    /// <summary>
    /// URL 匹配：host 精确（含子域后缀匹配），忽略协议/端口/路径。纯函数可单测。
    ///
    /// 后缀匹配只对"机构主域"开放：单标签 host（`com`）与多级公共后缀（`github.io`）
    /// 一律降级为精确相等，否则 `evil.github.io` 会命中 `github.io` 条目并自动填充密码。
    /// </summary>
    public static bool UrlMatches(string entryUrl, string requestUrl)
    {
        if (string.IsNullOrWhiteSpace(entryUrl) || string.IsNullOrWhiteSpace(requestUrl))
        {
            return false;
        }

        var entryHost = HostOf(entryUrl);
        var requestHost = HostOf(requestUrl);
        if (entryHost.Length == 0 || requestHost.Length == 0)
        {
            return false;
        }

        if (requestHost.Equals(entryHost, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!VaultSuffixPolicy.AllowsSuffixMatch(entryHost))
        {
            return false;
        }

        // 带前导点比较：`notgithub.com` 不会命中 `github.com`。
        return requestHost.EndsWith("." + entryHost, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>取 URL 的 host；没有 scheme 的裸域名（用户手存的 "example.com"）原样返回；www. 前缀视作等价。</summary>
    private static string HostOf(string url)
    {
        string text;
        if (Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "ftp")
        {
            text = uri.Host;
        }
        else
        {
            // 去掉可能存在的路径尾巴。
            text = url.Trim().TrimEnd('/');
            var slash = text.IndexOf('/');
            text = slash > 0 ? text[..slash] : text;
        }

        return text.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? text[4..] : text;
    }
}

internal sealed class VaultBridge : IDisposable
{
    public const string PipeName = "SnapFlowVault";

    private readonly DispatcherQueue _queue;
    private readonly VaultSession _vault;
    private readonly Func<AppSettings> _settings;
    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptLoop;

    /// <summary>配对槽位（原子占用，防并发连接同时"抢到"）。</summary>
    private readonly VaultPairingGate _pairing = new();

    /// <summary>请求限流：60 次/分钟，超限暂停 60 秒。</summary>
    private readonly VaultRateLimiter _limiter = new();

    /// <summary>内存审计（200 条，不落盘、不含用户名与口令）。</summary>
    private readonly VaultAuditLog _audit = new();

    /// <summary>UI 订阅：弹出关联确认（displayName 供用户核对），CompletePair 给出结果。</summary>
    public event Action<string, string>? PairRequested;

    public VaultBridge(DispatcherQueue queue, VaultSession vault, Func<AppSettings> settings)
    {
        _queue = queue;
        _vault = vault;
        _settings = settings;
    }

    /// <summary>供设置页展示"最近的取用记录"。</summary>
    public IReadOnlyList<VaultAuditRecord> RecentAudit => _audit.Snapshot();

    public void Start()
    {
        _acceptLoop ??= Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    /// <summary>UI 线程调用：处理关联确认结果。</summary>
    public void CompletePair(string pairId, bool accept)
    {
        var pending = _pairing.Current;
        if (pending?.PairId != pairId)
        {
            return;
        }

        if (accept)
        {
            var data = _vault.Data;
            if (data != null)
            {
                data.TrustedBrowsers.RemoveAll(b => b.TokenHash == pending.TokenHash);
                data.TrustedBrowsers.Add(new VaultTrustedBrowser
                {
                    Id = Guid.NewGuid().ToString("N"),
                    DisplayName = pending.DisplayName,
                    TokenHash = pending.TokenHash,
                });

                // 走异步保存管线：配对是低频操作，但没必要为它开一条同步写盘路径。
                _vault.RequestSave();
            }
        }

        _pairing.Complete(pairId, accept);
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = CreatePipe();
                await pipe.WaitForConnectionAsync(ct);
                _ = Task.Run(() => HandleClientAsync(pipe, ct), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Info("VaultBridge 管道接受失败：" + ex.Message);
                pipe?.Dispose();
                try
                {
                    await Task.Delay(2000, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    /// <summary>管道 ACL：只允许当前用户完全控制（自定义 SD 取代默认 DACL，其余账户无任何权限）。</summary>
    private static NamedPipeServerStream CreatePipe()
    {
        var security = new PipeSecurity();
        var identity = WindowsIdentity.GetCurrent();
        security.AddAccessRule(new PipeAccessRule(
            identity.User!,
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        // 注意：CurrentUserOnly 与自定义 PipeSecurity 互斥（运行时校验拒绝）；
        // 同用户限制由显式 DACL 承担。
        return NamedPipeServerStreamAcl.Create(
            PipeName,
            PipeDirection.InOut,
            4,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            0,
            0,
            security);
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        try
        {
            using var reader = new StreamReader(pipe, System.Text.Encoding.UTF8, leaveOpen: true);
            await using var writer = new StreamWriter(pipe, System.Text.Encoding.UTF8, leaveOpen: true) { AutoFlush = true };
            while (!ct.IsCancellationRequested && pipe.IsConnected)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line == null)
                {
                    return;
                }

                var response = await HandleMessageAsync(line);
                await writer.WriteLineAsync(response);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
            // 客户端断开是常态，不是错误。
        }
        catch (Exception ex)
        {
            Log.Info("VaultBridge 客户端处理失败：" + ex.Message);
        }
        finally
        {
            try { pipe.Dispose(); } catch { }
        }
    }

    private async Task<string> HandleMessageAsync(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var type = doc.RootElement.TryGetProperty("type", out var t) ? t.GetString() : null;
            return type switch
            {
                "hello" => await HandleHelloAsync(doc.RootElement),
                "status" => await StatusAsync(),
                "get-logins" => await GetLoginsAsync(doc.RootElement),
                "generate" => Generate(doc.RootElement),
                _ => Error("unknown-type"),
            };
        }
        catch (JsonException)
        {
            return Error("bad-json");
        }
    }

    /// <summary>
    /// 把对会话状态的读取编组到 UI 线程执行：VaultData 只允许 UI 线程读写
    /// （见 VaultModels 的线程纪律），桥线程池直接遍历 Entries/TrustedBrowsers
    /// 会与编辑器并发写撕裂。lambda 必须只返回 string / 值类型 / 不可变快照，
    /// 不得把活对象带出 UI 线程。
    /// </summary>
    private async Task<T> ReadVaultAsync<T>(Func<VaultSession, T> read)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_queue.TryEnqueue(() =>
            {
                try
                {
                    tcs.TrySetResult(read(_vault));
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            }))
        {
            throw new InvalidOperationException("UI 线程不可用，无法读取密码库状态。");
        }

        return await tcs.Task.ConfigureAwait(false);
    }

    private async Task<string> HandleHelloAsync(JsonElement root)
    {
        if (!_settings().VaultBrowserIntegration)
        {
            return Error("integration-disabled");
        }

        var token = GetString(root, "token");
        if (token is not { Length: >= 32 })
        {
            return Error("bad-token");
        }

        var tokenHash = Hashing.OfText(token);
        var displayName = GetString(root, "name") ?? "未知浏览器";
        var (trusted, unlocked) = await ReadVaultAsync(v => (
            v.Data?.TrustedBrowsers.Any(b => b.TokenHash == tokenHash) == true,
            v.IsUnlocked));

        if (trusted)
        {
            return Json(new { type = "hello-result", ok = true, paired = true, locked = !unlocked });
        }

        if (!unlocked)
        {
            return Json(new { type = "hello-result", ok = false, paired = false, locked = true, reason = "vault-locked" });
        }

        // 未关联：弹桌面确认。同一时刻只允许一个待确认请求，其余直接拒绝。
        // TryBegin 把"判断是否已有等待"和"占位"放进同一个临界区：
        // 原来的先判 null 再赋值在并发连接下会让两个请求都认为自己抢到了。
        var pairId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pairing.TryBegin(new VaultPairingGate.PendingPair(pairId, displayName, tokenHash, tcs)))
        {
            return Error("pair-busy");
        }

        _queue.TryEnqueue(() => PairRequested?.Invoke(pairId, displayName));

        // 确认超时 60 秒：超时按拒绝处理。
        var timeout = Task.Delay(60_000);
        var finished = await Task.WhenAny(tcs.Task, timeout);
        var accepted = finished == tcs.Task && tcs.Task.Result;

        // 只清掉自己那一个槽位：若已超时并被新请求占用，不能把新请求一起清掉。
        _pairing.Abandon(pairId);

        return accepted
            ? Json(new { type = "hello-result", ok = true, paired = true, locked = false })
            : Error("pair-rejected");
    }

    private async Task<string> StatusAsync()
    {
        var (exists, locked, blockKind, blockReason) = await ReadVaultAsync(v => (
            v.Exists,
            !v.IsUnlocked,
            v.Block.Kind,
            v.Block.Reason));
        // blocked 为 null 表示"库可用/未创建"；有值时是 newer-version / integrity / unknown-file。
        // formatVersion 只用于让扩展区分"正常库"与"损坏"，不做任何解密推测。
        var formatVersion = blockKind switch
        {
            VaultBlockKind.None => exists ? 2 : 0,
            _ => 0,
        };
        return Json(new
        {
            type = "status",
            exists,
            locked,
            blocked = blockKind == VaultBlockKind.None ? null : blockReason,
            formatVersion,
            integration = _settings().VaultBrowserIntegration,
        });
    }

    /// <summary>get-logins 的判定结果：错误 reason / 锁定 / 已过滤的条目快照三选一（Result 对应审计分类）。</summary>
    private sealed record LoginsOutcome(VaultAuditResult? Result, string? Error, string? Detail, bool Locked, VaultBridgeLogin[] Entries);

    private async Task<string> GetLoginsAsync(JsonElement root)
    {
        var url = GetString(root, "url") ?? "";
        var host = VaultBridgeProtocol.HostOfUrl(url);

        // 限流放在最前：异常程序连"探测开关状态"都不该有免费额度。
        if (!_limiter.TryAcquire(out var retryAfter))
        {
            _audit.Record(host, VaultAuditResult.RateLimited);
            return Json(new { type = "error", reason = "rate-limited", retryAfterSeconds = retryAfter });
        }

        // 集成开关必须在每条取凭据的请求上生效：只在配对时检查的话，
        // 用户关闭集成后已配对的扩展仍能继续读密码。
        if (!_settings().VaultBrowserIntegration)
        {
            _audit.Record(host, VaultAuditResult.IntegrationDisabled);
            return Error("integration-disabled");
        }

        var token = GetString(root, "token");
        var outcome = await ReadVaultAsync(v =>
        {
            // 阻断态必须显式告诉扩展，否则扩展会把"读不到"当成"没有这个站点的密码"。
            if (v.IsBlocked)
            {
                return new LoginsOutcome(VaultAuditResult.Blocked, "vault-blocked", v.Block.Reason, false, []);
            }

            if (!v.IsUnlocked)
            {
                return new LoginsOutcome(null, null, null, true, []);
            }

            if (token is not { Length: >= 32 }
                || v.Data?.TrustedBrowsers.Any(b => b.TokenHash == Hashing.OfText(token)) != true)
            {
                return new LoginsOutcome(VaultAuditResult.NotPaired, "not-paired", null, false, []);
            }

            // 只返回该域名的前 5 条：填充菜单放不下更多，而返回越少，单次请求的泄漏面越小
            // （每一条都含明文密码）。回收站条目不在 Entries 里，天然被排除。
            var entries = v.Data!.Entries
                .Where(e => e.Kind == "login" && VaultBridgeProtocol.UrlMatches(e.Url, url))
                .OrderByDescending(e => e.UpdatedUtc)
                .Take(VaultBridgeProtocol.MaxReturnedLogins)
                .Select(e => new VaultBridgeLogin(e.Title, e.Username, e.Password, e.Url))
                .ToArray();
            return new LoginsOutcome(null, null, null, false, entries);
        });

        if (outcome.Result is { } denied)
        {
            _audit.Record(host, denied);
            return outcome.Detail is { } detail
                ? Json(new { type = "error", reason = outcome.Error, detail })
                : Error(outcome.Error!);
        }

        if (outcome.Locked)
        {
            _audit.Record(host, VaultAuditResult.VaultLocked);
            return Json(new { type = "logins", locked = true, entries = Array.Empty<VaultBridgeLogin>() });
        }

        _audit.Record(host, VaultAuditResult.Ok, outcome.Entries.Length);
        return Json(new { type = "logins", locked = false, entries = outcome.Entries });
    }

    private string Generate(JsonElement root)
    {
        try
        {
            var password = GetInt(root, "words") is > 0 and var words
                ? PasswordGenerator.GeneratePassphrase(Math.Clamp(words, PasswordGenerator.MinWords, PasswordGenerator.MaxWords))
                : PasswordGenerator.Generate(
                    Math.Clamp(GetInt(root, "length") ?? 16, PasswordGenerator.MinLength, PasswordGenerator.MaxLength));
            return Json(new { type = "generated", password });
        }
        catch (Exception ex)
        {
            // reason 是协议码不是文案：中文异常文本不进协议，只进日志。
            Log.Info("VaultBridge 生成密码失败：" + ex.Message);
            return Error("generate-failed");
        }
    }

    private static string? GetString(JsonElement root, string name)
        => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? GetInt(JsonElement root, string name)
        => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    private static string Error(string reason) => Json(new { type = "error", reason });

    private static string Json(object payload)
        => JsonSerializer.Serialize(payload, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _acceptLoop?.Wait(1500);
        }
        catch
        {
            // 关闭时不等待卡死的连接。
        }

        _cts.Dispose();
    }
}
