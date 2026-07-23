namespace PassiveSkillTreeNotes;

internal static class PassiveTreeFingerprint
{
    public static string? FromUrl(string? treeUrl)
    {
        if (string.IsNullOrWhiteSpace(treeUrl) ||
            !Uri.TryCreate(treeUrl.Trim(), UriKind.Absolute, out var uri))
        {
            return null;
        }

        var buildCode = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault();
        if (string.IsNullOrWhiteSpace(buildCode))
        {
            return null;
        }

        try
        {
            var normalized = buildCode.Replace('-', '+').Replace('_', '/');
            normalized += (normalized.Length % 4) switch
            {
                2 => "==",
                3 => "=",
                _ => string.Empty
            };

            var data = Convert.FromBase64String(normalized);
            if (data.Length < 8)
            {
                return null;
            }

            var version =
                (data[0] << 24) |
                (data[1] << 16) |
                (data[2] << 8) |
                data[3];
            var firstNodeOffset = version > 3 ? 7 : 6;
            var nodes = new HashSet<ushort>();

            for (var index = firstNodeOffset; index + 1 < data.Length; index += 2)
            {
                var node = (ushort)((data[index] << 8) | data[index + 1]);
                if (node != 0)
                {
                    nodes.Add(node);
                }
            }

            return string.Join(",", nodes.OrderBy(node => node));
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
