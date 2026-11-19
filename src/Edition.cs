namespace Arabara
{
    // The public download is built with build.ps1 -Release (PUBLIC_RELEASE): its start page
    // backgrounds are MARK + SEA, SOUND WAVE and RINGS. VIDEO LOOP and PICTURE are not loaded and
    // the logo's right-click does not choose files; the preferences keep any that were set.
    // A personal build has them all.
    internal static class Edition
    {
#if PUBLIC_RELEASE
        public static readonly bool Public = true;
#else
        public static readonly bool Public = false;
#endif

        // --edition: the exit code tells package.ps1 which build this is.
        public const int PublicExitCode = 70, PersonalExitCode = 71;
    }
}
