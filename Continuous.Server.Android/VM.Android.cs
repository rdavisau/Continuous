using System;
using Mono.CSharp;

namespace Continuous.Server
{
	public class VM : VMBase
	{
        protected override void ApplyCompilerSettings(CompilerSettings settings)
        {
            base.ApplyCompilerSettings(settings);
            settings.AddConditionalSymbol ("__ANDROID__");
		}

		protected override void Init()
		{
            base.Init();

			object res;
			bool hasRes;
			Evaluator.Evaluate ("using Android.OS;", out res, out hasRes);
            Evaluator.Evaluate ("using Android.App;", out res, out hasRes);
            Evaluator.Evaluate ("using Android.Widget;", out res, out hasRes);
		}
	}
}

