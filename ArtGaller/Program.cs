using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Hosting;

namespace ArtGaller
{
    public class Program
    {
        public static void Main(string[] args)
        {
            CreateHostBuilder(args).Build().Run();
        }

        public static IHostBuilder CreateHostBuilder(string[] args)
        {
            return Host.CreateDefaultBuilder(args)
                .ConfigureWebHostDefaults(webBuilder =>
                {
                    webBuilder
                        .ConfigureKestrel((context, options) =>
                        {
                            options.ConfigureEndpointDefaults(listenOptions =>
                            {
                                listenOptions.Protocols = HttpProtocols.Http1AndHttp2AndHttp3;
                                listenOptions.UseHttps();
                            });
                        })
                        .UseStartup<Startup>();
                });
        }
    }
}
