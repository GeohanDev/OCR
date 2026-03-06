namespace OcrErpSystem.Application.Auth;

public interface IAcumaticaTokenContext
{
    string? ForwardedToken { get; set; }
}
