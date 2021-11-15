using System.Reflection.Metadata;
using MaximalWebView;

[assembly: MetadataUpdateHandler(typeof(HotReloadManager))]

namespace MaximalWebView;

internal static class HotReloadManager
{
    public static void ClearCache(Type[]? types)
    {
        Console.WriteLine($"ClearCache called for {types?.Length ?? 0} type(s): {string.Join(';', types?.Select(t => t.Name) ?? Array.Empty<string>())}");
    }

    public static void UpdateApplication(Type[]? types)
    {
        Console.WriteLine($"UpdateApplication called for {types?.Length ?? 0} type(s): {string.Join(';', types?.Select(t => t.Name) ?? Array.Empty<string>())}");
        Program._uiThreadSyncCtx.Post(_ =>
        {
            Program._controller.CoreWebView2.Reload();
        }, null);
    }

    /// <summary>
    /// Best-effort guess at whether hot reload is enabled. Useful for determining whether to enable our own custom hot reload features.
    /// </summary>
    public static bool IsHotReloadEnabled()
    {
        // Using the DOTNET_MODIFIABLE_ASSEMBLIES env var introduced here: https://github.com/dotnet/runtime/issues/47274
        // There might be better ways to do this, but custom hot reload scenarios aren't really documented yet.
        // Keep an eye on this space.
        string? modifiableAssembliesVar = Environment.GetEnvironmentVariable("DOTNET_MODIFIABLE_ASSEMBLIES");
        return modifiableAssembliesVar != null && modifiableAssembliesVar.Equals("debug", StringComparison.OrdinalIgnoreCase);
    }
}
