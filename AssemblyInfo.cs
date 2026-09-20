using System.Reflection;
using System.Runtime.InteropServices;

[assembly: AssemblyTitle("Overwashed")]
[assembly: AssemblyDescription("Overwashed - Automated dishwasher and serving assistant for Overcooked! 2")]
[assembly: AssemblyCompany("hy")]
[assembly: AssemblyProduct("Overwashed")]
[assembly: AssemblyCopyright("Copyright © 2026 hy")]
[assembly: ComVisible(false)]
[assembly: AssemblyVersion(Overcooked2DishwasherBot.BuildInfo.AssemblyVersion)]
[assembly: AssemblyFileVersion(Overcooked2DishwasherBot.BuildInfo.AssemblyVersion)]
[assembly: AssemblyInformationalVersion(Overcooked2DishwasherBot.BuildInfo.Version)]

namespace Overcooked2DishwasherBot
{
    internal static class BuildInfo
    {
        internal const string Version = "1.5.10";
        internal const string AssemblyVersion = Version + ".0";
    }
}
