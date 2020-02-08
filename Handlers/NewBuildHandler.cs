using System;
using System.IO;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;
using System.Net;
using Newtonsoft.Json;

using Faction.Common;
using Faction.Common.Backend.Database;
using Faction.Common.Backend.EventBus.Abstractions;
using Faction.Common.Messages;
using Faction.Common.Models;

using Faction.Build.Dotnet;
using Faction.Build.Dotnet.Objects;

namespace Faction.Build.Dotnet.Handlers
{
  public class NewPayloadBuildEventHandler : IEventHandler<NewPayloadBuild>
  {
    public string apiUrl = "http://api:5000/api/v1/payload";
    private readonly IEventBus _eventBus;
    private static FactionRepository _taskRepository;

    public NewPayloadBuildEventHandler(IEventBus eventBus, FactionRepository taskRepository)
    {
      _eventBus = eventBus; // Inject the EventBus into this Handler to Publish a message, insert AppDbContext here for DB Access
      _taskRepository = taskRepository;
    }

    public BuildConfig CreateBuildConfig(Payload payload)
    {
      BuildConfig buildConfig = new BuildConfig();
      buildConfig.BeaconInterval = payload.BeaconInterval;
      buildConfig.Jitter = payload.Jitter;
      buildConfig.PayloadName = payload.Name;
      buildConfig.PayloadKey = payload.Key;
      if (payload.ExpirationDate.HasValue)
      {
        buildConfig.ExpirationDate = payload.ExpirationDate.Value.ToString("o");
      }
      buildConfig.OperatingSystem = _taskRepository.GetAgentTypeOperatingSystem(payload.AgentTypeOperatingSystemId).Name;
      buildConfig.Version = _taskRepository.GetAgentTypeVersion(payload.AgentTypeVersionId).Name;
      buildConfig.Architecture = _taskRepository.GetAgentTypeVersion(payload.AgentTypeVersionId).Name;
      buildConfig.Configuration = _taskRepository.GetAgentTypeConfiguration(payload.AgentTypeConfigurationId).Name;
      buildConfig.InitialTransportType = payload.Transport.TransportType;
      buildConfig.TransportConfiguration = payload.Transport.Configuration;
      buildConfig.Debug = payload.Debug;
      return buildConfig;
    }

    public Dictionary<string, string> RunCommand(string agentDirectory, string cmd) {
      var escapedArgs = cmd.Replace("\"", "\\\"");
      Console.WriteLine($"[i] Executing build command: {escapedArgs}");
      Process proc = new Process()
      {
        StartInfo = new ProcessStartInfo
        {
          FileName = "/bin/bash",
          Arguments = $"-c \"{escapedArgs}\"",
          RedirectStandardError = true,
          RedirectStandardOutput = true,
          UseShellExecute = false,
          CreateNoWindow = true,
          WorkingDirectory = agentDirectory
        }
      };

      proc.Start();
      proc.WaitForExit();
      
      string output = proc.StandardOutput.ReadToEnd();
      string error = proc.StandardError.ReadToEnd();

      Dictionary<string, string> result = new Dictionary<string, string>();
      result["ExitCode"] = proc.ExitCode.ToString();
      result["Output"] = output;
      result["Error"] = error;
      return result;

    }

