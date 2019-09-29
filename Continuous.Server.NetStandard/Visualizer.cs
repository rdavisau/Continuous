using System;
using Continuous.Server.NetStandard;

namespace Continuous.Server
{
    public class Visualizer : VisualizerBase
    {
        public Visualizer(object context = null) : base(context)
        {

        }

        protected override void Initialize()
            => Throw();

        public override void Visualize(EvalResult res)
            => Throw();

        public override void StopVisualizing()
            => Throw();

        private void Throw()
            => throw new Exception(Messages.NotImplemented("visualizer"));
    }
}
