// SnapFlow 密码库 - popup：状态展示 + 当前站点凭据列表 + 填充 + 生成器。
// 每次打开 popup 都会经原生消息与桌面端通信（sendNativeMessage 每条拉起一次宿主，
// 宿主进程只做 stdio↔管道转发，开销可忽略）。

const HOST = "com.snapflow.vault";

// 桌面端挂起时原生消息可能永不回应：所有请求都带超时，避免 popup 永远停在「读取中」。
const NATIVE_TIMEOUT_MS = 10000;

// 关联令牌：首次生成后存在扩展本地，桌面端只存其哈希。
async function getToken() {
    const store = await chrome.storage.local.get("token");
    if (store.token && store.token.length >= 32) return store.token;
    const bytes = new Uint8Array(32);
    crypto.getRandomValues(bytes);
    const token = [...bytes].map(b => b.toString(16).padStart(2, "0")).join("");
    await chrome.storage.local.set({ token });
    return token;
}

function sendNative(message) {
    let timer;
    const timeout = new Promise((_, reject) => {
        timer = setTimeout(() => reject(new Error("桌面端响应超时")), NATIVE_TIMEOUT_MS);
    });
    return Promise.race([chrome.runtime.sendNativeMessage(HOST, message), timeout])
        .finally(() => clearTimeout(timer));
}

function setDot(kind, text) {
    document.getElementById("dot").className = kind;
    document.getElementById("status").textContent = text;
}

function setHint(html) {
    const el = document.getElementById("hint");
    if (!html) { el.hidden = true; el.innerHTML = ""; return; }
    el.hidden = false;
    el.innerHTML = html;
}

// 错误路径上清掉列表里的「读取中…」：留着会与红色错误提示自相矛盾。
function clearList() {
    const list = document.getElementById("list");
    list.className = "";
    list.textContent = "";
}

