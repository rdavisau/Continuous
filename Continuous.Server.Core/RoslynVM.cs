using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Newtonsoft.Json;

namespace Continuous.Server
{
    public class RoslynVM : IVM
    {
        const string EvalAssemblyPrefix = "ℛ*";
        protected ScriptOptions Options { get; set; }
		public bool LogToConsole { get; set; }
        
        public RoslynVM(bool log = false)
        {
	        LogToConsole = log;
        }

        public EvalResult Eval(EvalRequest code, TaskScheduler mainScheduler, CancellationToken token)
        {
            var result = new EvalResult();
            
            RemoveNamespace(code);
            
            Task.Factory.StartNew(
	            async () => result = await EvalOnMainThread (code, token), 
	            token, TaskCreationOptions.None, mainScheduler)
	            .Wait (token);
			
            return result;
        }

        private readonly Regex _namespaceRegex = 
	        new Regex(@"namespace (?<ns>(\w+.?)*).*\v*.*\{", 
		        RegexOptions.Multiline | RegexOptions.Compiled);

        private void RemoveNamespace(EvalRequest request)
        {
	        var declarations = request.Declarations;
	        var valueExpression = request.ValueExpression;
	        var ns = _namespaceRegex.Match(declarations).Groups["ns"].Value;
	        var additionalUsings = String.Join(" ", ConvertToUsings(ns));
	        
	        Log($"Original Declaration:\r\n{declarations}");
	        Log($"Original Value Expression:\r\n{valueExpression}");
	        Log($"Detected Namespace:\r\n{ns}");

	        declarations = _namespaceRegex.Replace(declarations, "").Trim();
	        
	        if (!String.IsNullOrWhiteSpace(ns))
		        declarations = declarations.Substring(0, declarations.Length - 1);
    
	        declarations = $"{additionalUsings}\r\n{declarations};";
            declarations = ExtractAndRepositionUsings(declarations);
            declarations = $"#define DEBUG\r\n{declarations}";
            
	        valueExpression = valueExpression.Replace($"{ns}.", "");
	        
	        request.Declarations = declarations;
	        request.ValueExpression = valueExpression;
	        
	        Log($"Final Declaration:\r\n{request.Declarations}");
	        Log($"Final Value Expression:\r\n{request.ValueExpression}");
        }

        private Regex _usingsRegex = new Regex(@"using (?>(\w+\.?)*);", RegexOptions.Compiled | RegexOptions.Multiline);
        private Regex _usingEqualsRegex = new Regex(@"using (?>(\w+\.?)*) = (?>(\w+\.?)*);", RegexOptions.Compiled | RegexOptions.Multiline);
        private string ExtractAndRepositionUsings(string declarations)
        {
            var usingEqualses = _usingEqualsRegex.Matches(declarations);
            var usings = _usingsRegex.Matches(declarations);

            declarations = _usingEqualsRegex.Replace(declarations, String.Empty).Trim();
            declarations = _usingsRegex.Replace(declarations, String.Empty).Trim();

            var finalUsings = Enumerable.Concat(
		        usings.OfType<Match>().Select(x => x.Value),  
		        usingEqualses.OfType<Match>().Select(x => x.Value)
	        )
		    .Distinct()
            .ToList();
            
	        declarations =
		        $"{String.Join(Environment.NewLine, finalUsings)}\r\n{declarations}";
	        
	        return declarations;
        }

        private IEnumerable<string> ConvertToUsings(string ns)
        {
	        if (String.IsNullOrWhiteSpace(ns))
		        yield break;
	        
	        var parts = ns.Split('.').ToList();
	
	        while (parts.Any())
	        {
		        yield return $"using {String.Join(".", parts)};";
		        parts.RemoveAt(parts.Count - 1);
	        }
        }
        
