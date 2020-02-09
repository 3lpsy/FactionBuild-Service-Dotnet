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

            // Check to see if the database is listening and receptive to commands. 
            // does not check if database is configured/setup
            ConfirmDbReady(services);

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

          })
          .Build();

      ConfirmDbSetup(host);

      using (var scope = host.Services.CreateScope()) {
        var dbRepository = scope.ServiceProvider.GetService<FactionRepository>();
        Console.WriteLine("Loading build service");
        Loader.LoadSelf(dbRepository);
        Loader.LoadAgents(dbRepository);
        Loader.LoadModules(dbRepository);
      }

      Console.WriteLine("Starting the Dotnet Build Service");

      using (host) {
        host.Start();
      }
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

    public static void ConfirmDbReady(IServiceCollection services)
    {
      bool dbReady = false;
      var dbContext = services.BuildServiceProvider().GetService<FactionDbContext>();
      while (!dbReady) {
        using (var command = dbContext.Database.GetDbConnection().CreateCommand()) {
          command.CommandText = "SELECT 1;";
          command.CommandType = System.Data.CommandType.Text;
          try {
            dbContext.Database.OpenConnection();
          } catch (System.InvalidOperationException ex) {
            // TODO: handle errors
            Console.WriteLine($"Database not ready yet. Waiting 5 seconds. Error: {ex.GetType()}");
            Task.Delay(5000).Wait();
            continue;
          }
          using (var reader = command.ExecuteReader()) {
            while (reader.HasRows) {
              while (reader.Read()) {
                var result = (int)reader.GetInt32(0);
                if (result == 1) {
                  Console.WriteLine("Database is listening...");
                  dbReady = true;
                }
              }
              reader.NextResult();
            }
          }
        }
      }
    }

    public static IHost ConfirmDbSetup(IHost host)
    {
      bool dbLoaded = false;
      Console.WriteLine("Checking if Database is setup...");
      using (var scope = host.Services.CreateScope()) {
        var dbContext = scope.ServiceProvider.GetService<FactionDbContext>();
        while (!dbLoaded) {
          try {
            var language = dbContext.Language.CountAsync();
            language.Wait();
            dbLoaded = true;
            Console.WriteLine("Database is setup");
          } catch (Exception exception) {
            Console.WriteLine($"Database not setup, waiting 5 seconds. Error: {exception.GetType()} - {exception.Message}");
            Task.Delay(5000).Wait();
          }
        }
      }
      return host;
    }
  }
}
