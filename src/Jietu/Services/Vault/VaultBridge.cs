using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.UI.Dispatching;

namespace Jietu;

/// <summary>返回给扩展的登录条目：含密码（填充需要），仅限已解锁 + 已关联令牌。</summary>
public sealed record VaultBridgeLogin(string Id, string Title, string Username, string Password, string Url);

/// <summary>
/// 本机命名管道桥：浏览器扩展的原生消息宿主进程经此管道与主程序通信。
/// 管道 ACL 只允许当前用户；协议层的准入靠「关联令牌」——扩展首次连接时
/// 出示随机令牌，桌面端弹窗确认后只保存其 SHA-256，此后每条请求比对哈希。
/// </summary>
public static class VaultBridgeProtocol
{
    /// <summary>URL 匹配：host 精确（含子域后缀匹配），忽略协议/端口/路径。纯函数可单测。</summary>
    public static bool UrlMatches(string entryUrl, string requestUrl)
    {
        if (string.IsNullOrWhiteSpace(entryUrl) || string.IsNullOrWhiteSpace(requestUrl))
        {
            return false;
        }

        var entryHost = HostOf(entryUrl);
        var requestHost = HostOf(requestUrl);
        return entryHost.Length > 0
               && requestHost.Length > 0
               && (requestHost.Equals(entryHost, StringComparison.OrdinalIgnoreCase)
                   || requestHost.EndsWith("." + entryHost, StringComparison.OrdinalIgnoreCase));
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

    private sealed record PendingPair(string PairId, string DisplayName, string TokenHash, TaskCompletionSource<bool> Tcs);
    private PendingPair? _pendingPair;

    /// <summary>UI 订阅：弹出关联确认（displayName 供用户核对），CompletePair 给出结果。</summary>
    public event Action<string, string>? PairRequested;

    public VaultBridge(DispatcherQueue queue, VaultSession vault, Func<AppSettings> settings)
    {
        _queue = queue;
        _vault = vault;
        _settings = settings;
    }

    public void Start()
    {
        _acceptLoop ??= Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    /// <summary>UI 线程调用：处理关联确认结果。</summary>
    public void CompletePair(string pairId, bool accept)
    {
        if (_pendingPair?.PairId != pairId)
        {
            return;
        }

        if (accept)
        {
            var data = _vault.Data;
            if (data != null)
            {
                data.TrustedBrowsers.RemoveAll(b => b.TokenHash == _pendingPair.TokenHash);
                data.TrustedBrowsers.Add(new VaultTrustedBrowser
                {
                    Id = Guid.NewGuid().ToString("N"),
                    DisplayName = _pendingPair.DisplayName,
                    TokenHash = _pendingPair.TokenHash,
                });
                try
                {
                    _vault.Save();
                }
                catch (Exception ex)
                {
                    Log.Error(ex);
                }
            }
        }

        _pendingPair.Tcs.TrySetResult(accept);
        _pendingPair = null;
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
                "status" => Status(),
                "get-logins" => GetLogins(doc.RootElement),
                "generate" => Generate(doc.RootElement),
                "lock" => LockVault(),
                _ => Error("unknown-type"),
            };
        }
        catch (JsonException)
        {
            return Error("bad-json");
        }
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
        var trusted = _vault.Data?.TrustedBrowsers.Any(b => b.TokenHash == tokenHash) == true;

        if (trusted)
        {
            return Json(new { type = "hello-result", ok = true, paired = true, locked = !_vault.IsUnlocked });
        }

        if (!_vault.IsUnlocked)
        {
            return Json(new { type = "hello-result", ok = false, paired = false, locked = true, reason = "vault-locked" });
        }

        // 未关联：弹桌面确认。同一时刻只允许一个待确认请求，其余直接拒绝。
        if (_pendingPair != null)
        {
            return Error("pair-busy");
        }

        var pairId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingPair = new PendingPair(pairId, displayName, tokenHash, tcs);
        _queue.TryEnqueue(() => PairRequested?.Invoke(pairId, displayName));

        // 确认超时 60 秒：超时按拒绝处理。
        var timeout = Task.Delay(60_000);
        var finished = await Task.WhenAny(tcs.Task, timeout);
        var accepted = finished == tcs.Task && tcs.Task.Result;
        if (_pendingPair?.PairId == pairId)
        {
            _pendingPair = null;
        }

        return accepted
            ? Json(new { type = "hello-result", ok = true, paired = true, locked = false })
            : Error("pair-rejected");
    }

    private string Status()
        => Json(new { type = "status", exists = _vault.Exists, locked = !_vault.IsUnlocked, integration = _settings().VaultBrowserIntegration });

    private string GetLogins(JsonElement root)
    {
        // 集成开关必须在每条取凭据的请求上生效：只在配对时检查的话，
        // 用户关闭集成后已配对的扩展仍能继续读密码。
        if (!_settings().VaultBrowserIntegration)
        {
            return Error("integration-disabled");
        }

        if (!_vault.IsUnlocked)
        {
            return Json(new { type = "logins", locked = true, entries = Array.Empty<object>() });
        }

        var token = GetString(root, "token");
        if (token is not { Length: >= 32 }
            || _vault.Data?.TrustedBrowsers.Any(b => b.TokenHash == Hashing.OfText(token)) != true)
        {
            return Error("not-paired");
        }

        var url = GetString(root, "url") ?? "";
        var entries = _vault.Data!.Entries
            .Where(e => e.Kind == "login" && VaultBridgeProtocol.UrlMatches(e.Url, url))
            .Select(e => new VaultBridgeLogin(e.Id, e.Title, e.Username, e.Password, e.Url))
            .ToArray();
        return Json(new { type = "logins", locked = false, entries });
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
            return Error(ex.Message);
        }
    }

    private string LockVault()
    {
        if (_vault.IsUnlocked)
        {
            _queue.TryEnqueue(_vault.Lock);
        }

        return Json(new { type = "ok" });
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
