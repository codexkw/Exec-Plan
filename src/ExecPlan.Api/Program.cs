using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using ExecPlan.Api.Auth;
using ExecPlan.Api.Hubs;
using ExecPlan.Api.Middleware;
using ExecPlan.Application;
using ExecPlan.Application.Abstractions;
using ExecPlan.Domain.Enums;
using ExecPlan.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Localization;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddApplication(builder.Configuration);

// Replace the Infrastructure no-op IRealtimeNotifier with the SignalR-backed one (Api layer owns the
// hub coupling; Application stays SignalR-free). Must run AFTER AddInfrastructure/AddApplication so it
// wins the last-registration-wins resolution.
builder.Services.RemoveAll<IRealtimeNotifier>();
builder.Services.AddScoped<IRealtimeNotifier, SignalRRealtimeNotifier>();

builder.Services.AddControllers();
builder.Services.AddControllersWithViews();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, CurrentUser>();
builder.Services.AddSignalR();

// Arabic-first localization (CLAUDE.md convention 7): supported ar/en, default ar (RTL), resolved
// cookie → query → accept-language.
builder.Services.AddLocalization();
var supportedCultures = new[] { new CultureInfo("ar"), new CultureInfo("en") };
builder.Services.Configure<RequestLocalizationOptions>(options =>
{
    options.DefaultRequestCulture = new RequestCulture("ar");
    options.SupportedCultures = supportedCultures;
    options.SupportedUICultures = supportedCultures;
    options.RequestCultureProviders =
    [
        new CookieRequestCultureProvider(),
        new QueryStringRequestCultureProvider(),
        new AcceptLanguageHeaderRequestCultureProvider(),
    ];
});

var jwtSigningKey = builder.Configuration["Jwt:SigningKey"]
    ?? throw new InvalidOperationException("Jwt:SigningKey is not configured.");
var jwtIssuer = builder.Configuration["Jwt:Issuer"]
    ?? throw new InvalidOperationException("Jwt:Issuer is not configured.");
var jwtAudience = builder.Configuration["Jwt:Audience"]
    ?? throw new InvalidOperationException("Jwt:Audience is not configured.");

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        // JwtTokenFactory (Infrastructure) writes "sub"/"name" as their short JWT names and the role
        // claim as the long-form ClaimTypes.Role URI. MapInboundClaims=false stops the handler from
        // remapping "sub"->ClaimTypes.NameIdentifier/"name"->ClaimTypes.Name, so CurrentUser (which
        // reads JwtRegisteredClaimNames.Sub directly) and RoleClaimType below see exactly what was
        // issued.
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtIssuer,
            ValidateAudience = true,
            ValidAudience = jwtAudience,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSigningKey)),
            RoleClaimType = ClaimTypes.Role,
            NameClaimType = JwtRegisteredClaimNames.Name,
        };

        // SignalR's WebSocket/SSE handshake cannot send an Authorization header, so the hub client
        // carries the JWT on the access_token query string instead. Lift it onto ctx.Token for any
        // request under /hubs so the bearer handler validates it normally.
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = ctx =>
            {
                var accessToken = ctx.Request.Query["access_token"];
                var path = ctx.HttpContext.Request.Path;
                if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/hubs"))
                {
                    ctx.Token = accessToken;
                }

                return Task.CompletedTask;
            },
        };
    })
    .AddCookie(AuthPolicies.AdminCookieScheme, options =>
    {
        // Reserved for the future MVC admin area (Task 19+); only the scheme + login path are wired now.
        options.LoginPath = "/admin/login";
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(AuthPolicies.Admin, p => p.RequireRole(UserRole.SystemAdmin.ToString()));
    options.AddPolicy(AuthPolicies.Manager, p => p.RequireRole(UserRole.PlanManager.ToString()));
    options.AddPolicy(AuthPolicies.Leader, p => p.RequireRole(UserRole.TeamLeader.ToString()));
    options.AddPolicy(AuthPolicies.Member, p => p.RequireRole(UserRole.TeamMember.ToString()));
    options.AddPolicy(AuthPolicies.ManagerOrAdmin, p => p.RequireRole(
        UserRole.PlanManager.ToString(), UserRole.SystemAdmin.ToString()));
});

var app = builder.Build();

// Consistent AppException → HTTP mapping for the whole pipeline. Registered before auth so it also
// wraps the authentication/authorization middleware and any controller-level throws.
app.UseMiddleware<AppExceptionMiddleware>();

app.UseAuthentication();
app.UseAuthorization();

// Resolve the request culture (cookie → query → accept-language) before the endpoints run.
app.UseRequestLocalization();

app.MapControllers();
app.MapHub<DashboardHub>("/hubs/dashboard");

app.Run();

public partial class Program { }
