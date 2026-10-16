namespace TaskbarLyrics.App;

internal static class UpdateChecker
{
    internal const string RepositoryUrl = "https://github.com/NoctisWang528/TaskBarWidget";
    internal static string GetCurrentVersion() => typeof(UpdateChecker).Assembly
        .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "dev";
}
