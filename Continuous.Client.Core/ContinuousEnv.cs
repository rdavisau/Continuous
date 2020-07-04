using System;
using System.Threading.Tasks;
using System.Linq;
using System.Diagnostics;
using System.Collections.Generic;

namespace Continuous.Client
{
	public abstract partial class ContinuousEnv
	{
		public static ContinuousEnv Shared;

        static ContinuousEnv()
        {
            SetSharedPlatformEnvImpl ();
        }

		public ContinuousEnv ()
		{
			IP = Http.DefaultHost;
			Port = Http.DefaultPort;
		}

        static partial void SetSharedPlatformEnvImpl ();

		public readonly DiscoveryReceiver Discovery = new DiscoveryReceiver ();

        public string MonitorTypeName = "";

		HttpClient conn = null;
		void Connect ()
		{
			if (conn == null || conn.BaseUrl != ServerUrl) {
				conn = CreateConnection ();
			}
		}

		public string IP { get; set; }
		public int Port { get; set; }

		Uri ServerUrl {
			get {
				return new Uri ("http://" + Discovery.Resolve (IP.Trim ()) + ":" + Port);
			}
		}

		protected HttpClient CreateConnection ()
		{
			return new HttpClient (ServerUrl);
		}

		public event Action<string> Failure;
		public event Action<string> Success;

		public void Succeed (string format, params object[] args)
		{
			OnSucceed (format, args);
		}

		protected virtual void OnSucceed (string format, params object[] args)
		{
			Log (format, args);

			var a = Success;
			if (a != null)
			{
				var m = string.Format (System.Globalization.CultureInfo.CurrentUICulture, format, args);
				a (m);
			}
		}

		public void Fail (string format, params object[] args)
        {
			OnFail (format, args);
        }

		protected virtual void OnFail (string format, params object[] args)
		{
			Log (format, args);

			var a = Failure;
			if (a != null)
			{
				var m = string.Format (System.Globalization.CultureInfo.CurrentUICulture, format, args);
				a (m);
			}
		}

		protected async Task<EvalResponse> EvalForResponseAsync(string declarations, string valueExpression,
			string codeFilePath, bool showError)
		{
			Connect ();
			var r = await conn.VisualizeAsync (declarations, valueExpression, codeFilePath);
			var err = r.HasErrors;
			if (err)
			{
				var message = string.Join ("\n", r.Messages.Select (m => m.MessageType + ": " + m.Text));
				if (showError)
				{
					Fail ("{0}", message);
				}
			}
			else
			{
				if (r.WatchValues.ContainsKey (valueExpression))
				{
					Succeed ("{0} = {1}", valueExpression, r.WatchValues[valueExpression]);
				}
				else
				{
					Succeed ("{0}", valueExpression);
				}
			}
			return r;
		}

        public async Task VisualizeAsync ()
        {
            var typedecl = await FindTypeAtCursorAsync ();

            if (typedecl == null) {
                Fail ("Could not find a type at the cursor.");
                return;
            }

            EnsureMonitoring ();

            var typeName = typedecl.Name;

            MonitorTypeName = typeName;
            TargetDocument = MonoDevelop.Ide.IdeApp.Workbench.ActiveDocument;
            //			monitorNamespace = nsName;

            await SetTypesAndVisualizeMonitoredTypeAsync (forceEval: true, showError: true);
        }

        public MonoDevelop.Ide.Gui.Document TargetDocument;
        public HashSet<MonoDevelop.Ide.Gui.Document> AdditionalDocuments = new HashSet<MonoDevelop.Ide.Gui.Document>();
        public async Task AddTypeAsync()
        {
            var doc = MonoDevelop.Ide.IdeApp.Workbench.ActiveDocument;
            if (doc == null)
            {
                Log("no active doc");
                return;
            }

            AdditionalDocuments.Add(doc);

            await SetTypesAndVisualizeMonitoredTypeAsync(forceEval: true, showError: true);
        }

        protected async Task SetTypesAndVisualizeMonitoredTypeAsync (bool forceEval, bool showError)
        {
	        TypeCode.Clear();
	        
            //
            // Gobble up all we can about the types in the active document
            //
            var typeDecls = await GetTopLevelTypeDeclsForDocumentAsync(TargetDocument);

            foreach (var doc in AdditionalDocuments)
            {
                var docTypeDecls = await GetTopLevelTypeDeclsForDocumentAsync(doc);

                typeDecls = Enumerable.Concat(typeDecls, docTypeDecls).ToArray();
            }

            Debug.WriteLine("SET TYPE CODE");
            foreach (var td in typeDecls) {
                td.SetTypeCode ();
            }

            await VisualizeMonitoredTypeAsync (forceEval, showError);
        }

        bool monitoring = false;
        void EnsureMonitoring ()
        {
            if (monitoring) return;

            MonitorEditorChanges ();
            MonitorWatchChanges ();

            monitoring = true;
        }

        protected abstract void MonitorEditorChanges ();

        protected abstract Task<TypeDecl[]> GetTopLevelTypeDeclsAsync();
        protected abstract Task<TypeDecl[]> GetTopLevelTypeDeclsForDocumentAsync(object document);