        async Task<EvalResult> EvalOnMainThread (EvalRequest code, CancellationToken token)
        {
            var sw = new System.Diagnostics.Stopwatch();
            var errors = new List<string>();
			object result = null;
            var hasResult = false;
            var failed = false;
            
            var shouldInstantiate = ShouldInstantiate(code);
            var src = code.Declarations +
                      (shouldInstantiate
	                      ? $"\r\n{code.ValueExpression}"
	                      : "");

            await InitIfNeeded();
            
            // be ready to capture assemblies as they are produced by the evaluator;
			var assemblies = new List<Assembly>();
			void OnAssemblyLoad(object sender, AssemblyLoadEventArgs args) => assemblies.Add(args.LoadedAssembly);

			var options = Options;
            if (!String.IsNullOrWhiteSpace(code.FilePath))
                options = options.WithFilePath(code.FilePath)
                                 .WithFileEncoding(System.Text.Encoding.UTF8);
			
			Log ("EVAL ON THREAD {0}", Thread.CurrentThread.ManagedThreadId);
			sw.Start ();
			
            try
            {
                AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
                
                result = await CSharpScript.EvaluateAsync(src, GetOptions(options), cancellationToken: token);

                AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad;

            }
            catch (Exception ex) {
	            
                failed = true;
                errors.Add(ex.Message);
                AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad;
            }

            sw.Stop ();
            Log ("END EVAL ON THREAD {0}", Thread.CurrentThread.ManagedThreadId);

            var primaryTypeName = code.ValueExpression.Replace("new ", "").Replace("()", "").Replace(";", "").Trim();
            var newTypes = GetTypesFromAssemblies(assemblies);
            var primaryType = newTypes.FirstOrDefault(t => t.FullName.EndsWith(primaryTypeName));

            var ret = new EvalResult {
                Messages = errors.Select(x => new EvalMessage { Text = x } ).ToArray(),
                Duration = sw.Elapsed,
                Result = result,
                HasResult = result != null,
                NewTypes = newTypes,
                PrimaryType = primaryType,
            };
            
            if (LogToConsole)
	            Log(JsonConvert.SerializeObject(ret, Formatting.Indented));

            return ret;
        }

        protected virtual ScriptOptions GetOptions(ScriptOptions options)
        {
	        return options;
        }

        async Task InitIfNeeded()
        {
	        if (Options != null)
		        return;

	        Options =
		        ScriptOptions.Default
					.WithEmitDebugInformation(true)
			        .WithReferences(
				        AppDomain
					        .CurrentDomain
					        .GetAssemblies()
					        .Where(ShouldAddAssemblyReference)
					        .ToArray());

            var seen = new HashSet<string>();
            AppDomain.CurrentDomain.AssemblyLoad += (_, e) =>
            {
                var asm = e.LoadedAssembly;

                if (!ShouldAddAssemblyReference(asm) || seen.Contains(asm.FullName))
                    return;

                seen.Add(asm.FullName);
                Log("DYNAMIC REF {0}", asm);
                Options = Options.AddReferences(e.LoadedAssembly);
            };
        }

        protected virtual bool ShouldAddAssemblyReference(Assembly asm)
        {
            var isEvalAssembly = asm.FullName.StartsWith(EvalAssemblyPrefix, StringComparison.Ordinal);
            var isDynamic = asm.IsDynamic;
            var hasLocation = !asm.IsDynamic && !String.IsNullOrWhiteSpace(asm.Location);

            // don't reference dynamic assemblies or results of previous evaluations
            return hasLocation && (!isEvalAssembly && !isDynamic);
        }

        protected virtual void ApplyScriptOptions(ScriptOptions settings)
        {

        }

        protected virtual void Init()
        {

        }

        protected virtual bool ShouldInstantiate(EvalRequest code) => true;

        void Log (string format, params object[] args)
        {
            Log (string.Format (format, args));
        }

        void Log (string msg)
        {
	        if (LogToConsole)
		        Console.WriteLine(msg);
	        else
		        Debug.WriteLine(msg);
        }
        
        public List<Type> GetTypesFromAssemblies(List<Assembly> assemblies)
        {
            return assemblies
                .Where(x => x.FullName.StartsWith(EvalAssemblyPrefix))
                .SelectMany(x => x.GetTypes())
                .Distinct()
                .ToList();
        }
    }
}