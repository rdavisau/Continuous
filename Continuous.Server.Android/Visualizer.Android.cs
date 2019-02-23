using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Linq;
using Android.App;
using Android.Views;

namespace Continuous.Server
{
	public class Visualizer : VisualizerBase
    {
        public Visualizer(object context) : base(context)
        {
            
        }

        protected override void Initialize()
        {

        }

        public override void Visualize(EvalResult resp)
        {
            var val = resp.Result;
            var ty = val != null ? val.GetType() : typeof(object);

            Log("{0} value = {1}", ty.FullName, val);

            ShowViewer(GetViewer(resp));
        }

        public override void StopVisualizing ()
		{

		}

		object GetViewer (EvalResult resp)
		{
			return resp.Result;
		}

		void ShowViewer (object obj)
		{
			var c = Context as global::Android.Content.Context;
			if (c == null)
				return;
			var key = Guid.NewGuid ().ToString ();
			ObjectInspector.SetKeyedObject (key, obj);
			var intent = new global::Android.Content.Intent (c, typeof (ObjectInspector));
			intent.PutExtra ("objectKey", key);
			c.StartActivity (intent);
		}
	}
}

