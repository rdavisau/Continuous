namespace Continuous.Server.NetStandard
{
    public static class Messages
    {
        public static string NotImplemented(string component)
            => $"You should provide a custom {component} implementation when using the netstandard library.";
    }
}
