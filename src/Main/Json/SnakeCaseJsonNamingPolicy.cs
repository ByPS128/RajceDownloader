using System.Text;
using System.Text.Json;

namespace RajceDownloader.Main.Json;

internal sealed class SnakeCaseJsonNamingPolicy : JsonNamingPolicy
{
    // ReSharper disable once MemberCanBePrivate.Global
    public static string ToSnakeCase(string s)
    {
        if (string.IsNullOrEmpty(s))
        {
            return s;
        }

        var sb = new StringBuilder();
        for (var i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (char.IsUpper(c) is false)
            {
                sb.Append(c);
                continue;
            }

            if (i is 0)
            {
                sb.Append(char.ToLowerInvariant(c));
            }
            else if (char.IsUpper(s[i - 1]))
            {
                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
        }

        return sb.ToString();
    }

    public override string ConvertName(string name)
    {
        return ToSnakeCase(name);
    }
}