        async Task<TypeDecl> FindTypeAtCursorAsync ()
        {
            var editLoc = await GetCursorLocationAsync ();
            if (!editLoc.HasValue)
                return null;
            var editTLoc = editLoc.Value;

            var selTypeDecl =
                (await GetTopLevelTypeDeclsAsync ()).
                FirstOrDefault (x => x.StartLocation <= editTLoc && editTLoc <= x.EndLocation);
            return selTypeDecl;
        }

        protected abstract Task<TextLoc?> GetCursorLocationAsync ();

        LinkedCode lastLinkedCode = null;


        public async Task VisualizeMonitoredTypeAsync(bool forceEval, bool showError)
        {
            //
            // Refresh the monitored type
            //
            if (string.IsNullOrWhiteSpace(MonitorTypeName))
                return;

            var monitoredTypeCode = TypeCode.Get(MonitorTypeName); 

            var typesWithCode =
                TypeCode.All.Where(x => x.HasCode)
                    .OrderByDescending(x => x.Name != MonitorTypeName)
                    .ToList();

            var sharedSuffix = DateTime.UtcNow.Ticks.ToString();

            foreach (var tc in typesWithCode) // new[] { monitoredTypeCode })// typesWithCode)
            {
                var isMonitoredType = tc.Key == monitoredTypeCode.Key;

                var code = await Task.Run(() => tc.GetLinkedCode(instantiate: isMonitoredType, sharedSuffix));
                OnLinkedMonitoredCode(code);

                foreach (var other in typesWithCode.Where(twc => twc != tc))
                {
                    foreach (var dep in other.AllDependencies)
                        if (dep.Name == tc.Name)
                            dep.CodeChanged = true;
                }

                if (!forceEval && lastLinkedCode != null && lastLinkedCode.CacheKey == code.CacheKey)
                {
                    continue;
                }

                //
                // Send the code to the device
                //
                try
                {
                    //
                    // Declare and Show it
                    //
                    Log(code.ValueExpression);
                    var resp = await EvalForResponseAsync(code.Declarations, code.ValueExpression, code.FilePath, showError);
                    if (resp.HasErrors)
                        return;

                    //
                    // If we made it this far, remember so we don't re-send the same
                    // thing immediately
                    //
                    lastLinkedCode = code;

                    //
                    // Update the editor
                    //
                    await UpdateEditorAsync(code, resp);

                }
                catch (Exception ex)
                {
                    if (showError)
                    {
                        Fail("Could not communicate with the app.\n\n{0}: {1}", ex.GetType(), ex.Message);
                    }
                }
            }
        }

        public async Task StopVisualizingAsync ()
		{
			MonitorTypeName = "";
			TypeCode.Clear ();
			try {
				Connect ();
				await conn.StopVisualizingAsync ();
			} catch (Exception ex) {
				Log ("ERROR: {0}", ex);
			}
		}

        async Task UpdateEditorAsync (LinkedCode code, EvalResponse resp)
        {
            await UpdateEditorWatchesAsync (code.Types.SelectMany (x => x.Watches), resp.WatchValues);
        }

        List<WatchVariable> lastWatches = new List<WatchVariable> ();

        async Task UpdateEditorWatchesAsync (WatchValuesResponse watchValues)
        {
            await UpdateEditorWatchesAsync (lastWatches, watchValues.WatchValues);
        }

        async Task UpdateEditorWatchesAsync (IEnumerable<WatchVariable> watches, Dictionary<string, List<string>> watchValues)
        {
            var ws = watches.ToList ();
            foreach (var w in ws) {
                if (!watchValues.TryGetValue(w.Id, out var vals)) continue;
                await SetWatchTextAsync (w, vals);
            }
            lastWatches = ws;
        }

        protected abstract Task SetWatchTextAsync (WatchVariable w, List<string> vals);

        protected string GetValsText (List<string> vals)
        {
            var maxLength = 72;
            var newText = string.Join (", ", vals);
            newText = newText.Replace ("\r\n", " ").Replace ("\n", " ").Replace ("\t", " ");
            if (newText.Length > maxLength) {
                newText = "..." + newText.Substring (newText.Length - maxLength);
            }
            return newText;
        }

        async void MonitorWatchChanges ()
        {
            var version = 0L;
            var conn = CreateConnection ();
            for (;;) {
                try {
                    //					Console.WriteLine ("MON WATCH " + DateTime.Now);
                    var res = await conn.WatchChangesAsync (version);
                    if (res != null) {
                        version = res.Version;
                        await UpdateEditorWatchesAsync (res);
                    }
                    else {
                        await Task.Delay (1000);
                    }
                }
                catch (Exception ex) {
                    Console.WriteLine (ex);
                    await Task.Delay (3000);
                }
            }
        }



        public event Action<LinkedCode> LinkedMonitoredCode = delegate {};

		protected void OnLinkedMonitoredCode (LinkedCode code)
		{
			LinkedMonitoredCode (code);
		}

        protected abstract Task<string> GetSelectedTextAsync ();

        protected void Log (string format, params object[] args)
		{
#if DEBUG
			Log (string.Format (format, args));
#endif
		}

		protected void Log (string msg)
		{
#if DEBUG
			Console.WriteLine (msg);
#endif
		}

		protected void Log (Exception ex)
		{
#if DEBUG
			Console.WriteLine (ex.ToString ());
#endif
		}
	}

}

