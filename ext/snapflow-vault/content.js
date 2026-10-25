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

    // 密码框所在范围内可见、可用的文本类输入框候选（不含密码框本身）。
    function textCandidates(scope, passwordInput) {
        return [...scope.querySelectorAll("input")]
            .filter(el => ["text", "email", "", "tel"].includes((el.getAttribute("type") || "").toLowerCase()))
            .filter(el => el.getClientRects().length > 0 && !el.disabled && el !== passwordInput);
    }

    // 找用户名输入框，两条规则按优先级：
    // ① 语义匹配：autocomplete 指明 username/email，或 name/id 含 user/email/account/login 等词；
    // ② DOM 顺序上离密码框最近的**前序**文本框（登录表单的用户名几乎总在密码框上方）。
    // 不能笼统取"最后一个"：密码框不在 form 内时范围是全文档，页面底部的搜索框/订阅邮箱会误中。
    function findUsernameInput(passwordInput) {
        const scope = passwordInput.closest("form") || document;
        const candidates = textCandidates(scope, passwordInput);

        const semantic = candidates.find(el => {
            const ac = (el.getAttribute("autocomplete") || "").toLowerCase();
            if (ac === "username" || ac === "email") return true;
            const hint = ((el.name || "") + " " + (el.id || "")).toLowerCase();
            return /user|email|account|login|phone|mobile/.test(hint);
        });
        if (semantic) return semantic;

        let best = null;
        for (const el of candidates) {
            // el 在密码框之前（passwordInput 跟在 el 后面）才算前序；遍历按文档序，
            // 满足条件的最后一个即"离密码框最近的前序"。
            if (el.compareDocumentPosition(passwordInput) & Node.DOCUMENT_POSITION_FOLLOWING) {
                best = el;
            }
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
