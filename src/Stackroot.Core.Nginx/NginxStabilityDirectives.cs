using System.Text;
using Stackroot.Core.Abstractions;

namespace Stackroot.Core.Nginx;

public static class NginxStabilityDirectives
{
    public static void AppendHttpDefaults(StringBuilder sb, NginxHttpSettings? http = null, string indent = "    ")
    {
        var settings = http ?? new NginxHttpSettings();
        sb.AppendLine($"{indent}fastcgi_connect_timeout {settings.FastCgiConnectTimeoutSeconds}s;");
        sb.AppendLine($"{indent}fastcgi_send_timeout {settings.FastCgiSendTimeoutSeconds}s;");
        sb.AppendLine($"{indent}fastcgi_read_timeout {settings.FastCgiReadTimeoutSeconds}s;");
        sb.AppendLine($"{indent}fastcgi_buffer_size 128k;");
        sb.AppendLine($"{indent}fastcgi_buffers 16 128k;");
        sb.AppendLine($"{indent}fastcgi_busy_buffers_size 256k;");
        sb.AppendLine($"{indent}proxy_connect_timeout {settings.ProxyConnectTimeoutSeconds}s;");
        sb.AppendLine($"{indent}proxy_send_timeout {settings.ProxySendTimeoutSeconds}s;");
        sb.AppendLine($"{indent}proxy_read_timeout {settings.ProxyReadTimeoutSeconds}s;");
    }

    public static void AppendFastCgiLocation(StringBuilder sb, NginxHttpSettings? http = null, string indent = "        ")
    {
        var settings = http ?? new NginxHttpSettings();
        sb.AppendLine($"{indent}fastcgi_connect_timeout {settings.FastCgiConnectTimeoutSeconds}s;");
        sb.AppendLine($"{indent}fastcgi_send_timeout {settings.FastCgiSendTimeoutSeconds}s;");
        sb.AppendLine($"{indent}fastcgi_read_timeout {settings.FastCgiReadTimeoutSeconds}s;");
        sb.AppendLine($"{indent}fastcgi_buffer_size 128k;");
        sb.AppendLine($"{indent}fastcgi_buffers 16 128k;");
        sb.AppendLine($"{indent}fastcgi_busy_buffers_size 256k;");
    }

    public static void AppendProxyLocation(StringBuilder sb, NginxHttpSettings? http = null, string indent = "        ")
    {
        var settings = http ?? new NginxHttpSettings();
        sb.AppendLine($"{indent}proxy_connect_timeout {settings.ProxyConnectTimeoutSeconds}s;");
        sb.AppendLine($"{indent}proxy_send_timeout {settings.ProxySendTimeoutSeconds}s;");
        sb.AppendLine($"{indent}proxy_read_timeout {settings.ProxyReadTimeoutSeconds}s;");
        sb.AppendLine($"{indent}proxy_buffering on;");
        sb.AppendLine($"{indent}proxy_buffer_size 128k;");
        sb.AppendLine($"{indent}proxy_buffers 16 128k;");
    }
}
