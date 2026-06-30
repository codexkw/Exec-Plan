using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using ExecPlan.Api.Auth;
using ExecPlan.Application;
using ExecPlan.Application.Abstractions;
using ExecPlan.Domain.Enums;
using ExecPlan.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddApplication(builder.Configuration);

builder.Services.AddControllers();
builder.Services.AddControllersWithViews();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, CurrentUser>();

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

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();

public partial class Program { }
