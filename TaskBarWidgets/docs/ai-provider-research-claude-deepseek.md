# DeepSeek 与 Claude 用量接入研究

研究日期：2026-11-15。本文记录官方接口能力与接入边界，包含尚未实现的后续方案。用户随后确认同时需要订阅与 API；当前实际实现范围见 [AI 用量](ai-usage.md)。未安装额外客户端或变更其全局设置。

## 已确认的数据范围

| 来源 | 可获得的数据 | 接入条件 | 不能据此获得的数据 |
| --- | --- | --- | --- |
| DeepSeek API | 按币种的当前余额 | 用户主动提供 DeepSeek API key | 网页聊天历史、全账户历史 Token、剩余百分比 |
| Claude Console API | 组织历史 Token 与费用 | 组织和可访问分析接口的管理凭证 | 个人网页订阅剩余额度、可花费余额 |
| Claude Enterprise 分析 API | 获授权组织的历史 Token 与费用 | 启用分析服务和相应权限的 key | 普通个人订阅的全量数据 |
| Claude Code statusline | 官方返回的订阅额度窗口快照 | 安装并登录官方客户端，用户选择配置转发 | 可直接累计的每日 Token 历史 |
| Claude Code 本地遥测 | 启用采集后的本地请求用量事件 | 用户明确配置官方遥测导出 | 启用前历史、未采集设备或网页全部活动 |

## DeepSeek：余额与历史必须分开

官方 `GET https://api.deepseek.com/user/balance` 通过 Bearer API key 认证，返回可用状态，以及各币种的总余额、赠送余额和充值余额；金额是十进制字符串。应按币种保留精度，单独展示金额，不构造百分比。[官方余额接口](https://api-docs.deepseek.com/api/get-user-balance/)

本次查阅公开接口目录未找到全账户历史用量接口。这是当前研究结论，不等同于声明未来永远没有该接口。余额变化包含充值、赠送及到期等因素，不能换算 Token 或补成热力图。未接入实际历史来源时，`days` 为 `null`；不能把不支持显示成零用量。

## Claude：订阅和 API 是不同数据域

新集成应优先考虑官方 Usage and Cost Analytics API：`GET /v1/organizations/analytics/usage` 与 `/cost`。前者可按 UTC 日桶查询 Token，后者报告费用；需处理分页和适用的 beta/version 请求头。Console 组织使用管理 API 凭证；Enterprise 使用其分析权限凭证。普通 workspace key 不应假设具有组织分析权限。[官方分析接口](https://platform.claude.com/docs/en/manage-claude/usage-cost-analytics-api)、[管理 API 的可用范围](https://platform.claude.com/docs/en/manage-claude/admin-api)

费用不是余额，也不是订阅已用百分比。本次未确认可读取个人订阅余额的正式公共接口，不能从历史费用推断剩余额度。组织级数据也不能默认为当前用户个人使用量；界面需标明数据范围，必要时明确筛选。

## Claude Code statusline：适合额度，不适合 Token 账本

官方 statusline 可提供 `rate_limits.five_hour` 和 `seven_day` 中的 `used_percentage`、`resets_at`。适用订阅、首次响应、各窗口独立缺失及过期等条件都要处理。它是客户端推送的快照，不是本产品可独立调用的账户历史 API。[官方 statusline 字段](https://code.claude.com/docs/en/statusline#available-data)

尤其不能因名称含 `total` 就累计：`context_window.total_input_tokens`、`total_output_tokens` 表示最新响应对应的上下文；`current_usage` 不是单调计数器，压缩后还可能为空。轮询、恢复和界面更新会重复出现同一数据，也可能跳过中间响应；累加、差值或取最大值都不能构成可靠的每日 Token 账本。`prompt_id` 也不能替代逐请求标识。[上下文字段定义](https://code.claude.com/docs/en/statusline#context-window-fields)

如以后接入转发器，应只复制额度白名单字段，并让原 statusline 继续收到相同 stdin，保留原 stdout 和相关设置。不得把采集诊断写到原显示输出。已有 command 在 shell 中执行，不能用字符串拼接重写其引号；需备份配置、展示具体变更、保留项目配置优先级，并提供恢复原配置的方式。当前不自动修改任何用户配置。

## 真实本地热力图的候选方案

推荐另行评估官方 OpenTelemetry 的 `claude_code.api_request` 事件，读取事件时间及 input、output、cache-read、cache-creation 四类用量。官方提供请求相关 ID；无需打开聊天记录。`Stop` hook 本身没有可靠的逐请求 Token 账目，且包含回复文字，不能用停止次数推算 Token。[官方遥测事件](https://code.claude.com/docs/en/monitoring-usage#api-request-event)、[Stop hook 输入](https://code.claude.com/docs/en/hooks#stop-input)

以下是产品实现建议，尚未验证或实现：

1. 由用户选择启用本地采集，保留既有遥测目的地；不擅自覆盖组织管理配置或开启正文日志。只接收本机、当前用户授权的聚合记录。
2. 接收端按白名单提取时间、来源范围、去重 ID 和非负 64 位 Token 数，立即丢弃其余属性；不保存提示词、回复、路径、邮箱或原始遥测包。
3. 优先以来源范围和服务 `request_id` 幂等写入；经版本及传输实测确认后，才考虑 `client_request_id` 回退。两者都缺失时不靠“Token 数相同”猜测去重，应标记缺口。拒绝将 `api_error`、拒绝事件与同一请求成功记录重复相加。
4. 保存最小请求账目和去重键，以便持久去重、重算每日聚合；明确缓存读写是否计入总量，不把缓存细分类再次加到已包含它们的总数上。子 agent 和辅助请求的范围需一致，避免重复汇总。
5. 按事件时间分日，声明 UTC 或本地日期规则；不要按延迟接收时间误记到次日。缺失、离线和启用前日期保持未知，只有已确认覆盖的日期才可记零。
6. 界面标为“本机 Claude Code 活动”，不能宣称全账户历史；不能据此推订阅剩余额度。遥测开销、丢失、重传及跨进程去重仍需实际客户端验证。

此方案比 statusline 复杂，当前没有必要先搭建通用导入服务。待用户确认产品类型后，先实现所需的最小可验证来源。

## 后端合约与凭证建议

提供方各自持有缓存、取消令牌和数据版本。金额型 `balances` 与百分比型 `quotas` 分开；历史、余额和额度分别报告能力与可用状态。切换提供方时，由聚合层生成单调展示版本，或显式重置原生卡片的版本门槛，避免不同提供方的独立版本互相阻挡。

需要 API key 的模式沿用天气设置的只写交互：空白保持、明确清除、验证成功后保存、响应仅返回存在状态；使用独立的 Windows 当前用户加密存储，不进入普通设置导出、快照或日志。后端固定官方 HTTPS 主机，禁止携带凭证跟随重定向；凭证变更取消旧请求并清空旧数据，防止账户混合。不得收集 Claude 网页 session token 或读取官方客户端认证文件。

## 尚待确认

- 用户各服务主要使用网页订阅、官方编码客户端还是 API。
- Claude 是否有组织管理／分析权限，DeepSeek 是否愿意主动配置 API key。
- 是否接受仅从启用采集起记录的“本机活动”，以及所需的历史范围和日期规则。
- Gemini 的数据源由独立研究确认；本文不对其接口作结论。
