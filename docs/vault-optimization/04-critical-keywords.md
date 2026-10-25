# 密码体检 · 关键账号关键词表

> 用途：供 UX-02 密码体检判定「关键账号未开启两步验证」这一维度使用。
> 配套需求：`01-prd.md` 的 UX-02（体检）、第 8 章 Q7（关键账号识别方式）。
> 本文档只给**子串关键词**，不给精确域名清单——精确域名清单会随时间失效并制造误报，且 LLM 生成的域名列表必然包含不存在或已停用的条目。

---

## 1. 用途与边界（先说清楚，避免被误用）

| 项 | 说明 |
|---|---|
| 这是**提示**，不是**阻断** | 命中只影响体检里「关键账号建议启用两步验证」这一条建议的出现与否 |
| 不影响什么 | 搜索、排序、列表筛选、浏览器扩展匹配与填充、CSV 导入导出、任何安全阻断逻辑，都与本表**无关** |
| 用户能否否决 | 能。编辑器里有「标记为关键账号」开关（UI-17），用户手动标记的结果**优先于**关键词判定 |
| 误报容忍度 | 高。多提示一条只是多看一眼 |
| 漏报容忍度 | 低。漏掉意味着用户永远不知道这个账号建议开两步验证。所以关键词**宁可宽一点**，不要追求零误报 |
| 判定失败时的默认 | 判定不出来就当作「非关键账号」，不提示。不要因为关键词表加载失败而把所有条目都标成关键 |

---

## 2. 匹配规则（写死，不要扩展）

| 编号 | 规则 |
|---|---|
| M1 | 取条目的 host，执行 `host.ToLowerInvariant().Contains(keyword)` |
| M2 | 只匹配 host，**不匹配路径、不匹配查询串、不匹配端口**。`example.com/pay/x` 不因 `/pay/` 命中 |
| M3 | 关键词为**小写 ASCII**，长度 **≥ 3**。`qq`、`x`、`t` 这类 2 字符以下的词不进表（`qq.com`、`x.com`、`t.me` 属已知漏报，见第 4 章） |
| M4 | 命中任意一条关键词即判定为关键账号，不做加权、不做多词组合、不做优先级 |
| M5 | 条目无 host（纯笔记、只有用户名没网址）→ 不参与本维度判定 |
| M6 | 关键词表改动后，下一次进入体检页重新计算即可，不需要库版本号变化 |

**不需要的复杂度**：不需要正则、不需要 TLD 解析、不需要排除表（前提是主表不含泛词，见第 5 章）。

---

## 3. 关键词表（73 条，7 组）

> **样例列说明**：host 样例为**构造示意**，未逐一核验其存在性，仅用于说明「关键词出现在 host 的哪个位置」，**不参与匹配**。真正参与匹配的只有关键词列。

### 3.1 银行（10 条）

| 关键词 | 中文说明 | 典型 host 样例（仅作说明，不参与匹配） |
|---|---|---|
| icbc | 中国工商银行 | `www.icbc.com.cn` |
| abchina | 中国农业银行 | `www.abchina.com` |
| ccb | 中国建设银行 | `www.ccb.com` |
| boc | 中国银行 | `www.boc.cn` |
| cmbchina | 招商银行 | `www.cmbchina.com` |
| spdb | 浦发银行 | `www.spdb.com.cn` |
| cib | 兴业银行 | `www.cib.com.cn` |
| psbc | 中国邮政储蓄银行 | `www.psbc.com` |
| bankcomm | 交通银行 | `www.bankcomm.com` |
| citic | 中信银行 / 中信集团 | `www.citicbank.com` |

### 3.2 支付（8 条）

| 关键词 | 中文说明 | 典型 host 样例（仅作说明，不参与匹配） |
|---|---|---|
| alipay | 支付宝 | `www.alipay.com` |
| tenpay | 财付通 / 微信支付商户 | `pay.tenpay.com` |
| unionpay | 银联 | `www.unionpay.com` |
| paypal | PayPal | `www.paypal.com` |
| stripe | Stripe（海外收付） | `dashboard.stripe.com` |
| payoneer | Payoneer | `www.payoneer.com` |
| klarna | Klarna（先买后付） | `app.klarna.com` |
| adyen | Adyen（商户收单） | `ca-live.adyen.com` |

### 3.3 邮箱（11 条）

