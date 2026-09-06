namespace ValetudoUI.Valetudo;

public sealed class ValetudoApiException(string message, int statusCode, Exception? inner = null) : Exception(message, inner)
{
    public int StatusCode { get; } = statusCode;
}
