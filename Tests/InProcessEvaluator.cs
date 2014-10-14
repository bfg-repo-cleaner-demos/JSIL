using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

#if INPROCESS_JS
using Spidermonkey.Managed;
#endif

namespace JSIL.Tests {
#if INPROCESS_JS
        static class InProcessRuntimePool {
        }

        class InProcessEvaluator : IDisposable {
            public readonly JSRuntime Runtime;
            public readonly JSContext Context;
            private readonly JSRequest Request;
            public readonly JSGlobalObject Global;
            private readonly JSCompartmentEntry Entry;
            private int LoadDepth = 0;
            private bool Trace = false;

            public readonly StringBuilder StdOut = new StringBuilder();
            public readonly StringBuilder StdErr = new StringBuilder();

            public InProcessEvaluator () {
                Runtime = new JSRuntime(1024 * 1024 * 16);
                Context = new JSContext(Runtime);
                Request = Context.Request();
                Global = new JSGlobalObject(Context);
                Entry = Context.EnterCompartment(Global);

                if (!JSAPI.InitStandardClasses(Context, Global))
                    throw new Exception("Failed to initialize standard classes");

                Global.DefineFunction(
                    Context, "load", (Action<string>)Load
                );
                Global.DefineFunction(
                    Context, "print", (Action<object>)Print
                );
                Global.DefineFunction(
                    Context, "printErr", (Action<object>)PrintErr
                );
                Global.DefineFunction(
                    Context, "putstr", (Action<object>)Putstr
                );
                Global.DefineFunction(
                    Context, "timeout", NoOp
                );
            }

            private JSBool NoOp (JSContextPtr cx, uint argc, JSCallArgumentsPtr argv) {
                argv.Result = JS.Value.Undefined;

                return true;
            }

            private void Load (string filename) {
                try {
                    if (Trace)
                        Console.WriteLine("// {0}Loading {1}...", new String(' ', LoadDepth), filename);
                    LoadDepth += 1;

                    var js = File.ReadAllText(filename);

                    JSError error;
                    Context.Evaluate(
                        Global, js,
                        out error,
                        filename: filename
                    );

                    if (error != null) {
                        if (error != null)
                            throw new Exception("Error while loading " + filename, error.ToException());
                    } else {
                        if (Trace)
                            Console.WriteLine("// {0}Loaded {1}", new String(' ', LoadDepth - 1), filename);
                    }
                } finally {
                    LoadDepth -= 1;
                }
            }

            private void Print (object o) {
                StdOut.AppendLine(Convert.ToString(o));
            }

            private void PrintErr (object o) {
                StdErr.AppendLine(Convert.ToString(o));
            }

            private void Putstr (object o) {
                StdOut.Append(Convert.ToString(o));
            }

            public void Dispose () {
                Entry.Dispose();
                Global.Dispose();
                Request.Dispose();
                Context.Dispose();
                Runtime.Dispose();
            }
        }
#endif
}
