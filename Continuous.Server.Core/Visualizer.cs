using System;

namespace Continuous.Server
{
	public abstract class VisualizerBase
	{
		protected readonly object Context;

		public VisualizerBase(object context)
		{
			Context = context;
			Initialize ();
		}

        protected abstract void Initialize();
        public abstract void Visualize(EvalResult res);
        public abstract void StopVisualizing();
        
		protected void Log (string format, params object[] args)
		{
			#if DEBUG
			Log (string.Format (format, args));
			#endif
		}

        protected void Log (string msg)
		{
			#if DEBUG
			System.Diagnostics.Debug.WriteLine (msg);
			#endif
		}
	}

}

