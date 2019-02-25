namespace Continuous.Server
{
    public interface INeedToKnowAboutContinuousServer
    {
        HttpServer ContinuousServer { get; set; }
    }
}