// 动态内容（错误消息等）拼进 innerHTML 前必须转义：popup 是特权页面。
const esc = (s) => String(s).replace(/[&<>"']/g, c => (
    { "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));

function notEnabledHint() {
    setDot("warn", "未启用");
    setHint("在 SnapFlow 设置 → 密码库 中启用「浏览器自动填充」，并确认已填写本扩展 ID。");
}

function disconnectedHint(detail) {
    setDot("err", "未连接");
    setHint("无法连接 SnapFlow 桌面端。请确认：<br>1. SnapFlow 正在运行<br>2. 设置 → 密码库 → 浏览器自动填充已启用"
        + (detail ? "<br><code>" + esc(String(detail).slice(0, 120)) + "</code>" : ""));
}

// 桥协议错误 reason → 用户可读的提示（与桌面端 VaultBridge 的错误码一一对应）。
function describeBridgeError(result) {
    switch (result && result.reason) {
        case "rate-limited":
            return result.retryAfterSeconds
                ? `请求过于频繁，已暂停 ${result.retryAfterSeconds} 秒，稍后再试。`
                : "请求过于频繁，稍后再试。";
        case "vault-blocked":
            return "密码库文件无法打开（版本过新或已损坏）。请在 SnapFlow 密码库窗口中按指引处理。"
                + (result.detail ? `<br><code>${esc(result.detail)}</code>` : "");
        case "integration-disabled":
            notEnabledHint();
            return null;
        default:
            return "桌面端返回异常：<code>" + esc(JSON.stringify(result).slice(0, 140)) + "</code>";
    }
}

async function pairIfNeeded(token) {
    // 未关联时触发桌面端确认弹窗；用户批准后 get-logins 即可用。
    const browser = navigator.userAgent.includes("Edg/") ? "Edge" : "Chrome";
    return sendNative({ type: "hello", token, name: `${browser} · SnapFlow 扩展` });
}

async function fetchLogins(url) {
    const token = await getToken();

    let status;
    try {
        status = await sendNative({ type: "status" });
    } catch (err) {
        clearList();
        disconnectedHint(err.message || err);
        return null;
    }
    if (status.integration === false) {
        clearList();
        notEnabledHint();
        return null;
    }

    let result;
    try {
        result = await sendNative({ type: "get-logins", token, url });
    } catch (err) {
        clearList();
        disconnectedHint(err.message || err);
        return null;
    }
    if (result.type === "error") {
        clearList();
        if (result.reason === "not-paired") {
            // 首次连接：走关联确认（桌面端会弹窗）。
            let paired;
            try {
                paired = await pairIfNeeded(token);
            } catch (err) {
                disconnectedHint(err.message || err);
                return null;
            }
            if (paired.type === "error") {
                setDot("warn", "未关联");
                setHint(paired.reason === "pair-busy"
                    ? "已有一个关联确认在等待，先在桌面端完成那个确认再重试。"
                    : paired.reason === "vault-locked"
                        ? "密码库已锁定，无法关联。先在 SnapFlow 中解锁（Alt+P），再重开本弹窗。"
                        : "关联被拒绝或超时。重试：重新打开本弹窗。");
                return null;
            }
            if (paired.type !== "hello-result" || !paired.ok) {
                setDot("warn", "未关联");
                setHint(paired.reason === "vault-locked"
                    ? "密码库已锁定，无法关联。先在 SnapFlow 中解锁（Alt+P），再重开本弹窗。"
                    : "关联被拒绝或超时。重试：重新打开本弹窗。");
                return null;
            }
            result = await sendNative({ type: "get-logins", token, url });
        } else {
            const hint = describeBridgeError(result);
            if (hint === null) {
                clearList();
                return null; // notEnabledHint 已展示。
            }
            setDot("err", "错误");
            setHint(hint);
            return null;
        }
    }

    if (result.type !== "logins") {
        clearList();
        setDot("err", "错误");
        setHint(describeBridgeError(result) || "桌面端返回异常。");
        return null;
    }

    if (result.locked) {
        setDot("warn", "已锁定");
        setHint("密码库已锁定。打开 SnapFlow 密码库窗口（Alt+P）解锁后再试。");
        const list = document.getElementById("list");
        list.className = "empty";
        list.textContent = "解锁后重开本弹窗即可填充";
        return null;
    }

    setDot("ok", "已连接");
    return result.entries;
}

function renderEntries(entries, tabId) {
    const list = document.getElementById("list");
    list.className = "";
    list.innerHTML = "";
    if (!entries || entries.length === 0) {
        list.className = "empty";
        list.textContent = "当前站点没有匹配的条目";
        return;
    }

    for (const entry of entries) {
        const row = document.createElement("div");
        row.className = "entry";
        const text = document.createElement("div");
        text.style.minWidth = "0";
        const title = document.createElement("div");
        title.className = "title";
        title.textContent = entry.title || "(无标题)";
        const sub = document.createElement("div");
        sub.className = "sub";
        sub.textContent = entry.username || entry.url || "";
        text.append(title, sub);
        const button = document.createElement("button");
        button.className = "fill-btn";
        button.textContent = "填充";
        button.addEventListener("click", async () => {
            try {
                const result = await chrome.tabs.sendMessage(tabId, {
                    action: "snapflow-fill",
                    username: entry.username,
                    password: entry.password,
                });
                button.textContent = result?.ok ? "已填 ✓" : "失败";
            } catch {
                // 页面在扩展安装前已打开（无内容脚本）、或浏览器扩展页等不可注入页面。
                button.textContent = "失败";
                setHint("这个页面无法注入填充脚本。刷新页面后重试；仍失败时请确认页面不是浏览器内置页面。");
                return;
            }
            setTimeout(() => window.close(), 600);
        });
        row.append(text, button);
        list.appendChild(row);
    }
}

async function init() {
    try {
        const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
        if (!tab?.url || !/^https?:/i.test(tab.url)) {
            setDot("warn", "不可用");
            document.getElementById("list").textContent = "当前页面不支持填充（仅 http/https）";
            return;
        }

        const entries = await fetchLogins(tab.url);
        if (entries !== null) {
            renderEntries(entries, tab.id);
        }
    } catch (err) {
        clearList();
        setDot("err", "错误");
        setHint("弹窗初始化失败：<code>" + esc(String(err && err.message || err).slice(0, 120)) + "</code>");
    }
}

document.getElementById("genBtn").addEventListener("click", async () => {
    const kind = document.getElementById("genKind").value;
    // 词数与桌面端推荐口径一致（6 词 ≈ 77 bit）。
    const request = kind === "phrase" ? { type: "generate", words: 6 } : { type: "generate", length: 16 };
    try {
        const result = await sendNative(request);
        const output = document.getElementById("genOut");
        if (result.password) {
            output.value = result.password;
        } else {
            output.value = "";
            setHint(result.reason === "generate-failed" ? "生成失败，请重试。" : "桌面端返回异常，请重试。");
        }
    } catch {
        document.getElementById("genOut").value = "";
        setHint("生成失败：桌面端未连接");
    }
});

document.getElementById("genCopy").addEventListener("click", async () => {
    const value = document.getElementById("genOut").value;
    if (value) {
        await navigator.clipboard.writeText(value);
        document.getElementById("genCopy").textContent = "已复制";
        setTimeout(() => { document.getElementById("genCopy").textContent = "复制"; }, 900);
    }
});

init();
