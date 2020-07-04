using System;
using System.Collections.Generic;
using System.Linq;
using Mono.CSharp;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Continuous.Server
{
	/// <summary>
	/// Evaluates expressions using the mono C# REPL.
	/// This method is thread safe so you can call it from anywhere.
	/// </summary>
	public class VMBase : IVM
	{
		const string EvalAssemblyPrefix = "eval-";

		readonly object mutex = new object ();
		readonly Printer printer = new Printer ();

		protected Evaluator Evaluator;

		public EvalResult Eval (EvalRequest code, TaskScheduler mainScheduler, CancellationToken token)
		{
			var r = new EvalResult ();
			Task.Factory.StartNew (async () =>
			{
				r = await EvalOnMainThread (code, token);
			}, token, TaskCreationOptions.None, mainScheduler).Wait ();
			return r;
		}

		async Task<EvalResult> EvalOnMainThread (EvalRequest code, CancellationToken token)
		{
			var sw = new System.Diagnostics.Stopwatch ();

			object result = null;
			bool hasResult = false;
            bool failed = false;

            // be ready to capture assemblies as they are produced by the evaluator;
            var assemblies = new List<Assembly>();
            void OnAssemblyLoad(object sender, AssemblyLoadEventArgs args) => assemblies.Add(args.LoadedAssembly);

            //lock (mutex) {
				await InitIfNeeded ();

				Log ("EVAL ON THREAD {0}", System.Threading.Thread.CurrentThread.ManagedThreadId);

				printer.Messages.Clear ();

				sw.Start ();

                var shouldInstantiate = ShouldInstantiate(code);

                try
                {
                    AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;

                    if (!string.IsNullOrEmpty (code.Declarations))
					{
						Evaluator.Evaluate (code.Declarations, out result, out hasResult);
					}
					if (!string.IsNullOrEmpty (code.ValueExpression) && shouldInstantiate)
					{
						Evaluator.Evaluate (code.ValueExpression, out result, out hasResult);
					}

                    AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad;

                }
                catch (InternalErrorException ex1) {
                    failed = true;
					Evaluator = null; // Force re-init
                    AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad;
                }
                catch (Exception ex) {
					// Sometimes Mono.CSharp fails when constructing failure messages
					if (ex.StackTrace.Contains ("Mono.CSharp.InternalErrorException")) {
						Evaluator = null; // Force re-init
					}
                    failed = true;
                    printer.AddError (ex);
                    AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad;
                }

                sw.Stop ();

				Log ("END EVAL ON THREAD {0}", System.Threading.Thread.CurrentThread.ManagedThreadId);
			//}

			var primaryTypeName = code.ValueExpression.Replace("new ", "").Replace("()", "").Replace(";", "").Trim();
			var newTypes = GetTypesFromAssemblies(assemblies);
			var primaryType = newTypes.FirstOrDefault(t => t.FullName.EndsWith(primaryTypeName));

			return new EvalResult {
				Messages = printer.Messages.ToArray (),
				Duration = sw.Elapsed,
				Result = result,
				HasResult = hasResult,
                Failed = failed,
				NewTypes = newTypes,
				PrimaryType = primaryType,
			};
		}

		async Task InitIfNeeded()
		{
			if (Evaluator == null) {

				Log ("INIT EVAL");

                var settings = new CompilerSettings
                {
                    GenerateDebugInfo = true,
                    Version = LanguageVersion.Experimental
                };

				settings.AddConditionalSymbol ("__Continuous__");
				settings.AddConditionalSymbol ("DEBUG");
                ApplyCompilerSettings(settings);
				var context = new CompilerContext (settings, printer);
				Evaluator = new Evaluator (context);

				//
				// Add References to get UIKit, etc. Also add a hook to catch dynamically loaded assemblies.
				//
				AppDomain.CurrentDomain.AssemblyLoad += (_, e) => {
                    if (!ShouldAddAssemblyReference(e.LoadedAssembly))
                        return;

					Log ("DYNAMIC REF {0}", e.LoadedAssembly);
					AddReference (e.LoadedAssembly);
				};

				foreach (var a in AppDomain.CurrentDomain.GetAssemblies ()) {
                    if (!ShouldAddAssemblyReference(a))
                        continue;

                    Log ("STATIC REF {0}", a);
                    await Task.Run(async () => AddReference(a));
				}

				//
				// Add default namespaces
				//
				object res;
				bool hasRes;
				Evaluator.Evaluate ("using System;", out res, out hasRes);
				Evaluator.Evaluate ("using System.Collections.Generic;", out res, out hasRes);
				Evaluator.Evaluate ("using System.Linq;", out res, out hasRes);
				Init ();
			}
		}

        protected virtual bool ShouldAddAssemblyReference(Assembly asm)
        {
            var isEvalAssembly = asm.FullName.StartsWith(EvalAssemblyPrefix, StringComparison.Ordinal);

            // don't reference results of previous evaluations
            return !isEvalAssembly;
        }

        protected virtual void ApplyCompilerSettings(CompilerSettings settings)
        {

        }

        protected virtual void Init()
        {

        }

        protected virtual bool ShouldInstantiate(EvalRequest code) => true;

		void AddReference (Assembly a)
		{
			//
			// Avoid duplicates of what comes prereferenced with Mono.CSharp.Evaluator
			// or ones that cause problems.
			//
			var name = a.GetName ().Name;
			if (name == "mscorlib" || name == "System" || name == "System.Core" ||
			    name == "Xamarin.Interactive" || name == "Xamarin.Interactive.iOS")
				return;

			//
			// TODO: Should this lock if called from the AssemblyLoad event?
			//
			Evaluator.ReferenceAssembly (a);
		}

		void Log (string format, params object[] args)
		{
			#if DEBUG
			Log (string.Format (format, args));
			#endif
		}

		void Log (string msg)
		{
			#if DEBUG
			System.Diagnostics.Debug.WriteLine (msg);
			#endif
		}
        
        public List<Type> GetTypesFromAssemblies(List<Assembly> assemblies)
        {
            return assemblies
                .Where(x => x.FullName.StartsWith("eval-"))
                .SelectMany(x => x.GetTypes())
                .ToList();
        }

        class Printer : ReportPrinter
		{
			public readonly List<EvalMessage> Messages = new List<EvalMessage> ();
			public override void Print (AbstractMessage msg, bool showFullPath)
			{
				var line = 0;
				var column = 0;
				try {
					line = msg.Location.Row;
					column = msg.Location.Column;
				} catch {
					//Log (ex);
				}
				var m = new EvalMessage {
					MessageType = msg.MessageType,
					Text = msg.Text,
					Line = line,
					Column = column,
				};

				Messages.Add (m);

				//
				// Print it to the console if there's an error
				//
				if (msg.MessageType == "error") {
					var tm = msg.Text;
					System.Threading.ThreadPool.QueueUserWorkItem (_ =>
						Console.WriteLine ("ERROR: {0}", tm));
				}
			}
			public void AddError (Exception ex)
			{
				var text = ex.ToString ();

				var m = new EvalMessage {
					MessageType = "error",
					Text = text,
					Line = 0,
					Column = 0,
				};

				Messages.Add (m);

				//
				// Print it to the console if there's an error
				//
				System.Threading.ThreadPool.QueueUserWorkItem (_ =>
					Console.WriteLine ("EVAL ERROR: {0}", text));
			}
		}
	}
}

