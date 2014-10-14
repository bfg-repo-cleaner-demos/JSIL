using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Spidermonkey;
#if INPROCESS_JS
using Spidermonkey.Managed;
#endif

namespace JSIL.Tests {
#if INPROCESS_JS
    public class InProcessEvaluatorPool : IDisposable {
        public const int Capacity = 2;

        public readonly Action<InProcessEvaluator> Initializer;

        private readonly ConcurrentBag<InProcessEvaluator> Evaluators = new ConcurrentBag<InProcessEvaluator>();
        private readonly AutoResetEvent EvaluatorReadySignal = new AutoResetEvent(false);
        private readonly AutoResetEvent WakeSignal = new AutoResetEvent(false);
        private readonly ConcurrentQueue<InProcessEvaluator> DisposeQueue = new ConcurrentQueue<InProcessEvaluator>();
        private readonly Thread PoolManager;

        private volatile int IsDisposed = 0;

        public InProcessEvaluatorPool (Action<InProcessEvaluator> initializer) {
            Initializer = initializer;

            PoolManager = new Thread(ThreadProc);
            PoolManager.Priority = ThreadPriority.AboveNormal;
            PoolManager.IsBackground = true;
            PoolManager.Name = "Evaluator Pool Manager";
            PoolManager.Start();
        }

        ~InProcessEvaluatorPool () {
            Dispose();
        }

        public void Dispose () {
            if (Interlocked.CompareExchange(ref IsDisposed, 1, 0) != 0)
                return;

            GC.SuppressFinalize(this);

            // The pool manager might dispose the signal before we get to it.
            try {
                WakeSignal.Set();
            } catch {
            }

            if (!PoolManager.Join(100))
                throw new Exception("Pool manager thread hung");
        }

        public InProcessEvaluator Get () {
            InProcessEvaluator result;

            var started = DateTime.UtcNow.Ticks;

            while (!Evaluators.TryTake(out result)) {
                WakeSignal.Set();
                EvaluatorReadySignal.WaitOne();
            }

            WakeSignal.Set();

            var ended = DateTime.UtcNow.Ticks;
            // Console.WriteLine("Took {0:0000}ms to get an evaluator", TimeSpan.FromTicks(ended - started).TotalMilliseconds);

            return result;
        }

        private InProcessEvaluator CreateEvaluator () {
            var result = new InProcessEvaluator();
            Initializer(result);

            return result;
        }

        public void QueueDispose (InProcessEvaluator evaluator) {
            DisposeQueue.Enqueue(evaluator);
            WakeSignal.Set();
        }

        private void FlushDisposeQueue () {
            // HACK
            return;

            InProcessEvaluator toDispose;
            while (DisposeQueue.TryDequeue(out toDispose))
                toDispose.Internal_Dispose();
        }

        private void ThreadProc () {
            try {
                while (IsDisposed == 0) {
                    FlushDisposeQueue();

                    while (Evaluators.Count < Capacity)
                        Evaluators.Add(CreateEvaluator());

                    EvaluatorReadySignal.Set();
                    WakeSignal.WaitOne();
                }
            } finally {
                EvaluatorReadySignal.Dispose();
                WakeSignal.Dispose();

                // HACK
                /*
                InProcessEvaluator evaluator;
                while (Evaluators.TryTake(out evaluator))
                    evaluator.Internal_Dispose();
                 */

                FlushDisposeQueue();
            }
        }
    }

    public class InProcessEvaluator {
        public readonly JSRuntime Runtime;
        public readonly JSContext Context;
        private JSRequest Request;
        public readonly JSGlobalObject Global;
        private JSCompartmentEntry Entry;
        private int LoadDepth = 0;
        private bool Trace = false;

        public readonly StringBuilder StdOut = new StringBuilder();
        public readonly StringBuilder StdErr = new StringBuilder();

        public InProcessEvaluator () {
            Runtime = new JSRuntime(1024 * 1024 * 32);
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

        public void Enter () {
            Request = Context.Request();
            Entry = Context.EnterCompartment(Global);
        }

        public void Leave () {
            Entry.Dispose();
            Request.Dispose();
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

        internal void Internal_Dispose () {
            Leave();
            Global.Dispose();
            Context.Dispose(false);
            Runtime.Dispose();
        }
    }
#endif
}
