﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using JSIL.Internal;
using JSIL.Translator;
using Microsoft.CSharp;
using System.CodeDom.Compiler;
using System.IO;
using System.Diagnostics;
using System.Threading;

namespace JSIL.Try {
    public class CompiledSnippet {
        public string JavaScript;
        public string OriginalSource;
        public string EntryPoint;
        public string Warnings;

        // Seconds
        public double CompileElapsed;
        public double TranslateElapsed;
    }

    public static class SnippetCompiler {
        public const int MaxPendingCompiles = 4;

        public static int PendingCompiles = 0;
        public static readonly AssemblyCache AssemblyCache = new AssemblyCache();
        public static volatile TypeInfoProvider TypeInfo = null;

        public static readonly string[] AssemblyReferences = new[] {
            "mscorlib.dll", "System.dll", 
            "System.Core.dll", "System.Xml.dll", 
            "Microsoft.CSharp.dll", "JSIL.Meta.dll"
        };

        /// <summary>
        /// Compiles the provided C# and then translates it into JavaScript.
        /// On success, returns the JS. On failure, throws.
        /// </summary>
        public static CompiledSnippet Compile (string csharp) {
            if (PendingCompiles >= MaxPendingCompiles)
                throw new Exception(String.Format(
                    "Sorry, the server is currently busy compiling code for {0} people. Please try again later."
                ));

            Interlocked.Increment(ref PendingCompiles);

            try {
                var result = new CompiledSnippet {
                    OriginalSource = csharp
                };

                var tempPath = Path.Combine(Path.GetTempPath(), "JSIL.Try");

                if (Directory.Exists(tempPath)) {
                    try {
                        Directory.Delete(tempPath, true);
                    } catch (Exception exc) {
                        Console.WriteLine("Failed to empty temporary directory: {0}", exc.Message);
                    }
                }

                if (!Directory.Exists(tempPath))
                    Directory.CreateDirectory(tempPath);

                Assembly resultAssembly = null;
                string resultPath = null;
                string compilerOutput = null;

                using (var provider = new CSharpCodeProvider(new Dictionary<string, string>() { 
                    { "CompilerVersion", "v4.0" } 
                })) {

                    var parameters = new CompilerParameters(
                        AssemblyReferences
                    ) {
                        CompilerOptions = "",
                        GenerateExecutable = true,
                        GenerateInMemory = false,
                        IncludeDebugInformation = true,
                        TempFiles = new TempFileCollection(tempPath, false)
                    };

                    long compileStarted = DateTime.UtcNow.Ticks;
                    var results = provider.CompileAssemblyFromSource(parameters, csharp);
                    compilerOutput = String.Join(Environment.NewLine, results.Output.OfType<string>().ToArray());

                    if (results.Errors.Count > 0) {
                        throw new Exception(String.Format(
                            "Compile failed with {0} error(s):{1}{2}{1}{3}",
                            results.Errors.Count, Environment.NewLine,
                            compilerOutput ?? "",
                            String.Join(
                                Environment.NewLine,
                                (from CompilerError err in results.Errors select err.ToString()).ToArray()
                            )
                        ));
                    }

                    resultAssembly = results.CompiledAssembly;
                    resultPath = results.PathToAssembly;

                    result.CompileElapsed = TimeSpan.FromTicks(DateTime.UtcNow.Ticks - compileStarted).TotalSeconds;
                }

                if ((resultPath == null) || !File.Exists(resultPath)) {
                    throw new Exception("Compile failed." + Environment.NewLine + (compilerOutput ?? ""));
                }

                var translatorConfiguration = new Configuration {
                    ApplyDefaults = false,
                    Assemblies = {
                        Stubbed = {
                            "mscorlib,",
                            "System.*",
                            "Microsoft.*"
                        },
                        Ignored = {
                            "Microsoft.VisualC,",
                            "Accessibility,",
                            "SMDiagnostics,",
                            "System.EnterpriseServices,",
                            "JSIL.Meta,"
                        }
                    },
                    FrameworkVersion = 4.0,
                    GenerateSkeletonsForStubbedAssemblies = false,
                    GenerateContentManifest = false,
                    IncludeDependencies = false,
                    UseSymbols = true,
                    UseThreads = false
                };

                var translatorOutput = new StringBuilder();

                var typeInfo = TypeInfo;

                using (var translator = new AssemblyTranslator(
                    translatorConfiguration,
                    // Reuse the cached type info provider, if one exists.
                    typeInfo,
                    // Can't reuse a manifest meaningfully here.
                    null,
                    // Reuse the assembly cache so that mscorlib doesn't get loaded every time.
                    AssemblyCache
                )) {
                    translator.CouldNotDecompileMethod += (s, exception) => {
                        lock (translatorOutput)
                            translatorOutput.AppendFormat(
                                "Could not decompile method '{0}': {1}{2}",
                                s, exception.Message, Environment.NewLine
                            );
                    };

                    translator.CouldNotResolveAssembly += (s, exception) => {
                        lock (translatorOutput)
                            translatorOutput.AppendFormat(
                                "Could not resolve assembly '{0}': {1}{2}",
                                s, exception.Message, Environment.NewLine
                            );
                    };

                    translator.Warning += (s) => {
                        lock (translatorOutput)
                            translatorOutput.AppendLine(s);
                    };

                    var translateStarted = DateTime.UtcNow.Ticks;
                    var translationResult = translator.Translate(resultPath, true);

                    AssemblyTranslator.GenerateManifest(
                        translator.Manifest, Path.GetDirectoryName(resultPath), translationResult
                    );

                    result.EntryPoint = String.Format(
                        "{0}.{1}.{2}",
                        translator.Manifest.GetPrivateToken(resultAssembly.FullName).IDString,
                        GetFullName(resultAssembly.EntryPoint.DeclaringType),
                        resultAssembly.EntryPoint.Name
                    );

                    result.Warnings = translatorOutput.ToString().Trim();
                    result.TranslateElapsed = TimeSpan.FromTicks(DateTime.UtcNow.Ticks - translateStarted).TotalSeconds;
                    result.JavaScript = translationResult.WriteToString();

                    if (typeInfo != null) {
                        // Remove the temporary assembly from the type info provider.
                        typeInfo.Remove(translationResult.Assemblies.ToArray());
                    } else {
                        // We didn't have a type info provider to reuse, so store the translator's.
                        typeInfo = translator.GetTypeInfoProvider();
                        // We need to do a compare-exchange since another thread may have already made a provider.
                        Interlocked.CompareExchange(ref TypeInfo, typeInfo, null);
                    }

                    /*
                    result.Warnings += String.Format(
                        "{0}TypeInfo.Count = {1}{0}AssemblyCache.Count = {2}{0}",
                        Environment.NewLine, TypeInfo.Count, AssemblyCache.Count
                    );
                     */
                }

                return result;
            } finally {
                Interlocked.Decrement(ref PendingCompiles);
            }
        }

        static string GetFullName (Type type) {
            if (type.DeclaringType != null)
                return String.Format("{0}_{1}", GetFullName(type.DeclaringType), type.Name);
            else if (!String.IsNullOrWhiteSpace(type.Namespace))
                return String.Format("{0}.{1}", type.Namespace, type.Name);
            else
                return type.Name;
        }
    }
}
