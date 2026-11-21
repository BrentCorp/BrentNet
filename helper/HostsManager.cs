using System.Text;

public static class HostsManager
{
    private const string BrentNetStart = "# BEGIN BRENTNET";
    private const string BrentNetEnd = "# END BRENTNET";
    private const string ManagedNotice =
        "# Managed by BrentNET Client. Do not edit this section manually.";

    private static readonly string HostsPath =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            @"drivers\etc\hosts"
        );

    public static void InstallRoutes(IEnumerable<string> hosts)
    {
        ArgumentNullException.ThrowIfNull(hosts);

        var cleanHosts = hosts
            .Where(host => !string.IsNullOrWhiteSpace(host))
            .Select(host => host.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(host => host, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var contents = File.Exists(HostsPath)
            ? File.ReadAllText(HostsPath)
            : string.Empty;

        var cleanedContents = RemoveExistingBrentNetSections(contents);
        var section = BuildBrentNetSection(cleanHosts);

        var updatedContents = string.IsNullOrWhiteSpace(cleanedContents)
            ? section
            : cleanedContents.TrimEnd() +
              Environment.NewLine +
              Environment.NewLine +
              section;

        File.WriteAllText(
            HostsPath,
            updatedContents.TrimEnd() + Environment.NewLine,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
        );
    }

    public static void RemoveRoutes()
    {
        if (!File.Exists(HostsPath))
        {
            return;
        }

        var contents = File.ReadAllText(HostsPath);
        var cleanedContents = RemoveExistingBrentNetSections(contents);

        File.WriteAllText(
            HostsPath,
            cleanedContents.TrimEnd() + Environment.NewLine,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
        );
    }

    public static string GetHostsPath()
    {
        return HostsPath;
    }

    private static string BuildBrentNetSection(IEnumerable<string> hosts)
    {
        var sb = new StringBuilder();

        sb.AppendLine(BrentNetStart);
        sb.AppendLine(ManagedNotice);

        foreach (var host in hosts)
        {
            sb.AppendLine($"127.0.0.1 {host}");
        }

        sb.Append(BrentNetEnd);

        return sb.ToString();
    }

    private static string RemoveExistingBrentNetSections(string contents)
    {
        var cleanedContents = contents;

        while (true)
        {
            var startIndex = cleanedContents.IndexOf(
                BrentNetStart,
                StringComparison.OrdinalIgnoreCase
            );

            if (startIndex < 0)
            {
                break;
            }

            var endIndex = cleanedContents.IndexOf(
                BrentNetEnd,
                startIndex,
                StringComparison.OrdinalIgnoreCase
            );

            if (endIndex < 0)
            {
                cleanedContents = cleanedContents.Remove(startIndex);
                break;
            }

            endIndex += BrentNetEnd.Length;

            while (
                endIndex < cleanedContents.Length &&
                (
                    cleanedContents[endIndex] == '\r' ||
                    cleanedContents[endIndex] == '\n'
                )
            )
            {
                endIndex++;
            }

            cleanedContents = cleanedContents.Remove(
                startIndex,
                endIndex - startIndex
            );
        }

        return cleanedContents;
    }
}