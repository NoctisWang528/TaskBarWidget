// 独立来源在没有 SMTC 时也可参与选择；策略不持有资源，读取器仍由 Provider 管理。

namespace AFMediaBar.Classes.Abstractions;

/// <summary>
/// 提供独立来源身份及双通道仲裁规则的可选能力。注册为 IMediaSourceProvider 即可由目录自动发现。
/// Optional standalone-source capability, discovered through the existing IMediaSourceProvider registration.
/// </summary>
public interface IIndependentMediaSourceProvider : IMediaSourceProvider
{
    /// <summary>稳定且无副作用的来源策略；同一 Provider 生命周期内不得更换。</summary>
    IMediaSourcePolicy SourcePolicy { get; }
}
