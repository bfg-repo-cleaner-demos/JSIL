using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using JSIL.Compiler.Extensibility;
using JSIL.SolutionBuilder;
using JSIL.Utilities;

namespace JSIL.Compiler.Profiles {
    public abstract class BaseProfile : IProfile {
        public abstract bool IsAppropriateForSolution (SolutionBuilder.BuildResult buildResult);

        public virtual Configuration GetConfiguration (Configuration defaultConfiguration) {
            return defaultConfiguration;
        }

        public virtual TranslationResult Translate (
            VariableSet variables, AssemblyTranslator translator, Configuration configuration, 
            string assemblyPath, bool scanForProxies
        ) {
            var result = translator.Translate(assemblyPath, scanForProxies);

            AssemblyTranslator.GenerateManifest(translator.Manifest, assemblyPath, result);

            return result;
        }

        public virtual void WriteOutputs (VariableSet variables, Configuration configuration, TranslationResult result, string path, string manifestPrefix) {
            var manifestPath = manifestPrefix + "manifest.js";
            Console.WriteLine(manifestPath);

            foreach (var fe in result.OrderedFiles)
                Console.WriteLine(fe.Filename);

            result.WriteToDirectory(path, manifestPrefix);

            if (configuration.ArchiveCreator.PackScripts.GetValueOrDefault(false))
                ArchiveCreator.CreateArchiveFromManifest(variables, configuration, Path.Combine(path, manifestPath));
        }

        public virtual SolutionBuilder.BuildResult ProcessBuildResult (
            VariableSet variables, Configuration configuration, SolutionBuilder.BuildResult buildResult
        ) {
            return buildResult;
        }
    }
}
