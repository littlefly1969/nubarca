using System.Text.RegularExpressions;

namespace NubArca.Api.Tests.Deployment;

/// <summary>
/// The API project's out-of-project assets must be inside the image build
/// context.
///
/// This exists because of a defect that reached a production image build. The
/// party-print renderer draws on the approved fonts and wordmarks, and the
/// csproj references them WHERE THEY LIVE — one manifested home for brand
/// assets, rather than a second copy of the bytes to keep true. But that home
/// is outside `src/NubArca.Api/`, which is all the Dockerfile copies, so the
/// project built perfectly from a full checkout, passed every test and every CI
/// lane, and failed only at `dotnet publish` INSIDE the image, fifteen minutes
/// into a release.
///
/// A test suite cannot notice that: it always has the whole repository. So this
/// reads the two files against each other and says, in a second, what the image
/// build says in fifteen minutes.
/// </summary>
public sealed class ProductionImageContextTests
{
    [Fact]
    public void Every_Out_Of_Project_Asset_Is_Copied_Into_The_Image_Build_Context()
    {
        var root = FindRepositoryRoot();
        var csproj = File.ReadAllText(
            Path.Combine(root, "src", "NubArca.Api", "NubArca.Api.csproj"));
        var dockerfile = File.ReadAllText(
            Path.Combine(root, "src", "NubArca.Api", "Dockerfile"));

        // Everything the project pulls in from outside its own directory.
        var escapes = Regex.Matches(csproj, @"Include=""((?:\.\.[\\/])+[^""]+)""")
            .Select(m => m.Groups[1].Value.Replace('\\', '/'))
            .Select(NormaliseFromProject)
            .Distinct()
            .ToList();

        // Deliberately asserted non-empty: if the csproj ever stops reaching
        // outside, this test passing would mean nothing, and it should be
        // deleted rather than left as decoration.
        Assert.NotEmpty(escapes);

        var copied = Regex.Matches(dockerfile, @"^COPY\s+(?!--from)(.+)$", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value.Trim())
            .SelectMany(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            // The last token is the destination, not a source.
            .Where(token => !token.StartsWith('-'))
            .Select(token => token.TrimEnd('/'))
            .ToList();

        foreach (var asset in escapes)
        {
            Assert.True(
                File.Exists(Path.Combine(root, asset.Replace('/', Path.DirectorySeparatorChar))),
                $"{asset} is referenced by the csproj but is not in the repository");

            var covered = copied.Any(source =>
                asset == source || asset.StartsWith(source + "/", StringComparison.Ordinal));
            Assert.True(covered,
                $"{asset} is referenced by NubArca.Api.csproj but no COPY in " +
                "src/NubArca.Api/Dockerfile brings it into the build context, so " +
                "`dotnet publish` will fail inside the image while every test here passes.");
        }
    }

    /// <summary>Turns a path relative to the project into one relative to the root.</summary>
    private static string NormaliseFromProject(string relative)
    {
        // The project sits two levels down, so each leading `../` climbs one.
        var segments = new List<string>(["src", "NubArca.Api"]);
        foreach (var part in relative.Split('/'))
        {
            if (part == "..")
            {
                if (segments.Count > 0) segments.RemoveAt(segments.Count - 1);
            }
            else if (part.Length > 0 && part != ".")
            {
                segments.Add(part);
            }
        }
        return string.Join('/', segments);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "NubArca.sln")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new InvalidOperationException("could not locate the repository root (NubArca.sln)");
    }
}