| 关键词 | 中文说明 | 典型 host 样例（仅作说明，不参与匹配） |
|---|---|---|
| gmail | Gmail | `mail.gmail.com` |
| outlook | Outlook / Hotmail 登录 | `outlook.office.com` |
| hotmail | Hotmail | `www.hotmail.com` |
| yahoo | Yahoo 邮箱 | `mail.yahoo.com` |
| icloud | iCloud 邮箱 | `www.icloud.com` |
| protonmail | Proton Mail | `mail.protonmail.com` |
| yandex | Yandex 邮箱 | `mail.yandex.com` |
| zoho | Zoho 邮箱 | `mail.zoho.com` |
| exmail | 腾讯企业邮 | `exmail.qq.com` |
| 163 | 网易 163 邮箱（数字词，误伤率见 3.8 注） | `mail.163.com` |
| 126 | 网易 126 邮箱（数字词） | `mail.126.com` |

### 3.4 云服务（12 条）

| 关键词 | 中文说明 | 典型 host 样例（仅作说明，不参与匹配） |
|---|---|---|
| aws | Amazon Web Services | `console.aws.amazon.com` |
| azure | Microsoft Azure | `portal.azure.com` |
| aliyun | 阿里云 | `www.aliyun.com` |
| tencentcloud | 腾讯云 | `console.tencentcloud.com` |
| huaweicloud | 华为云 | `www.huaweicloud.com` |
| qcloud | 腾讯云（旧域名形式） | `console.qcloud.com` |
| digitalocean | DigitalOcean | `cloud.digitalocean.com` |
| linode | Linode / Akamai | `cloud.linode.com` |
| vultr | Vultr | `my.vultr.com` |
| heroku | Heroku | `dashboard.heroku.com` |
| vercel | Vercel | `vercel.com` |
| netlify | Netlify | `app.netlify.com` |

### 3.5 社交与内容社区（12 条）

| 关键词 | 中文说明 | 典型 host 样例（仅作说明，不参与匹配） |
|---|---|---|
| wechat | 微信 | `wechat.com` |
| weixin | 微信生态域名（含企业微信 `work.weixin.*`） | `work.weixin.qq.com` |
| weibo | 微博 | `weibo.com` |
| douyin | 抖音 | `www.douyin.com` |
| kuaishou | 快手 | `www.kuaishou.com` |
| xiaohongshu | 小红书 | `www.xiaohongshu.com` |
| zhihu | 知乎 | `www.zhihu.com` |
| bilibili | 哔哩哔哩 | `www.bilibili.com` |
| telegram | Telegram | `web.telegram.org` |
| discord | Discord | `discord.com` |
| slack | Slack | `app.slack.com` |
| whatsapp | WhatsApp | `web.whatsapp.com` |

### 3.6 政务与社保（8 条）

| 关键词 | 中文说明 | 典型 host 样例（仅作说明，不参与匹配） |
|---|---|---|
| gov | 各级政府门户（`.gov.cn` / `.gov`） | `www.gov.cn` |
| mohrss | 人力资源和社会保障部 | `www.mohrss.gov.cn` |
| nhsa | 国家医保局 | `www.nhsa.gov.cn` |
| chinatax | 国家税务总局 | `www.chinatax.gov.cn` |
| gjj | 住房公积金（拼音缩写） | `gjj.example.gov.cn` |
| zwfw | 政务服务网 | `zwfw.example.gov.cn` |
| 12333 | 人社服务热线站点（数字词） | `12333.example.gov.cn` |
| 12329 | 公积金服务热线站点（数字词） | `12329.example.gov.cn` |

### 3.7 企业办公与代码托管（12 条）

| 关键词 | 中文说明 | 典型 host 样例（仅作说明，不参与匹配） |
|---|---|---|
| feishu | 飞书 | `www.feishu.cn` |
| larksuite | 飞书国际版 | `www.larksuite.com` |
| dingtalk | 钉钉 | `login.dingtalk.com` |
| wecom | 企业微信（服务端域名形式） | `open.wecom.qq.com` |
| microsoftonline | Microsoft 365 / Teams 登录 | `login.microsoftonline.com` |
| gitlab | GitLab | `gitlab.com` |
| github | GitHub | `github.com` |
| atlassian | Atlassian（Jira/Confluence 统一登录） | `id.atlassian.com` |
| jira | Jira（含自建部署） | `jira.example.com` |
| confluence | Confluence | `confluence.example.com` |
| salesforce | Salesforce | `login.salesforce.com` |
| workday | Workday | `www.workday.com` |

### 3.8 三字母与数字词的误伤说明

