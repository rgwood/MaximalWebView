using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using System.Reflection.Metadata;
using MaximalWebView;
using System.Diagnostics;

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
    }
}
