﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using BasicApi.Models;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Newtonsoft.Json.Serialization;
using Npgsql;

namespace BasicApi
{
    public class Startup
    {
        // Optional feature for console debugging.
        private static readonly bool _displaySqlScripts = false;

        private bool _isSQLite;

        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        public IConfiguration Configuration { get; }

        public void ConfigureServices(IServiceCollection services)
        {
            var rsa = new RSACryptoServiceProvider(2048);
            var key = new RsaSecurityKey(rsa.ExportParameters(true));

            services.AddSingleton(new SigningCredentials(
                key,
                SecurityAlgorithms.RsaSha256Signature));

            services.AddAuthentication().AddJwtBearer(options =>
            {
                options.TokenValidationParameters.IssuerSigningKey = key;
                options.TokenValidationParameters.ValidAudience = "Myself";
                options.TokenValidationParameters.ValidIssuer = "BasicApi";
            });

            var connectionString = Configuration["ConnectionString"];
            var databaseType = Configuration["Database"];
            if (string.IsNullOrEmpty(databaseType))
            {
                // Use SQLite when running outside a benchmark test or if benchmarks user specified "None".
                // ("None" is not passed to the web application.)
                databaseType = "SQLite";
            }
            else if (string.IsNullOrEmpty(connectionString))
            {
                throw new ArgumentException("Connection string must be specified for {databaseType}.");
            }

            switch (databaseType.ToUpper())
            {
#if !NET461
                case "MYSQL":
                    services
                        .AddEntityFrameworkMySql()
                        .AddDbContext<BasicApiContext>(options => options.UseMySql(connectionString));
                    break;
#endif

                case "POSTGRESQL":
                    var settings = new NpgsqlConnectionStringBuilder(connectionString);
                    if (!settings.NoResetOnClose)
                    {
                        throw new ArgumentException("No Reset On Close=true must be specified for Npgsql.");
                    }
                    if (settings.Enlist)
                    {
                        throw new ArgumentException("Enlist=false must be specified for Npgsql.");
                    }

                    services
                        .AddEntityFrameworkNpgsql()
                        .AddDbContextPool<BasicApiContext>(options => options.UseNpgsql(connectionString));
                    break;

                case "SQLITE":
                    _isSQLite = true;
                    services
                        .AddEntityFrameworkSqlite()
                        .AddDbContextPool<BasicApiContext>(options => options.UseSqlite("Data Source=BasicApi.db"));
                    break;

                case "SQLSERVER":
                    services
                        .AddEntityFrameworkSqlServer()
                        .AddDbContextPool<BasicApiContext>(options => options.UseSqlServer(connectionString));
                    break;

                default:
                    throw new ArgumentException($"Application does not support database type {databaseType}.");
            }

            services.AddAuthorization(options =>
            {
                options.AddPolicy(
                    "pet-store-reader",
                    builder => builder
                        .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
                        .RequireAuthenticatedUser()
                        .RequireClaim("scope", "pet-store-reader"));

                options.AddPolicy(
                    "pet-store-writer",
                    builder => builder
                        .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
                        .RequireAuthenticatedUser()
                        .RequireClaim("scope", "pet-store-writer"));
            });

            services
                .AddMvcCore()
                .AddAuthorization()
                .AddJsonFormatters(json => json.ContractResolver = new CamelCasePropertyNamesContractResolver())
                .AddDataAnnotations();

            services.AddSingleton(new PetRepository());
        }

        public void Configure(IApplicationBuilder app, IApplicationLifetime lifetime)
        {
            var services = app.ApplicationServices;
            CreateDatabaseTables(services);
            if (_isSQLite)
            {
                lifetime.ApplicationStopping.Register(() => DropDatabase(services));
            }
            else
            {
                lifetime.ApplicationStopping.Register(() => DropDatabaseTables(services));
            }

            app.Use(next => async context =>
            {
                try
                {
                    await next(context);
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex);
                    throw;
                }
            });

            app.UseAuthentication();
            app.UseMvc();
        }

        private void CreateDatabaseTables(IServiceProvider services)
        {
            using (var serviceScope = services.GetRequiredService<IServiceScopeFactory>().CreateScope())
            {
                using (var dbContext = serviceScope.ServiceProvider.GetRequiredService<BasicApiContext>())
                {
                    if (_displaySqlScripts)
                    {
                        var migrator = dbContext.GetService<IMigrator>();
                        var script = migrator.GenerateScript(
                            fromMigration: Migration.InitialDatabase,
                            toMigration: dbContext.Database.GetMigrations().LastOrDefault());
                        Console.WriteLine("Create script:");
                        Console.WriteLine(script);
                    }

                    dbContext.Database.Migrate();
                }
            }
        }

        // Don't leave SQLite's .db file behind.
        public static void DropDatabase(IServiceProvider services)
        {
            using (var serviceScope = services.GetRequiredService<IServiceScopeFactory>().CreateScope())
            {
                using (var dbContext = serviceScope.ServiceProvider.GetRequiredService<BasicApiContext>())
                {
                    if (_displaySqlScripts)
                    {
                        var migrator = dbContext.GetService<IMigrator>();
                        var script = migrator.GenerateScript(
                            fromMigration: dbContext.Database.GetAppliedMigrations().LastOrDefault(),
                            toMigration: Migration.InitialDatabase);
                        Console.WriteLine("Delete script:");
                        Console.WriteLine(script);
                    }

                    dbContext.Database.EnsureDeleted();
                }
            }
        }

        private void DropDatabaseTables(IServiceProvider services)
        {
            using (var serviceScope = services.GetRequiredService<IServiceScopeFactory>().CreateScope())
            {
                using (var dbContext = serviceScope.ServiceProvider.GetRequiredService<BasicApiContext>())
                {
                    var migrator = dbContext.GetService<IMigrator>();
                    if (_displaySqlScripts)
                    {
                        var script = migrator.GenerateScript(
                            fromMigration: dbContext.Database.GetAppliedMigrations().LastOrDefault(),
                            toMigration: Migration.InitialDatabase);
                        Console.WriteLine("Delete script:");
                        Console.WriteLine(script);
                    }

                    migrator.Migrate(Migration.InitialDatabase);
                }
            }
        }

        public static void Main(string[] args)
        {
            var host = CreateWebHostBuilder(args)
                .Build();

            host.Run();
        }

        public static IWebHostBuilder CreateWebHostBuilder(string[] args)
        {
            var configuration = new ConfigurationBuilder()
                .AddEnvironmentVariables()
                .AddCommandLine(args)
                .Build();

            return new WebHostBuilder()
                .UseKestrel()
                .UseUrls("http://+:5000")
                .UseConfiguration(configuration)
                .UseContentRoot(Directory.GetCurrentDirectory())
                .UseStartup<Startup>();
        }
    }
}
