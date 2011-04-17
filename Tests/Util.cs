﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CSharp;
using System.CodeDom.Compiler;
using System.IO;
using System.Diagnostics;
using System.Runtime.Serialization.Json;
using System.Threading;
using NUnit.Framework;

namespace JSIL.Tests {
    public class JavaScriptException : Exception {
        public readonly string ErrorText;

        public JavaScriptException (int exitCode, string errorText)
            : base(String.Format("JavaScript interpreter exited with code {0}\r\n{1}", exitCode, errorText)) 
        {
            ErrorText = errorText;
        }
    }

    public static class CSharpUtil {
        public static string TempPath;

        // Attempt to clean up stray assembly files from previous test runs
        //  since the assemblies would have remained locked and undeletable 
        //  due to being loaded
        static CSharpUtil () {
            TempPath = Path.Combine(Path.GetTempPath(), "JSILTests");
            if (!Directory.Exists(TempPath))
                Directory.CreateDirectory(TempPath);

            foreach (var filename in Directory.GetFiles(TempPath))
                try {
                    File.Delete(filename);
                } catch {
                }
        }

        public static Assembly Compile (string sourceCode, out TempFileCollection temporaryFiles) {
            using (var csc = new CSharpCodeProvider(new Dictionary<string, string>() { 
                { "CompilerVersion", "v4.0" } 
            })) {

                var parameters = new CompilerParameters(new[] {
                    "mscorlib.dll", "System.Core.dll", "Microsoft.CSharp.dll",
                    typeof(JSIL.Meta.JSIgnore).Assembly.Location
                }) {
                    GenerateExecutable = true,
                    GenerateInMemory = false,
                    IncludeDebugInformation = true,
                    TempFiles = new TempFileCollection(TempPath, true)
                };

                var results = csc.CompileAssemblyFromSource(parameters, sourceCode);

                if (results.Errors.Count > 0) {
                    throw new Exception(
                        String.Join(Environment.NewLine, results.Errors.Cast<CompilerError>().Select((ce) => ce.ErrorText).ToArray())
                    );
                }

                temporaryFiles = results.TempFiles;
                return results.CompiledAssembly;
            }
        }
    }

    public class ComparisonTest : IDisposable {
        public const float JavascriptExecutionTimeout = 30.0f;

        public static readonly Regex ElapsedRegex = new Regex(
            @"runtime = (?'elapsed'[0-9]*(\.[0-9]*)?) ms", RegexOptions.Compiled | RegexOptions.ExplicitCapture
        );

        protected TempFileCollection TemporaryFiles;

        public static readonly string TestSourceFolder;
        public static readonly string JSShellPath;
        public static readonly string BootstrapJS;

        public readonly string Filename;
        public readonly Assembly Assembly;
        public readonly MethodInfo TestMethod;

        static string GetPathOfAssembly (Assembly assembly) {
            return new Uri(assembly.CodeBase).AbsolutePath.Replace("/", "\\");
        }