    public async Task Handle(NewPayloadBuild newPayloadBuild, string replyTo, string correlationId)
    {
      Console.WriteLine($"[i] Got New Payload Message.");
      if (Settings.LanguageName != newPayloadBuild.LanguageName)
      {
        Console.WriteLine($"[i] Build is for {newPayloadBuild.LanguageName}. Not our problem.");
      }
      else
      {
        Console.WriteLine($"[i] Build is ours. Lets do this..");
        Payload payload = _taskRepository.GetPayload(newPayloadBuild.PayloadId);
        payload.AgentType = _taskRepository.GetAgentType(payload.AgentTypeId);
        payload.AgentTransportType = _taskRepository.GetAgentTransportType(payload.AgentTransportTypeId);
        payload.AgentTypeConfiguration = _taskRepository.GetAgentTypeConfiguration(payload.AgentTypeConfigurationId);
        payload.Transport = _taskRepository.GetTransport(payload.TransportId);
        string workingDir = Path.Join(Settings.AgentsPath, payload.AgentType.Name);

        // Create build config file
        BuildConfig buildConfig = CreateBuildConfig(payload);

        string buildConfigFile = Path.GetTempFileName();
        File.AppendAllText(buildConfigFile, JsonConvert.SerializeObject(buildConfig, Formatting.Indented));

        // Build transport first
        File.Delete(Path.Join(workingDir, payload.AgentTransportType.BuildLocation));

        string transportBuildCommand = $"{payload.AgentTransportType.BuildCommand} {buildConfigFile}";

        Dictionary<string, string> cmdResult = RunCommand(workingDir, transportBuildCommand);
        string transportB64 = "";
        if (cmdResult["ExitCode"] == "0") {
          byte[] transportBytes = File.ReadAllBytes(Path.Join(workingDir, payload.AgentTransportType.BuildLocation));
          transportB64 = Convert.ToBase64String(transportBytes);
          File.Delete(buildConfigFile);
        }
        else {
          Console.WriteLine($"ERROR DURING TRANSPORT BUILD: \nStdout: {cmdResult["Output"]}\n Stderr: {cmdResult["Error"]}");
          NewErrorMessage response = new NewErrorMessage();
          response.Source = ".NET Build Server";
          response.Message = $"Error building {payload.AgentType.Name}";
          response.Details = $"Stdout: {cmdResult["Output"]}\n Stderr: {cmdResult["Error"]}";
          _eventBus.Publish(response, replyTo=null, correlationId=null);
        }

        // Build the agent
        if (!String.IsNullOrEmpty(transportB64)) {
          buildConfig.TransportModule = transportB64;
          File.AppendAllText(buildConfigFile, JsonConvert.SerializeObject(buildConfig, Formatting.Indented));

          File.Delete(Path.Join(workingDir, payload.AgentType.BuildLocation));
          string buildCommand = $"{payload.AgentType.BuildCommand} {buildConfigFile}";
          cmdResult = RunCommand(workingDir, buildCommand);

          if (cmdResult["ExitCode"] == "0") {
            try {
              Console.WriteLine($"[PayloadBuildService] Build Successful!");
              string originalPath = Path.Join(workingDir, payload.AgentType.BuildLocation);
              string fileExtension = Path.GetExtension(originalPath);
              string payloadPath = Path.Join(Settings.AgentsPath, "/build/", $"{payload.AgentType.Name}_{payload.AgentTypeConfiguration.Name}_{payload.Name}_{DateTime.Now.ToString("yyyyMMddHHmmss")}{fileExtension}");
              Console.WriteLine($"[PayloadBuildService] Moving from {originalPath} to {payloadPath}");
              File.Move(originalPath, payloadPath);
              string uploadUlr = $"{apiUrl}/{payload.Id}/file/";
              WebClient wc = new WebClient();
              wc.Headers.Add("build-token", payload.BuildToken);
              Console.WriteLine($"[PayloadBuildService] Uploading to {uploadUlr} with token {payload.BuildToken}");
              byte[] resp = wc.UploadFile(uploadUlr, payloadPath);
              Console.WriteLine($"[PayloadBuildService] Response: {wc.Encoding.GetString(resp)}");
              //File.Delete(buildConfigFile);
              payload.Built = true;
              PayloadUpdated payloadUpdated = new PayloadUpdated {Success = true, Payload = payload};
              _eventBus.Publish(payloadUpdated, replyTo, correlationId);
            }
            catch (Exception e) {
              Console.WriteLine($"ERROR UPLOADING PAYLOAD TO API: \n{e.Message}");
              NewErrorMessage response = new NewErrorMessage
              {
                Source = ".NET Build Server",
                Message = $"Error uploading {payload.AgentType.Name} payload to API",
                Details = $"{e.Message}"
              };
              _eventBus.Publish(response, replyTo=null, correlationId=null);
            }
          }
          else {
            Console.WriteLine($"ERROR DURING AGENT BUILD: \nStdout: {cmdResult["Output"]}\n Stderr: {cmdResult["Error"]}");
            NewErrorMessage response = new NewErrorMessage
            {
              Source = ".NET Build Server",
              Message = $"Error building {payload.AgentType.Name}",
              Details = $"Stdout: {cmdResult["Output"]}\n Stderr: {cmdResult["Error"]}"
            };
            _eventBus.Publish(response, replyTo=null, correlationId=null);
          }
        }
        else
        {
          Console.WriteLine(
            $"ERROR DURING AGENT BUILD: \nStdout: {cmdResult["Output"]}\n Stderr: {cmdResult["Error"]}");
          NewErrorMessage response = new NewErrorMessage
          {
            Source = ".NET Build Server",
            Message = $"Error building {payload.AgentType.Name}",
            Details =
              $"Tried to build an agent without a Base64 encoded transport string. Transport build must have failed."
          };
          _eventBus.Publish(response, replyTo = null, correlationId = null);
        }
      }
    }
  }
}