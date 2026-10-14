namespace TaskBarWidgets.Media.Integration;

public enum AudioRole { Console, Multimedia, Communications }

public sealed record AudioRoleResult(AudioRole Role, bool IsSelected, string? Error);

public sealed record AudioSwitchResult(IReadOnlyList<AudioRoleResult> Roles)
{
    public bool Succeeded => Roles.Count > 0 && Roles.All(role => role.IsSelected);
    public bool PartiallySucceeded => !Succeeded && Roles.Any(role => role.IsSelected);
    public string Message => Succeeded ? "默认设备已切换" : PartiallySucceeded
        ? "部分默认角色已切换，请检查系统音频设置"
        : "默认设备切换失败";
}

public static class AudioRolePolicy
{
    public static IReadOnlyList<AudioRole> GetRoles(bool includeCommunications) => includeCommunications
        ? [AudioRole.Console, AudioRole.Multimedia, AudioRole.Communications]
        : [AudioRole.Console, AudioRole.Multimedia];

    public static AudioSwitchResult ReadBack(string requestedId, bool communications,
        Func<AudioRole, string?> getDefault, IReadOnlyDictionary<AudioRole, string?> errors) => new(
        GetRoles(communications).Select(role =>
        {
            var matches = string.Equals(getDefault(role), requestedId, StringComparison.OrdinalIgnoreCase);
            return new AudioRoleResult(role, matches,
                matches ? null : errors.GetValueOrDefault(role) ?? "系统默认角色读回不匹配");
        }).ToArray());
}
