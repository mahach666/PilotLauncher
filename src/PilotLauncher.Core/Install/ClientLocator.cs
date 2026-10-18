using PilotLauncher.Core.Catalog;

namespace PilotLauncher.Core.Install;

public sealed record ClientLocation(string AppDir, string ExeRelativePath);

public static class ClientLocator
{
    /// <summary>
    /// Finds the client executable under an unpacked MSI image. The known name wins; otherwise a single
    /// <c>Ascon.Pilot.Pilot*.exe</c> at the top of one folder is accepted.
    /// </summary>
    public static ClientLocation Find(string root, Edition edition)
    {
        var exes = Directory.EnumerateFiles(root, "*.exe", SearchOption.AllDirectories).ToList();
        foreach (var candidate in edition.ExeCandidates)
        {
            var hit = exes.FirstOrDefault(e => string.Equals(Path.GetFileName(e), candidate, StringComparison.OrdinalIgnoreCase));
            if (hit is not null)
                return At(hit);
        }

        var guesses = exes.Where(e => Path.GetFileName(e).StartsWith("Ascon.Pilot.Pilot", StringComparison.OrdinalIgnoreCase)).ToList();
        if (guesses.Count == 1)
            return At(guesses[0]);

        throw new FileNotFoundException(
            $"The {edition.DisplayName} client executable was not found. Expected {string.Join(" or ", edition.ExeCandidates)}; " +
            $"executables present: {(exes.Count == 0 ? "none" : string.Join(", ", exes.Select(Path.GetFileName)))}.");
    }

    private static ClientLocation At(string exePath) => new(Path.GetDirectoryName(exePath)!, Path.GetFileName(exePath));
}
