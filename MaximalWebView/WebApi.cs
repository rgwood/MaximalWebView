namespace MaximalWebView;
using Microsoft.AspNetCore.Builder;

public static class WebApi
{
    public static async Task<WebApplication> StartOnThreadpool() =>
        await Task.Run(async () =>
        {
            var app = WebApplication.CreateBuilder().Build();
            app.Urls.Add("http://localhost:5003");
            app.UseDeveloperExceptionPage();
            app.MapGet("/", () => "Hello World!");

            await app.StartAsync();
            return app;
        });
}

// // TODO: use WebApplicationBuilderOptions once we're on .NET 6 RC 1 https://github.com/dotnet/aspnetcore/issues/34837

// var sw = Stopwatch.StartNew();
// Debug.WriteLine($"ASP.NET took {sw.ElapsedMilliseconds}ms to start up");