        static ComparisonTest () {
            var testAssembly = typeof(ComparisonTest).Assembly;
            var assemblyPath = Path.GetDirectoryName(GetPathOfAssembly(testAssembly));

            TestSourceFolder = Path.GetFullPath(Path.Combine(assemblyPath, @"..\"));
            JSShellPath = Path.GetFullPath(Path.Combine(assemblyPath, @"..\..\Upstream\SpiderMonkey\js.exe"));

            using (var resourceStream = testAssembly.GetManifestResourceStream("JSIL.Tests.bootstrap.js"))
            using (var sr = new StreamReader(resourceStream))
                BootstrapJS = sr.ReadToEnd();
        }

        public ComparisonTest (string filename) {
            Filename = Path.Combine(TestSourceFolder, filename);

            var sourceCode = File.ReadAllText(Filename);
            Assembly = CSharpUtil.Compile(sourceCode, out TemporaryFiles);

            TestMethod = Assembly.GetType("Program").GetMethod("Main");
        }

        public void Dispose () {
            foreach (string filename in TemporaryFiles)
                try {
                    File.Delete(filename);
                } catch {
                }
        }

        public string RunCSharp (string[] args, out long elapsed) {
            var oldStdout = Console.Out;
            using (var sw = new StringWriter())
                try {
                    Console.SetOut(sw);
                    long startedCs = DateTime.UtcNow.Ticks;
                    TestMethod.Invoke(null, new object[] { args });
                    long endedCs = DateTime.UtcNow.Ticks;
                    elapsed = endedCs - startedCs;
                    return sw.ToString();
                } finally {
                    Console.SetOut(oldStdout);
                }
        }

        public string RunJavascript (string[] args, out string generatedJavascript, out long elapsed) {
            var tempFilename = Path.GetTempFileName();
            var translator = new JSIL.AssemblyTranslator();
            var translatedJs = translator.Translate(GetPathOfAssembly(Assembly));
            var declaringType = JSIL.Internal.Util.EscapeIdentifier(TestMethod.DeclaringType.FullName, false);

            string argsJson;
            var jsonSerializer = new DataContractJsonSerializer(typeof(string[]));
            using (var ms = new MemoryStream()) {
                jsonSerializer.WriteObject(ms, args);
                argsJson = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
            }

            var invocationJs = String.Format(
                @"timeout({0}); {1}.Main({2});", 
                JavascriptExecutionTimeout, declaringType, argsJson
            );

            generatedJavascript = translatedJs;
            translatedJs = BootstrapJS + Environment.NewLine + translatedJs + Environment.NewLine + invocationJs;

            File.WriteAllText(tempFilename, translatedJs);

            try {
                // throw new Exception();

                var psi = new ProcessStartInfo(JSShellPath, String.Format("-j -m -f {0}", tempFilename)) {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                ManualResetEventSlim stdoutSignal, stderrSignal;
                stdoutSignal = new ManualResetEventSlim(false);
                stderrSignal = new ManualResetEventSlim(false);
                var output = new string[2];

                long startedJs = DateTime.UtcNow.Ticks;
                using (var process = Process.Start(psi)) {
                    ThreadPool.QueueUserWorkItem((_) => {
                        output[0] = process.StandardOutput.ReadToEnd();
                        stdoutSignal.Set();
                    });
                    ThreadPool.QueueUserWorkItem((_) => {
                        output[1] = process.StandardError.ReadToEnd();
                        stderrSignal.Set();
                    });

                    stdoutSignal.Wait();
                    stderrSignal.Wait();
                    process.WaitForExit();

                    if (process.ExitCode != 0)
                        throw new JavaScriptException(process.ExitCode, (output[1] ?? "").Trim());
                }

                long endedJs = DateTime.UtcNow.Ticks;
                elapsed = endedJs - startedJs;

                if (output[0] != null) {
                    var m = ElapsedRegex.Match(output[0]);
                    if (m.Success) {
                        elapsed = TimeSpan.FromMilliseconds(
                            double.Parse(m.Groups["elapsed"].Value)
                        ).Ticks;
                        output[0] = output[0].Replace(m.Value, "");
                    }
                }

                return output[0] ?? "";
            } finally {
                var jsFile = Filename.Replace(".cs", ".js");
                if (File.Exists(jsFile))
                    File.Delete(jsFile);
                File.Copy(tempFilename, jsFile);

                File.Delete(tempFilename);
            }
        }

        public void Run (params string[] args) {
            var signals = new[] {
                new ManualResetEventSlim(false), new ManualResetEventSlim(false)
            };
            var generatedJs = new string[1];
            var errors = new Exception[2];
            var outputs = new string[2];
            var elapsed = new long[2];

            ThreadPool.QueueUserWorkItem((_) => {
                try {
                    outputs[0] = RunCSharp(args, out elapsed[0]).Replace("\r", "").Trim();
                } catch (Exception ex) {
                    errors[0] = ex;
                }
                signals[0].Set();
            });

            ThreadPool.QueueUserWorkItem((_) => {
                try {
                    outputs[1] = RunJavascript(args, out generatedJs[0], out elapsed[1]).Replace("\r", "").Trim();
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
                    throw new Exception("JS test failed", errors[1]);
                else
                    Assert.AreEqual(outputs[0], outputs[1]);

                Console.WriteLine(
                    "passed: C# in {0:00.0000}s, JS in {1:00.0000}s",
                    TimeSpan.FromTicks(elapsed[0]).TotalSeconds,
                    TimeSpan.FromTicks(elapsed[1]).TotalSeconds
                );
            } catch {
                Console.WriteLine("failed");
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
