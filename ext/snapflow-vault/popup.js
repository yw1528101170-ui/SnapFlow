// SnapFlow 密码库 - popup：状态展示 + 当前站点凭据列表 + 填充 + 生成器。
// 每次打开 popup 都会经原生消息与桌面端通信（sendNativeMessage 每条拉起一次宿主，
// 宿主进程只做 stdio↔管道转发，开销可忽略）。

const HOST = "com.snapflow.vault";

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
    return chrome.runtime.sendNativeMessage(HOST, message);
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

async function pairIfNeeded(token) {
    // 未关联时触发桌面端确认弹窗；用户批准后 get-logins 即可用。
    const extName = chrome.i18n?.getMessage?.("extName") || "Chrome 扩展";
    return sendNative({ type: "hello", token, name: `${navigator.userAgent.includes("Edg/") ? "Edge" : "Chrome"} · ${extName}` });
}

async function fetchLogins(url) {
    const token = await getToken();

    let status;
    try {
        status = await sendNative({ type: "status" });
    } catch (err) {
        setDot("err", "未连接");
        setHint("无法连接 SnapFlow 桌面端。请确认：<br>1. SnapFlow 正在运行<br>2. 设置 → 密码库 → 浏览器自动填充已启用<br><code>" + String(err.message || err).slice(0, 120) + "</code>");
        return null;
    }
    if (status.type === "error" && status.reason === "integration-disabled") {
        setDot("warn", "未启用");
        setHint("在 SnapFlow 设置 → 密码库中启用浏览器自动填充，并填写本扩展 ID。");
        return null;
    }

    let result = await sendNative({ type: "get-logins", token, url });
    if (result.type === "error" && result.reason === "not-paired") {
        // 首次连接：走关联确认（桌面端会弹窗）。
        const paired = await pairIfNeeded(token);
        if (paired.type !== "hello-result" || !paired.ok) {
            setDot("warn", "未关联");
            setHint("关联被拒绝或超时。重试：重新打开本弹窗。");
            return null;
        }
        result = await sendNative({ type: "get-logins", token, url });
    }

    if (result.type !== "logins") {
        setDot("err", "错误");
        setHint("桌面端返回异常：<code>" + JSON.stringify(result).slice(0, 140) + "</code>");
        return null;
    }

    if (result.locked) {
        setDot("warn", "已锁定");
        setHint("密码库已锁定。打开 SnapFlow 密码库窗口（Alt+P）解锁后再试。");
        return [];
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
            const result = await chrome.tabs.sendMessage(tabId, {
                action: "snapflow-fill",
                username: entry.username,
                password: entry.password,
            });
            button.textContent = result?.ok ? "已填 ✓" : "失败";
            setTimeout(() => window.close(), 600);
        });
        row.append(text, button);
        list.appendChild(row);
    }
}

async function init() {
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
}

document.getElementById("genBtn").addEventListener("click", async () => {
    const kind = document.getElementById("genKind").value;
    const request = kind === "phrase" ? { type: "generate", words: 4 } : { type: "generate", length: 16 };
    try {
        const result = await sendNative(request);
        document.getElementById("genOut").value = result.password || ("错误: " + (result.reason || "?"));
    } catch (err) {
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
