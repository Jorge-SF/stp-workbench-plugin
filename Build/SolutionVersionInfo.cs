// Shared assembly attributes. Linked into every project (see the <Compile Include="..\..\Build\SolutionVersionInfo.cs"> item),
// so that the DLL actually carries the version from Build\SolutionVersionNumber.props.
using System.Reflection;

[assembly: AssemblyDescription(SolutionVersionInfo.ProductName + " " + AssemblyVersionInfo.VersionLabel)]
#if DEBUG
[assembly: AssemblyConfiguration("Debug")]
#else
[assembly: AssemblyConfiguration("Release")]
#endif
[assembly: AssemblyCompany(SolutionVersionInfo.CompanyName)]
[assembly: AssemblyProduct(SolutionVersionInfo.ProductName)]
[assembly: AssemblyCopyright("Copyright (c) " + AssemblyVersionInfo.CurrentYear + " " + SolutionVersionInfo.CompanyName)]
[assembly: AssemblyVersion(AssemblyVersionInfo.Version)]
[assembly: AssemblyFileVersion(AssemblyVersionInfo.Version)]

internal static class SolutionVersionInfo
{
    public const string CompanyName = @"Maptek";
    public const string ProductName = @"STP UG";
}
