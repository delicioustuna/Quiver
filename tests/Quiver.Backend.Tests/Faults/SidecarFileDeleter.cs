namespace Quiver.Backend.Tests.Faults;

/// <summary>
/// BA-9 fault injector: deletes a sidecar / metadata file from the database
/// directory. The backend must either rebuild the missing file from primary
/// data or fail-fast with a clear error — silent corruption (using stale or
/// partial state) is a contract violation.
/// </summary>
internal static class SidecarFileDeleter
{
    public static bool TryDelete(string path)
    {
        if (!File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }

    public static int DeleteByExtension(string directory, string extension)
    {
        if (!Directory.Exists(directory)) return 0;
        int n = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "*" + extension))
        {
            File.Delete(file);
            n++;
        }
        return n;
    }
}
