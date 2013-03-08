﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using JSIL.Internal;
using JSIL.Translator;
using NUnit.Framework;
using MethodInfo = System.Reflection.MethodInfo;

namespace JSIL.Tests {
    public class ComparisonTest : IDisposable {
        public float JavascriptExecutionTimeout = 30.0f;

        public static readonly Regex ElapsedRegex = new Regex(
            @"// elapsed: (?'elapsed'[0-9]+(\.[0-9]*)?)",
            RegexOptions.Compiled | RegexOptions.ExplicitCapture
        );
        public static readonly Regex ExceptionRegex = new Regex(
            @"(// EXCEPTION:)(?'errorText'.*)(// STACK:(?'stack'.*))(// ENDEXCEPTION)",
            RegexOptions.Compiled | RegexOptions.ExplicitCapture | RegexOptions.Singleline
        );

        public static readonly string TestSourceFolder;
        public static readonly string JSShellPath;
        public static readonly string LoaderJSPath;
        public static readonly string EvaluatorSetupCode;

        public string StartupPrologue;

        public readonly TypeInfoProvider TypeInfo;
        public readonly AssemblyCache AssemblyCache;
        public readonly string[] StubbedAssemblies;
        public readonly string OutputPath;
        public readonly Assembly Assembly;
        public readonly TimeSpan CompilationElapsed;
        public readonly EvaluatorPool EvaluatorPool;

        protected bool? MainAcceptsArguments;

