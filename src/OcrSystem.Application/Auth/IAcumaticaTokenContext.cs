namespace OcrSystem.Application.Auth;

public interface IAcumaticaTokenContext
{
    string? ForwardedToken { get; set; }
}
