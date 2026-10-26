using HitlRelay;
using HitlRelay.Data;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddHitlCore(builder.Configuration);
builder.Services.AddHitlChannels(builder.Configuration);
// Replace this with your own IWorkflowResumer registration when embedding the module in a real host.
builder.Services.AddSampleWorkflow();

var app = builder.Build();

// Assumption: EnsureCreated builds the schema on first start so the sample runs without tooling.
// For production use EF migrations: dotnet ef migrations add Initial --project src/HitlRelay
using (var scope = app.Services.CreateScope())
{
    scope.ServiceProvider.GetRequiredService<HitlDbContext>().Database.EnsureCreated();
}

app.UseDefaultFiles();   // serves wwwroot/index.html: the test console (only useful when Hitl:EnableDemo is true)
app.UseStaticFiles();
app.MapControllers();
app.Run();

public partial class Program { }
