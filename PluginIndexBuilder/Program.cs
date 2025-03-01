using CommandLine;
using CommandLine.Text;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Chorizite.PluginIndexBuilder {
    public class Options {
        [Option('v', "verbose", Required = false, HelpText = "Set output to verbose messages.")]
        public bool Verbose { get; set; }

        [Option('o', "output", Required = false, HelpText = "Output directory", Default = "out")]
        public string OutputDirectory { get; set; }

        [Option('w', "workdir", Required = false, HelpText = "Work directory", Default = "tmp")]
        public string WorkDirectory { get; set; }

        [Value(0, MetaName = "repos-json-file", Required = true, HelpText = "Path to repositories.json")]
        public string RespositoriesJsonPath { get; set; }
    }

    internal class Program {
        static void Main(string[] args) {
            var parser = new Parser(with => with.HelpWriter = null);
            var parserResult = parser.ParseArguments<Options>(args);

            parserResult.WithParsed<Options>(o => {
                if (!File.Exists(o.RespositoriesJsonPath)) {
                    Console.WriteLine($"File does not exist: {o.RespositoriesJsonPath}");
                    return;
                }

                var indexBuilder = new IndexBuilder(o);
                indexBuilder.Build().Wait();
            }).WithNotParsed(errs => DisplayHelp(parserResult, errs)); ;
        }
        static void DisplayHelp<T>(ParserResult<T> result, IEnumerable<Error> errs) {
            var helpText = HelpText.AutoBuild(result, h => {
                h.AddPostOptionsLine($"Examples:");
                h.AddPostOptionsLine("");
                h.AddPostOptionsLine($"  dotnet tool run chorizite-plugin-index-builder ./repositories.json");

                return h;
            }, e => e);

            Console.WriteLine(HelpText.DefaultParsingErrorsHandler(result, helpText));
            if (errs != null && errs.Count() > 0) {
                Environment.Exit(1);
            }
        }
    }
}
