using NuGet.Common;
using System;
using System.Threading.Tasks;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace Chorizite.PluginIndexBuilder {
    internal class Logger : ILogger {
        private string name;
        private Options options;

        public Logger(string name, Options options) {
            this.name = name;
            this.options = options;
        }

        public void Log(LogLevel level, string data) {
            if (options.Verbose || level >= LogLevel.Warning) {
                Console.WriteLine($"{name}[{level}]: {data}");
            }
        }

        public void Log(ILogMessage message) {
            Console.WriteLine($"{name}: {message.Message}");
        }

        public Task LogAsync(LogLevel level, string data) {
            if (options.Verbose || level >= LogLevel.Warning) {
                Console.WriteLine($"{name}[{level}]: {data}");
            }
            return Task.CompletedTask;
        }

        public Task LogAsync(ILogMessage message) {
            Console.WriteLine($"{name}: {message.Message}");
            return Task.CompletedTask;
        }

        public void LogDebug(string data) {
            if (options.Verbose) {
                Log(LogLevel.Debug, data);
            }
        }

        public void LogError(string data) {
            Log(LogLevel.Error, data);
        }

        public void LogInformation(string data) {
            if (options.Verbose) {
                Log(LogLevel.Information, data);
            }
        }

        public void LogInformationSummary(string data) {
            if (options.Verbose) {
                Log(LogLevel.Information, data);
            }
        }

        public void LogMinimal(string data) {
            if (options.Verbose) {
                Log(LogLevel.Minimal, data);
            }
        }

        public void LogVerbose(string data) {
            if (options.Verbose) {
                Log(LogLevel.Verbose, data);
            }
        }

        public void LogWarning(string data) {
            if (options.Verbose) {
                Log(LogLevel.Warning, data);
            }
        }
    }
}