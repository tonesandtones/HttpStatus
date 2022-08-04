using System.Net;

var builder = WebApplication.CreateBuilder(args);

//Linux and MacOS can't set environment variables with a '.' in them, so we can't override
//"Logging:LogLevel:Microsoft.AspNetCore.HttpLogging.HttpLoggingMiddleware" with an environment variable.
//But we can override "Logging:LogLevel:HttpLoggingMiddlewareOverride" with
//Logging__LogLevel__HttpLoggingMiddlewareOverride
var httpLoggingLevelOverride = builder.Configuration["Logging:LogLevel:HttpLoggingMiddlewareOverride"];
if (httpLoggingLevelOverride != null)
{
    builder.Configuration.AddInMemoryCollection(
        new []{new KeyValuePair<string, string>(
            "Logging:LogLevel:Microsoft.AspNetCore.HttpLogging.HttpLoggingMiddleware", 
            httpLoggingLevelOverride)});
}

var app = builder.Build();

app.UseRouting();
app.MapGet("/", () => "Hello World!");

app.UseHttpLogging();
app.UseEndpoints(_ => { });
app.Use(async (context, next) =>
{
    if (!string.Equals(context.Request.Method, HttpMethods.Get, StringComparison.InvariantCultureIgnoreCase))
    {
        await next(context);
        return;
    }

    if (int.TryParse(context?.Request.Path.Value?.Trim('/'), out var candidate)
        && candidate is >= 100 and <= 999)
    {
        context.Response.StatusCode = candidate;
        await context.Response.WriteAsync($"{candidate}");
        return;
    }

    await next(context!);
});

app.Use(async (HttpContext context, RequestDelegate next) =>
{
    context.Response.StatusCode = (int)HttpStatusCode.NotFound;
    await context.Response.WriteAsync("Not found");
});

app.Run();

public partial class Program
{
}