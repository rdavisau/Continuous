using System;
using Mono.CSharp;

namespace Continuous.Server
{
	public class VM : VMBase
	{
		protected override void ApplyCompilerSettings(CompilerSettings settings)
        {
            base.ApplyCompilerSettings(settings);

            settings.AddConditionalSymbol ("__IOS__");
		}

		protected override void Init()
        {
            base.Init();

            object res;
			bool hasRes;
			Evaluator.Evaluate ("using Foundation;", out res, out hasRes);
			Evaluator.Evaluate ("using CoreGraphics;", out res, out hasRes);
			Evaluator.Evaluate ("using UIKit;", out res, out hasRes);
		}
	}
}