        static ComparisonTest () {
            var testAssembly = typeof(ComparisonTest).Assembly;
            var assemblyPath = Path.GetDirectoryName(Util.GetPathOfAssembly(testAssembly));

            TestSourceFolder = Path.GetFullPath(Path.Combine(assemblyPath, @"..\Tests\"));
            JSShellPath = Path.GetFullPath(Path.Combine(assemblyPath, @"..\Upstream\SpiderMonkey\js.exe"));

            var librarySourceFolder = Path.GetFullPath(Path.Combine(TestSourceFolder, @"..\Libraries\"));

            LoaderJSPath = Path.Combine(librarySourceFolder, @"JSIL.js");

            EvaluatorSetupCode = String.Format(
    @"var jsilConfig = {{
        libraryRoot: {1},
        environment: 'spidermonkey_shell'
    }}; load({0});",
             Util.EscapeString(LoaderJSPath),
             Util.EscapeString(librarySourceFolder)
           );
        }

        public static string MapSourceFileToTestFile (string sourceFile) {
            return Regex.Replace(
                sourceFile, "(\\.cs|\\.vb|\\.exe|\\.dll)$", "$0.js"
            );
        }

        public ComparisonTest (
            EvaluatorPool pool,
            string filename, string[] stubbedAssemblies = null,
            TypeInfoProvider typeInfo = null, AssemblyCache assemblyCache = null
        )
            : this(
                  pool,
                  new[] { filename },
                  Path.Combine(
                      TestSourceFolder,
                      MapSourceFileToTestFile(filename)
                  ),
                  stubbedAssemblies, typeInfo, assemblyCache
              ) {
        }

        public ComparisonTest (
            EvaluatorPool pool,
            IEnumerable<string> filenames, string outputPath,
            string[] stubbedAssemblies = null, TypeInfoProvider typeInfo = null,
            AssemblyCache assemblyCache = null
        ) {
            var started = DateTime.UtcNow.Ticks;
            OutputPath = outputPath;
            EvaluatorPool = pool;

            var extensions = (from f in filenames select Path.GetExtension(f).ToLower()).Distinct().ToArray();
            var absoluteFilenames = (from f in filenames select Path.Combine(TestSourceFolder, f));

            if (extensions.Length != 1)
                throw new InvalidOperationException("Mixture of different source languages provided.");

            var assemblyNamePrefix = Path.GetDirectoryName(outputPath).Split(new char[] { '\\', '/' }).Last();
            var assemblyName = Path.Combine(
                assemblyNamePrefix,
                Path.GetFileName(outputPath).Replace(".js", "")
            );

            switch (extensions[0]) {
                case ".cs":
                    Assembly = CompilerUtil.CompileCS(absoluteFilenames, assemblyName);
                    break;
                case ".vb":
                    Assembly = CompilerUtil.CompileVB(absoluteFilenames, assemblyName);
                    break;
                case ".exe":
                case ".dll":
                    var fns = absoluteFilenames.ToArray();
                    if (fns.Length > 1)
                        throw new InvalidOperationException("Multiple binary assemblies provided.");

                    Assembly = Assembly.LoadFile(fns[0]);
                    break;
                default:
                    throw new ArgumentException("Unsupported source file type for test");
            }

            if (typeInfo != null)
                typeInfo.ClearCaches();

            StubbedAssemblies = stubbedAssemblies;
            TypeInfo = typeInfo;
            AssemblyCache = assemblyCache;

            var ended = DateTime.UtcNow.Ticks;
            CompilationElapsed = TimeSpan.FromTicks(ended - started);
        }

        public static string GetTestRunnerLink (string testFile) {
            var rootPath = Path.GetFullPath(Path.Combine(
                Path.GetDirectoryName(LoaderJSPath),
                @"..\"
            ));

            var uri = new Uri(
                Path.Combine(rootPath, "test_runner.html"), UriKind.Absolute
            );

            return String.Format(
                "{0}#{1}", uri,
                MapSourceFileToTestFile(Path.GetFullPath(testFile))
                    .Replace(rootPath, "")
                    .Replace("\\", "/")
            );
        }

        public void Dispose () {
        }

        protected MethodInfo GetTestMethod () {
            var entryPoint = Assembly.EntryPoint;

            if (entryPoint == null) {
                var program = Assembly.GetType("Program");
                if (program == null)
                    throw new Exception("Test missing 'Program' main class");

                var testMethod = program.GetMethod("Main");
                if (testMethod == null)
                    throw new Exception("Test missing 'Main' method of 'Program' main class");

                entryPoint = testMethod;
            }

            MainAcceptsArguments = entryPoint.GetParameters().Length > 0;

            return entryPoint;
        }

        public string RunCSharp (string[] args, out long elapsed) {
            TextWriter oldStdout = null;

            using (var sw = new StringWriter()) {
                oldStdout = Console.Out;
                try {
                    oldStdout.Flush();
                    Console.SetOut(sw);

                    var testMethod = GetTestMethod();
                    long startedCs = DateTime.UtcNow.Ticks;

                    if (MainAcceptsArguments.Value) {
                        testMethod.Invoke(null, new object[] { args });
                    } else {
                        if ((args != null) && (args.Length > 0))
                            throw new ArgumentException("Test case does not accept arguments");

                        testMethod.Invoke(null, new object[] { });
                    }

                    long endedCs = DateTime.UtcNow.Ticks;

                    elapsed = endedCs - startedCs;
                    sw.Flush();
                    return sw.ToString();
                } finally {
                    Console.SetOut(oldStdout);
                }
            }
        }

        public static Configuration MakeDefaultConfiguration () {
            return new Configuration {
                FrameworkVersion = 4.0,
                IncludeDependencies = false,
                ApplyDefaults = false
            };
        }

        public TOutput Translate<TOutput> (
            Func<TranslationResult, TOutput> processResult,
            Func<Configuration> makeConfiguration = null
        ) {
            Configuration configuration;

            if (makeConfiguration != null)
                configuration = makeConfiguration();
            else
                configuration = MakeDefaultConfiguration();

            if (StubbedAssemblies != null)
                configuration.Assemblies.Stubbed.AddRange(StubbedAssemblies);

            TOutput result;

            using (var translator = new JSIL.AssemblyTranslator(configuration, TypeInfo, null, AssemblyCache)) {
                var assemblyPath = Util.GetPathOfAssembly(Assembly);

                var translationResult = translator.Translate(
                    assemblyPath, TypeInfo == null
                );

                AssemblyTranslator.GenerateManifest(translator.Manifest, assemblyPath, translationResult);

                result = processResult(translationResult);

                // If we're using a preconstructed type information provider, we need to remove the type information
                //  from the assembly we just translated
                if (TypeInfo != null) {
                    Assert.AreEqual(1, translationResult.Assemblies.Count);
                    TypeInfo.Remove(translationResult.Assemblies.ToArray());
                }

                // If we're using a preconstructed assembly cache, make sure the test case assembly didn't get into
                //  the cache, since that would leak memory.
                if (AssemblyCache != null) {
                    AssemblyCache.TryRemove(Assembly.FullName);
                }
            }

            return result;
        }

        public string GenerateJavascript (
            string[] args, out string generatedJavascript, out long elapsedTranslation,
            Func<Configuration> makeConfiguration = null
        ) {

            var translationStarted = DateTime.UtcNow.Ticks;

            string translatedJs = Translate(
                (tr) => tr.WriteToString(), makeConfiguration
            );

            elapsedTranslation = DateTime.UtcNow.Ticks - translationStarted;

            var testMethod = GetTestMethod();
            var declaringType = JSIL.Internal.Util.EscapeIdentifier(
                testMethod.DeclaringType.FullName, Internal.EscapingMode.TypeIdentifier
            );

            string argsJson;

            if (MainAcceptsArguments.Value) {
                var jsonSerializer = new DataContractJsonSerializer(typeof(string[]));
                using (var ms2 = new MemoryStream()) {
                    jsonSerializer.WriteObject(ms2, args);
                    argsJson = Encoding.UTF8.GetString(ms2.GetBuffer(), 0, (int)ms2.Length);
                }
            } else {
                if ((args != null) && (args.Length > 0))
                    throw new ArgumentException("Test case does not accept arguments");

                argsJson = "";
            }

            var invocationJs = String.Format(
                "function runTestCase (timeout, dateNow) {{\r\n" +
                "  JSIL.ThrowOnUnimplementedExternals = true;\r\n" +
                "  timeout({0});\r\n" +
                "  var started = dateNow();\r\n" +
                "  var testAssembly = JSIL.GetAssembly({1}, true);\r\n" +
                "  testAssembly.{2}.{3}({4});\r\n" +
                "  var ended = dateNow();\r\n" +
                "  return (ended - started);\r\n" +
                "}}",
                JavascriptExecutionTimeout,
                Util.EscapeString(testMethod.Module.Assembly.FullName),
                declaringType, Util.EscapeIdentifier(testMethod.Name),
                argsJson
            );

            generatedJavascript = translatedJs;

            var tempFilename = Path.GetTempFileName();
            File.WriteAllText(tempFilename, translatedJs + Environment.NewLine + invocationJs);

            var jsFile = OutputPath;
            if (File.Exists(jsFile))
                File.Delete(jsFile);
            File.Copy(tempFilename, jsFile);

            File.Delete(tempFilename);

            return OutputPath;
        }

        public string RunJavascript (
            string[] args, out string generatedJavascript, out long elapsedTranslation, out long elapsedJs,
            Func<Configuration> makeConfiguration = null
        ) {
            var tempFilename = GenerateJavascript(args, out generatedJavascript, out elapsedTranslation, makeConfiguration);

            using (var evaluator = EvaluatorPool.Get()) {
                var startedJs = DateTime.UtcNow.Ticks;
                var sentinelStart = "// Test output begins here //";
                var sentinelEnd = "// Test output ends here //";
                var elapsedPrefix = "// elapsed: ";

                StartupPrologue = String.Format("contentManifest['Test'] = [['Script', {0}]]; " +
                    "function runMain () {{ " +
                    "print({1}); try {{ var elapsedTime = runTestCase(timeout, dateNow); }} catch (exc) {{ reportException(exc); }} print({2}); print({3} + elapsedTime);" +
                    "}}; shellStartup();",
                    Util.EscapeString(tempFilename),
                    Util.EscapeString(sentinelStart),
                    Util.EscapeString(sentinelEnd),
                    Util.EscapeString(elapsedPrefix)
                );

                evaluator.WriteInput(StartupPrologue);

                evaluator.Join();

                long endedJs = DateTime.UtcNow.Ticks;
                elapsedJs = endedJs - startedJs;

                if (evaluator.ExitCode != 0) {
                    var _stdout = (evaluator.StandardOutput ?? "").Trim();
                    var _stderr = (evaluator.StandardError ?? "").Trim();

                    var exceptions = new List<JavaScriptException>();

                    var exceptionMatches = ExceptionRegex.Matches(_stdout);
                    foreach (Match match in exceptionMatches) {
                        var errorText = match.Groups["errorText"].Value;
                        string stackText = null;

                        if (match.Groups["stack"].Success)
                            stackText = match.Groups["stack"].Value;

                        var exception = new JavaScriptException(errorText, stackText);
                        exceptions.Add(exception);
                    }

                    throw new JavaScriptEvaluatorException(
                        evaluator.ExitCode, _stdout, _stderr, exceptions.ToArray()
                    );
                }

                var stdout = evaluator.StandardOutput;

                if (stdout != null) {
                    var m = ElapsedRegex.Match(stdout);
                    if (m.Success) {
                        elapsedJs = TimeSpan.FromMilliseconds(
                            double.Parse(m.Groups["elapsed"].Value)
                        ).Ticks;
                        stdout = stdout.Replace(m.Value, "");
                    }
                }

                // Strip spurious output from the JS.exe REPL and from the standard libraries
                if (stdout != null) {
                    var startOffset = stdout.IndexOf(sentinelStart);

                    if (startOffset >= 0) {
                        startOffset += sentinelStart.Length;

                        // End sentinel might not be there if the test case calls quit().
                        var endOffset = stdout.IndexOf(sentinelEnd, startOffset);
                        if (endOffset >= 0) {
                            stdout = stdout.Substring(startOffset, endOffset - startOffset);
                        } else {
                            stdout = stdout.Substring(startOffset);
                        }
                    }
                }

                return stdout ?? "";
            }
        }

        public void Run (string[] args = null, Func<Configuration> makeConfiguration = null) {
            var signals = new[] {
                    new ManualResetEventSlim(false), new ManualResetEventSlim(false)
                };
            var generatedJs = new string[1];
            var errors = new Exception[2];
            var outputs = new string[2];
            var elapsed = new long[3];

            args = args ?? new string[0];

            ThreadPool.QueueUserWorkItem((_) => {
                var oldCulture = Thread.CurrentThread.CurrentCulture;
                try {
                    Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
                    outputs[0] = RunCSharp(args, out elapsed[0]).Replace("\r", "").Trim();
                } catch (Exception ex) {
                    errors[0] = ex;
                } finally {
                    Thread.CurrentThread.CurrentCulture = oldCulture;
                }
                signals[0].Set();
            });

            ThreadPool.QueueUserWorkItem((_) => {
                try {
                    outputs[1] = RunJavascript(args, out generatedJs[0], out elapsed[1], out elapsed[2], makeConfiguration: makeConfiguration).Replace("\r", "").Trim();
                } catch (Exception ex) {
                    errors[1] = ex;
                }
                signals[1].Set();
            });

            signals[0].Wait();
            signals[1].Wait();

            try {
                if (errors[0] != null)
                    throw new Exception("C# test failed", errors[0]);
                else if (errors[1] != null)
                    throw errors[1];
                else
                    Assert.AreEqual(outputs[0], outputs[1]);

                Console.WriteLine(
                    "passed: CL:{0:0000}ms TR:{2:0000}ms C#:{1:0000}ms JS:{3:0000}ms",
                    CompilationElapsed.TotalMilliseconds,
                    TimeSpan.FromTicks(elapsed[0]).TotalMilliseconds,
                    TimeSpan.FromTicks(elapsed[1]).TotalMilliseconds,
                    TimeSpan.FromTicks(elapsed[2]).TotalMilliseconds
                );
            } catch (Exception ex) {
                Console.WriteLine("failed: " + ex.Message + " " + (ex.InnerException == null ? "" : ex.InnerException.Message));

                Console.WriteLine("// {0}", GetTestRunnerLink(OutputPath));

                if (outputs[0] != null) {
                    Console.WriteLine("// C# output begins //");
                    Console.WriteLine(outputs[0]);
                }
                if (outputs[1] != null) {
                    Console.WriteLine("// JavaScript output begins //");
                    Console.WriteLine(outputs[1]);
                }
                if (generatedJs[0] != null) {
                    Console.WriteLine("// Generated javascript begins here //");
                    Console.WriteLine(generatedJs[0]);
                    Console.WriteLine("// Generated javascript ends here //");
                }

                throw;
            }
        }
    }
}