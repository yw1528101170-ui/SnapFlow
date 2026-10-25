// SnapFlow 密码库 - 内容脚本：只负责接收 popup 的填充指令并写进登录表单。
// 不与桌面端通信；凭据只在填充瞬间存在，不做任何存储。

(() => {
    "use strict";

    // 把值写进输入框并派发 input/change：兼容 React/Vue 这类受控组件。
    function setNativeValue(el, value) {
        const proto = el instanceof HTMLInputElement ? HTMLInputElement.prototype
            : el instanceof HTMLTextAreaElement ? HTMLTextAreaElement.prototype
                : null;
        if (!proto) return;
        const setter = Object.getOwnPropertyDescriptor(proto, "value")?.setter;
        if (setter) setter.call(el, value); else el.value = value;
        el.dispatchEvent(new Event("input", { bubbles: true }));
        el.dispatchEvent(new Event("change", { bubbles: true }));
    }

    // 找可见的密码框；有多个时取第一个（登录表单通常只有一个密码框）。
    function findPasswordInputs() {
        return [...document.querySelectorAll("input[type=password]")]
            .filter(el => el.getClientRects().length > 0);
    }

    // 在密码框附近找用户名输入框：同一表单内（或全页）DOM 顺序上最近的前序文本框。
    function findUsernameInput(passwordInput) {
        const scope = passwordInput.closest("form") || document;
        const candidates = [...scope.querySelectorAll("input")]
            .filter(el => ["text", "email", "", "tel"].includes((el.getAttribute("type") || "").toLowerCase()))
            .filter(el => el.getClientRects().length > 0);
        let best = null;
        for (const el of candidates) {
            // 前序（文档顺序在密码框之前）且更靠近密码框的优先。
            if (el === passwordInput) break;
            best = el;
        }
        return best;
    }

    function fill(username, password) {
        const passwords = findPasswordInputs();
        if (passwords.length === 0) {
            return { ok: false, error: "页面上没有找到可见的密码输入框" };
        }

        const usernameInput = findUsernameInput(passwords[0]);
        let filledUsername = false;
        if (username && usernameInput) {
            setNativeValue(usernameInput, username);
            usernameInput.focus();
            filledUsername = true;
        }
        setNativeValue(passwords[0], password);
        return { ok: true, filledUsername };
    }

    chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
        if (message?.action !== "snapflow-fill") return;
        sendResponse(fill(message.username, message.password));
    });
})();
