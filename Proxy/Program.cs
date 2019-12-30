using System;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Proxy
{
    public class Program
    {
        public static void Main(string[] args)
        {
            using var host = CreateHostBuilder(args).Build();
            Console.WriteLine($"Log of SM3-Gateway @ {DateTime.UtcNow}");
            Console.WriteLine($"Version: 0.1.0");
            host.Run();
        }

        public static IHostBuilder CreateHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args)
                .ConfigureWebHostDefaults(webBuilder =>
                {
                    webBuilder
                        .ConfigureLogging(((context, builder)
                                              => builder.AddConsole((options => options.IncludeScopes = true))
                                                        .AddDebug()
                                                        .AddEventSourceLogger()))
                        .UseStartup<Startup>()
                        .UseKestrel(((context, options) =>
                        {
                            options.ListenAnyIP(25565, listenOptions =>
                            {
                                // listenOptions.Protocols = HttpProtocols.None;
                                listenOptions.UseConnectionHandler<MCConnectionHandler>();
                            });
                        }));
                });
    }
}
