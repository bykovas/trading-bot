using TradingBot.Web.Data;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews();
builder.Services.AddSingleton<TradingBotReadStore>();

var app = builder.Build();

var pathBase = builder.Configuration["PathBase"]
    ?? Environment.GetEnvironmentVariable("ASPNETCORE_PATHBASE")
    ?? "/web";
if (!string.IsNullOrWhiteSpace(pathBase) && pathBase != "/")
{
    app.UsePathBase(pathBase);
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error");
}

app.UseStaticFiles();
app.UseRouting();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Dashboard}/{action=Index}/{id?}");

app.Run();
