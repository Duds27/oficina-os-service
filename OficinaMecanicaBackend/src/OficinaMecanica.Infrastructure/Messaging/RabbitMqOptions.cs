namespace OficinaMecanica.Infrastructure.Messaging;

/// <summary>Conexão com o RabbitMQ (seção <c>RabbitMq</c>; variáveis <c>RabbitMq__*</c>).</summary>
public class RabbitMqOptions
{
    public const string Secao = "RabbitMq";

    public string Host { get; set; } = "localhost";
    public ushort Port { get; set; } = 5672;
    public string VirtualHost { get; set; } = "oficina";
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}