以下关键词长度只有 3 且为常见字母组合或纯数字，`Contains` 匹配的误伤率高于品牌全称词：`boc`、`cib`、`aws`、`gjj`、`zwfw`、`163`、`126`、`12333`、`12329`。

- 典型误伤：`laws`、`awsome` 之类的 host 会命中 `aws`；`bbc` 之外的随机串可能命中 `boc`/`cib`。
- **不建议**为此修改 M1~M6 的匹配规则（加边界判断会让规则变复杂且难解释）。
- 建议：这些词保留在表内（漏报代价高于误报代价），若架构师希望更保守，可以只把 `aws` 降级为 `amazonaws`（`console.aws.amazon.com` 同时含 `amazonaws`? 实际 host 是 `console.aws.amazon.com`，不含 `amazonaws`；`s3.amazonaws.com` 才含）——**因此 `aws` 不要改**，改了会漏掉控制台。

---

## 4. 已知漏报（无法用 host 子串关键词覆盖，必须靠用户手动标记）

这些不是表的缺陷，是 host 匹配方法的固有限制。产品上接受，靠「标记为关键账号」开关兜底。

| # | 场景 | 为什么漏 | 缓解 |
|---|---|---|---|
| L1 | `x.com`（原 Twitter） | 关键词 `x` 只有 1 字符，违反 M3 | 用户手动标记 |
| L2 | `t.me`（Telegram 短链） | 关键词 `t` 过短；`telegram` 匹配不到 `t.me` | 用户手动标记 |
| L3 | `qq.com`（QQ 邮箱/账号） | 关键词 `qq` 只有 2 字符，违反 M3 | 用户手动标记 |
| L4 | Apple ID、Google 账号 | `appleid.apple.com` 需匹配 `apple`（会命中全部 Apple 服务）；`accounts.google.com` 的 `google` 会命中搜索/云/邮件全部，无法区分账号类型 | 用户手动标记；已有 `gmail`/`icloud` 覆盖邮箱场景 |
| L5 | 平台内支付（Apple Pay / Google Pay / 华为 Pay） | 无独立 host，登录发生在系统或平台账号内 | 不处理（本身不产生独立密码） |
| L6 | 企业自有域名邮箱 `mail.company.com` | host 不含任何已知品牌词 | 用户手动标记；可在 UX 上提示「企业邮箱建议手动标记」 |
| L7 | 小众地方银行、农商行 | 品牌词未收录 | 用户手动标记；表按季度补充 |

**结论**：手动标记不是可选项，是这张表的必要组成部分。体检页的空状态与汇总条都要能容纳「已手动标记但未开动态验证码」的条目（现有 UX-02 设计已包含，无需改）。

---

## 5. 泛词组与我的结论（请 team-lead 拍板）

`bank`、`pay`、`mail`、`cloud`、`tax`、`wallet` 这类词命中率高，但**没有服务辨识力**——它们描述的是业务类型，不是具体账号归属。

| 泛词 | 误伤例子 | 判断 |
|---|---|---|
| `bank` | `bankruptcy`、`riverbank`、`sandbank` | 不建议进主表 |
| `pay` | `payload`、`payroll`、`prepay` | 不建议进主表 |
| `mail` | `mailchimp`、`mailgun`、`mailerlite`（都是营销工具，不是邮箱账号） | 不建议进主表 |
| `cloud` | `soundcloud`、`cloudinary`、`cloudfront` | 不建议进主表 |
| `tax` | `syntax`、`taxonomy` | 不建议进主表 |
| `wallet` | 无明显典型误伤，但覆盖价值也低 | 不建议进主表 |

**我的结论：主表不含泛词。若一定要兜住「未被品牌词覆盖的金融机构」，唯一可接受的做法是把 `bank` 换成带前导点的 `.bank`**（`.bank` 是需资质审核的受限 gTLD，精度高得多；但它会顺带命中 `.bank` 之外拼接的情形，属于可接受误差）。其余五个泛词一律不进表。

支持这个结论的四条理由：

1. **误报是 100% 用户都会遇到的，漏报只影响小众机构用户**。泛词会把体检变成一个噪声很大的页面，用户学会忽略它之后，真正的风险提示也一起被忽略——提示疲劳的代价远大于少覆盖几家小银行。
2. **排除表是无底洞**。每加一个泛词，架构师就要维护一份排除表，而排除表会随新站点出现持续失效，维护成本落在最不该复杂的地方（一个提示功能）。
3. **品牌词已经覆盖了绝大多数高价值账号**。银行 10 个品牌词 + 支付 8 个 + 邮箱 11 个，覆盖的是用户真正会担心丢钱的账号。
4. **有手动标记兜底**。用户对小众机构的账号可以自己标，比全局误报干净。

