using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using RabbitMQ.Client;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;

using Faction.Common;
using Faction.Common.Models;
using Faction.Common.Messages;
using Faction.Common.Backend.Database;
using Faction.Common.Backend.EventBus.Abstractions;
using Faction.Common.Backend.EventBus.RabbitMQ;
using Faction.Common.Backend.EventBus;

using Faction.Build.Dotnet.Objects;
using Faction.Build.Dotnet.Handlers;

namespace Faction.Build.Dotnet
{  
  class Program
  {
    public static void Main(string[] args)
    {
      FactionSettings factionSettings = Utility.GetConfiguration();
      string connectionString = $"Host={factionSettings.POSTGRES_HOST};Database={factionSettings.POSTGRES_DATABASE};Username={factionSettings.POSTGRES_USERNAME};Password={factionSettings.POSTGRES_PASSWORD}";
      
      var host = new HostBuilder()
          .ConfigureAppConfiguration((hostingContext, config) =>
          {
            // config.AddJsonFile("appsettings.json", optional: true);
          })
          .ConfigureLogging((hostingContext, logging) =>
          {
            logging.AddConfiguration(hostingContext.Configuration.GetSection("Logging"));
            logging.AddConsole();
            logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Warning);

          })
          .ConfigureServices((hostContext, services) =>
          {
            services.AddEntityFrameworkNpgsql().AddDbContext<FactionDbContext>(options =>
                      options.UseNpgsql(connectionString)
                  );

            // Open a connection to RabbitMQ and register it with DI
            services.AddSingleton<IRabbitMQPersistentConnection>(options =>
            {
              var factory = new ConnectionFactory()
              {
                HostName = factionSettings.RABBIT_HOST,
                UserName = factionSettings.RABBIT_USERNAME,
                Password = factionSettings.RABBIT_PASSWORD
              };
              return new DefaultRabbitMQPersistentConnection(factory);
            });

            services.AddSingleton<FactionRepository>();

            // Register the RabbitMQ EventBus with all the supporting Services (Event Handlers) with DI  
            RegisterEventBus(services);

            // Configure the above registered EventBus with all the Event to EventHandler mappings
            ConfigureEventBus(services);

            // Ensure the DB is initalized and seeding data
            // SeedData(services);
            bool dbLoaded = false;
            Console.WriteLine("Checking if database is ready");
            using (var context = new FactionDbContext())
            {
              while (!dbLoaded) {
                try {
                  var language = context.Language.CountAsync();
                  language.Wait();
                  dbLoaded = true;
                  Console.WriteLine("Database is ready");
                }
                catch {
                  Console.WriteLine("Database not ready, waiting for 5 seconds");
                  Task.Delay(5000).Wait();
                }
              }
            }

            var sp = services.BuildServiceProvider();
            FactionRepository dbRepository = sp.GetService<FactionRepository>();

            Loader.LoadSelf(dbRepository);
            Loader.LoadAgents(dbRepository);
            Loader.LoadModules(dbRepository);
          })
          .Build();;
      host.Start();
    }

    // TODO: Pass in the Exchange and Queue names to the constrcutors here (from appsettings.json)
    private static void RegisterEventBus(IServiceCollection services)
    {
      services.AddSingleton<IEventBus, EventBusRabbitMQ>(sp =>
      {
        var rabbitMQPersistentConnection = sp.GetRequiredService<IRabbitMQPersistentConnection>();
        var logger = sp.GetRequiredService<ILogger<EventBusRabbitMQ>>();
        var eventBusSubcriptionsManager = sp.GetRequiredService<IEventBusSubscriptionsManager>();
        return new EventBusRabbitMQ("Core", "DotnetBuildServer", rabbitMQPersistentConnection, eventBusSubcriptionsManager, sp, logger);
      });

      // Internal Service for keeping track of Event Subscription handlers (which Event maps to which Handler)
      services.AddSingleton<IEventBusSubscriptionsManager, InMemoryEventBusSubscriptionsManager>();

      // Add instances of our Message Event Handler to the DI pipeline
      services.AddTransient<NewPayloadBuildEventHandler>();
      services.AddTransient<LoadModuleEventHandler>();
    }
    private static void ConfigureEventBus(IServiceCollection services)
    {
      var sp = services.BuildServiceProvider();
      var eventBus = sp.GetRequiredService<IEventBus>();
      // Map the Message Event Type to the proper Event Handler
      eventBus.Initialize();
      eventBus.Subscribe<LoadModule, LoadModuleEventHandler>();
      eventBus.Subscribe<NewPayloadBuild, NewPayloadBuildEventHandler>();
    }
  }
}
