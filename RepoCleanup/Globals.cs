using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RepoCleanup
{
    static class Globals
    {

        public static HttpClient Client { set; get; }

        public static bool IsDryRun { get; set; } = true;

        public static string GiteaToken { get; internal set; }

        public static string RepositoryBaseUrl { get; internal set; }

        public static readonly JsonSerializerOptions SerializerOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters =
            {
                new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)
            }
        };
                
        public static IConfigurationRoot Configuration { get; } =
            (                
                ReadConfig()                
            );

        public static IConfigurationRoot ReadConfig()
        {
            var configBuilder = new ConfigurationBuilder();
            
            configBuilder
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);

            return configBuilder.Build();
        }

        public static ILogger<T> CreateLogger<T>() => LogFactory.CreateLogger<T>();

        public static ILoggerFactory LogFactory { get; } = LoggerFactory.Create(builder =>
        {
            builder.ClearProviders();            
            builder
                .AddConfiguration(Configuration.GetSection("Logging"))
                .AddSimpleConsole(options =>
                { 
                    options.IncludeScopes = true;
                    options.TimestampFormat = "hh:mm:ss ";
                });            
        });
    }
}
