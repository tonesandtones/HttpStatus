using System.Net;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.UseRouting();
app.MapGet("/", () => "Hello World!");

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