using System;
using System.IO;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;


using Faction.Common.Backend.Database;
using Faction.Common.Backend.EventBus.Abstractions;
using Faction.Common.Models;
using Faction.Common.Messages;

namespace Faction.Build.Dotnet.Handlers
{
  public class LoadModuleEventHandler : IEventHandler<LoadModule>
  {
    private readonly IEventBus _eventBus;
    private static FactionRepository _taskRepository;

    public LoadModuleEventHandler(IEventBus eventBus, FactionRepository taskRepository)
    {
      _eventBus = eventBus; // Inject the EventBus into this Handler to Publish a message, insert AppDbContext here for DB Access
      _taskRepository = taskRepository;
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

    public async Task Handle(LoadModule moduleRequest, string replyTo, string correlationId)
    {
      Console.WriteLine($"[i] Got New LoadModule Message.");
      
      Module module = _taskRepository.GetModule(moduleRequest.Name, moduleRequest.Language);

      string moduleb64 = "";
      string workingDir = Path.Join(Settings.ModulesPath, Settings.LanguageName);
      string exitCode = "0";
      string output = "";
      string error = "";
      if (!String.IsNullOrEmpty(module.BuildCommand))
      {
        Dictionary<string, string> cmdResult = RunCommand(workingDir, module.BuildCommand);
        exitCode = cmdResult["ExitCode"];
        output = cmdResult["Output"];
        error = cmdResult["Error"];
      }

      if (exitCode == "0") {
        byte[] moduleBytes = File.ReadAllBytes(Path.Join(workingDir, module.BuildLocation));
        moduleb64 = Convert.ToBase64String(moduleBytes);
      }

      if (String.IsNullOrEmpty(moduleb64)) {
        NewErrorMessage response = new NewErrorMessage();
        response.Source = ".NET Build Server";
        response.Message = $"Error building {moduleRequest.Name} module.";
        response.Details = $"Stdout: {output}\n Stderr: {error}";
        _eventBus.Publish(response, replyTo=null, correlationId=null);
      }
      else 
      {
        ModuleResponse response = new ModuleResponse();
        response.Success = true;
        response.Contents = $"module {moduleRequest.Name} {moduleb64}";
        _eventBus.Publish(response, replyTo, correlationId);
      }
    }
  }
}