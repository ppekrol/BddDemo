using Digitalis.Infrastructure;
using FluentValidation.AspNetCore;
using Hellang.Middleware.ProblemDetails;
using MediatR;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Session;
using System;
using System.Net.Http;
using System.Reflection;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Digitalis.Infrastructure.Mediatr;
using Digitalis.Infrastructure.OpenApi;
using Digitalis.Services;
using Serilog;
using Serilog.Context;
using Serilog.Core.Enrichers;

namespace Digitalis
{
    public class Startup
    {
        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        public IConfiguration Configuration { get; }

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddHealthChecks();
            services.AddControllers()
                .AddFluentValidation(s => s.RegisterValidatorsFromAssemblyContaining<Startup>());

            services.AddSingleton<IDocumentStore>(_ =>
            {
                var dbConfig = Configuration.GetSection("Database").Get<Settings.DatabaseSettings>();

                var store = new DocumentStore
                {
                    Urls = dbConfig.Urls,
                    Database = dbConfig.DatabaseName
                };

                if (!string.IsNullOrWhiteSpace(dbConfig.CertPath))
                    store.Certificate = new X509Certificate2(dbConfig.CertPath, dbConfig.CertPass);

                store.Initialize();

                IndexCreation.CreateIndexes(typeof(Startup).Assembly, store);

                return store;
            });

            services.AddScoped<IAsyncDocumentSession>(sp => sp.GetService<IDocumentStore>()?.OpenAsyncSession());
            services.AddScoped<IDocumentSession>(sp => sp.GetService<IDocumentStore>()?.OpenSession());

            services.AddMediatR(typeof(Startup));
            services.AddTransient(typeof(IPipelineBehavior<,>), typeof(LoggingPipelineBehavior<,>));
            services.AddTransient(typeof(IPipelineBehavior<,>), typeof(AuthorizerPipelineBehavior<,>));
            services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ValidatorPipelineBehavior<,>));

            services.AddAuthorization();
            services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
                .AddCookie(CookieAuthenticationDefaults.AuthenticationScheme,
                    options =>
                    {
                        options.LoginPath = new PathString("/auth/login");
                        options.AccessDeniedPath = new PathString("/auth/denied");
                    });

            services.Scan(
                x =>
                {
                    var entryAssembly = Assembly.GetAssembly(typeof(Startup));

                    x.FromAssemblies(entryAssembly)
                        .AddClasses(classes => classes.AssignableTo(typeof(IAuthorizer<>)))
                        .AsImplementedInterfaces()
                        .WithScopedLifetime();
                });

            services.AddTransient<IMailer, Mailer>();

            services.AddProblemDetails(ConfigureProblemDetails);

            services.AddSwaggerDocument(settings =>
            {
                settings.SchemaNameGenerator = new CustomSchemaNameGenerator();
                settings.Title = App.Title;
            });
        }

        private void ConfigureProblemDetails(ProblemDetailsOptions options)
        {
            options.Rethrow<NotSupportedException>();

            options.MapToStatusCode<NotImplementedException>(StatusCodes.Status501NotImplemented);
            options.MapToStatusCode<AuthenticationException>(StatusCodes.Status401Unauthorized);
            options.MapToStatusCode<UnauthorizedAccessException>(StatusCodes.Status403Forbidden);
            options.MapToStatusCode<InputValidationException>(StatusCodes.Status400BadRequest);

            options.MapToStatusCode<HttpRequestException>(StatusCodes.Status503ServiceUnavailable);

            // Because exceptions are handled polymorphically, this will act as a "catch all" mapping, which is why it's added last.
            // If an exception other than NotImplementedException and HttpRequestException is thrown, this will handle it.
            options.MapToStatusCode<Exception>(StatusCodes.Status500InternalServerError);
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            app.UseProblemDetails();

            if (env.IsDevelopment())
            {
                //app.UseDeveloperExceptionPage();
            }

            app.Use(async (ctx, next) =>
            {
                using (LogContext.Push(
                    new PropertyEnricher("IPAddress", ctx.Connection.RemoteIpAddress),
                    new PropertyEnricher("RequestHost", ctx.Request.Host),
                    new PropertyEnricher("RequestBasePath", ctx.Request.Path),
                    new PropertyEnricher("RequestQueryParams", ctx.Request.QueryString)))
                {
                    await next();
                }
            });

            app.UseSerilogRequestLogging();

            app.UseRouting();

            app.UseAuthentication();
            app.UseAuthorization();

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapHealthChecks("/healthcheck");
                endpoints.MapControllers();
            });

            app.UseOpenApi();
            app.UseSwaggerUi3(settings =>
            {
                settings.DocExpansion = "list";
            });
        }
    }
}
