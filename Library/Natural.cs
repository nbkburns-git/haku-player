using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace AnimePlayer;

// Windows Explorer's name order: "OP2" before "OP10", "Episode 9" before "Episode 10".
sealed class Natural : IComparer<string?>
{
    public static readonly Natural I = new();
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)] static extern int StrCmpLogicalW(string a, string b);
    public int Compare(string? a, string? b) => StrCmpLogicalW(a ?? "", b ?? ""); // "OP2" before "OP10"
}