### 5.1 已决（team-lead 拍板，随 PRD v0.5 一并冻结）

| 议题 | 决定 |
|---|---|
| 六个泛词（`bank`/`pay`/`mail`/`cloud`/`tax`/`wallet`） | **全部不进主表**，按上述四条理由执行 |
| `.bank` 特例 | **不采纳** |
| 手动标记 | 作为关键账号判定的一等公民保留，体检面板必须能展示「已手动标记但未启用动态验证码」的条目 |

**`.bank` 被否决的理由（记录，避免以后有人重新提出）**：问题不在精度，而在于它是**唯一一个需要后缀语义的词**。其余 72 条都靠 `Contains`，唯独 `.bank` 必须写成 `EndsWith(".bank")` 才能不被 `x.bankruptcy.com` 误伤——`Contains(".bank")` 会命中 `bankruptcy` 中的 `.bank` 前缀。为覆盖全球几十家、国内基本不使用该 gTLD 的机构，在匹配规则里开一个特例，会长期留下「为什么这个词和别的词不一样」的阅读与维护成本，不值得。

**由此产生的规则收益**：73 条关键词**全部**走同一条 `Contains` 路径，M1~M6 无任何例外分支，实现上就是一个循环。这与 §2 的设计意图一致——规则越少例外，越不会被后人改坏。

---

## 6. 维护口径

| 项 | 约定 |
|---|---|
| 谁来加词 | 产品维护，改动直接改本文档第 3 章，不需要走格式版本（关键词表不参与库格式） |
| 加词标准 | ① 有独立登录入口；② 账号丢失会造成实质性损失（资金 / 身份 / 通信 / 生产资料）；③ 关键词长度 ≥ 3、纯小写 ASCII |
| 不加的词 | 泛词（`bank`/`pay`/`mail`/`cloud`/`tax`/`wallet` 等，见第 5 章）、带点的词（`.bank` 这类需要后缀语义的词，会破坏 M1 的统一 `Contains` 规则）、单个字母、纯数字域名主体（`163`/`126` 例外已标注）、一次性活动站点 |
| 复核节奏 | 每季度过一遍，删掉已停服的服务，补充新增的高价值服务；不做自动更新（体检是本机功能，不应联网拉词表） |
| 词表版本 | 表内条目数写进本文档标题，便于审查时发现意外删减 |

---

## 7. 交付前自检（已人工过一遍）

| 检查项 | 结果 |
|---|---|
| 关键词全部小写 ASCII | 通过 |
| 关键词长度 ≥ 3 | 通过（最短为 `aws`/`boc`/`cib`/`gjj`/`163`/`126`，均为 3） |
| 组内无重复 | 通过 |
| 跨组无重复 | 通过（`wechat` 与 `weixin` 为不同 host 形态，均保留） |
| 每组条数在 6~12 之间 | 通过（8~12） |
| 总数在 60~80 之间 | 通过（73） |
| 主表不含泛词 | 通过（泛词单列第 5 章，未计入 73） |
| 主表不含带点词（需后缀语义） | 通过（73 条全部为裸词，统一走 `Contains`，无例外分支） |
| 未包含真实凭据 / 完整银行 URL 清单 | 通过（样例均为构造示意） |

---

## 附录 A：纯词表（供直接取用，按组分隔）

```
# 银行
icbc
abchina
ccb
boc
cmbchina
spdb
cib
psbc
bankcomm
citic
# 支付
alipay
tenpay
unionpay
paypal
stripe
payoneer
klarna
adyen
# 邮箱
gmail
outlook
hotmail
yahoo
icloud
protonmail
yandex
zoho
exmail
163
126
# 云服务
aws
azure
aliyun
tencentcloud
huaweicloud
qcloud
digitalocean
linode
vultr
heroku
vercel
netlify
# 社交与内容社区
wechat
weixin
weibo
douyin
kuaishou
xiaohongshu
zhihu
bilibili
telegram
discord
slack
whatsapp
# 政务与社保
gov
mohrss
nhsa
chinatax
gjj
zwfw
12333
12329
# 企业办公与代码托管
feishu
larksuite
dingtalk
wecom
microsoftonline
gitlab
github
atlassian
jira
confluence
salesforce
workday
```
