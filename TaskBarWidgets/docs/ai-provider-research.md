# Gemini 用量来源与接入条件

Gemini API、Gemini Apps 订阅与 Antigravity 属于不同产品的统计来源，不能相互替代或合并成同一剩余额度。

| 来源 | 官方能力 | 接入条件与限制 |
| --- | --- | --- |
| Gemini API / AI Studio | 按项目、模型查看 API 用量和限制；限制包括 RPM、TPM、RPD 等维度。Cloud Monitoring 提供 `generativelanguage.googleapis.com` 的 quota limit/usage 指标。 | 自动读取需项目 ID、适当 OAuth scope 和项目监控读取权限；普通 Gemini API Key 不能替代监控授权。指标存在采样和可见性延迟，应显示更新时间，避免将速率限制称为账户余额。 |
| Gemini Apps 网页订阅 | 官方界面提供 Settings → Usage limits。 | 本次研究未确认可供第三方读取订阅剩余额度的公开接口；不能用 Gemini API 项目用量推算。可引导用户查看官方界面，自动读取能力需另行验证。 |
| Antigravity | 官方 CLI `/usage`（别名 `/quota`）刷新模型额度并打开交互面板；套餐包含该产品自己的基准额度与可选 AI credits。 | 本次研究未确认该命令支持稳定、机器可读的额度导出，也未确认公开额度读取接口。只有在正式能力与授权方式验证后才接入；不能将 Antigravity 额度标记为 Gemini Apps 订阅余额。 |

## 展示约定

- 服务标识应区分 Gemini API（项目）、Gemini（网页订阅）、Antigravity。
- 额度应保留模型、统计窗口与单位；单位可能是百分比、请求、Token、金额或 credits，未知值保持未知。
- 每日活动只能展示真实返回的对应指标；没有 Token 历史时不绘制 Token 热力图，不从请求次数推算 Token。
- 未确认用户使用的产品和授权方式前，不预设 API Key 表单，也不调用未验证的内部接口。

## 官方资料

- [Gemini API rate limits](https://ai.google.dev/gemini-api/docs/rate-limits)：项目级限制、RPM/TPM/RPD 与日窗口重置规则。
- [Google Cloud metrics: D–H](https://docs.cloud.google.com/monitoring/api/metrics_gcp_d_h)：`generativelanguage.googleapis.com` 用量与额度指标及采样延迟。
- [Cloud Monitoring projects.timeSeries.list](https://docs.cloud.google.com/monitoring/api/ref_v3/rest/v3/projects.timeSeries/list)：正式时间序列读取接口及 OAuth scopes。
- [Cloud Monitoring IAM roles](https://docs.cloud.google.com/iam/docs/roles-permissions/monitoring)：Monitoring Viewer 与 `monitoring.timeSeries.list` 权限。
- [About Gemini models](https://support.google.com/gemini/answer/18648722?hl=en)：Gemini Apps 的 Usage limits 设置入口。
- [Antigravity model quotas](https://antigravity.google/docs/cli/commands/usage/)：官方 `/usage` 交互面板。
- [Antigravity plans](https://antigravity.google/docs/plans)：基准额度与 AI credits。
