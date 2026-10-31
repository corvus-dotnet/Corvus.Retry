<#
This example demonstrates a software build process using the 'ZeroFailed.Build.DotNet' extension
to provide the features needed when building a .NET solutions.
#>

$zerofailedExtensions = @(
    @{
        # References the extension from its GitHub repository. If not already installed, use latest version from 'main' will be downloaded.
        Name = "ZeroFailed.Build.DotNet"
        GitRepository = "https://github.com/zerofailed/ZeroFailed.Build.DotNet"
        GitRef = "main"
    }
    @{
        # Creates the GitHub Release for a tagged version and attaches the packages and SBOM to it.
        Name = "ZeroFailed.Build.GitHub"
        GitRepository = "https://github.com/zerofailed/ZeroFailed.Build.GitHub"
        GitRef = "main"
    }
)

# Load the tasks and process
. ZeroFailed.tasks -ZfPath $here/.zf


#
# Build process control options
#
$SkipInit = $false
$SkipVersion = $false
$SkipBuild = $false
$CleanBuild = $Clean
$SkipTest = $false
$SkipTestReport = $false
$SkipAnalysis = $false
$SkipPackage = $false


#
# Build process configuration
#
$SolutionToBuild = (Resolve-Path (Join-Path $here "Solutions/Corvus.Retry.slnx")).Path
$ProjectsToPublish = @()
$NuSpecFilesToPackage = @()
$NugetPublishSource = property ZF_NUGET_PUBLISH_SOURCE "$here/_local-nuget-feed"

# Code coverage report filters (reportgenerator assembly filters; one wildcard expression per array element)
$IncludeAssembliesInCodeCoverage = @("Corvus.Retry*")
$ExcludeAssembliesInCodeCoverage = @("Corvus.Retry.Tests*")

# SBOM: Covenant always writes Corvus.Retry.sbom.json and .html to the repository root; also produce the
# standard SPDX and CycloneDX formats so they can be attached to the GitHub Release.
$CovenantIncludeSpdxReport = $true
$CovenantIncludeCycloneDxReport = $true

# GitHub Release. The Publish stage runs for tags (and for manually forced publishes). Only create a release
# for a tag: 'gh release create' for any other version would create a new tag at the head of the default branch.
$CreateGitHubRelease = $env:GITHUB_REF_TYPE -eq "tag"
$PublishNuGetPackagesAsGitHubReleaseArtefacts = $true
$GitHubReleaseArtefacts = @(
    "$here/Corvus.Retry.sbom.json"
    "$here/Corvus.Retry.sbom.spdx.json"
    "$here/Corvus.Retry.sbom.cyclonedx.xml"
    "$here/Corvus.Retry.sbom.html"
)

# Tests run on Microsoft.Testing.Platform (enabled by the "test.runner" setting in global.json).
# ZeroFailed.Build.DotNet detects this automatically and switches to the MTP 'dotnet test' arguments
# ('--solution', '--report-trx', '--diagnostic'), while still collecting coverage via 'dotnet-coverage collect'.
# No MTP-specific configuration is needed here; the '--report-trx' option requires the test project to
# reference the TRX report extension, which the MSTest.Sdk 'Default' TestingExtensionsProfile provides.


# Customise the build process
task . FullBuild

#
# Build Process Extensibility Points - uncomment and implement as required
#

# task RunFirst {}
# task PreInit {}
# task PostInit {}
# task PreVersion {}
# task PostVersion {}
# task PreBuild {}
# task PostBuild {}
# task PreTest {}
# task PostTest {}
# task PreTestReport {}
# task PostTestReport {}
# task PreAnalysis {}
# task PostAnalysis {}
# task PrePackage {}
# task PostPackage {}
# The GitHub extension attaches only the .nupkg files; earlier releases also carried the symbol packages.
task PrePublish -If { $CreateGitHubRelease } Version,{
    $script:GitHubReleaseArtefacts += (Get-ChildItem -Path $PackagesDir -Filter "*.$($GitVersion.SemVer).snupkg").FullName
}
# task PostPublish {}
# task RunLast {}
