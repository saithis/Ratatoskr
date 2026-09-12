using System.Runtime.CompilerServices;

namespace Ratatoskr.Tests;

internal static class TestAssemblySetup
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        // Disables reloadOnChange for physical file configuration providers in test WebApplicationFactory/Host instances.
        // On Linux machines with a low inotify user limit (e.g. fs.inotify.max_user_instances = 128), parallel test runs
        // creating numerous host builders otherwise exhaust file watch tokens and throw IOException.
        Environment.SetEnvironmentVariable("DOTNET_HOSTBUILDER__RELOADCONFIGONCHANGE", "false");
    }
}
