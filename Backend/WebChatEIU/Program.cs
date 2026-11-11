
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using FirebaseAdmin;
using FirebaseAdmin.Auth;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.Firestore;
using WebChatEIU.Hubs;
using WebChatEIU.Services;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;

namespace WebChatEIU
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            // ===========================
            // FIREBASE ADMIN SDK
            // ===========================
            if (FirebaseApp.DefaultInstance == null)
            {
                FirebaseApp.Create(new AppOptions
                {
                    Credential = GoogleCredential.GetApplicationDefault(),
                    ProjectId = "ezone-afa80"
                });
            }

            // ===========================
            // FIREBASE FIRESTORE
            // ===========================
            builder.Services.AddSingleton(
                FirestoreDb.Create("ezone-afa80")
            );

            builder.Services.AddSingleton(FirebaseAuth.DefaultInstance);
            builder.Services.AddHostedService<FirebaseRoomExpirationWorker>();
            builder.Services.AddScoped<IEmailService, EmailService>();
            AddApiServices(builder.Services);

            var app = builder.Build();
            ConfigureApiPipeline(app);
            app.Run();
        }

        public static void AddApiServices(IServiceCollection services)
        {
            services.AddScoped<FirebaseTokenService>();
            services.AddScoped<FirebaseMatchEligibilityService>();
            services.AddScoped<FirebaseAuthService>();
            services.AddScoped<FirebaseUserService>();
            services.AddScoped<FirebaseChatRoomService>();
            services.AddScoped<FirebaseChatMessageService>();
            services.AddScoped<FirebaseMatchHistoryService>();
            services.AddScoped<FirebaseTemporaryMessageCleanupService>();
            services.AddScoped<FirebaseReportService>();
            services.AddScoped<FirebaseMatchReservationService>();
            services.AddScoped<FirebaseRandomMatchQueueService>();

            // ===========================
            // SERVICES
            // ===========================
            services.AddControllers();
            services.AddSignalR();

            services.AddSingleton<ModerationService>();

            // ===========================
            // RATE LIMITING
            // ===========================
            services.AddRateLimiter(options =>
            {
                options.RejectionStatusCode =
                    StatusCodes.Status429TooManyRequests;

                options.AddPolicy("ResendVerification", context =>
                    RateLimitPartition.GetFixedWindowLimiter(
                        partitionKey:
                            context.Connection.RemoteIpAddress?.ToString()
                            ?? "unknown",
                        factory: _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = 3,
                            Window = TimeSpan.FromMinutes(15),
                            QueueLimit = 0,
                            AutoReplenishment = true
                        }));
            });

            // ===========================
            // CORS
            // ===========================
            services.AddCors(options =>
            {
                options.AddPolicy("AllowReactApp", policy =>
                {
                    policy.WithOrigins("http://localhost:3000")
                          .AllowAnyHeader()
                          .AllowAnyMethod()
                          .AllowCredentials();
                });
            });

            // ===========================
            // AUTHENTICATION
            // ===========================
            services
                .AddAuthentication("Firebase")
                .AddJwtBearer("Firebase", options =>
                {
                    options.Authority =
                        "https://securetoken.google.com/ezone-afa80";

                    options.RequireHttpsMetadata = true;
                    options.MapInboundClaims = false;


                    options.Events = new JwtBearerEvents
                    {
                        OnMessageReceived = context =>
                        {
                            var accessToken =
                                context.Request.Query["access_token"];

                            var path = context.HttpContext.Request.Path;

                            if (!string.IsNullOrEmpty(accessToken) &&
                                path.StartsWithSegments("/firebaseChatHub"))
                            {
                                context.Token = accessToken;
                            }

                            return Task.CompletedTask;
                        }
                    };


                    options.TokenValidationParameters =
                        new TokenValidationParameters
                        {
                            ValidateIssuer = true,
                            ValidIssuer =
                                "https://securetoken.google.com/ezone-afa80",

                            ValidateAudience = true,
                            ValidAudience = "ezone-afa80",

                            ValidateLifetime = true,
                            ValidateIssuerSigningKey = true,

                            NameClaimType = "user_id"
                        };
                });

            // ===========================
            // AUTHORIZATION
            // ===========================
            services.AddAuthorization(options =>
            {
                // Firebase authentication policy
                options.AddPolicy("FirebaseUser", policy =>
                {
                    policy.AddAuthenticationSchemes("Firebase");
                    policy.RequireAuthenticatedUser();
                });
            });

            // ===========================
            // SWAGGER
            // ===========================
            services.AddEndpointsApiExplorer();

            services.AddSwaggerGen(options =>
            {
                options.SwaggerDoc("v1", new OpenApiInfo
                {
                    Title = "EZone API",
                    Version = "v1"
                });

                options.AddSecurityDefinition(
                    "Bearer",
                    new OpenApiSecurityScheme
                    {
                        Name = "Authorization",
                        Type = SecuritySchemeType.Http,
                        Scheme = "bearer",
                        BearerFormat = "JWT",
                        In = ParameterLocation.Header,
                        Description = "Enter Firebase ID Token"
                    });

                options.AddSecurityRequirement(
                    new OpenApiSecurityRequirement
                    {
                        {
                            new OpenApiSecurityScheme
                            {
                                Reference = new OpenApiReference
                                {
                                    Type = ReferenceType.SecurityScheme,
                                    Id = "Bearer"
                                }
                            },
                            Array.Empty<string>()
                        }
                    });
            });
        }

        public static void ConfigureApiPipeline(WebApplication app)
        {
            // ===========================
            // MIDDLEWARE
            // ===========================
            if (app.Environment.IsDevelopment()
                || app.Environment.EnvironmentName == "Docker")
            {
                app.UseSwagger();
                app.UseSwaggerUI();
            }

            app.UseHttpsRedirection();
            app.UseStaticFiles();

            app.UseRouting();

            app.UseCors("AllowReactApp");

            app.UseRateLimiter();

            app.UseAuthentication();
            app.UseAuthorization();

            // ===========================
            // ENDPOINTS
            // ===========================
            app.MapControllers();
            app.MapHub<FirebaseChatHub>("/firebaseChatHub");
        }
    }
}
