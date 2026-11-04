using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using MVCapplication.Data;
using MVCapplication.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews();
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));
builder.Services.AddSingleton<ITableDetector, HeuristicTableDetector>();
builder.Services.AddScoped<IPdfExtractor, PdfExtractor>();
builder.Services.AddScoped<IExcelExportService, ExcelExportService>();

// Keep multipart form uploads within the app's 10 MB limit.
builder.Services.Configure<FormOptions>(options =>
    options.MultipartBodyLengthLimit = 10 * 1024 * 1024);

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await database.Database.EnsureCreatedAsync();
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Pdf}/{action=Upload}/{id?}");

app.Run();
