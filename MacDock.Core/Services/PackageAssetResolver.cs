namespace MacDock.Core.Services;

/// <summary>
/// 解析 MSIX/AppX 包中的图标资源。Windows 包常只部署带 <c>scale-*</c> 或
/// <c>targetsize-*</c> 限定符的文件，因此清单中的未限定路径可能并不存在。
/// </summary>
internal static class PackageAssetResolver
{
    private const int PreferredAssetSize = 256;
    private const int PreferredScale = 200;

    /// <summary>
    /// 在包根目录内解析图标路径；精确文件不存在时选择同目录中的限定版本。
    /// 所有结果都必须保持在包根目录内，异常或越界时返回 <see langword="null"/>。
    /// </summary>
    internal static string? ResolveLogoPath(string packageRoot, string? requestedPath)
    {
        if (string.IsNullOrWhiteSpace(packageRoot)
            || string.IsNullOrWhiteSpace(requestedPath))
        {
            return null;
        }

        try
        {
            var root = Path.GetFullPath(packageRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            var candidate = Path.IsPathFullyQualified(requestedPath)
                ? Path.GetFullPath(requestedPath)
                : Path.GetFullPath(Path.Combine(root, requestedPath));

            if (!IsWithinRoot(candidate, root))
                return null;

            if (File.Exists(candidate))
                return candidate;

            var directory = Path.GetDirectoryName(candidate);
            var extension = Path.GetExtension(candidate);
            var stem = Path.GetFileNameWithoutExtension(candidate);
            if (string.IsNullOrWhiteSpace(directory)
                || string.IsNullOrWhiteSpace(extension)
                || string.IsNullOrWhiteSpace(stem)
                || !Directory.Exists(directory))
            {
                return null;
            }

            var qualifiedPrefix = stem + ".";
            return Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                .Where(path => IsWithinRoot(path, root))
                .Where(path => string.Equals(
                    Path.GetExtension(path),
                    extension,
                    StringComparison.OrdinalIgnoreCase))
                .Where(path => Path.GetFileNameWithoutExtension(path)
                    .StartsWith(qualifiedPrefix, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(path => GetPreferenceScore(
                    Path.GetFileNameWithoutExtension(path),
                    qualifiedPrefix))
                .ThenBy(static path => path, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static path => path, StringComparer.Ordinal)
                .FirstOrDefault();
        }
        catch (Exception exception) when (exception is ArgumentException
            or IOException
            or NotSupportedException
            or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsWithinRoot(string path, string root)
    {
        var fullPath = Path.GetFullPath(path);
        return fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static int GetPreferenceScore(string fileNameWithoutExtension, string prefix)
    {
        var qualifiers = fileNameWithoutExtension[prefix.Length..];
        var score = qualifiers.Contains("contrast-", StringComparison.OrdinalIgnoreCase)
            || qualifiers.Contains("theme-", StringComparison.OrdinalIgnoreCase)
                ? -1_000_000
                : 0;

        if (TryReadPositiveQualifier(qualifiers, "targetsize-", out var targetSize))
            return score + 200_000 - Math.Abs(targetSize - PreferredAssetSize);

        if (TryReadPositiveQualifier(qualifiers, "scale-", out var scale))
            return score + 100_000 - Math.Abs(scale - PreferredScale);

        return score;
    }

    private static bool TryReadPositiveQualifier(
        string qualifiers,
        string prefix,
        out int value)
    {
        value = 0;
        var start = qualifiers.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            return false;

        start += prefix.Length;
        var end = start;
        while (end < qualifiers.Length && char.IsAsciiDigit(qualifiers[end]))
            end++;

        return end > start
            && int.TryParse(qualifiers.AsSpan(start, end - start), out value)
            && value > 0;
    }
}
