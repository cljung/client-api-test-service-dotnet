using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.AzureAppServices;
using System;

namespace AA.DIDApi
{
    public class Program
    {
        public static void Main(string[] args) => CreateWebHostBuilder(args).Build().Run();

        public static IWebHostBuilder CreateWebHostBuilder(string[] args) =>
            WebHost.CreateDefaultBuilder(args)
                .ConfigureLogging(logging =>
                {
                    logging.ClearProviders();
                    // We have to be precise on the logging levels
                    logging.AddConsole();
                    logging.AddDebug();
                    logging.AddAzureWebAppDiagnostics();
                })
                .ConfigureServices(services =>
                {
                    services.Configure<AzureFileLoggerOptions>(options =>
                    {
                        options.FileName = $"my-azure-diagnostics-{DateTime.UtcNow:yyyyMMdd-hhmmss}-";
                        options.FileSizeLimit = 50 * 1024;
                        options.RetainedFileCountLimit = 5;
                    });
                })
                .UseStartup<Startup>();
    }
}
