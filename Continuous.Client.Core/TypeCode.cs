using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
#if MONODEVELOP
using ICSharpCode.NRefactory;
using ICSharpCode.NRefactory.CSharp;
using ICSharpCode.NRefactory.CSharp.Resolver;
#endif

namespace Continuous.Client
{
	public class LinkedCode
	{
		public readonly string Declarations;
		public readonly string ValueExpression;
		public readonly TypeCode[] Types;
		public readonly string CacheKey;
		public LinkedCode (string declarations, string valueExpression, TypeCode[] types, TypeCode mainType)
		{
			Declarations = declarations??"";
			ValueExpression = valueExpression??"";
			Types = types;

			CacheKey = mainType.UsingsAndCode + string.Join ("", types.Select (x => x.UsingsAndCode));
		}

		public string FilePath { get; set; }
	}

	public class TypeCode
	{
		// Only partial namespace support because I can't figureout how to
		// get good TypeDeclarations from SimpleTypes.

		public string FilePath { get; set; }
		public string Name = "";
		public TypeCode[] Dependencies = new TypeCode[0];
		public string[] Usings = new string[0];
		public string Code = "";
		public string RawCode = "";
		public bool CodeChanged = false;
		public DateTime CodeChangedTime = DateTime.MinValue;
		public string FullNamespace = "";
		public WatchVariable[] Watches = new WatchVariable[0];

		public string Key {
			get { return Name; }
		}

		public override string ToString ()
		{
			return Name;
		}

		public bool HasCode { get { return !string.IsNullOrWhiteSpace (Code); } }
		public bool HasNamespace { get { return !string.IsNullOrWhiteSpace (FullNamespace); } }

		static readonly Dictionary<string, TypeCode> infos = new Dictionary<string, TypeCode> ();

		public static IEnumerable<TypeCode> All {
			get { return infos.Values; }
		}

		public static void Clear ()
		{
			infos.Clear ();
		}

		public static void ClearEdits ()
		{
			foreach (var t in infos.Values) {
				t.CodeChanged = false;
			}
		}

		public static TypeCode Get (string name)
		{			
			var key = name;
			TypeCode ci;
			if (infos.TryGetValue (key, out ci)) {
				return ci;
			}

			ci = new TypeCode {
				Name = name,
			};
			infos [key] = ci;
			return ci;
		}

		public static TypeCode Set (string name, IEnumerable<string> usings, string code, IEnumerable<string> deps, string fullNamespace = "", IEnumerable<WatchVariable> watches = null)
		{
			return Set (name, usings, code, code, deps, fullNamespace, "", watches);
		}

		public static TypeCode Set (string name, IEnumerable<string> usings, string rawCode, string instrumentedCode, IEnumerable<string> deps, string filePath, string fullNamespace = "", IEnumerable<WatchVariable> watches = null)
		{
			var tc = Get (name);

			tc.FilePath = filePath;
			tc.Usings = usings.ToArray ();
			tc.Dependencies = deps.Distinct ().Select (Get).ToArray ();
			tc.FullNamespace = fullNamespace;
			tc.Watches = watches != null ? watches.ToArray () : new WatchVariable[0];

			var safeICode = instrumentedCode ?? "";
			var safeRCode = rawCode ?? "";

			if (!string.IsNullOrEmpty (safeICode)) {
				if (string.IsNullOrWhiteSpace (tc.Code)) {
					tc.Code = safeICode;
					tc.CodeChanged = false;
					tc.RawCode = safeRCode;
				} else {
					if (tc.RawCode != safeRCode) {
						tc.Code = safeICode;
						tc.RawCode = safeRCode;
						tc.CodeChanged = true;
						tc.CodeChangedTime = DateTime.UtcNow;
					}
				}
			}

			return tc;
		}

		void GetDependencies (List<TypeCode> code)
		{
			if (code.Contains (this))
				return;
			code.Add (this);
			foreach (var d in Dependencies) {
				d.GetDependencies (code);
			}
			// Move us to the back
			code.Remove (this);
			code.Add (this);
		}

		public List<TypeCode> AllDependencies {
			get {
				var codes = new List<TypeCode> ();
				GetDependencies (codes);
				return codes;
			}
		}

		public string UsingsAndCode {
			get {
				return string.Join (Environment.NewLine, Usings) + Environment.NewLine + Code;
			}
		}

		public LinkedCode GetLinkedCode (bool instantiate, string suffix = null)
		{
			var allDeps = AllDependencies.Where (x => x.HasCode).ToList ();

			var changedDeps = allDeps.Where (x => x.CodeChanged || x == this).ToList ();
			var notChangedDeps = allDeps.Where (x => !x.CodeChanged && x != this).ToList ();

			var changedDepsChanged = true;
			while (changedDepsChanged) {
				changedDepsChanged = false;
				foreach (var nc in notChangedDeps) {
					var ncDeps = nc.AllDependencies;
					var depChanged = ncDeps.Any (changedDeps.Contains);
					if (depChanged) {
						changedDeps.Add (nc);
						notChangedDeps.Remove (nc);
						changedDepsChanged = true;
						break;
					}
				}
			}
			var codes = changedDeps;

			var namespaceUsings = new[] { String.Join(" ", ConvertToUsings(FullNamespace)) };
			var usings = codes.SelectMany (x => x.Usings).Concat(namespaceUsings).Distinct ().ToList ();

			suffix = suffix ?? DateTime.UtcNow.Ticks.ToString ();

			var renames =
				codes.
				Select (x => Tuple.Create (
					new System.Text.RegularExpressions.Regex ("\\b" + x.Name + "\\b"),
					x.Name + suffix)).
				ToList ();

            var valueExpression =
                instantiate
                ? ("new " +
                    (HasNamespace ? FullNamespace + "." : "") +
                    Name + suffix + "()")
                : "";

			Func<string, string> rename = c => {
				var rc = c;
				foreach (var r in renames) {
					rc = r.Item1.Replace (rc, r.Item2);
				}
				return rc;
			};
            
			var code = string.Join(Environment.NewLine, codes.Select(x => rename(x.Code)));
			
			var sb = new StringBuilder();
			sb.Append(string.Join(Environment.NewLine, usings));
			sb.Append("\r\n");
			sb.Append(code);
			
			// roslyn friendly ver
			return new LinkedCode (
				valueExpression:
				$"new {Name}{suffix}()",
				declarations: sb.ToString(),
				types: codes.ToArray (),
				mainType: this)
			{
				FilePath = FilePath
			};
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
	}
}

