using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using MagicOnion.Server;
using MagicOnion.Server.OpenTelemetry;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Exporter.Prometheus;
using OpenTelemetry.Trace;

namespace ChatApp.Server
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
            services.AddGrpc(); // MagicOnion depends on ASP.NET Core gRPC service.
            services.AddMagicOnion(options =>
            {
                options.GlobalFilters.Add(new OpenTelemetryCollectorTracerFilterFactoryAttribute());
                options.GlobalStreamingHubFilters.Add(new OpenTelemetryHubCollectorTracerFilterFactoryAttribute());
            });

            // MagicOnion's OpenTelemetry
            services.AddMagicOnionOpenTelemetry((options, meterOptions) =>
            {
                // open-telemetry with Prometheus exporter
                meterOptions.MetricsExporter = new PrometheusExporter(new PrometheusExporterOptions() { Url = options.MetricsExporterEndpoint });
                meterOptions.MeterLogger = (mp) => new OpenTelemetryCollectorMeterLogger(mp, "0.8.0-beta.1");
            },
            (options, provider, tracerBuilder) =>
            {
                // Switch between Jaeger/Zipkin by setting UseExporter in appsettings.json.
                var exporter = this.Configuration.GetValue<string>("UseExporter").ToLowerInvariant();
                switch (exporter)
                {
                    case "jaeger":
                        tracerBuilder
                            .AddAspNetCoreInstrumentation()
                            .AddJaegerExporter(jaegerOptions =>
                            {
                                jaegerOptions.ServiceName = "chatapp.server";
                                jaegerOptions.AgentHost = this.Configuration.GetValue<string>("Jaeger:Host");
                                jaegerOptions.AgentPort = this.Configuration.GetValue<int>("Jaeger:Port");
                            });
                        break;
                    case "zipkin":
                        tracerBuilder
                            .AddAspNetCoreInstrumentation()
                            .AddZipkinExporter(zipkinOptions =>
                            {
                                zipkinOptions.ServiceName = "chatapp.server";
                                zipkinOptions.Endpoint = new Uri(this.Configuration.GetValue<string>("Zipkin:Endpoint"));
                            });
                        break;
                    default:
                        // ConsoleExporter will show current tracer activity
                        tracerBuilder
                            .AddAspNetCoreInstrumentation()
                            .AddConsoleExporter();
                        break;
                }
            });

            // additional Tracer for user self-service.
            OpenTelemetry.Sdk.CreateTracerProviderBuilder()
                .AddSource("mysql")
                .AddJaegerExporter(jaegerOptions =>
                {
                    jaegerOptions.ServiceName = "mysql";
                    jaegerOptions.AgentHost = this.Configuration.GetValue<string>("Jaeger:Host");
                    jaegerOptions.AgentPort = this.Configuration.GetValue<int>("Jaeger:Port");
                })
                .Build();
            OpenTelemetry.Sdk.CreateTracerProviderBuilder()
                .AddSource("redis")
                .AddJaegerExporter(jaegerOptions =>
                {
                    jaegerOptions.ServiceName = "redis";
                    jaegerOptions.AgentHost = this.Configuration.GetValue<string>("Jaeger:Host");
                    jaegerOptions.AgentPort = this.Configuration.GetValue<int>("Jaeger:Port");
                })
                .Build();
            services.AddSingleton(new BackendActivitySources(new[] { new ActivitySource("mysql"), new ActivitySource("redis") }));

            // host Prometheus Metrics Server
            services.AddHostedService<PrometheusExporterMetricsService>();
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }
            else
            {
                app.UseExceptionHandler("/Home/Error");
                // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
                app.UseHsts();
            }
            //app.UseHttpsRedirection();

            app.UseRouting();

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapMagicOnionService();
            });
        }
    }
}
