using System.Text;

namespace Stackroot.Core.Nginx;

public static class NginxStabilityDirectives
{
    public static void AppendHttpDefaults(StringBuilder sb, string indent = "    ")
    {
        sb.AppendLine($"{indent}fastcgi_connect_timeout 60s;");
        sb.AppendLine($"{indent}fastcgi_send_timeout 600s;");
        sb.AppendLine($"{indent}fastcgi_read_timeout 600s;");
        sb.AppendLine($"{indent}fastcgi_buffer_size 128k;");
        sb.AppendLine($"{indent}fastcgi_buffers 16 128k;");
        sb.AppendLine($"{indent}fastcgi_busy_buffers_size 256k;");
        sb.AppendLine($"{indent}proxy_connect_timeout 60s;");
        sb.AppendLine($"{indent}proxy_send_timeout 600s;");
        sb.AppendLine($"{indent}proxy_read_timeout 600s;");
    }

    public static void AppendFastCgiLocation(StringBuilder sb, string indent = "        ")
    {
        sb.AppendLine($"{indent}fastcgi_connect_timeout 60s;");
        sb.AppendLine($"{indent}fastcgi_send_timeout 600s;");
        sb.AppendLine($"{indent}fastcgi_read_timeout 600s;");
        sb.AppendLine($"{indent}fastcgi_buffer_size 128k;");
        sb.AppendLine($"{indent}fastcgi_buffers 16 128k;");
        sb.AppendLine($"{indent}fastcgi_busy_buffers_size 256k;");
    }

    public static void AppendProxyLocation(StringBuilder sb, string indent = "        ")
    {
        sb.AppendLine($"{indent}proxy_connect_timeout 60s;");
        sb.AppendLine($"{indent}proxy_send_timeout 600s;");
        sb.AppendLine($"{indent}proxy_read_timeout 600s;");
        sb.AppendLine($"{indent}proxy_buffering on;");
        sb.AppendLine($"{indent}proxy_buffer_size 128k;");
        sb.AppendLine($"{indent}proxy_buffers 16 128k;");
    }
}
